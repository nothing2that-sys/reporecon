using RepoRecon.Core.Data;
using RepoRecon.Core.Scanning;
using Xunit;

namespace RepoRecon.Core.Tests;

public class ScanRootManagerTests
{
    [Fact]
    public async Task AddOrAbsorbRootAsync_NestedRoot_DistinguishesRejectionAndAbsorption()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), "reporecon_root_test_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var migrator = new SchemaMigrator();
            migrator.EnsureInitialized(tempDb);

            var store = new SqliteIndexStore(tempDb);
            var manager = new ScanRootManager(store);

            // 1. Add parent root
            var (root1Id, absorbed1, rejected1, _) = await manager.AddOrAbsorbRootAsync(@"C:\Projects");
            Assert.True(root1Id > 0);
            Assert.False(absorbed1);
            Assert.False(rejected1);

            // 2. Add child root -> should be rejected because parent already covers it (B-15)
            var (root2Id, absorbed2, rejected2, msg2) = await manager.AddOrAbsorbRootAsync(@"C:\Projects\ClientA");
            Assert.Equal(root1Id, root2Id);
            Assert.False(absorbed2);
            Assert.True(rejected2);
            Assert.Contains("이미 포함되어 있어", msg2);

            var roots = await manager.GetRootsAsync();
            Assert.Single(roots);
            Assert.Equal(@"C:\Projects", roots[0].Path);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(tempDb))
            {
                try { File.Delete(tempDb); } catch { }
            }
        }
    }
}
