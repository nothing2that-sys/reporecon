namespace RepoRecon.Core.Models;

public record GitEnvironmentInfo(
    bool IsAvailable,
    Version? Version,
    string? VersionString,
    string? GitPath,
    string? ErrorMessage);
