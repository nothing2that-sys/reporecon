namespace RepoRecon.Core.Models;

public record ScanReport(
    int TotalDiscovered,
    int AddedCount,
    int UpdatedCount,
    int UnchangedCount,
    IReadOnlyList<string> InaccessiblePaths,
    TimeSpan Duration);
