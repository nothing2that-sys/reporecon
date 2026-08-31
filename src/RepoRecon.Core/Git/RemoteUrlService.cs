using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoRecon.Core.Data;
using RepoRecon.Core.Models;
using RepoRecon.Core.Urls;

namespace RepoRecon.Core.Git;

public class RemoteUrlService : IRemoteUrlService
{
    private readonly IGitRunner _gitRunner;
    private readonly SqliteIndexStore _indexStore;
    private readonly HostAliasTable _aliasTable;
    private readonly ILogger<RemoteUrlService> _logger;

    public RemoteUrlService(
        IGitRunner gitRunner,
        SqliteIndexStore indexStore,
        HostAliasTable? aliasTable = null,
        ILogger<RemoteUrlService>? logger = null)
    {
        _gitRunner = gitRunner;
        _indexStore = indexStore;
        _aliasTable = aliasTable ?? new HostAliasTable();
        _logger = logger ?? NullLogger<RemoteUrlService>.Instance;
    }

    public async Task<(bool IsReachable, string? ErrorMessage)> TestRemoteReachableAsync(
        string repoPath,
        string remoteUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return (false, "URL이 비어 있습니다.");
        }

        try
        {
            // Execute git ls-remote with 10s timeout (§10.7-a)
            var result = await _gitRunner.ExecuteAsync(
                repoPath,
                $"ls-remote \"{remoteUrl}\"",
                TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                return (true, null);
            }

            var err = string.IsNullOrWhiteSpace(result.StandardError)
                ? $"종료 코드 {result.ExitCode}"
                : result.StandardError.Trim();

            return (false, err);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<string?> GetRemoteUrlAsync(
        string repoPath,
        string remoteName = "origin",
        CancellationToken cancellationToken = default)
    {
        var result = await _gitRunner.ExecuteAsync(
            repoPath,
            $"config --get remote.{remoteName}.url",
            TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return result.StandardOutput.Trim();
        }

        return null;
    }

    public async Task<(bool Success, string? ErrorMessage, string? NormalizedKey)> ChangeRemoteUrlAsync(
        long repoId,
        string repoPath,
        string remoteName,
        string newUrl,
        CancellationToken cancellationToken = default)
    {
        var oldUrl = await GetRemoteUrlAsync(repoPath, remoteName, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Changing remote URL for {Path} remote {Remote}: '{OldUrl}' -> '{NewUrl}'",
            repoPath, remoteName, oldUrl, newUrl);

        var result = await _gitRunner.ExecuteAsync(
            repoPath,
            $"remote set-url {remoteName} \"{newUrl}\"",
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            var newNormKey = RemoteUrlNormalizer.Normalize(newUrl, _aliasTable);

            // Update SQLite LocalRepo
            var allRepos = await _indexStore.GetAllLocalReposWithStatusAsync(cancellationToken).ConfigureAwait(false);
            var existing = allRepos.FirstOrDefault(r => r.Repo.Id == repoId || string.Equals(r.Repo.AbsPath, repoPath, StringComparison.OrdinalIgnoreCase));

            if (existing.Repo != null)
            {
                var updatedRepo = existing.Repo with { NormalizedKey = newNormKey };
                await _indexStore.UpsertLocalRepoAsync(updatedRepo, cancellationToken).ConfigureAwait(false);
            }

            // Record in job_history (§7.6, §10.7-c)
            try
            {
                await _indexStore.InsertJobHistoryAsync(new JobHistoryEntry(
                    0,
                    "remote_url_change",
                    repoPath,
                    oldUrl,
                    newUrl,
                    "Success",
                    null,
                    DateTime.UtcNow
                ), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record job_history for remote URL change on {Path}", repoPath);
            }

            return (true, null, newNormKey);
        }
        else
        {
            var errorMsg = string.IsNullOrWhiteSpace(result.StandardError)
                ? $"종료 코드 {result.ExitCode}"
                : result.StandardError.Trim();

            var errorCat = ErrorClassifier.ClassifyToString(result.StandardError);

            try
            {
                await _indexStore.InsertJobHistoryAsync(new JobHistoryEntry(
                    0,
                    "remote_url_change",
                    repoPath,
                    oldUrl,
                    newUrl,
                    "Failed",
                    errorCat,
                    DateTime.UtcNow
                ), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record failed job_history for {Path}", repoPath);
            }

            return (false, errorMsg, null);
        }
    }

    public async Task<(bool Success, string? ErrorMessage)> SetUpstreamBranchAsync(
        long repoId,
        string repoPath,
        string defaultBranch,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(defaultBranch))
        {
            return (false, "기본 브랜치명이 비어 있습니다.");
        }

        _logger.LogInformation("Setting upstream for {Path} to origin/{Branch}", repoPath, defaultBranch);

        var result = await _gitRunner.ExecuteAsync(
            repoPath,
            $"branch --set-upstream-to=origin/{defaultBranch}",
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            // Refresh LocalStatus in SQLite
            try
            {
                var statusResult = await _gitRunner.ExecuteAsync(
                    repoPath,
                    "status --porcelain=v2 --branch",
                    TimeSpan.FromSeconds(10),
                    cancellationToken).ConfigureAwait(false);

                if (statusResult.IsSuccess)
                {
                    var status = GitStatusParser.Parse(repoId, statusResult.StandardOutput, hasRemote: true) with { LastFetchedAt = DateTime.UtcNow };
                    await _indexStore.UpsertLocalStatusAsync(status, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to refresh LocalStatus after setting upstream for {Path}", repoPath);
            }

            return (true, null);
        }
        else
        {
            var err = string.IsNullOrWhiteSpace(result.StandardError)
                ? $"종료 코드 {result.ExitCode}"
                : result.StandardError.Trim();
            return (false, err);
        }
    }

    public IReadOnlyList<BulkHostReplacePreviewItem> PreviewBulkHostReplace(
        string oldHost,
        string newHost,
        IEnumerable<(LocalRepo Repo, string CurrentUrl)> targets)
    {
        var list = new List<BulkHostReplacePreviewItem>();
        var cleanOld = oldHost.Trim();
        var cleanNew = newHost.Trim();

        foreach (var (repo, currentUrl) in targets)
        {
            if (string.IsNullOrWhiteSpace(currentUrl))
            {
                list.Add(new BulkHostReplacePreviewItem(repo, currentUrl, string.Empty, false, "Remote URL 없음"));
                continue;
            }

            if (!ContainsHost(currentUrl, cleanOld))
            {
                list.Add(new BulkHostReplacePreviewItem(repo, currentUrl, currentUrl, false, $"호스트 '{cleanOld}' 불일치"));
                continue;
            }

            var replacedUrl = ReplaceHost(currentUrl, cleanOld, cleanNew);
            list.Add(new BulkHostReplacePreviewItem(repo, currentUrl, replacedUrl, true, null));
        }

        return list;
    }

    public async Task<IReadOnlyList<(LocalRepo Repo, bool Success, string? ErrorMessage)>> ExecuteBulkHostReplaceAsync(
        string oldHost,
        string newHost,
        IEnumerable<(LocalRepo Repo, string CurrentUrl)> targets,
        IProgress<(int Current, int Total, string Path)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var preview = PreviewBulkHostReplace(oldHost, newHost, targets);
        var eligible = preview.Where(p => p.IsEligible).ToList();
        var results = new List<(LocalRepo Repo, bool Success, string? ErrorMessage)>();

        var total = eligible.Count;
        var current = 0;

        foreach (var item in eligible)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current++;
            progress?.Report((current, total, item.Repo.AbsPath));

            var (success, errorMsg, _) = await ChangeRemoteUrlAsync(
                item.Repo.Id,
                item.Repo.AbsPath,
                "origin",
                item.NewUrl,
                cancellationToken).ConfigureAwait(false);

            results.Add((item.Repo, success, errorMsg));
        }

        return results;
    }

    public static bool ContainsHost(string url, string host)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(host)) return false;

        // Try standard URI
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase);
        }

        // SCP style: git@host:group/repo.git
        if (url.Contains('@') && url.Contains(':'))
        {
            var at = url.IndexOf('@');
            var colon = url.IndexOf(':', at);
            if (colon > at)
            {
                var scpHost = url.Substring(at + 1, colon - at - 1);
                return string.Equals(scpHost, host, StringComparison.OrdinalIgnoreCase);
            }
        }

        return url.Contains(host, StringComparison.OrdinalIgnoreCase);
    }

    public static string ReplaceHost(string url, string oldHost, string newHost)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;

        // Try URI replacement
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var builder = new UriBuilder(uri)
            {
                Host = newHost
            };
            return builder.Uri.ToString();
        }

        // SCP style: git@oldHost:group/proj.git -> git@newHost:group/proj.git
        if (url.Contains('@') && url.Contains(':'))
        {
            var at = url.IndexOf('@');
            var colon = url.IndexOf(':', at);
            if (colon > at)
            {
                var prefix = url.Substring(0, at + 1);
                var path = url.Substring(colon);
                return $"{prefix}{newHost}{path}";
            }
        }

        // Simple string replacement fallback (preserving path)
        return url.Replace(oldHost, newHost, StringComparison.OrdinalIgnoreCase);
    }
}
