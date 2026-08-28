namespace RepoRecon.Core.Models;

public enum RepoStateCode
{
    UpToDate = 0,
    Behind = 1,
    Ahead = 2,
    Diverged = 3,
    NoUpstream = 5,
    UpstreamGone = 6,
    DetachedHead = 7,
    NotCloned = 8,
    Orphaned = 9,
    External = 10,
    Untracked = 11,
    UnknownUnfetched = 12,
    Conflicted = 13
}
