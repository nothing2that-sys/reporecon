using RepoRecon.Core.Models;
using RepoRecon.Core.Tree;
using Xunit;

namespace RepoRecon.Core.Tests;

public class StateSplitTests
{
    [Fact]
    public void RepoTreeBuilder_SplitsStateCodes_IntoOrphanedExternalAndUntracked()
    {
        var roots = new List<ScanRoot>
        {
            new(1, @"D:\Work", 10, true, DateTime.UtcNow, false)
        };

        var profile1 = new ServerProfile(1, "gitlab.mycorp.test", ServerApiKind.GitLab, new[] { "192.0.2.11" }, "token1");
        var serverProfiles = new List<ServerProfile> { profile1 };

        // Catalog has only project-a
        var catalogRepoA = new ServerRepo(1, 1, "team", "project-a", "https://gitlab.mycorp.test/team/project-a.git", "gitlab.mycorp.test:443/team/project-a", "main", false, DateTime.UtcNow);
        var serverRepos = new List<ServerRepo> { catalogRepoA };

        // 1. Matched repo (project-a)
        var repoMatched = new LocalRepo(1, @"D:\Work\project-a", "gitlab.mycorp.test:443/team/project-a", null, GitDirKind.Normal, false, false, false, null);
        var statusMatched = new LocalStatus(1, "main", "origin/main", 0, 0, 0, 0, "hash1", RepoStateCode.UpToDate, DateTime.UtcNow);

        // 2. Orphaned repo (project-deleted - host is registered gitlab.mycorp.test, but deleted from catalog)
        var repoOrphaned = new LocalRepo(2, @"D:\Work\project-deleted", "gitlab.mycorp.test:443/team/project-deleted", null, GitDirKind.Normal, false, false, false, null);
        var statusOrphaned = new LocalStatus(2, "main", "origin/main", 0, 0, 0, 0, "hash2", RepoStateCode.UpToDate, DateTime.UtcNow);

        // 3. External repo (github.com repo - github.com is NOT registered in profiles)
        var repoExternal = new LocalRepo(3, @"D:\Work\oss-tool", "github.com:443/open/oss-tool", null, GitDirKind.Normal, false, false, false, null);
        var statusExternal = new LocalStatus(3, "main", "origin/main", 0, 0, 0, 0, "hash3", RepoStateCode.UpToDate, DateTime.UtcNow);

        // 4. Untracked repo (no remote origin / local repo only)
        var repoUntracked = new LocalRepo(4, @"D:\Work\local-scratch", null, null, GitDirKind.Normal, false, false, false, null);
        var statusUntracked = new LocalStatus(4, "master", null, 0, 0, 0, 0, "hash4", RepoStateCode.Untracked, null);

        var reposWithStatus = new List<(LocalRepo, LocalStatus?)>
        {
            (repoMatched, statusMatched),
            (repoOrphaned, statusOrphaned),
            (repoExternal, statusExternal),
            (repoUntracked, statusUntracked)
        };

        var tree = RepoTreeBuilder.BuildTree(roots, reposWithStatus, serverProfiles, serverRepos);

        var allNodes = new List<TreeRepoNode>();
        void Collect(TreeFolderNode folder)
        {
            allNodes.AddRange(folder.Repos);
            foreach (var sub in folder.SubFolders) Collect(sub);
        }
        foreach (var root in tree) Collect(root);

        Assert.Equal(4, allNodes.Count);

        var nodeMatched = allNodes.First(n => n.Repo.Id == 1);
        var nodeOrphaned = allNodes.First(n => n.Repo.Id == 2);
        var nodeExternal = allNodes.First(n => n.Repo.Id == 3);
        var nodeUntracked = allNodes.First(n => n.Repo.Id == 4);

        // Verify Matched
        Assert.NotNull(nodeMatched.MatchedServerRepo);
        Assert.NotNull(nodeMatched.MatchedProfile);
        Assert.False(nodeMatched.IsOrphaned);
        Assert.False(nodeMatched.IsExternal);
        Assert.False(nodeMatched.IsUntracked);

        // Verify Orphaned (§10 Criterion 16)
        Assert.Null(nodeOrphaned.MatchedServerRepo);
        Assert.NotNull(nodeOrphaned.MatchedProfile);
        Assert.True(nodeOrphaned.IsOrphaned);
        Assert.False(nodeOrphaned.IsExternal);
        Assert.False(nodeOrphaned.IsUntracked);

        // Verify External (§10 Criterion 16)
        Assert.Null(nodeExternal.MatchedProfile);
        Assert.False(nodeExternal.IsOrphaned);
        Assert.True(nodeExternal.IsExternal);
        Assert.False(nodeExternal.IsUntracked);

        // Verify Untracked (§10 Criterion 16)
        Assert.Null(nodeUntracked.MatchedProfile);
        Assert.False(nodeUntracked.IsOrphaned);
        Assert.False(nodeUntracked.IsExternal);
        Assert.True(nodeUntracked.IsUntracked);
    }
}
