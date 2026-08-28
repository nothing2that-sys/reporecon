namespace RepoRecon.Core.Adapters;

public record DiscoveredRemoteRepo(
    string Namespace,
    string Name,
    string CloneUrl,
    string? DefaultBranch,
    bool IsArchived);

public record ServerConnectionResult(
    bool IsSuccess,
    string? ApiVersion,
    string? ErrorMessage,
    string? ErrorCategory);

public record CatalogProgressInfo(
    int PageCount,
    int TotalCollected,
    string StatusDetail);
