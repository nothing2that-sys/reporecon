using RepoRecon.Core.Models;

namespace RepoRecon.Core.Tree;

public static class RepoTreeBuilder
{
    public static List<TreeFolderNode> BuildTree(
        IReadOnlyList<ScanRoot> roots,
        IReadOnlyList<(LocalRepo Repo, LocalStatus? Status)> reposWithStatus,
        IReadOnlyList<ServerProfile>? serverProfiles = null,
        IReadOnlyList<ServerRepo>? serverRepos = null)
    {
        var repoNodesMap = new Dictionary<long, TreeRepoNode>();
        var repoByPathMap = new Dictionary<string, TreeRepoNode>(StringComparer.OrdinalIgnoreCase);

        // Catalog lookup map
        var catalogMap = serverRepos != null
            ? serverRepos.GroupBy(r => r.NormalizedKey, StringComparer.OrdinalIgnoreCase)
                         .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ServerRepo>(StringComparer.OrdinalIgnoreCase);

        // 1. Create all TreeRepoNodes
        foreach (var (repo, status) in reposWithStatus)
        {
            var node = new TreeRepoNode(repo, status);

            // Reconcile server status (§6)
            if (!string.IsNullOrWhiteSpace(repo.NormalizedKey))
            {
                var hostPart = node.HostName;
                ServerProfile? matchedProfile = null;

                if (serverProfiles != null && serverProfiles.Count > 0)
                {
                    matchedProfile = serverProfiles.FirstOrDefault(p =>
                        p.Host.Equals(hostPart, StringComparison.OrdinalIgnoreCase) ||
                        p.Aliases.Any(a => a.Equals(hostPart, StringComparison.OrdinalIgnoreCase)) ||
                        repo.NormalizedKey.StartsWith(p.Host + ":", StringComparison.OrdinalIgnoreCase) ||
                        repo.NormalizedKey.StartsWith(p.Host + "/", StringComparison.OrdinalIgnoreCase));
                }

                if (matchedProfile != null)
                {
                    node.MatchedProfile = matchedProfile;
                    if (catalogMap.TryGetValue(repo.NormalizedKey, out var sRepo))
                    {
                        node.MatchedServerRepo = sRepo;
                    }
                    else
                    {
                        node.IsOrphaned = true;
                    }
                }
                else
                {
                    node.IsExternal = true;
                }
            }

            repoNodesMap[repo.Id] = node;
            repoByPathMap[repo.AbsPath] = node;
        }

        // 2. Identify duplicate clones by NormalizedKey (excluding null)
        var duplicateGroups = repoNodesMap.Values
            .Where(n => !string.IsNullOrWhiteSpace(n.Repo.NormalizedKey))
            .GroupBy(n => n.Repo.NormalizedKey!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var group in duplicateGroups)
        {
            var groupList = group.ToList();
            var allIds = groupList.Select(x => x.Repo.Id).ToList();
            foreach (var node in groupList)
            {
                node.DuplicateCloneCount = groupList.Count;
                node.DuplicateSiblingIds.AddRange(allIds.Where(id => id != node.Repo.Id));
            }
        }

        // 3. Connect nested child repos to their parent TreeRepoNode
        var nestedChildRepoIds = new HashSet<long>();
        foreach (var node in repoNodesMap.Values)
        {
            TreeRepoNode? parentNode = null;
            if (node.Repo.ParentRepoId.HasValue && repoNodesMap.TryGetValue(node.Repo.ParentRepoId.Value, out var p))
            {
                parentNode = p;
            }
            else
            {
                var potentialParents = repoNodesMap.Values
                    .Where(p => p.Repo.Id != node.Repo.Id && IsSubPath(p.Repo.AbsPath, node.Repo.AbsPath))
                    .OrderByDescending(p => p.Repo.AbsPath.Length)
                    .ToList();
                if (potentialParents.Count > 0)
                {
                    parentNode = potentialParents[0];
                }
            }

            if (parentNode != null)
            {
                parentNode.NestedChildRepos.Add(node);
                nestedChildRepoIds.Add(node.Repo.Id);
            }
        }

        // 4. Build folder hierarchy per active scan root
        var rootFolderNodes = new List<TreeFolderNode>();
        var activeRoots = roots.Where(r => r.IsActive).OrderBy(r => r.Path.Length).ToList();

        // Repos that are not nested inside another repo
        var topLevelRepoNodes = repoNodesMap.Values
            .Where(n => !nestedChildRepoIds.Contains(n.Repo.Id))
            .OrderBy(n => n.Repo.AbsPath)
            .ToList();

        // Fallback root for repos outside explicit scan roots
        var fallbackRoots = new Dictionary<string, TreeFolderNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var repoNode in topLevelRepoNodes)
        {
            var matchedRoot = activeRoots.FirstOrDefault(r => IsSubPath(r.Path, repoNode.Repo.AbsPath));

            TreeFolderNode rootFolder;
            string relativePath;

            if (matchedRoot != null)
            {
                var existing = rootFolderNodes.FirstOrDefault(f => f.FullPath.Equals(matchedRoot.Path, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    existing = new TreeFolderNode(Path.GetFileName(matchedRoot.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? matchedRoot.Path, matchedRoot.Path);
                    rootFolderNodes.Add(existing);
                }
                rootFolder = existing;
                relativePath = Path.GetRelativePath(matchedRoot.Path, repoNode.Repo.AbsPath);
            }
            else
            {
                var driveRoot = Path.GetPathRoot(repoNode.Repo.AbsPath) ?? "Root";
                if (!fallbackRoots.TryGetValue(driveRoot, out rootFolder!))
                {
                    rootFolder = new TreeFolderNode(driveRoot, driveRoot);
                    fallbackRoots[driveRoot] = rootFolder;
                    rootFolderNodes.Add(rootFolder);
                }
                relativePath = Path.GetRelativePath(driveRoot, repoNode.Repo.AbsPath);
            }

            if (string.IsNullOrWhiteSpace(relativePath) || relativePath == ".")
            {
                rootFolder.Repos.Add(repoNode);
            }
            else
            {
                var segments = relativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                var currentFolder = rootFolder;

                for (var i = 0; i < segments.Length - 1; i++)
                {
                    var seg = segments[i];
                    var sub = currentFolder.SubFolders.FirstOrDefault(f => f.Name.Equals(seg, StringComparison.OrdinalIgnoreCase));
                    if (sub == null)
                    {
                        var fullSubPath = Path.Combine(currentFolder.FullPath, seg);
                        sub = new TreeFolderNode(seg, fullSubPath);
                        currentFolder.SubFolders.Add(sub);
                    }
                    currentFolder = sub;
                }

                currentFolder.Repos.Add(repoNode);
            }
        }

        // 5. Apply single-child chain compression to intermediate folders
        foreach (var root in rootFolderNodes)
        {
            CompressFolderChains(root);
        }

        // 6. Compute status rollup counts recursively
        foreach (var root in rootFolderNodes)
        {
            ComputeRollup(root);
        }

        return rootFolderNodes;
    }

    private static void CompressFolderChains(TreeFolderNode folder)
    {
        // Compress children first
        for (var i = 0; i < folder.SubFolders.Count; i++)
        {
            CompressFolderChains(folder.SubFolders[i]);
        }

        // If this folder has 0 repos and exactly 1 subfolder, compress child into this folder
        while (folder.Repos.Count == 0 && folder.SubFolders.Count == 1)
        {
            var singleChild = folder.SubFolders[0];
            folder.Name = singleChild.Name;
            folder.FullPath = singleChild.FullPath;
            folder.CompressedSegments.AddRange(singleChild.CompressedSegments);
            folder.Repos.AddRange(singleChild.Repos);
            folder.SubFolders.Clear();
            folder.SubFolders.AddRange(singleChild.SubFolders);
        }
    }

    private static void ComputeRollup(TreeFolderNode folder)
    {
        var behind = 0;
        var ahead = 0;
        var warning = 0;
        var total = 0;

        foreach (var repo in folder.Repos)
        {
            // Missing repos are excluded from rollup (설계 §5.3-e)
            if (repo.Repo.IsMissing)
                continue;

            total++;

            var status = repo.Status;
            if (status != null)
            {
                if (status.Behind > 0 || status.StateCode == RepoStateCode.Behind)
                {
                    behind++;
                }

                if (status.Ahead > 0 || status.StateCode == RepoStateCode.Ahead)
                {
                    ahead++;
                }

                var isWarning = status.DirtyCount > 0 ||
                                status.ConflictedCount > 0 ||
                                status.StateCode == RepoStateCode.Diverged ||
                                status.StateCode == RepoStateCode.NoUpstream ||
                                status.StateCode == RepoStateCode.UpstreamGone ||
                                status.StateCode == RepoStateCode.DetachedHead ||
                                status.StateCode == RepoStateCode.Untracked;

                if (isWarning)
                {
                    warning++;
                }
            }

            // Include nested child repos in rollup
            foreach (var nested in repo.NestedChildRepos)
            {
                if (nested.Repo.IsMissing)
                    continue;

                total++;
                var nestedStatus = nested.Status;
                if (nestedStatus != null)
                {
                    if (nestedStatus.Behind > 0 || nestedStatus.StateCode == RepoStateCode.Behind)
                        behind++;
                    if (nestedStatus.Ahead > 0 || nestedStatus.StateCode == RepoStateCode.Ahead)
                        ahead++;
                    if (nestedStatus.DirtyCount > 0 ||
                        nestedStatus.ConflictedCount > 0 ||
                        nestedStatus.StateCode == RepoStateCode.Diverged ||
                        nestedStatus.StateCode == RepoStateCode.NoUpstream ||
                        nestedStatus.StateCode == RepoStateCode.UpstreamGone ||
                        nestedStatus.StateCode == RepoStateCode.DetachedHead ||
                        nestedStatus.StateCode == RepoStateCode.Untracked)
                    {
                        warning++;
                    }
                }
            }
        }

        foreach (var sub in folder.SubFolders)
        {
            ComputeRollup(sub);
            behind += sub.RollupBehind;
            ahead += sub.RollupAhead;
            warning += sub.RollupWarning;
            total += sub.RollupTotalRepos;
        }

        folder.RollupBehind = behind;
        folder.RollupAhead = ahead;
        folder.RollupWarning = warning;
        folder.RollupTotalRepos = total;
    }

    private static bool IsSubPath(string parentPath, string childPath)
    {
        var parent = Path.GetFullPath(parentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var child = Path.GetFullPath(childPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return child.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }
}
