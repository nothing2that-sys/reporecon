using RepoRecon.Core.Models;

namespace RepoRecon.Core.Pull;

public interface IPullEngine
{
    IReadOnlyList<PullPreviewItem> GeneratePreview(IEnumerable<(LocalRepo Repo, LocalStatus? Status)> targets);

    Task<PullBatchReport> ExecuteBatchPullAsync(
        IEnumerable<(LocalRepo Repo, LocalStatus? Status)> targets,
        IProgress<PullProgressInfo>? progress = null,
        CancellationToken cancellationToken = default);

    bool IsExecuting { get; }
}
