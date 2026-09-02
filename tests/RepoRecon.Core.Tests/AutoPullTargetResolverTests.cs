using RepoRecon.Core.Models;
using RepoRecon.Core.Pull;
using Xunit;

namespace RepoRecon.Core.Tests;

public class AutoPullTargetResolverTests
{
    private static LocalRepo Repo(long id, string absPath, bool isMissing = false) =>
        new(id, absPath, NormalizedKey: null, IsMissing: isMissing);

    private static AutoPullTarget Target(string folderPath, bool recurse = true, bool isActive = true) =>
        new(1, folderPath, recurse, isActive, DateTime.UtcNow, null);

    [Fact]
    public void RepoUnderRecursiveTarget_IsEligible()
    {
        var targets = new[] { Target(@"D:\Work\TeamA") };
        var repos = new[] { (Repo(1, @"D:\Work\TeamA\sub\repo1"), (LocalStatus?)null) };

        var result = AutoPullTargetResolver.Resolve(targets, repos, Array.Empty<ScanRoot>());

        Assert.Single(result.Eligible);
        Assert.Empty(result.PreSkipped);
    }

    [Fact]
    public void RepoOutsideAnyTarget_IsExcludedEntirely()
    {
        var targets = new[] { Target(@"D:\Work\TeamA") };
        var repos = new[] { (Repo(1, @"D:\Work\TeamB\repo1"), (LocalStatus?)null) };

        var result = AutoPullTargetResolver.Resolve(targets, repos, Array.Empty<ScanRoot>());

        Assert.Empty(result.Eligible);
        Assert.Empty(result.PreSkipped);
    }

    [Fact]
    public void DirectOnlyTarget_ExcludesNestedRepo()
    {
        var targets = new[] { Target(@"D:\Work\TeamA", recurse: false) };
        var repos = new[]
        {
            (Repo(1, @"D:\Work\TeamA\direct-child"), (LocalStatus?)null),
            (Repo(2, @"D:\Work\TeamA\sub\nested-child"), (LocalStatus?)null),
        };

        var result = AutoPullTargetResolver.Resolve(targets, repos, Array.Empty<ScanRoot>());

        Assert.Single(result.Eligible);
        Assert.Equal(1, result.Eligible[0].Repo.Id);
    }

    [Fact]
    public void InactiveTarget_MatchesNothing()
    {
        var targets = new[] { Target(@"D:\Work\TeamA", isActive: false) };
        var repos = new[] { (Repo(1, @"D:\Work\TeamA\repo1"), (LocalStatus?)null) };

        var result = AutoPullTargetResolver.Resolve(targets, repos, Array.Empty<ScanRoot>());

        Assert.Empty(result.Eligible);
    }

    [Fact]
    public void RepoUnderOfflineScanRoot_IsPreSkippedNotAttempted()
    {
        var targets = new[] { Target(@"D:\Work\TeamA") };
        var repos = new[] { (Repo(1, @"D:\Work\TeamA\repo1"), (LocalStatus?)null) };
        var scanRoots = new[] { new ScanRoot(1, @"D:\Work", IsOffline: true) };

        var result = AutoPullTargetResolver.Resolve(targets, repos, scanRoots);

        Assert.Empty(result.Eligible);
        var skipped = Assert.Single(result.PreSkipped);
        Assert.Equal("스캔 루트 오프라인", skipped.SkipReason);
    }

    [Fact]
    public void MissingRepo_StillPassedThrough_NotPreFiltered()
    {
        // is_missing repos are deliberately NOT filtered here -- PullEngine.EvaluateEligibility
        // already skips them (as a Skip, not a Failure) once they reach ExecuteBatchPullAsync.
        var targets = new[] { Target(@"D:\Work\TeamA") };
        var repos = new[] { (Repo(1, @"D:\Work\TeamA\repo1", isMissing: true), (LocalStatus?)null) };

        var result = AutoPullTargetResolver.Resolve(targets, repos, Array.Empty<ScanRoot>());

        Assert.Single(result.Eligible);
    }

    [Fact]
    public void MultipleTargets_RepoMatchingAnyOneIsEligible()
    {
        var targets = new[] { Target(@"D:\Work\TeamA"), Target(@"D:\Work\TeamB") };
        var repos = new[]
        {
            (Repo(1, @"D:\Work\TeamA\repo1"), (LocalStatus?)null),
            (Repo(2, @"D:\Work\TeamB\repo2"), (LocalStatus?)null),
            (Repo(3, @"D:\Work\TeamC\repo3"), (LocalStatus?)null),
        };

        var result = AutoPullTargetResolver.Resolve(targets, repos, Array.Empty<ScanRoot>());

        Assert.Equal(2, result.Eligible.Count);
    }
}
