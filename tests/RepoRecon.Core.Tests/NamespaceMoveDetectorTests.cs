using RepoRecon.Core.Models;
using RepoRecon.Core.Urls;
using Xunit;

namespace RepoRecon.Core.Tests;

/// <summary>
/// §3.5 (b): same-host namespace-move detection. The design's own uniqueness
/// warning ("프로젝트명은 유일하지 않다") is deliberately symmetric here --
/// a group only auto-suggests when exactly one local orphan AND exactly one
/// server repo share a trailing project name. The sample-image case is a real
/// production near-miss: two unrelated local folders (different namespaces)
/// both end in "sample-image", and only one genuinely moved server-side.
/// Auto-suggesting either from a naive "server side is unique" check alone
/// would silently repoint one of them at the wrong project.
/// </summary>
public class NamespaceMoveDetectorTests
{
    private static LocalRepo Local(long id, string absPath, string? normalizedKey) =>
        new(id, absPath, normalizedKey);

    private static ServerRepo Server(long id, string ns, string name, string normalizedKey) =>
        new(id, ProfileId: 1, Namespace: ns, Name: name, CloneUrl: $"https://gitlab/{ns}/{name}.git",
            NormalizedKey: normalizedKey, DefaultBranch: "main", IsArchived: false, CollectedAt: DateTime.UnixEpoch);

    [Fact]
    public void Detect_OneLocalOrphanOneServerCandidate_IsUniqueTrue()
    {
        var local = new[]
        {
            Local(1, @"D:\Work\sample-tool", "gitlab:80/customer/sample-client/sample-tool"),
        };
        var server = new[]
        {
            Server(100, "customer/sample-client/new-namespace", "sample-tool", "gitlab:80/customer/sample-client/new-namespace/sample-tool"),
        };

        var groups = NamespaceMoveDetector.Detect(local, server, "gitlab:80");

        var group = Assert.Single(groups);
        Assert.Equal("sample-tool", group.ProjectName);
        Assert.True(group.IsUnique);
        Assert.Single(group.LocalCandidates);
        Assert.Single(group.ServerCandidates);
    }

    [Fact]
    public void Detect_RealImageTest2Collision_TwoUnrelatedLocalOrphansOneServerMatch_IsUniqueFalse()
    {
        // Real production shape: local id 11 is an unrelated "tools" repo
        // that happens to end in the same trailing name; local id 21 is the
        // actual moved repo (old path, missing the new-namespace segment).
        // Neither should be auto-suggested -- only surfaced for manual review.
        var local = new[]
        {
            Local(11, @"D:\sample-workspace\items\image inspection\sample-image",
                "192.0.2.9:10012/sample-workspace/tools/sample-image"),
            Local(21, @"D:\sample-workspace\items\customer\sample-client\sample-image-local",
                "192.0.2.9:10012/sample-workspace/customer/sample-client/sample-image"),
        };
        var server = new[]
        {
            Server(823, "sample-workspace/customer/sample-client/new-namespace", "sample-image",
                "192.0.2.9:10012/sample-workspace/customer/sample-client/new-namespace/sample-image"),
        };

        var groups = NamespaceMoveDetector.Detect(local, server, "192.0.2.9:10012");

        var group = Assert.Single(groups);
        Assert.Equal("sample-image", group.ProjectName);
        Assert.False(group.IsUnique);
        Assert.Equal(2, group.LocalCandidates.Count);
        Assert.Single(group.ServerCandidates);
        Assert.Contains(group.LocalCandidates, l => l.Id == 11);
        Assert.Contains(group.LocalCandidates, l => l.Id == 21);
    }

    [Fact]
    public void Detect_TwoServerCandidatesSameProjectNameDifferentGroups_IsUniqueFalse()
    {
        var local = new[]
        {
            Local(1, @"D:\Work\utils", "gitlab:80/team-a/utils"),
        };
        var server = new[]
        {
            Server(100, "team-a-renamed", "utils", "gitlab:80/team-a-renamed/utils"),
            Server(101, "team-b", "utils", "gitlab:80/team-b/utils"),
        };

        var groups = NamespaceMoveDetector.Detect(local, server, "gitlab:80");

        var group = Assert.Single(groups);
        Assert.False(group.IsUnique);
        Assert.Equal(2, group.ServerCandidates.Count);
    }

    [Fact]
    public void Detect_LocalAlreadyMatchesCatalogExactly_NotTreatedAsOrphan()
    {
        var local = new[]
        {
            Local(1, @"D:\Work\sample-tool", "gitlab:80/customer/sample-client/sample-tool"),
        };
        var server = new[]
        {
            Server(100, "customer/sample-client", "sample-tool", "gitlab:80/customer/sample-client/sample-tool"),
        };

        var groups = NamespaceMoveDetector.Detect(local, server, "gitlab:80");

        Assert.Empty(groups);
    }

    [Fact]
    public void Detect_DifferentHost_Excluded()
    {
        var local = new[]
        {
            Local(1, @"D:\Work\sample-tool", "othergitlab:443/customer/sample-client/sample-tool"),
        };
        var server = new[]
        {
            Server(100, "customer/sample-client/new-namespace", "sample-tool", "gitlab:80/customer/sample-client/new-namespace/sample-tool"),
        };

        var groups = NamespaceMoveDetector.Detect(local, server, "gitlab:80");

        Assert.Empty(groups);
    }

    [Fact]
    public void Detect_NoServerCandidateForProjectName_NotReturned()
    {
        var local = new[]
        {
            Local(1, @"D:\Work\deleted-project", "gitlab:80/customer/deleted-project"),
        };
        var server = Array.Empty<ServerRepo>();

        var groups = NamespaceMoveDetector.Detect(local, server, "gitlab:80");

        Assert.Empty(groups);
    }
}
