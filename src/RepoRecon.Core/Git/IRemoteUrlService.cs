using RepoRecon.Core.Models;

namespace RepoRecon.Core.Git;

public record BulkHostReplacePreviewItem(
    LocalRepo Repo,
    string CurrentUrl,
    string NewUrl,
    bool IsEligible,
    string? SkipReason);

public interface IRemoteUrlService
{
    Task<(bool IsReachable, string? ErrorMessage)> TestRemoteReachableAsync(string repoPath, string remoteUrl, CancellationToken cancellationToken = default);

    Task<string?> GetRemoteUrlAsync(string repoPath, string remoteName = "origin", CancellationToken cancellationToken = default);

    Task<(bool Success, string? ErrorMessage, string? NormalizedKey)> ChangeRemoteUrlAsync(
        long repoId,
        string repoPath,
        string remoteName,
        string newUrl,
        CancellationToken cancellationToken = default);

    Task<(bool Success, string? ErrorMessage)> SetUpstreamBranchAsync(
        long repoId,
        string repoPath,
        string defaultBranch,
        CancellationToken cancellationToken = default);

    IReadOnlyList<BulkHostReplacePreviewItem> PreviewBulkHostReplace(
        string oldHost,
        string newHost,
        IEnumerable<(LocalRepo Repo, string CurrentUrl)> targets);

    Task<IReadOnlyList<(LocalRepo Repo, bool Success, string? ErrorMessage)>> ExecuteBulkHostReplaceAsync(
        string oldHost,
        string newHost,
        IEnumerable<(LocalRepo Repo, string CurrentUrl)> targets,
        IProgress<(int Current, int Total, string Path)>? progress = null,
        CancellationToken cancellationToken = default);
}
