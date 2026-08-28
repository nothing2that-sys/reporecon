using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoRecon.Core.Git;
using RepoRecon.Core.Models;

namespace RepoRecon.Core.Adapters;

public class GitLabAdapter : IServerAdapter
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GitLabAdapter> _logger;

    public ServerApiKind ApiKind => ServerApiKind.GitLab;

    public GitLabAdapter(HttpClient? httpClient = null, ILogger<GitLabAdapter>? logger = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _logger = logger ?? NullLogger<GitLabAdapter>.Instance;
    }

    private static string NormalizeBaseUrl(string host)
    {
        if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return host.TrimEnd('/');
        }
        if (host.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            return $"http://{host.TrimEnd('/')}";
        }
        return $"https://{host.TrimEnd('/')}";
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string? token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RepoRecon", "1.0"));
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Add("PRIVATE-TOKEN", token);
        }
        return request;
    }

    public async Task<ServerConnectionResult> TestConnectionAsync(string host, string? token, CancellationToken cancellationToken = default)
    {
        var baseUrl = NormalizeBaseUrl(host);
        try
        {
            // Try /api/v4/version first
            using var request = CreateRequest(HttpMethod.Get, $"{baseUrl}/api/v4/version", token);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(content);
                var version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : "v4";
                return new ServerConnectionResult(true, version, null, null);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new ServerConnectionResult(false, null, "401 Unauthorized: GitLab 토큰이 유효하지 않습니다.", ErrorCategory.Authentication.ToString());
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                // Fallback to /api/v4/projects?per_page=1 if /version endpoint is restricted to admins
                using var fallbackRequest = CreateRequest(HttpMethod.Get, $"{baseUrl}/api/v4/projects?per_page=1", token);
                using var fallbackResponse = await _httpClient.SendAsync(fallbackRequest, cancellationToken).ConfigureAwait(false);
                if (fallbackResponse.IsSuccessStatusCode)
                {
                    return new ServerConnectionResult(true, "GitLab v4", null, null);
                }

                return new ServerConnectionResult(false, null, "403 Forbidden: 접근 권한이 없습니다.", ErrorCategory.Permission.ToString());
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
            _logger.LogWarning(ex, "GitLab connection test HTTP error for {Host}", host);
            var errCategory = ErrorClassifier.ClassifyToString(ex.ToString());
            if (errCategory == ErrorCategory.Other.ToString())
            {
                errCategory = ErrorCategory.Network.ToString();
            }
            return new ServerConnectionResult(false, null, ex.Message, errCategory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GitLab connection test failed for {Host}", host);
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
        var baseUrl = NormalizeBaseUrl(profile.Host);
        var repos = new List<DiscoveredRemoteRepo>();
        var page = 1;
        const int perPage = 100;
        var queryParams = $"min_access_level=10&per_page={perPage}&simple=false&archived=false"; // Note: we can also include archived if API permits or filter later

        _logger.LogInformation("Starting GitLab catalog collection for {Host} with query: {QueryParams}", profile.Host, queryParams);

        while (!cancellationToken.IsCancellationRequested)
        {
            var url = $"{baseUrl}/api/v4/projects?min_access_level=10&per_page={perPage}&page={page}";
            using var request = CreateRequest(HttpMethod.Get, url, token);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var errorCategory = ErrorClassifier.ClassifyToString(errorBody);
                throw new HttpRequestException($"GitLab API returned {(int)response.StatusCode}: {response.ReasonPhrase}. Category: {errorCategory}", null, response.StatusCode);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var discovered = ParseProjectElement(item);
                if (discovered != null)
                {
                    repos.Add(discovered);
                }
            }

            progress?.Report(new CatalogProgressInfo(page, repos.Count, $"GitLab 카탈로그 수집 중 ({page}페이지, {repos.Count}개)..."));

            // Check X-Next-Page header
            if (response.Headers.TryGetValues("X-Next-Page", out var nextPages))
            {
                var nextPageStr = nextPages.FirstOrDefault();
                if (int.TryParse(nextPageStr, out var nextPage) && nextPage > page)
                {
                    page = nextPage;
                    continue;
                }
                break;
            }

            if (doc.RootElement.GetArrayLength() < perPage)
            {
                break;
            }

            page++;
        }

        _logger.LogInformation(
            "GitLabCatalogCollected Host={Host} Query={Query} TotalPages={TotalPages} TotalRepos={TotalRepos}",
            profile.Host, queryParams, page, repos.Count);

        return repos;
    }

    public static DiscoveredRemoteRepo? ParseProjectElement(JsonElement item)
    {
        try
        {
            var pathWithNamespace = item.TryGetProperty("path_with_namespace", out var pwn) ? pwn.GetString() : null;
            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
            var path = item.TryGetProperty("path", out var p) ? p.GetString() : null;

            string ns;
            string projectName;

            if (!string.IsNullOrWhiteSpace(pathWithNamespace) && pathWithNamespace.Contains('/'))
            {
                var lastSlash = pathWithNamespace.LastIndexOf('/');
                ns = pathWithNamespace[..lastSlash];
                projectName = pathWithNamespace[(lastSlash + 1)..];
            }
            else
            {
                ns = item.TryGetProperty("namespace", out var nsObj) && nsObj.TryGetProperty("full_path", out var fp)
                    ? fp.GetString() ?? string.Empty
                    : string.Empty;
                projectName = path ?? name ?? "unknown";
            }

            var httpUrl = item.TryGetProperty("http_url_to_repo", out var hUrl) ? hUrl.GetString() : null;
            var sshUrl = item.TryGetProperty("ssh_url_to_repo", out var sUrl) ? sUrl.GetString() : null;
            var webUrl = item.TryGetProperty("web_url", out var wUrl) ? wUrl.GetString() : null;

            var cloneUrl = httpUrl ?? sshUrl ?? (webUrl != null ? $"{webUrl}.git" : string.Empty);
            if (string.IsNullOrWhiteSpace(cloneUrl)) return null;

            var defaultBranch = item.TryGetProperty("default_branch", out var db) ? db.GetString() : null;
            var isArchived = item.TryGetProperty("archived", out var arch) && arch.GetBoolean();

            return new DiscoveredRemoteRepo(ns, projectName, cloneUrl, defaultBranch, isArchived);
        }
        catch
        {
            return null; // Tolerant parsing: ignore defective items
        }
    }
}
