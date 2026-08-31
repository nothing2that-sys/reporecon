namespace RepoRecon.Core.Git;

public interface IRollbackService
{
    Task<(bool Success, string? ErrorMessage)> RollbackCommitAsync(
        long repoId,
        string repoPath,
        string preHeadHash,
        CancellationToken cancellationToken = default);
}
