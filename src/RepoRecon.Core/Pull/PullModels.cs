namespace RepoRecon.Core.Pull;

public enum PullJobStatus
{
    Pending,
    Running,
    Success,
    Failed,
    Skipped,
    Canceled
}

public record PullPreviewItem(
    long RepoId,
    string AbsPath,
    string? NormalizedKey,
    string? Branch,
    string? Upstream,
    int Ahead,
    int Behind,
    int DirtyCount,
    int ConflictedCount,
    string Command,
    bool IsEligible,
    string? SkipReason,
    bool HasLfs,
    string HostKey);

public record PullJobResult(
    long RepoId,
    string AbsPath,
    PullJobStatus Status,
    string? PreHead,
    string? PostHead,
    string? ErrorMessage,
    string? SkipReason,
    bool WasReevaluatedOut,
    TimeSpan Duration,
    string? HostKey = null);

public record PullProgressInfo(
    int TotalCount,
    int CompletedCount,
    int RunningCount,
    long CurrentRepoId,
    string CurrentPath,
    PullJobStatus CurrentStatus,
    string? StatusDetail);

public record PullBatchReport(
    int TotalSelected,
    int ExecutedCount,
    int SuccessCount,
    int FailedCount,
    int SkippedCount,
    int ReevaluatedOutCount,
    int CanceledCount,
    TimeSpan Duration,
    IReadOnlyList<PullJobResult> Results,
    string SummaryText);
