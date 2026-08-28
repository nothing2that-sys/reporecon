using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoRecon.Core.Git;
using RepoRecon.Core.Models;

namespace RepoRecon.Core.Adapters;

public class GitHubAdapter : IServerAdapter
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubAdapter> _logger;

    public ServerApiKind ApiKind => ServerApiKind.GitHub;

    public GitHubAdapter(HttpClient? httpClient = null, ILogger<GitHubAdapter>? logger = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _logger = logger ?? NullLogger<GitHubAdapter>.Instance;
    }

    private static string GetApiBaseUrl(string host)
    {
        var cleanHost = host.Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
                            .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
                            .TrimEnd('/');
        if (cleanHost.Equals("github.com", StringComparison.OrdinalIgnoreCase) || cleanHost.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            return "https://api.github.com";
        }
        return $"https://{cleanHost}/api/v3";
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string? token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RepoRecon", "1.0"));
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return request;
    }

    public async Task<ServerConnectionResult> TestConnectionAsync(string host, string? token, CancellationToken cancellationToken = default)
    {
        var baseUrl = GetApiBaseUrl(host);
        try
        {
            var url = string.IsNullOrWhiteSpace(token) ? $"{baseUrl}/zen" : $"{baseUrl}/user";
            using var request = CreateRequest(HttpMethod.Get, url, token);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var apiVersion = "GitHub REST API v3";
                if (response.Headers.TryGetValues("X-GitHub-Media-Type", out var mediaTypes))
                {
                    apiVersion = mediaTypes.FirstOrDefault() ?? apiVersion;
                }
                return new ServerConnectionResult(true, apiVersion, null, null);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new ServerConnectionResult(false, null, "401 Unauthorized: GitHub 토큰이 유효하지 않습니다.", ErrorCategory.Authentication.ToString());
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return new ServerConnectionResult(false, null, "403 Forbidden: API 속도 제한 초과 또는 접근 권한 없음", ErrorCategory.Permission.ToString());
            }

            var errCategory = ErrorClassifier.ClassifyToString(response.ReasonPhrase);
            return new ServerConnectionResult(false, null, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", errCategory);
        }
        catch (OperationCanceledException)
        {
            return new ServerConnectionResult(false, null, "연결 시간 초과", ErrorCategory.Timeout.ToString());
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "GitHub connection test HTTP error for {Host}", host);
            var errCategory = ErrorClassifier.ClassifyToString(ex.ToString());
            if (errCategory == ErrorCategory.Other.ToString())
            {
                errCategory = ErrorCategory.Network.ToString();
            }
            return new ServerConnectionResult(false, null, ex.Message, errCategory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GitHub connection test failed for {Host}", host);
            var errCategory = ErrorClassifier.ClassifyToString(ex.ToString());
            return new ServerConnectionResult(false, null, ex.Message, errCategory);
        }
    }

    public async Task<IReadOnlyList<DiscoveredRemoteRepo>> FetchCatalogAsync(
        ServerProfile profile,
        string? token,
        IProgress<CatalogProgressInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = GetApiBaseUrl(profile.Host);
        var reposMap = new Dictionary<string, DiscoveredRemoteRepo>(StringComparer.OrdinalIgnoreCase);
        var pageCount = 0;

        _logger.LogInformation("Starting GitHub catalog collection for {Host} at {ApiUrl}", profile.Host, baseUrl);

        // 1. Fetch User Repos (type=all to include private, public, member, owner)
        var userReposUrl = $"{baseUrl}/user/repos?per_page=100&type=all";
        await FetchPaginatedReposAsync(userReposUrl, token, reposMap, progress, () => Interlocked.Increment(ref pageCount), cancellationToken).ConfigureAwait(false);

        // 2. Fetch Organizations & Org Repos (§5 trap 2)
        if (!string.IsNullOrWhiteSpace(token))
        {
            try
            {
                var orgsUrl = $"{baseUrl}/user/orgs?per_page=100";
                using var orgsReq = CreateRequest(HttpMethod.Get, orgsUrl, token);
                using var orgsResp = await _httpClient.SendAsync(orgsReq, cancellationToken).ConfigureAwait(false);
                if (orgsResp.IsSuccessStatusCode)
                {
                    var orgsJson = await orgsResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    using var orgsDoc = JsonDocument.Parse(orgsJson);
                    if (orgsDoc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var orgElem in orgsDoc.RootElement.EnumerateArray())
                        {
                            var orgLogin = orgElem.TryGetProperty("login", out var loginProp) ? loginProp.GetString() : null;
                            if (!string.IsNullOrWhiteSpace(orgLogin))
                            {
                                var orgReposUrl = $"{baseUrl}/orgs/{orgLogin}/repos?per_page=100&type=all";
                                await FetchPaginatedReposAsync(orgReposUrl, token, reposMap, progress, () => Interlocked.Increment(ref pageCount), cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to query organizations for GitHub user on {Host}", profile.Host);
            }
        }

        _logger.LogInformation(
            "GitHubCatalogCollected Host={Host} TotalPages={TotalPages} TotalRepos={TotalRepos}",
            profile.Host, pageCount, reposMap.Count);

        return reposMap.Values.ToList();
    }

    private async Task FetchPaginatedReposAsync(
        string initialUrl,
        string? token,
        Dictionary<string, DiscoveredRemoteRepo> resultsMap,
        IProgress<CatalogProgressInfo>? progress,
        Action onPageProcessed,
        CancellationToken cancellationToken)
    {
        string? nextUrl = initialUrl;

        while (!string.IsNullOrWhiteSpace(nextUrl) && !cancellationToken.IsCancellationRequested)
        {
            using var request = CreateRequest(HttpMethod.Get, nextUrl, token);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var errorCategory = ErrorClassifier.ClassifyToString(errorBody);
                _logger.LogWarning("GitHub API request failed: HTTP {Code} {Reason} (Category: {Category})", (int)response.StatusCode, response.ReasonPhrase, errorCategory);
                break;
            }

            onPageProcessed();

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var discovered = ParseRepoElement(item);
                    if (discovered != null)
                    {
                        var key = $"{discovered.Namespace}/{discovered.Name}";
                        resultsMap[key] = discovered;
                    }
                }
            }

            progress?.Report(new CatalogProgressInfo(0, resultsMap.Count, $"GitHub 카탈로그 수집 중 ({resultsMap.Count}개)..."));

            // Parse Link header for rel="next" (§10 Criterion 8)
            nextUrl = ExtractNextLink(response.Headers);
        }
    }

    public static string? ExtractNextLink(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Link", out var linkValues)) return null;

        foreach (var linkHeader in linkValues)
        {
            var match = Regex.Match(linkHeader, @"<([^>]+)>;\s*rel=""next""", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }
        return null;
    }

    public static DiscoveredRemoteRepo? ParseRepoElement(JsonElement item)
    {
        try
        {
            var fullName = item.TryGetProperty("full_name", out var fn) ? fn.GetString() : null;
            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
            var ownerObj = item.TryGetProperty("owner", out var ow) ? ow : default;
            var ownerName = ownerObj.ValueKind == JsonValueKind.Object && ownerObj.TryGetProperty("login", out var owLogin)
                ? owLogin.GetString()
                : "unknown";

            string ns;
            string projectName;

            if (!string.IsNullOrWhiteSpace(fullName) && fullName.Contains('/'))
            {
                var slash = fullName.IndexOf('/');
                ns = fullName[..slash];
                projectName = fullName[(slash + 1)..];
            }
            else
            {
                ns = ownerName ?? "unknown";
                projectName = name ?? "unknown";
            }

            var cloneUrl = item.TryGetProperty("clone_url", out var cu) ? cu.GetString() : null;
            var sshUrl = item.TryGetProperty("ssh_url", out var su) ? su.GetString() : null;
            var htmlUrl = item.TryGetProperty("html_url", out var hu) ? hu.GetString() : null;

            var finalCloneUrl = cloneUrl ?? sshUrl ?? (htmlUrl != null ? $"{htmlUrl}.git" : string.Empty);
            if (string.IsNullOrWhiteSpace(finalCloneUrl)) return null;

            var defaultBranch = item.TryGetProperty("default_branch", out var db) ? db.GetString() : null;
            var isArchived = item.TryGetProperty("archived", out var arch) && arch.GetBoolean();

            return new DiscoveredRemoteRepo(ns, projectName, finalCloneUrl, defaultBranch, isArchived);
        }
        catch
        {
            return null; // Tolerant parsing
        }
    }
}
