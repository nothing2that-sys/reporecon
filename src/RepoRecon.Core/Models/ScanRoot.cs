namespace RepoRecon.Core.Models;

public record ScanRoot(
    long Id,
    string Path,
    int MaxDepth = 10,
    bool IsActive = true,
    DateTime? LastScannedAt = null,
    bool IsOffline = false);
