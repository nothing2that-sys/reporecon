using System.Text.RegularExpressions;
using RepoRecon.Core.Models;

namespace RepoRecon.Core.Git;

public static class GitStatusParser
{
    private static readonly Regex AbRegex = new(@"^\#\s+branch\.ab\s+\+(?<ahead>\d+)\s+\-(?<behind>\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static LocalStatus Parse(long localRepoId, string porcelainV2Output, bool hasRemote = true)
    {
        if (string.IsNullOrWhiteSpace(porcelainV2Output))
        {
            return new LocalStatus(
                LocalRepoId: localRepoId,
                Branch: null,
                Upstream: null,
                Ahead: 0,
                Behind: 0,
                DirtyCount: 0,
                ConflictedCount: 0,
                HeadHash: null,
                StateCode: hasRemote ? RepoStateCode.UnknownUnfetched : RepoStateCode.Untracked);
        }

        string? branch = null;
        string? upstream = null;
        string? headHash = null;
        var ahead = 0;
        var behind = 0;
        var dirtyCount = 0;
        var conflictedCount = 0;
        var isDetached = false;
        var hasUpstreamHeader = false;
        var hasAbHeader = false;

        var lines = porcelainV2Output.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                var headVal = line["# branch.head ".Length..].Trim();
                if (headVal == "(detached)")
                {
                    isDetached = true;
                    branch = "(detached)";
                }
                else
                {
                    branch = headVal;
                }
            }
            else if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                upstream = line["# branch.upstream ".Length..].Trim();
                hasUpstreamHeader = !string.IsNullOrWhiteSpace(upstream);
            }
            else if (line.StartsWith("# branch.oid ", StringComparison.Ordinal))
            {
                headHash = line["# branch.oid ".Length..].Trim();
            }
            else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var abMatch = AbRegex.Match(line);
                if (abMatch.Success)
                {
                    hasAbHeader = true;
                    int.TryParse(abMatch.Groups["ahead"].Value, out ahead);
                    int.TryParse(abMatch.Groups["behind"].Value, out behind);
                }
            }
            else if (line.StartsWith("1 ", StringComparison.Ordinal) ||
                     line.StartsWith("2 ", StringComparison.Ordinal))
            {
                // Ordinary changes and renames/copies counted in dirty
                dirtyCount++;
            }
            else if (line.StartsWith("u ", StringComparison.Ordinal))
            {
                // Unmerged entries counted separately in conflicted_count (A-2)
                conflictedCount++;
            }
            // Lines starting with '?' (untracked) or '!' (ignored) are explicitly excluded from dirtyCount
        }

        var isUpstreamGone = hasUpstreamHeader && !hasAbHeader && hasRemote;

        var stateCode = DetermineState(hasRemote, isDetached, upstream, isUpstreamGone, conflictedCount, ahead, behind);

        return new LocalStatus(
            LocalRepoId: localRepoId,
            Branch: branch,
            Upstream: upstream,
            Ahead: ahead,
            Behind: behind,
            DirtyCount: dirtyCount,
            ConflictedCount: conflictedCount,
            HeadHash: headHash,
            StateCode: stateCode,
            LastFetchedAt: null);
    }

    public static RepoStateCode DetermineState(
        bool hasRemote,
        bool isDetached,
        string? upstream,
        bool isUpstreamGone,
        int conflictedCount,
        int ahead,
        int behind)
    {
        if (!hasRemote)
            return RepoStateCode.Untracked;

        if (isDetached)
            return RepoStateCode.DetachedHead;

        if (string.IsNullOrWhiteSpace(upstream))
            return RepoStateCode.NoUpstream;

        if (isUpstreamGone)
            return RepoStateCode.UpstreamGone;

        if (conflictedCount > 0)
            return RepoStateCode.Conflicted;

        if (ahead > 0 && behind > 0)
            return RepoStateCode.Diverged;

        if (ahead > 0)
            return RepoStateCode.Ahead;

        if (behind > 0)
            return RepoStateCode.Behind;

        return RepoStateCode.UpToDate;
    }
}
