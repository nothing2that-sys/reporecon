using RepoRecon.Core.Models;

namespace RepoRecon.Core.Git;

public interface IGitRunner
{
    Task<GitCommandResult> ExecuteAsync(
        string workingDirectory,
        string arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    Task<GitEnvironmentInfo> CheckEnvironmentAsync(CancellationToken cancellationToken = default);
}
