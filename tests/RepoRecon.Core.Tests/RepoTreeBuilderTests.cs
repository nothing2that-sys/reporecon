using RepoRecon.Core.Models;
using RepoRecon.Core.Tree;
using Xunit;
using Xunit.Abstractions;

namespace RepoRecon.Core.Tests;

public class RepoTreeBuilderTests
{
    private readonly ITestOutputHelper _output;

    public RepoTreeBuilderTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void BuildTree_SingleChildChain_IsCompressed()
    {
        var roots = new List<ScanRoot>
        {
            new(1, @"D:\Work", 10, true, null, false)
        };

        var repos = new List<(LocalRepo Repo, LocalStatus? Status)>
        {
            (new LocalRepo(1, @"D:\Work\Projects\2024\Client\RepoA", "gitlab.test:443/g/repo-a", null, GitDirKind.Normal, false, false, false, null),
             new LocalStatus(1, "main", "origin/main", 0, 2, 0, 0, "hash1", RepoStateCode.Behind, DateTime.UtcNow))
        };

        var tree = RepoTreeBuilder.BuildTree(roots, repos);

        Assert.Single(tree);
        var rootFolder = tree[0];
        _output.WriteLine($"Root folder: Name='{rootFolder.Name}', CompressedDisplayPath='{rootFolder.CompressedDisplayPath}'");

        // The intermediate chain Projects -> 2024 -> Client should be compressed
        Assert.Contains("Projects", rootFolder.CompressedDisplayPath);
        Assert.Contains("2024", rootFolder.CompressedDisplayPath);
        Assert.Contains("Client", rootFolder.CompressedDisplayPath);
        Assert.Single(rootFolder.Repos);
        Assert.Equal("RepoA", rootFolder.Repos[0].FolderName);
    }

    [Fact]
    public void BuildTree_StatusRollup_Accurate_AndExcludesMissing()
    {
        var roots = new List<ScanRoot>
        {
            new(1, @"D:\Work", 10, true, null, false)
        };

        var repos = new List<(LocalRepo Repo, LocalStatus? Status)>
        {
            // Repo 1: Behind 3
            (new LocalRepo(1, @"D:\Work\Group1\Repo1", "gitlab.test:443/g/r1", null, GitDirKind.Normal, false, false, false, null),
             new LocalStatus(1, "main", "origin/main", 0, 3, 0, 0, "h1", RepoStateCode.Behind, DateTime.UtcNow)),

            // Repo 2: Ahead 1, Dirty 2
            (new LocalRepo(2, @"D:\Work\Group1\Repo2", "gitlab.test:443/g/r2", null, GitDirKind.Normal, false, false, false, null),
             new LocalStatus(2, "dev", "origin/dev", 1, 0, 2, 0, "h2", RepoStateCode.Ahead, DateTime.UtcNow)),

            // Repo 3: Missing repo (Should be EXCLUDED from rollup)
            (new LocalRepo(3, @"D:\Work\Group1\Repo3", "gitlab.test:443/g/r3", null, GitDirKind.Normal, false, false, true, DateTime.UtcNow),
             new LocalStatus(3, "main", "origin/main", 0, 5, 0, 0, "h3", RepoStateCode.Behind, DateTime.UtcNow))
        };

        var tree = RepoTreeBuilder.BuildTree(roots, repos);

        Assert.Single(tree);
        var root = tree[0];

        _output.WriteLine($"Rollup: Behind={root.RollupBehind}, Ahead={root.RollupAhead}, Warning={root.RollupWarning}, Total={root.RollupTotalRepos}");
        _output.WriteLine($"Summary text: '{root.RollupSummaryText}'");

        Assert.Equal(1, root.RollupBehind); // Only Repo1 (Repo3 is missing, excluded)
        Assert.Equal(1, root.RollupAhead);  // Repo2
        Assert.Equal(1, root.RollupWarning); // Repo2 is Dirty 2
        Assert.Equal(2, root.RollupTotalRepos); // Repo1 + Repo2 (Repo3 excluded)
        Assert.Contains("↓1", root.RollupSummaryText);
        Assert.Contains("↑1", root.RollupSummaryText);
        Assert.Contains("⚠1", root.RollupSummaryText);
    }

    [Fact]
    public void BuildTree_DuplicateClones_DetectedAndLinked()
    {
        var roots = new List<ScanRoot>
        {
            new(1, @"D:\Work", 10, true, null, false)
        };

        var key = "gitlab.test:443/group/project-dup";
        var repos = new List<(LocalRepo Repo, LocalStatus? Status)>
        {
            (new LocalRepo(10, @"D:\Work\CopyA", key, null, GitDirKind.Normal, false, false, false, null), null),
            (new LocalRepo(20, @"D:\Work\CopyB", key, null, GitDirKind.Normal, false, false, false, null), null)
        };

        var tree = RepoTreeBuilder.BuildTree(roots, repos);
        Assert.Single(tree);

        var copyA = tree[0].Repos.First(r => r.Repo.Id == 10);
        var copyB = tree[0].Repos.First(r => r.Repo.Id == 20);

        Assert.Equal(2, copyA.DuplicateCloneCount);
        Assert.Equal(2, copyB.DuplicateCloneCount);
        Assert.Contains(20, copyA.DuplicateSiblingIds);
        Assert.Contains(10, copyB.DuplicateSiblingIds);
    }

    [Fact]
    public void BuildTree_NestedRepos_PlacedUnderParentRepo()
    {
        var roots = new List<ScanRoot>
        {
            new(1, @"D:\Work", 10, true, null, false)
        };

        var repos = new List<(LocalRepo Repo, LocalStatus? Status)>
        {
            (new LocalRepo(1, @"D:\Work\ParentRepo", "gitlab.test:443/g/parent", null, GitDirKind.Normal, false, false, false, null), null),
            (new LocalRepo(2, @"D:\Work\ParentRepo\libs\ChildRepo", "gitlab.test:443/g/child", 1, GitDirKind.Normal, false, false, false, null), null)
        };

        var tree = RepoTreeBuilder.BuildTree(roots, repos);
        Assert.Single(tree);

        var parentRepo = tree[0].Repos.First(r => r.Repo.Id == 1);
        Assert.Single(parentRepo.NestedChildRepos);
        Assert.Equal(2, parentRepo.NestedChildRepos[0].Repo.Id);
    }
}
