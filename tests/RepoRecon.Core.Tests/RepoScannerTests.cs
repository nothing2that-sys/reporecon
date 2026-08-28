using RepoRecon.Core.Data;
using RepoRecon.Core.Git;
using RepoRecon.Core.Models;
using RepoRecon.Core.Scanning;
using Xunit;

namespace RepoRecon.Core.Tests;

public class RepoScannerTests
{
    [Fact]
    public async Task ScanAsync_3LevelNestedRepos_ClosestAncestorAssigned()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "reporecon_nested3_test_" + Guid.NewGuid().ToString("N")[..8]);
        var tempDb = Path.Combine(Path.GetTempPath(), "reporecon_nested3_db_" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var gitRunner = new GitRunner();

        try
        {
            var grandParent = Path.Combine(tempRoot, "GrandParent");
            var parent = Path.Combine(grandParent, "libs", "Parent");
            var child = Path.Combine(parent, "submodules", "Child");

            Directory.CreateDirectory(grandParent);
            Directory.CreateDirectory(parent);
            Directory.CreateDirectory(child);

            // Init git in all three
            await gitRunner.ExecuteAsync(grandParent, "init");
            await gitRunner.ExecuteAsync(parent, "init");
            await gitRunner.ExecuteAsync(child, "init");

            var migrator = new SchemaMigrator();
            migrator.EnsureInitialized(tempDb);
            var store = new SqliteIndexStore(tempDb);

            await store.AddScanRootAsync(tempRoot);
            var roots = await store.GetScanRootsAsync();

            var scanner = new RepoScanner(gitRunner, store);

            // Act: Run Scan
            var report = await scanner.ScanAsync(roots);

            // Assert
            Assert.Equal(3, report.TotalDiscovered);

            var indexedRepos = await store.GetAllLocalReposWithStatusAsync();
            Assert.Equal(3, indexedRepos.Count);

            var gp = indexedRepos.First(r => r.Repo.AbsPath.Equals(grandParent, StringComparison.OrdinalIgnoreCase));
            var p = indexedRepos.First(r => r.Repo.AbsPath.Equals(parent, StringComparison.OrdinalIgnoreCase));
            var c = indexedRepos.First(r => r.Repo.AbsPath.Equals(child, StringComparison.OrdinalIgnoreCase));

            Assert.Null(gp.Repo.ParentRepoId);
            Assert.Equal(gp.Repo.Id, p.Repo.ParentRepoId);
            Assert.Equal(p.Repo.Id, c.Repo.ParentRepoId); // Closest parent (B-9)
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(tempDb)) try { File.Delete(tempDb); } catch { }
            if (Directory.Exists(tempRoot)) try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ScanAsync_UndiscoveredRepo_MarkedAsMissingWithoutDeleting()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "reporecon_missing_test_" + Guid.NewGuid().ToString("N")[..8]);
        var tempDb = Path.Combine(Path.GetTempPath(), "reporecon_missing_db_" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var gitRunner = new GitRunner();

        try
        {
            var repo1 = Path.Combine(tempRoot, "Repo1");
            var repo2 = Path.Combine(tempRoot, "Repo2");

            Directory.CreateDirectory(repo1);
            Directory.CreateDirectory(repo2);

            await gitRunner.ExecuteAsync(repo1, "init");
            await gitRunner.ExecuteAsync(repo2, "init");

            var migrator = new SchemaMigrator();
            migrator.EnsureInitialized(tempDb);
            var store = new SqliteIndexStore(tempDb);

            await store.AddScanRootAsync(tempRoot);
            var roots = await store.GetScanRootsAsync();

            var scanner = new RepoScanner(gitRunner, store);

            // Scan 1: Both discovered
            await scanner.ScanAsync(roots);

            var indexedBefore = await store.GetAllLocalReposWithStatusAsync();
            Assert.Equal(2, indexedBefore.Count);
            Assert.All(indexedBefore, r => Assert.False(r.Repo.IsMissing));

            // Delete repo2 directory from disk
            Directory.Delete(repo2, recursive: true);

            // Scan 2: repo2 missing
            var report2 = await scanner.ScanAsync(roots);
            Assert.Equal(1, report2.TotalDiscovered);

            var indexedAfter = await store.GetAllLocalReposWithStatusAsync();
            Assert.Equal(2, indexedAfter.Count); // A-5: NOT deleted!

            var r1 = indexedAfter.First(r => r.Repo.AbsPath.Equals(repo1, StringComparison.OrdinalIgnoreCase));
            var r2 = indexedAfter.First(r => r.Repo.AbsPath.Equals(repo2, StringComparison.OrdinalIgnoreCase));

            Assert.False(r1.Repo.IsMissing);
            Assert.True(r2.Repo.IsMissing); // Marked missing (A-5)
            Assert.NotNull(r2.Repo.MissingSince);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(tempDb)) try { File.Delete(tempDb); } catch { }
            if (Directory.Exists(tempRoot)) try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }
}
