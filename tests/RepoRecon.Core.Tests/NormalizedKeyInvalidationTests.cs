using RepoRecon.Core.Data;
using RepoRecon.Core.Models;
using Xunit;

namespace RepoRecon.Core.Tests;

/// <summary>
/// SS2.3/SS4.4: adding, changing, or removing a host alias can make an
/// already-cached local_repos.normalized_key wrong, and RepoScanner (SS4.4)
/// only re-queries remote.origin.url for a NULL key. These tests cover the
/// invalidation query directly -- exact authority match for a "host:port"
/// alias, port-ignoring match for a bare "host" alias, and that unrelated
/// rows (different authority entirely) are left alone.
/// </summary>
public class NormalizedKeyInvalidationTests
{
    private static async Task<SqliteIndexStore> CreateStoreWithReposAsync(string tempDbPath, params (string AbsPath, string NormalizedKey)[] repos)
    {
        var migrator = new SchemaMigrator();
        migrator.EnsureInitialized(tempDbPath);
        var store = new SqliteIndexStore(tempDbPath);

        foreach (var (absPath, normalizedKey) in repos)
        {
            await store.UpsertLocalRepoAsync(new LocalRepo(
                Id: 0,
                AbsPath: absPath,
                NormalizedKey: normalizedKey,
                ParentRepoId: null,
                GitDirKind: GitDirKind.Normal,
                HasLfs: false,
                HasSubmodule: false,
                IsMissing: false,
                MissingSince: null));
        }

        return store;
    }

    [Fact]
    public async Task InvalidateNormalizedKeysForAliasAsync_AuthorityAlias_MatchesExactHostAndPortOnly()
    {
        var tempDbPath = Path.Combine(Path.GetTempPath(), "reporecon_test_invalidate_authority_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var store = await CreateStoreWithReposAsync(
                tempDbPath,
                (@"D:\Work\A", "192.0.2.9:10012/customer/sample-client/sample-tool"),
                (@"D:\Work\B", "192.0.2.9:3000/customer/sample-client/other-repo"), // same host, different port -- must survive
                (@"D:\Work\C", "gitlab:80/customer/sample-client/sample-tool")); // unrelated authority -- must survive

            var invalidated = await store.InvalidateNormalizedKeysForAliasAsync("192.0.2.9:10012");

            Assert.Equal(1, invalidated);

            var repos = await store.GetAllLocalReposWithStatusAsync();
            var byPath = repos.ToDictionary(r => r.Repo.AbsPath, r => r.Repo.NormalizedKey);

            Assert.Null(byPath[@"D:\Work\A"]);
            Assert.Equal("192.0.2.9:3000/customer/sample-client/other-repo", byPath[@"D:\Work\B"]);
            Assert.Equal("gitlab:80/customer/sample-client/sample-tool", byPath[@"D:\Work\C"]);
        }
        finally
        {
            CleanupDb(tempDbPath);
        }
    }

    [Fact]
    public async Task InvalidateNormalizedKeysForAliasAsync_HostOnlyAlias_IgnoresPort()
    {
        var tempDbPath = Path.Combine(Path.GetTempPath(), "reporecon_test_invalidate_hostonly_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var store = await CreateStoreWithReposAsync(
                tempDbPath,
                (@"D:\Work\A", "gitlab-old.test:80/g/p1"),
                (@"D:\Work\B", "gitlab-old.test:2222/g/p2"),
                (@"D:\Work\C", "gitlab-old-2.test:80/g/p3")); // different host that happens to share a prefix -- must survive

            var invalidated = await store.InvalidateNormalizedKeysForAliasAsync("gitlab-old.test");

            Assert.Equal(2, invalidated);

            var repos = await store.GetAllLocalReposWithStatusAsync();
            var byPath = repos.ToDictionary(r => r.Repo.AbsPath, r => r.Repo.NormalizedKey);

            Assert.Null(byPath[@"D:\Work\A"]);
            Assert.Null(byPath[@"D:\Work\B"]);
            Assert.Equal("gitlab-old-2.test:80/g/p3", byPath[@"D:\Work\C"]);
        }
        finally
        {
            CleanupDb(tempDbPath);
        }
    }

    [Fact]
    public async Task InvalidateNormalizedKeysForAliasAsync_NoMatches_ReturnsZeroAndLeavesRowsUntouched()
    {
        var tempDbPath = Path.Combine(Path.GetTempPath(), "reporecon_test_invalidate_nomatch_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var store = await CreateStoreWithReposAsync(
                tempDbPath,
                (@"D:\Work\A", "gitlab.test:443/g/p1"));

            var invalidated = await store.InvalidateNormalizedKeysForAliasAsync("unrelated-host.test:443");

            Assert.Equal(0, invalidated);

            var repos = await store.GetAllLocalReposWithStatusAsync();
            Assert.Equal("gitlab.test:443/g/p1", repos.Single().Repo.NormalizedKey);
        }
        finally
        {
            CleanupDb(tempDbPath);
        }
    }

    private static void CleanupDb(string tempDbPath)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(tempDbPath);
        File.Delete($"{tempDbPath}-wal");
        File.Delete($"{tempDbPath}-shm");
    }
}
