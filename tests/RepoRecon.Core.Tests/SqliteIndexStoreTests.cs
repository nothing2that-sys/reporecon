using RepoRecon.Core.Data;
using RepoRecon.Core.Models;
using Xunit;

namespace RepoRecon.Core.Tests;

public class SqliteIndexStoreTests
{
    [Fact]
    public async Task SqliteIndexStore_CrudAndMigrationV2_WorksProperly()
    {
        var tempDbPath = Path.Combine(Path.GetTempPath(), "reporecon_test_v2_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var migrator = new SchemaMigrator();
            var wasRecreated = migrator.EnsureInitialized(tempDbPath);

            Assert.False(wasRecreated);
            Assert.True(File.Exists(tempDbPath));

            var store = new SqliteIndexStore(tempDbPath);

            // 1. ScanRoot CRUD
            var rootId = await store.AddScanRootAsync(@"D:\Work", maxDepth: 8);
            Assert.True(rootId > 0);

            var roots = await store.GetScanRootsAsync();
            Assert.Single(roots);
            Assert.Equal(@"D:\Work", roots[0].Path);
            Assert.Equal(8, roots[0].MaxDepth);
            Assert.True(roots[0].IsActive);
            Assert.False(roots[0].IsOffline);

            await store.UpdateScanRootAsync(roots[0] with { IsOffline = true });
            var updatedRoots = await store.GetScanRootsAsync();
            Assert.True(updatedRoots[0].IsOffline);

            // 2. LocalRepo & Status Upsert with v2 fields (normalized_key null, conflicted_count, is_missing)
            var repoId = await store.UpsertLocalRepoAsync(new LocalRepo(
                Id: 0,
                AbsPath: @"D:\Work\ProjectA",
                NormalizedKey: "gitlab.test:443/group/project-a",
                ParentRepoId: null,
                GitDirKind: GitDirKind.Normal,
                HasLfs: false,
                HasSubmodule: false,
                IsMissing: false,
                MissingSince: null));

            Assert.True(repoId > 0);

            await store.UpsertLocalStatusAsync(new LocalStatus(
                LocalRepoId: repoId,
                Branch: "main",
                Upstream: "origin/main",
                Ahead: 2,
                Behind: 0,
                DirtyCount: 1,
                ConflictedCount: 1,
                HeadHash: "abc1234",
                StateCode: RepoStateCode.Ahead,
                LastFetchedAt: null));

            var reposWithStatus = await store.GetAllLocalReposWithStatusAsync();
            Assert.Single(reposWithStatus);
            var (repo, status) = reposWithStatus[0];
            Assert.Equal(@"D:\Work\ProjectA", repo.AbsPath);
            Assert.Equal("gitlab.test:443/group/project-a", repo.NormalizedKey);
            Assert.NotNull(status);
            Assert.Equal("main", status.Branch);
            Assert.Equal("origin/main", status.Upstream);
            Assert.Equal(2, status.Ahead);
            Assert.Equal(0, status.Behind);
            Assert.Equal(1, status.DirtyCount);
            Assert.Equal(1, status.ConflictedCount);
            Assert.Equal(RepoStateCode.Ahead, status.StateCode);

            // 3. Mark missing & clear missing
            var missingTime = DateTime.UtcNow;
            await store.MarkRepoMissingAsync(repoId, missingTime);
            var missingList = await store.GetAllLocalReposWithStatusAsync();
            Assert.True(missingList[0].Repo.IsMissing);

            await store.ClearRepoMissingAsync(repoId);
            var clearedList = await store.GetAllLocalReposWithStatusAsync();
            Assert.False(clearedList[0].Repo.IsMissing);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(tempDbPath))
            {
                try { File.Delete(tempDbPath); } catch { }
            }
        }
    }

    [Fact]
    public void SchemaMigrator_RecreateDatabase_CleansMainAndSidecarFiles()
    {
        var tempDbPath = Path.Combine(Path.GetTempPath(), "reporecon_sidecar_test_" + Guid.NewGuid().ToString("N") + ".db");
        var walPath = $"{tempDbPath}-wal";
        var shmPath = $"{tempDbPath}-shm";

        try
        {
            File.WriteAllText(tempDbPath, "corrupted db content");
            File.WriteAllText(walPath, "dummy wal");
            File.WriteAllText(shmPath, "dummy shm");

            var migrator = new SchemaMigrator();
            var wasRecreated = migrator.EnsureInitialized(tempDbPath);

            Assert.True(wasRecreated);
            Assert.False(File.Exists(walPath));
            Assert.False(File.Exists(shmPath));
            Assert.True(File.Exists(tempDbPath));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(tempDbPath)) try { File.Delete(tempDbPath); } catch { }
            if (File.Exists(walPath)) try { File.Delete(walPath); } catch { }
            if (File.Exists(shmPath)) try { File.Delete(shmPath); } catch { }
        }
    }

    [Fact]
    public async Task SqliteIndexStore_JobHistory_InsertsAndQueriesCorrectly()
    {
        var tempDbPath = Path.Combine(Path.GetTempPath(), "reporecon_jobhistory_test_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var migrator = new SchemaMigrator();
            migrator.EnsureInitialized(tempDbPath);

            var store = new SqliteIndexStore(tempDbPath);

            var entry1 = new JobHistoryEntry(
                Id: 0,
                JobType: "pull",
                TargetPath: @"D:\Work\Repo1",
                PreHead: "aaa111",
                PostHead: "bbb222",
                Result: "Success",
                ErrorCategory: null,
                CreatedAt: DateTime.UtcNow.AddMinutes(-5));

            var entry2 = new JobHistoryEntry(
                Id: 0,
                JobType: "pull",
                TargetPath: @"D:\Work\Repo2",
                PreHead: "ccc333",
                PostHead: "ccc333",
                Result: "Failed",
                ErrorCategory: "Authentication",
                CreatedAt: DateTime.UtcNow);

            var id1 = await store.InsertJobHistoryAsync(entry1);
            var id2 = await store.InsertJobHistoryAsync(entry2);

            Assert.True(id1 > 0);
            Assert.True(id2 > 0);

            var allHistory = await store.GetJobHistoryAsync();
            Assert.Equal(2, allHistory.Count);

            var repo1History = await store.GetJobHistoryAsync(@"D:\Work\Repo1");
            Assert.Single(repo1History);
            Assert.Equal("Success", repo1History[0].Result);
            Assert.Equal("aaa111", repo1History[0].PreHead);
            Assert.Equal("bbb222", repo1History[0].PostHead);

            var repo2History = await store.GetJobHistoryAsync(@"D:\Work\Repo2");
            Assert.Single(repo2History);
            Assert.Equal("Failed", repo2History[0].Result);
            Assert.Equal("Authentication", repo2History[0].ErrorCategory);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(tempDbPath)) try { File.Delete(tempDbPath); } catch { }
        }
    }
}
