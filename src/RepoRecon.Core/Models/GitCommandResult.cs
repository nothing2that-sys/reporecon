namespace RepoRecon.Core.Models;

public record GitCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration)
{
    public bool IsSuccess => ExitCode == 0;
}
