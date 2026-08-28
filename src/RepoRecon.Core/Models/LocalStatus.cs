namespace RepoRecon.Core.Models;

public record LocalStatus(
    long LocalRepoId,
    string? Branch = null,
    string? Upstream = null,
    int Ahead = 0,
    int Behind = 0,
    int DirtyCount = 0,
    int ConflictedCount = 0,
    string? HeadHash = null,
    RepoStateCode StateCode = RepoStateCode.UnknownUnfetched,
    DateTime? LastFetchedAt = null);
