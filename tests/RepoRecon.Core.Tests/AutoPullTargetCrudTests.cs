using RepoRecon.Core.Data;
using Xunit;

namespace RepoRecon.Core.Tests;

public class AutoPullTargetCrudTests
{
    [Fact]
    public async Task AutoPullTargets_CrudRoundTrips()
    {
        var tempDbPath = Path.Combine(Path.GetTempPath(), "reporecon_test_autopull_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            new SchemaMigrator().EnsureInitialized(tempDbPath);
            var store = new SqliteIndexStore(tempDbPath);

            var id = await store.AddAutoPullTargetAsync(@"D:\Work\TeamA", recurse: true);
            Assert.True(id > 0);

            var targets = await store.GetAutoPullTargetsAsync();
            var target = Assert.Single(targets);
            Assert.Equal(@"D:\Work\TeamA", target.FolderPath);
            Assert.True(target.Recurse);
            Assert.True(target.IsActive);
            Assert.Null(target.LastRunAt);

            var runAt = DateTime.UtcNow;
            await store.UpdateAutoPullTargetLastRunAsync(target.Id, runAt);
            var afterRun = (await store.GetAutoPullTargetsAsync()).Single();
            Assert.NotNull(afterRun.LastRunAt);

            await store.RemoveAutoPullTargetAsync(@"D:\Work\TeamA");
            Assert.Empty(await store.GetAutoPullTargetsAsync());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(tempDbPath)) try { File.Delete(tempDbPath); } catch { }
            foreach (var ext in new[] { "-wal", "-shm" })
            {
                if (File.Exists(tempDbPath + ext)) try { File.Delete(tempDbPath + ext); } catch { }
            }
        }
    }

    [Fact]
    public async Task AddAutoPullTarget_SamePathTwice_ReactivatesInsteadOfDuplicating()
    {
        var tempDbPath = Path.Combine(Path.GetTempPath(), "reporecon_test_autopull2_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            new SchemaMigrator().EnsureInitialized(tempDbPath);
            var store = new SqliteIndexStore(tempDbPath);

            await store.AddAutoPullTargetAsync(@"D:\Work\TeamA");
            await store.RemoveAutoPullTargetAsync(@"D:\Work\TeamA");

            // Re-add after removal; folder_path is UNIQUE so this exercises the ON CONFLICT path
            // if the row still existed (it doesn't, after delete) -- also covers double-add without delete:
            await store.AddAutoPullTargetAsync(@"D:\Work\TeamA");
            var secondId = await store.AddAutoPullTargetAsync(@"D:\Work\TeamA");

            var targets = await store.GetAutoPullTargetsAsync();
            var target = Assert.Single(targets);
            Assert.Equal(secondId, target.Id);
            Assert.True(target.IsActive);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(tempDbPath)) try { File.Delete(tempDbPath); } catch { }
            foreach (var ext in new[] { "-wal", "-shm" })
            {
                if (File.Exists(tempDbPath + ext)) try { File.Delete(tempDbPath + ext); } catch { }
            }
        }
    }
}
