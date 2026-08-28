using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoRecon.Core.Git;
using RepoRecon.Core.Models;

namespace RepoRecon.Core.Adapters;

public class GiteaAdapter : IServerAdapter
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GiteaAdapter> _logger;

    public ServerApiKind ApiKind => ServerApiKind.Gitea;

    public GiteaAdapter(HttpClient? httpClient = null, ILogger<GiteaAdapter>? logger = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _logger = logger ?? NullLogger<GiteaAdapter>.Instance;
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
            var clean = token.Trim();
            request.Headers.TryAddWithoutValidation("Authorization", $"token {clean}");
            request.Headers.TryAddWithoutValidation("X-Gitea-Token", clean);
        }
        return request;
    }

    public async Task<ServerConnectionResult> TestConnectionAsync(string host, string? token, CancellationToken cancellationToken = default)
    {
        var baseUrl = NormalizeBaseUrl(host);
        try
        {
            // 1. Try /api/v1/version
            using var request = CreateRequest(HttpMethod.Get, $"{baseUrl}/api/v1/version", token);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(content);
                var version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : "Gitea v1";
                return new ServerConnectionResult(true, version, null, null);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new ServerConnectionResult(false, null, "401 Unauthorized: Gitea 토큰이 유효하지 않습니다.", ErrorCategory.Authentication.ToString());
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
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
            _logger.LogWarning(ex, "Gitea connection test HTTP error for {Host}", host);
            var errCategory = ErrorClassifier.ClassifyToString(ex.ToString());
            if (errCategory == ErrorCategory.Other.ToString())
            {
                errCategory = ErrorCategory.Network.ToString();
            }
            return new ServerConnectionResult(false, null, ex.Message, errCategory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gitea connection test failed for {Host}", host);
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
        const int limit = 100;
        var queryParams = $"limit={limit}&page={page}";

        _logger.LogInformation("Starting Gitea catalog collection for {Host} at {BaseUrl}", profile.Host, baseUrl);

        while (!cancellationToken.IsCancellationRequested)
        {
            var url = $"{baseUrl}/api/v1/user/repos?limit={limit}&page={page}";
            using var request = CreateRequest(HttpMethod.Get, url, token);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var errorCategory = ErrorClassifier.ClassifyToString(errorBody);
                throw new HttpRequestException($"Gitea API returned {(int)response.StatusCode}: {response.ReasonPhrase}. Category: {errorCategory}", null, response.StatusCode);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var discovered = ParseGiteaRepoElement(item);
                if (discovered != null)
                {
                    repos.Add(discovered);
                }
            }

            progress?.Report(new CatalogProgressInfo(page, repos.Count, $"Gitea 카탈로그 수집 중 ({page}페이지, {repos.Count}개)..."));

            // Check X-Total-Count header (§10 Criterion 9)
            if (response.Headers.TryGetValues("X-Total-Count", out var totalCounts))
            {
                if (int.TryParse(totalCounts.FirstOrDefault(), out var totalCount))
                {
                    if (repos.Count >= totalCount)
                    {
                        break;
                    }
                    page++;
                    continue;
                }
            }

            if (doc.RootElement.GetArrayLength() < limit)
            {
                break;
            }

            page++;
        }

        _logger.LogInformation(
            "GiteaCatalogCollected Host={Host} TotalPages={TotalPages} TotalRepos={TotalRepos}",
            profile.Host, page, repos.Count);

        return repos;
    }

    public static DiscoveredRemoteRepo? ParseGiteaRepoElement(JsonElement item)
    {
        try
        {
            var fullName = item.TryGetProperty("full_name", out var fn) ? fn.GetString() : null;
            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
            var ownerObj = item.TryGetProperty("owner", out var ow) ? ow : default;
            var ownerUsername = ownerObj.ValueKind == JsonValueKind.Object && ownerObj.TryGetProperty("username", out var owUser)
                ? owUser.GetString()
                : (ownerObj.ValueKind == JsonValueKind.Object && ownerObj.TryGetProperty("login", out var owLogin) ? owLogin.GetString() : "unknown");

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
                ns = ownerUsername ?? "unknown";
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
