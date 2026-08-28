using RepoRecon.Core.Models;

namespace RepoRecon.Core.Git;

public interface IGitRunner
{
    Task<GitCommandResult> ExecuteAsync(
        string workingDirectory,
        string arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    Task<GitCommandResult> ExecuteAsync(
        string workingDirectory,
        string arguments,
        TimeSpan? timeout,
        CancellationToken cancellationToken,
        IDictionary<string, string>? environmentVariables)
    {
        return ExecuteAsync(workingDirectory, arguments, timeout, cancellationToken);
    }

    Task<GitEnvironmentInfo> CheckEnvironmentAsync(CancellationToken cancellationToken = default);
}
