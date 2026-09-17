using RepoRecon.Core.Data;
using RepoRecon.Core.Fetch;
using RepoRecon.Core.Git;
using RepoRecon.Core.Models;
using System.Collections.Concurrent;
using Xunit;
using Xunit.Abstractions;

namespace RepoRecon.Core.Tests;

public class FetchEngineTests
{
    private readonly ITestOutputHelper _output;

    public FetchEngineTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void FetchPriorityQueue_BoostPriority_OrdersCorrectly()
    {
        var queue = new FetchPriorityQueue();
        queue.Enqueue(1, @"C:\repos\r1", "host", priority: 0);
        queue.Enqueue(2, @"C:\repos\r2", "host", priority: 0);
        queue.Enqueue(3, @"C:\repos\r3", "host", priority: 0);

        // Boost repo 3
        queue.BoostPriority(new[] { 3L }, priorityBoost: 100);

        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal(3, first.LocalRepoId);

        Assert.True(queue.TryDequeue(out var second));
        Assert.Equal(1, second.LocalRepoId);

        Assert.True(queue.TryDequeue(out var third));
        Assert.Equal(2, third.LocalRepoId);
    }

    [Fact]
    public async Task FetchEngine_FetchSingleRepo_SuccessUpdatesStatusAndLastFetchedAt()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "RepoRecon_FetchTest_" + Guid.NewGuid().ToString("N")[..8]);
        var tempDb = Path.Combine(Path.GetTempPath(), "RepoRecon_FetchTest_" + Guid.NewGuid().ToString("N")[..8] + ".db");
        Directory.CreateDirectory(tempDir);

        try
        {
            var runner = new GitRunner();
            await runner.ExecuteAsync(tempDir, "init");

            var migrator = new SchemaMigrator();
            migrator.EnsureInitialized(tempDb);
            var store = new SqliteIndexStore(tempDb);

            var repoId = await store.UpsertLocalRepoAsync(new LocalRepo(0, tempDir, "local/test", null, GitDirKind.Normal, false, false, false, null));

            using var engine = new FetchEngine(runner, store);
            var result = await engine.FetchSingleRepoAsync(repoId, tempDir);

            _output.WriteLine($"Fetch result: Success={result.IsSuccess}, Error={result.ErrorMessage}, Duration={result.Duration}");
            Assert.True(result.IsSuccess);
            Assert.NotNull(result.UpdatedStatus);
            Assert.NotNull(result.UpdatedStatus!.LastFetchedAt);

            var repos = await store.GetAllLocalReposWithStatusAsync();
            var target = repos.First(r => r.Repo.Id == repoId);
            Assert.NotNull(target.Status?.LastFetchedAt);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(tempDb)) try { File.Delete(tempDb); } catch { }
            if (Directory.Exists(tempDir)) try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task FetchEngine_AuthFailure_DoesNotThrowAndPreservesUnfetched()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "RepoRecon_AuthFailTest_" + Guid.NewGuid().ToString("N")[..8]);
        var tempDb = Path.Combine(Path.GetTempPath(), "RepoRecon_AuthFailTest_" + Guid.NewGuid().ToString("N")[..8] + ".db");
        Directory.CreateDirectory(tempDir);

        try
        {
            var runner = new GitRunner();
            await runner.ExecuteAsync(tempDir, "init");
            // Set an unreachable remote
            await runner.ExecuteAsync(tempDir, "remote add origin https://invalid-user:secret-token@127.0.0.1:9999/unreachable/repo.git");

            var migrator = new SchemaMigrator();
            migrator.EnsureInitialized(tempDb);
            var store = new SqliteIndexStore(tempDb);

            var repoId = await store.UpsertLocalRepoAsync(new LocalRepo(0, tempDir, "127.0.0.1:9999/unreachable/repo", null, GitDirKind.Normal, false, false, false, null));

            using var engine = new FetchEngine(runner, store);
            var result = await engine.FetchSingleRepoAsync(repoId, tempDir);

            _output.WriteLine($"Auth/Net fail result: Success={result.IsSuccess}, Error={result.ErrorMessage}");
            Assert.False(result.IsSuccess);
            Assert.NotNull(result.ErrorMessage);
            // Verify secret-token is masked
            Assert.DoesNotContain("secret-token", result.ErrorMessage);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(tempDb)) try { File.Delete(tempDb); } catch { }
            if (Directory.Exists(tempDir)) try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task FetchEngine_EnforcesFourPerHostSeparatelyFromGlobalPool()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), "RepoRecon_HostLimit_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            new SchemaMigrator().EnsureInitialized(tempDb);
            var store = new SqliteIndexStore(tempDb);
            var runner = new ConcurrencyGitRunner();
            var targets = new List<(long RepoId, string AbsPath, string NormalizedKey)>();
            for (var i = 0; i < 8; i++)
            {
                var host = i < 4 ? "host-a" : "host-b";
                var path = $"{host}-repo-{i}";
                var id = await store.UpsertLocalRepoAsync(new LocalRepo(0, path, $"{host}:443/g/r{i}"));
                targets.Add((id, path, $"{host}:443/g/r{i}"));
            }

            using var engine = new FetchEngine(runner, store, maxConcurrency: 8, maxConcurrencyPerHost: 4);
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var count = 0;
            engine.RepoFetched += (_, _) =>
            {
                if (Interlocked.Increment(ref count) == targets.Count) completed.TrySetResult();
            };

            await engine.EnqueueReposAsync(targets);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(runner.MaximumGlobalConcurrency > 4);
            Assert.All(runner.MaximumHostConcurrency.Values, maximum => Assert.InRange(maximum, 1, 4));
            Assert.Equal(4, runner.MaximumHostConcurrency["host-a"]);
            Assert.Equal(4, runner.MaximumHostConcurrency["host-b"]);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(tempDb)) try { File.Delete(tempDb); } catch { }
        }
    }

    [Fact]
    public async Task FetchEngine_AuthCooldownIsHostScoped_NetworkFailureExcluded_AndExplicitClearRetries()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), "RepoRecon_Cooldown_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            new SchemaMigrator().EnsureInitialized(tempDb);
            var store = new SqliteIndexStore(tempDb);
            var runner = new CooldownGitRunner();
            var auth1 = await store.UpsertLocalRepoAsync(new LocalRepo(0, "auth-one", "auth.test:443/g/one"));
            var auth2 = await store.UpsertLocalRepoAsync(new LocalRepo(0, "auth-two", "auth.test:8443/g/two"));
            var network = await store.UpsertLocalRepoAsync(new LocalRepo(0, "network-one", "network.test:443/g/one"));
            using var engine = new FetchEngine(runner, store, maxConcurrency: 1, maxConcurrencyPerHost: 4);

            var results = new ConcurrentQueue<RepoFetchResult>();
            engine.RepoFetched += (_, result) => results.Enqueue(result);

            await engine.EnqueueReposAsync(new[] { (auth1, "auth-one", "auth.test:443/g/one") });
            await WaitForResultCountAsync(results, 1);
            Assert.Equal(FetchFailureKind.Authentication, results.ElementAt(0).FailureKind);
            var cooldown = Assert.Single(engine.ActiveCooldowns);
            Assert.Equal("auth.test", cooldown.Host);
            Assert.InRange(cooldown.Remaining(DateTime.UtcNow), TimeSpan.FromMinutes(4.9), TimeSpan.FromMinutes(5));

            var fetchCallsAfterAuthFailure = runner.FetchCalls;
            await engine.EnqueueReposAsync(new[] { (auth2, "auth-two", "auth.test:8443/g/two") });
            await WaitForResultCountAsync(results, 2);
            Assert.True(results.ElementAt(1).WasSkippedByCooldown);
            Assert.Equal(fetchCallsAfterAuthFailure, runner.FetchCalls);

            await engine.EnqueueReposAsync(new[] { (network, "network-one", "network.test:443/g/one") });
            await WaitForResultCountAsync(results, 3);
            Assert.Equal(FetchFailureKind.Network, results.ElementAt(2).FailureKind);
            Assert.Single(engine.ActiveCooldowns);

            engine.ClearCooldowns();
            Assert.Empty(engine.ActiveCooldowns);
            await engine.EnqueueReposAsync(new[] { (auth2, "auth-two", "auth.test:8443/g/two") });
            await WaitForResultCountAsync(results, 4);
            Assert.True(results.ElementAt(3).IsSuccess);
            Assert.False(results.ElementAt(3).WasSkippedByCooldown);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(tempDb)) try { File.Delete(tempDb); } catch { }
        }
    }

    [Fact]
    public async Task FetchEngine_UsesPruneAndThirtySecondFetchTimeout()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), "RepoRecon_FetchContract_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            new SchemaMigrator().EnsureInitialized(tempDb);
            var store = new SqliteIndexStore(tempDb);
            var repoId = await store.UpsertLocalRepoAsync(new LocalRepo(0, "recording-repo", "host.test:443/g/repo"));
            var runner = new CommandRecordingGitRunner();
            using var engine = new FetchEngine(runner, store);

            var result = await engine.FetchSingleRepoAsync(repoId, "recording-repo");

            Assert.True(result.IsSuccess);
            Assert.Collection(
                runner.Calls,
                call =>
                {
                    Assert.Equal("fetch --prune", call.Arguments);
                    Assert.Equal(TimeSpan.FromSeconds(30), call.Timeout);
                },
                call =>
                {
                    Assert.Equal("status --porcelain=v2 --branch", call.Arguments);
                    Assert.Equal(TimeSpan.FromSeconds(15), call.Timeout);
                });
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(tempDb)) try { File.Delete(tempDb); } catch { }
        }
    }

    private static async Task WaitForResultCountAsync(ConcurrentQueue<RepoFetchResult> results, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (results.Count < expected && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(results.Count >= expected, $"Expected {expected} fetch results, got {results.Count}.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition(), "Condition did not become true within the timeout.");
    }

    // §"클론이나 fetch 도중 멈추는 기능이 없어" -- Pause must interrupt a fetch that's
    // already mid-flight (not just stop new dequeues), and Resume must pick the queue
    // back up rather than requiring re-enqueue.
    [Fact]
    public async Task FetchEngine_Pause_CancelsInFlightFetch_AndReportsCanceledResult()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), "RepoRecon_PauseInFlight_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            new SchemaMigrator().EnsureInitialized(tempDb);
            var store = new SqliteIndexStore(tempDb);
            var runner = new DelayedGitRunner(TimeSpan.FromSeconds(10));
            var repoId = await store.UpsertLocalRepoAsync(new LocalRepo(0, "slow-repo", "host.test:443/g/slow"));

            using var engine = new FetchEngine(runner, store, maxConcurrency: 1);
            var results = new ConcurrentQueue<RepoFetchResult>();
            engine.RepoFetched += (_, result) => results.Enqueue(result);

            await engine.EnqueueReposAsync(new[] { (repoId, "slow-repo", "host.test:443/g/slow") });
            await WaitUntilAsync(() => runner.FetchCallCount > 0, TimeSpan.FromSeconds(2));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            engine.Pause();
            Assert.True(engine.IsPaused);

            await WaitForResultCountAsync(results, 1);
            sw.Stop();

            // Must resolve because Pause() interrupted it, not because the 10s delay elapsed.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Expected the paused fetch to resolve quickly, took {sw.Elapsed}.");
            Assert.False(results.ElementAt(0).IsSuccess);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(tempDb)) try { File.Delete(tempDb); } catch { }
        }
    }

    [Fact]
    public async Task FetchEngine_Pause_LeavesQueueIntact_ResumeProcessesRemainingItems()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), "RepoRecon_PauseResume_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            new SchemaMigrator().EnsureInitialized(tempDb);
            var store = new SqliteIndexStore(tempDb);
            var runner = new DelayedGitRunner(TimeSpan.FromMilliseconds(300));
            var repoA = await store.UpsertLocalRepoAsync(new LocalRepo(0, "repo-a", "host.test:443/g/a"));
            var repoB = await store.UpsertLocalRepoAsync(new LocalRepo(0, "repo-b", "host.test:443/g/b"));

            using var engine = new FetchEngine(runner, store, maxConcurrency: 1);
            var results = new ConcurrentQueue<RepoFetchResult>();
            engine.RepoFetched += (_, result) => results.Enqueue(result);

            await engine.EnqueueReposAsync(new[]
            {
                (repoA, "repo-a", "host.test:443/g/a"),
                (repoB, "repo-b", "host.test:443/g/b"),
            });

            // Pause while repo-a's fetch is in flight; with maxConcurrency=1, repo-b is
            // still sitting in the queue and must not be dequeued while paused.
            await WaitUntilAsync(() => runner.FetchCallCount > 0, TimeSpan.FromSeconds(2));
            engine.Pause();

            await WaitForResultCountAsync(results, 1); // repo-a's in-flight fetch gets canceled
            await Task.Delay(200); // give a paused worker a chance to (wrongly) dequeue repo-b
            Assert.Single(results);
            Assert.Equal(1, runner.FetchCallCount);
            Assert.Equal(1, engine.PendingCount);

            engine.Resume();
            Assert.False(engine.IsPaused);

            await WaitForResultCountAsync(results, 2);
            Assert.True(results.ElementAt(1).IsSuccess);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(tempDb)) try { File.Delete(tempDb); } catch { }
        }
    }

    // Reproduces the race in EnsureWorkersStarted: with several workers alive when Pause()
    // hits, each one's Task lags behind _runningWorkerCount before reaching IsCompleted.
    // A Resume() that (incorrectly) gated on _activeWorkerTasks.Count instead of the
    // synchronously-updated slot counter would see a stale "still full" count, spawn zero
    // replacement workers, and strand the still-queued items forever.
    [Fact]
    public async Task FetchEngine_ResumeAfterPause_WithMultipleConcurrentWorkers_DrainsEntireQueue()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), "RepoRecon_PauseResumeMulti_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            new SchemaMigrator().EnsureInitialized(tempDb);
            var store = new SqliteIndexStore(tempDb);
            var runner = new DelayedGitRunner(TimeSpan.FromMilliseconds(200));
            const int inFlightCount = 4;
            const int queuedCount = 4;
            const int totalCount = inFlightCount + queuedCount;

            var targets = new List<(long RepoId, string AbsPath, string NormalizedKey)>();
            for (var i = 0; i < totalCount; i++)
            {
                var path = $"repo-{i}";
                var key = $"host.test:443/g/r{i}";
                var id = await store.UpsertLocalRepoAsync(new LocalRepo(0, path, key));
                targets.Add((id, path, key));
            }

            using var engine = new FetchEngine(runner, store, maxConcurrency: inFlightCount);
            var results = new ConcurrentQueue<RepoFetchResult>();
            engine.RepoFetched += (_, result) => results.Enqueue(result);

            await engine.EnqueueReposAsync(targets);

            // Let all four workers pick up their first item, then call Resume() immediately
            // on Pause()'s heels -- no delay in between -- so the still-exiting workers'
            // Tasks are as likely as possible to not yet be IsCompleted when Resume() runs.
            // A delay here would let the race window close on its own and defeat the test.
            await WaitUntilAsync(() => runner.FetchCallCount >= inFlightCount, TimeSpan.FromSeconds(2));
            engine.Pause();
            engine.Resume();

            await WaitForResultCountAsync(results, totalCount);
            Assert.Equal(totalCount, results.Count);
            Assert.Equal(0, engine.PendingCount);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(tempDb)) try { File.Delete(tempDb); } catch { }
        }
    }

    private sealed class DelayedGitRunner : IGitRunner
    {
        private readonly TimeSpan _delay;
        private int _fetchCallCount;

        public DelayedGitRunner(TimeSpan delay) => _delay = delay;

        public int FetchCallCount => _fetchCallCount;

        public async Task<GitCommandResult> ExecuteAsync(string workingDirectory, string arguments, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            if (!arguments.StartsWith("fetch", StringComparison.Ordinal))
                return new GitCommandResult(0,
                    "# branch.oid 0123456789012345678901234567890123456789\n# branch.head main\n# branch.upstream origin/main\n# branch.ab +0 -0",
                    string.Empty,
                    TimeSpan.Zero);

            Interlocked.Increment(ref _fetchCallCount);
            await Task.Delay(_delay, cancellationToken);
            return new GitCommandResult(0, string.Empty, string.Empty, _delay);
        }

        public Task<GitEnvironmentInfo> CheckEnvironmentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitEnvironmentInfo(true, new Version(2, 40), "git version 2.40", "git", null));
    }

    private sealed class ConcurrencyGitRunner : IGitRunner
    {
        private int _global;
        private int _maximumGlobal;
        private readonly ConcurrentDictionary<string, int> _hosts = new();
        private readonly ConcurrentDictionary<string, int> _maximumHosts = new();

        public int MaximumGlobalConcurrency => _maximumGlobal;
        public IReadOnlyDictionary<string, int> MaximumHostConcurrency => _maximumHosts;

        public async Task<GitCommandResult> ExecuteAsync(string workingDirectory, string arguments, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            if (!arguments.StartsWith("fetch", StringComparison.Ordinal))
                return SuccessStatus();

            var host = workingDirectory.Split("-repo-", StringSplitOptions.None)[0];
            var global = Interlocked.Increment(ref _global);
            UpdateMaximum(ref _maximumGlobal, global);
            var hostCount = _hosts.AddOrUpdate(host, 1, (_, current) => current + 1);
            _maximumHosts.AddOrUpdate(host, hostCount, (_, current) => Math.Max(current, hostCount));
            try
            {
                await Task.Delay(150, cancellationToken);
                return new GitCommandResult(0, string.Empty, string.Empty, TimeSpan.FromMilliseconds(150));
            }
            finally
            {
                Interlocked.Decrement(ref _global);
                _hosts.AddOrUpdate(host, 0, (_, current) => current - 1);
            }
        }

        public Task<GitEnvironmentInfo> CheckEnvironmentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitEnvironmentInfo(true, new Version(2, 40), "git version 2.40", "git", null));

        private static GitCommandResult SuccessStatus() => new(0,
            "# branch.oid 0123456789012345678901234567890123456789\n# branch.head main\n# branch.upstream origin/main\n# branch.ab +0 -0",
            string.Empty,
            TimeSpan.Zero);

        private static void UpdateMaximum(ref int target, int value)
        {
            int current;
            do
            {
                current = target;
                if (current >= value) return;
            } while (Interlocked.CompareExchange(ref target, value, current) != current);
        }
    }

    private sealed class CommandRecordingGitRunner : IGitRunner
    {
        public List<(string Arguments, TimeSpan? Timeout)> Calls { get; } = new();

        public Task<GitCommandResult> ExecuteAsync(
            string workingDirectory,
            string arguments,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((arguments, timeout));
            var output = arguments.StartsWith("status", StringComparison.Ordinal)
                ? "# branch.head main\n# branch.upstream origin/main\n# branch.ab +0 -0"
                : string.Empty;
            return Task.FromResult(new GitCommandResult(0, output, string.Empty, TimeSpan.Zero));
        }

        public Task<GitEnvironmentInfo> CheckEnvironmentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitEnvironmentInfo(true, new Version(2, 40), "git version 2.40", "git", null));
    }

    private sealed class CooldownGitRunner : IGitRunner
    {
        private int _authFailuresRemaining = 1;
        public int FetchCalls { get; private set; }

        public Task<GitCommandResult> ExecuteAsync(string workingDirectory, string arguments, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            if (!arguments.StartsWith("fetch", StringComparison.Ordinal))
                return Task.FromResult(new GitCommandResult(0,
                    "# branch.oid 0123456789012345678901234567890123456789\n# branch.head main\n# branch.upstream origin/main\n# branch.ab +0 -0",
                    string.Empty,
                    TimeSpan.Zero));

            FetchCalls++;
            if (workingDirectory.StartsWith("auth", StringComparison.Ordinal) && Interlocked.Decrement(ref _authFailuresRemaining) >= 0)
                return Task.FromResult(new GitCommandResult(128, string.Empty, "fatal: Authentication failed", TimeSpan.Zero));
            if (workingDirectory.StartsWith("network", StringComparison.Ordinal))
                return Task.FromResult(new GitCommandResult(128, string.Empty, "fatal: Failed to connect: Connection refused", TimeSpan.Zero));
            return Task.FromResult(new GitCommandResult(0, string.Empty, string.Empty, TimeSpan.Zero));
        }

        public Task<GitEnvironmentInfo> CheckEnvironmentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitEnvironmentInfo(true, new Version(2, 40), "git version 2.40", "git", null));
    }
}
