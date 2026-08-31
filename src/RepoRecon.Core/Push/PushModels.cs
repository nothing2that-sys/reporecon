using RepoRecon.Core.Models;

namespace RepoRecon.Core.Push;

/// <summary>
/// 일괄 Push 실행 전 미리보기(Dry-Run) 항목 정보.
/// </summary>
public record PushPreviewItem(
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
    string HostKey,
    string DryRunStatus,
    string? ServerRejectionMessage,
    string? DirtyWarning
);

/// <summary>
/// 개별 저장소 Push 실행 결과.
/// </summary>
public record PushExecutionItemResult(
    long RepoId,
    string AbsPath,
    bool Success,
    string? PreHead,
    string? PostHead,
    string? ErrorMessage,
    ErrorCategory? ErrorCategory,
    string? DirtyWarning,
    TimeSpan Duration
);

/// <summary>
/// 일괄 Push 실행 진행 상황.
/// </summary>
public record BatchPushProgress(
    int TotalCount,
    int ProcessedCount,
    int SuccessCount,
    int FailedCount,
    int SkippedCount,
    string CurrentRepoPath,
    string StatusMessage
);

/// <summary>
/// 일괄 Push 배치 전체 완료 결과.
/// </summary>
public record BatchPushResult(
    int TotalCount,
    int SuccessCount,
    int FailedCount,
    int SkippedCount,
    IReadOnlyList<PushExecutionItemResult> Results,
    TimeSpan TotalDuration
);
