using RepoRecon.Core.Models;

namespace RepoRecon.Core.Urls;

public static class NamespaceMoveDetector
{
    // §3.5 (b): a project name shared by every local repo and every server
    // repo that collided on it under the same host. A group only qualifies
    // for the one-click "이동 감지" suggestion when both sides are singletons
    // -- see IsUnique.
    public sealed record MoveGroup(
        string ProjectName,
        IReadOnlyList<LocalRepo> LocalCandidates,
        IReadOnlyList<ServerRepo> ServerCandidates)
    {
        public bool IsUnique => LocalCandidates.Count == 1 && ServerCandidates.Count == 1;
    }

    /// <summary>
    /// §3.5 (b): local repos on <paramref name="profileAuthority"/> whose
    /// normalized_key isn't in <paramref name="serverRepos"/> (i.e. orphaned),
    /// grouped by their normalized_key's last path segment (project name)
    /// against server repos on the same host sharing that segment.
    ///
    /// A group only auto-suggests (MoveGroup.IsUnique) when exactly one local
    /// orphan and exactly one server repo share that project name. Either
    /// side having more than one candidate -- two unrelated local folders
    /// that happen to share a generic trailing name, or the catalog having
    /// the same project name in two different groups -- must not be
    /// auto-decided (§3.5: "프로젝트명은 유일하지 않다"), only surfaced for
    /// manual review. This is deliberately symmetric: a single catalog match
    /// alone isn't enough if two different local orphans would both claim it.
    /// </summary>
    public static IReadOnlyList<MoveGroup> Detect(
        IReadOnlyList<LocalRepo> localRepos,
        IReadOnlyList<ServerRepo> serverRepos,
        string profileAuthority)
    {
        if (string.IsNullOrWhiteSpace(profileAuthority))
            return Array.Empty<MoveGroup>();

        var hostPrefix = profileAuthority.TrimEnd('/') + "/";

        var serverKeys = serverRepos
            .Select(s => s.NormalizedKey)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var orphanedLocal = localRepos
            .Where(l => !string.IsNullOrWhiteSpace(l.NormalizedKey) &&
                        l.NormalizedKey!.StartsWith(hostPrefix, StringComparison.OrdinalIgnoreCase) &&
                        !serverKeys.Contains(l.NormalizedKey))
            .ToList();

        if (orphanedLocal.Count == 0)
            return Array.Empty<MoveGroup>();

        var sameHostServerRepos = serverRepos
            .Where(s => !string.IsNullOrWhiteSpace(s.NormalizedKey) &&
                        s.NormalizedKey.StartsWith(hostPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var localByProject = orphanedLocal
            .GroupBy(l => GetLastSegment(l.NormalizedKey!), StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<LocalRepo>)g.ToList(), StringComparer.OrdinalIgnoreCase);

        var serverByProject = sameHostServerRepos
            .GroupBy(s => GetLastSegment(s.NormalizedKey), StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ServerRepo>)g.ToList(), StringComparer.OrdinalIgnoreCase);

        var results = new List<MoveGroup>();
        foreach (var (projectName, localGroup) in localByProject)
        {
            if (!serverByProject.TryGetValue(projectName, out var serverGroup))
                continue;

            results.Add(new MoveGroup(projectName, localGroup, serverGroup));
        }

        return results;
    }

    private static string GetLastSegment(string normalizedKey)
    {
        var slashIndex = normalizedKey.IndexOf('/');
        var pathPart = slashIndex >= 0 ? normalizedKey[(slashIndex + 1)..] : string.Empty;
        var lastSlash = pathPart.LastIndexOf('/');
        return lastSlash >= 0 ? pathPart[(lastSlash + 1)..] : pathPart;
    }
}
