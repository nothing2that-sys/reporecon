using RepoRecon.Core.Concurrency;
using Xunit;

namespace RepoRecon.Core.Tests;

public class RepoLockManagerTests
{
    [Fact]
    public async Task SamePath_AcquireLock_SerializesExecution()
    {
        var manager = new RepoLockManager();
        var path = @"C:\Test\MyRepo";
        var sequence = new List<int>();

        using (var lock1 = await manager.AcquireLockAsync(path))
        {
            Assert.True(manager.IsLocked(path));

            var task2 = Task.Run(async () =>
            {
                using var lock2 = await manager.AcquireLockAsync(@"C:\Test\MyRepo\");
                sequence.Add(2);
            });

            await Task.Delay(50);
            sequence.Add(1);
        }

        await Task.Delay(100);
        Assert.Equal(new[] { 1, 2 }, sequence);
        Assert.False(manager.IsLocked(path));
    }

    [Fact]
    public async Task DifferentPaths_CanAcquireConcurrently()
    {
        var manager = new RepoLockManager();
        var pathA = @"C:\Test\RepoA";
        var pathB = @"C:\Test\RepoB";

        using var lockA = await manager.AcquireLockAsync(pathA);
        using var lockB = await manager.AcquireLockAsync(pathB);

        Assert.True(manager.IsLocked(pathA));
        Assert.True(manager.IsLocked(pathB));
    }
}
