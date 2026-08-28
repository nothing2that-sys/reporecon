using RepoRecon.Core.Models;

namespace RepoRecon.Core.Fetch;

public record RepoFetchResult(
    long LocalRepoId,
    string AbsPath,
    bool IsSuccess,
    string? ErrorMessage,
    LocalStatus? UpdatedStatus,
    TimeSpan Duration,
    string? HostKey = null,
    FetchFailureKind FailureKind = FetchFailureKind.None,
    bool WasSkippedByCooldown = false
);
