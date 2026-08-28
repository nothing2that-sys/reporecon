using RepoRecon.Core.Git;

namespace RepoRecon.Core.Scanning;

public static class NestedRepoChecker
{
    public static async Task<bool> IsChildIgnoredByParentAsync(
        IGitRunner gitRunner,
        string parentRepoPath,
        string childRepoPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var relativePath = Path.GetRelativePath(parentRepoPath, childRepoPath);
            if (string.IsNullOrWhiteSpace(relativePath) || relativePath == "." || relativePath.StartsWith("..", StringComparison.Ordinal))
            {
                return true;
            }

            // Standardize path separators to forward slash for git check-ignore
            var forwardSlashPath = relativePath.Replace('\\', '/').TrimEnd('/') + "/";

            var result = await gitRunner.ExecuteAsync(
                parentRepoPath,
                $"check-ignore -q \"{forwardSlashPath}\"",
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);

            // Exit code 0 means git is ignoring this path
            return result.ExitCode == 0;
        }
        catch
        {
            // If any error occurs, return true to avoid false positive alarms
            return true;
        }
    }
}
