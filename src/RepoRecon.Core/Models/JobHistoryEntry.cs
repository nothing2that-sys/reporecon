namespace RepoRecon.Core.Models;

public record JobHistoryEntry(
    long Id,
    string JobType,
    string TargetPath,
    string? PreHead,
    string? PostHead,
    string Result,
    string? ErrorCategory,
    DateTime CreatedAt);
