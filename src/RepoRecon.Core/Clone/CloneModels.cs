using RepoRecon.Core.Models;

namespace RepoRecon.Core.Clone;

public record CloneRequest(
    ServerRepo Repo,
    string TargetRoot,
    bool IsLfs = false);

public record ClonePreviewItem(
    ServerRepo Repo,
    string TargetPath,
    bool HasCollision,
    string? CollisionReason,
    bool ExceedsLongPath);

public record CloneResult(
    ServerRepo Repo,
    string TargetPath,
    bool IsSuccess,
    string? ErrorMessage,
    string? ErrorCategory,
    string? ClonedHead);

public record BatchCloneProgress(
    int Completed,
    int Total,
    string CurrentRepoPath,
    string Result,
    string? ErrorMessage = null);

public record ResidualTempFolder(
    string TempPath,
    string ExpectedTargetPath,
    DateTime CreatedAt);
