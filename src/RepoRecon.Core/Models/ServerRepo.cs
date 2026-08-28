namespace RepoRecon.Core.Models;

public record ServerRepo(
    long Id,
    long ProfileId,
    string Namespace,
    string Name,
    string CloneUrl,
    string NormalizedKey,
    string? DefaultBranch,
    bool IsArchived,
    DateTime CollectedAt);
