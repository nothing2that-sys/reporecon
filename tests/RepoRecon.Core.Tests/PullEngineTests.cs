using System.Diagnostics;
using RepoRecon.Core.Concurrency;
using RepoRecon.Core.Data;
using RepoRecon.Core.Git;
using RepoRecon.Core.Models;
using RepoRecon.Core.Pull;
using Xunit;

namespace RepoRecon.Core.Tests;

public class PullEngineTests
{
    private readonly GitRunner _gitRunner = new();

    [Fact]
    public void GeneratePreview_EvaluatesAllStatusesAndReasonsCorrectly()
    {
        var migrator = new SchemaMigrator();
        var tempDb = Path.Combine(Path.GetTempPath(), "preview_test_" + Guid.NewGuid().ToString("N") + ".db");
        migrator.EnsureInitialized(tempDb);
        var store = new SqliteIndexStore(tempDb);
        var pullEngine = new PullEngine(_gitRunner, store);

        var now = DateTime.UtcNow;

        var repos = new List<(LocalRepo Repo, LocalStatus? Status)>
        {
            // 1. Behind Clean (Eligible)
            (CreateRepo(1, @"C:\repos\behind"), new LocalStatus(1, "main", "origin/main", 0, 2, 0, 0, "hash1", RepoStateCode.Behind, now)),
            // 2. Dirty
            (CreateRepo(2, @"C:\repos\dirty"), new LocalStatus(2, "main", "origin/main", 0, 1, 3, 0, "hash2", RepoStateCode.Behind, now)),
            // 3. Conflicted
            (CreateRepo(3, @"C:\repos\conflicted"), new LocalStatus(3, "main", "origin/main", 0, 1, 0, 2, "hash3", RepoStateCode.Conflicted, now)),
            // 4. Diverged
            (CreateRepo(4, @"C:\repos\diverged"), new LocalStatus(4, "main", "origin/main", 1, 1, 0, 0, "hash4", RepoStateCode.Diverged, now)),
            // 5. No Upstream
            (CreateRepo(5, @"C:\repos\noupstream"), new LocalStatus(5, "feature", null, 0, 0, 0, 0, "hash5", RepoStateCode.NoUpstream, now)),
            // 6. Upstream Gone
            (CreateRepo(6, @"C:\repos\upstreamgone"), new LocalStatus(6, "old-branch", "origin/old-branch", 0, 0, 0, 0, "hash6", RepoStateCode.UpstreamGone, now)),
            // 7. Detached HEAD
            (CreateRepo(7, @"C:\repos\detached"), new LocalStatus(7, null, null, 0, 0, 0, 0, "hash7", RepoStateCode.DetachedHead, now)),
            // 8. Untracked (No Remote)
            (CreateRepo(8, @"C:\repos\untracked", normalizedKey: null), new LocalStatus(8, "main", null, 0, 0, 0, 0, "hash8", RepoStateCode.Untracked, now)),
            // 9. Up-to-Date
            (CreateRepo(9, @"C:\repos\uptodate"), new LocalStatus(9, "main", "origin/main", 0, 0, 0, 0, "hash9", RepoStateCode.UpToDate, now)),
            // 10. Ahead
            (CreateRepo(10, @"C:\repos\ahead"), new LocalStatus(10, "main", "origin/main", 3, 0, 0, 0, "hash10", RepoStateCode.Ahead, now)),
            // 11. Missing
            (CreateRepo(11, @"C:\repos\missing", isMissing: true), new LocalStatus(11, "main", "origin/main", 0, 1, 0, 0, "hash11", RepoStateCode.Behind, now)),
            // 12. Bare
            (CreateRepo(12, @"C:\repos\bare", kind: GitDirKind.Bare), new LocalStatus(12, null, null, 0, 0, 0, 0, null, RepoStateCode.UnknownUnfetched, now)),
        };

        var preview = pullEngine.GeneratePreview(repos);

        Assert.Equal(12, preview.Count);

        // 1. Behind Clean
        Assert.True(preview[0].IsEligible);
        Assert.Null(preview[0].SkipReason);
        Assert.Contains("--ff-only", preview[0].Command);

        // 2. Dirty
        Assert.False(preview[1].IsEligible);
        Assert.Contains("미커밋 변경", preview[1].SkipReason);

        // 3. Conflicted
        Assert.False(preview[2].IsEligible);
        Assert.Contains("머지 충돌", preview[2].SkipReason);

        // 4. Diverged
        Assert.False(preview[3].IsEligible);
        Assert.Contains("수동 병합", preview[3].SkipReason);

        // 5. No Upstream
        Assert.False(preview[4].IsEligible);
        Assert.Contains("추적 브랜치 없음", preview[4].SkipReason);

        // 6. Upstream Gone
        Assert.False(preview[5].IsEligible);
        Assert.Contains("삭제됨", preview[5].SkipReason);

        // 7. Detached HEAD
        Assert.False(preview[6].IsEligible);
        Assert.Contains("브랜치 미체크아웃", preview[6].SkipReason);

        // 8. Untracked
        Assert.False(preview[7].IsEligible);
        Assert.Contains("remote 없음", preview[7].SkipReason);

        // 9. Up-to-Date
        Assert.False(preview[8].IsEligible);
        Assert.Contains("이미 최신", preview[8].SkipReason);

        // 10. Ahead
        Assert.False(preview[9].IsEligible);
        Assert.Contains("pull 불필요", preview[9].SkipReason);

        // 11. Missing
        Assert.False(preview[10].IsEligible);
        Assert.Contains("발견되지 않음", preview[10].SkipReason);

        // 12. Bare
        Assert.False(preview[11].IsEligible);
        Assert.Contains("일괄 작업 대상 아님", preview[11].SkipReason);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(tempDb)) try { File.Delete(tempDb); } catch { }
    }

    [Fact]
    public async Task ExecuteBatchPull_CleanBehind_PullsAndUpdatesWorkingTreeAndHead()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "reporecon_pull_test_" + Guid.NewGuid().ToString("N"));
        var remoteDir = Path.Combine(tempRoot, "remote.git");
        var localDir = Path.Combine(tempRoot, "local");
        var tempDb = Path.Combine(tempRoot, "index.db");

        try
        {
            Directory.CreateDirectory(tempRoot);

            // 1. Create bare remote
            await RunGitAsync(tempRoot, $"init --bare \"{remoteDir}\"");

            // 2. Clone to a staging dir to push an initial commit
            var stagingDir = Path.Combine(tempRoot, "staging");
            await RunGitAsync(tempRoot, $"clone \"{remoteDir}\" \"{stagingDir}\"");
            File.WriteAllText(Path.Combine(stagingDir, "file1.txt"), "initial content\n");
            await RunGitAsync(stagingDir, "config user.name \"Tester\"");
            await RunGitAsync(stagingDir, "config user.email \"tester@example.com\"");
            await RunGitAsync(stagingDir, "add file1.txt");
            await RunGitAsync(stagingDir, "commit -m \"Initial commit\"");
            await RunGitAsync(stagingDir, "branch -M main");
            await RunGitAsync(stagingDir, "push -u origin main");

            // 3. Clone to localDir
            await RunGitAsync(tempRoot, $"clone -b main \"{remoteDir}\" \"{localDir}\"");
            await RunGitAsync(localDir, "config user.name \"Tester\"");
            await RunGitAsync(localDir, "config user.email \"tester@example.com\"");

            var initialHead = (await _gitRunner.ExecuteAsync(localDir, "rev-parse HEAD")).StandardOutput.Trim();

            // 4. Add commit 2 in staging and push to remote
            File.WriteAllText(Path.Combine(stagingDir, "file2.txt"), "second commit content\n");
            await RunGitAsync(stagingDir, "add file2.txt");
            await RunGitAsync(stagingDir, "commit -m \"Second commit\"");
            await RunGitAsync(stagingDir, "push origin main");
            var remoteHead = (await _gitRunner.ExecuteAsync(stagingDir, "rev-parse HEAD")).StandardOutput.Trim();

            // 5. Setup DB and LocalRepo in RepoRecon
            var migrator = new SchemaMigrator();
            migrator.EnsureInitialized(tempDb);
            var store = new SqliteIndexStore(tempDb);

            var repoId = await store.UpsertLocalRepoAsync(new LocalRepo(0, localDir, "localhost:22/remote", null, GitDirKind.Normal, false, false, false, null));
            await store.UpsertLocalStatusAsync(new LocalStatus(repoId, "main", "origin/main", 0, 1, 0, 0, initialHead, RepoStateCode.Behind, DateTime.UtcNow));

            var targets = await store.GetAllLocalReposWithStatusAsync();
            var pullEngine = new PullEngine(_gitRunner, store);

            // 6. Execute Batch Pull
            var report = await pullEngine.ExecuteBatchPullAsync(targets);

            Assert.Equal(1, report.TotalSelected);
            Assert.Equal(1, report.SuccessCount);
            Assert.Equal(0, report.FailedCount);
            Assert.Equal(0, report.SkippedCount);

            // 7. Verify Working Tree & HEAD
            Assert.True(File.Exists(Path.Combine(localDir, "file2.txt")));
            Assert.Equal("second commit content", File.ReadAllText(Path.Combine(localDir, "file2.txt")).Trim());

            var localHeadAfter = (await _gitRunner.ExecuteAsync(localDir, "rev-parse HEAD")).StandardOutput.Trim();
            Assert.Equal(remoteHead, localHeadAfter);
            Assert.NotEqual(initialHead, localHeadAfter);

            // 8. Verify job_history in SQLite
            var history = await store.GetJobHistoryAsync(localDir);
            Assert.NotEmpty(history);
            var lastEntry = history[0];
            Assert.Equal("pull", lastEntry.JobType);
            Assert.Equal(localDir, lastEntry.TargetPath);
            Assert.Equal(initialHead, lastEntry.PreHead);
            Assert.Equal(remoteHead, lastEntry.PostHead);
            Assert.Equal("Success", lastEntry.Result);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot))
            {
                try { Directory.Delete(tempRoot, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task ExecuteBatchPull_DirtyRepo_IsSkipped_AndWorkingTreeUntouched_NoStash()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "reporecon_dirty_test_" + Guid.NewGuid().ToString("N"));
        var remoteDir = Path.Combine(tempRoot, "remote.git");
        var localDir = Path.Combine(tempRoot, "local");
        var tempDb = Path.Combine(tempRoot, "index.db");

        try
        {
            Directory.CreateDirectory(tempRoot);
            await RunGitAsync(tempRoot, $"init --bare \"{remoteDir}\"");

            var stagingDir = Path.Combine(tempRoot, "staging");
            await RunGitAsync(tempRoot, $"clone \"{remoteDir}\" \"{stagingDir}\"");
            File.WriteAllText(Path.Combine(stagingDir, "file1.txt"), "initial content\n");
            await RunGitAsync(stagingDir, "config user.name \"Tester\"");
            await RunGitAsync(stagingDir, "config user.email \"tester@example.com\"");
            await RunGitAsync(stagingDir, "add file1.txt");
            await RunGitAsync(stagingDir, "commit -m \"Initial commit\"");
            await RunGitAsync(stagingDir, "branch -M main");
            await RunGitAsync(stagingDir, "push -u origin main");

            await RunGitAsync(tempRoot, $"clone -b main \"{remoteDir}\" \"{localDir}\"");
            await RunGitAsync(localDir, "config user.name \"Tester\"");
            await RunGitAsync(localDir, "config user.email \"tester@example.com\"");

            // Make remote commit
            File.WriteAllText(Path.Combine(stagingDir, "file2.txt"), "remote second commit\n");
            await RunGitAsync(stagingDir, "add file2.txt");
            await RunGitAsync(stagingDir, "commit -m \"Second commit\"");
            await RunGitAsync(stagingDir, "push origin main");

            // Make local DIRTY
            var uncommittedLocalContent = "MY UNCOMMITTED WORK IN PROGRESS";
            File.WriteAllText(Path.Combine(localDir, "file1.txt"), uncommittedLocalContent);

            var migrator = new SchemaMigrator();
            migrator.EnsureInitialized(tempDb);
            var store = new SqliteIndexStore(tempDb);

            var repoId = await store.UpsertLocalRepoAsync(new LocalRepo(0, localDir, "localhost:22/remote", null, GitDirKind.Normal, false, false, false, null));
            await store.UpsertLocalStatusAsync(new LocalStatus(repoId, "main", "origin/main", 0, 1, 1, 0, "initial", RepoStateCode.Behind, DateTime.UtcNow));

            var targets = await store.GetAllLocalReposWithStatusAsync();
            var pullEngine = new PullEngine(_gitRunner, store);

            // Execute Batch Pull
            var report = await pullEngine.ExecuteBatchPullAsync(targets);

            Assert.Equal(1, report.TotalSelected);
            Assert.Equal(0, report.SuccessCount);
            Assert.Equal(1, report.SkippedCount);

            // Verify local uncommitted file is 100% UNTOUCHED
            Assert.Equal(uncommittedLocalContent, File.ReadAllText(Path.Combine(localDir, "file1.txt")));

            // Verify NO stash was created (§8.1, Criterion 4)
            var stashListResult = await _gitRunner.ExecuteAsync(localDir, "stash list");
            Assert.True(string.IsNullOrWhiteSpace(stashListResult.StandardOutput));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot))
            {
                try { Directory.Delete(tempRoot, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task ExecuteBatchPull_PreExecutionReevaluation_DropsRepoIfBecameDirty()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "reporecon_reeval_test_" + Guid.NewGuid().ToString("N"));
        var remoteDir = Path.Combine(tempRoot, "remote.git");
        var localDir = Path.Combine(tempRoot, "local");
        var tempDb = Path.Combine(tempRoot, "index.db");

        try
        {
            Directory.CreateDirectory(tempRoot);
            await RunGitAsync(tempRoot, $"init --bare \"{remoteDir}\"");

            var stagingDir = Path.Combine(tempRoot, "staging");
            await RunGitAsync(tempRoot, $"clone \"{remoteDir}\" \"{stagingDir}\"");
            File.WriteAllText(Path.Combine(stagingDir, "file1.txt"), "v1\n");
            await RunGitAsync(stagingDir, "config user.name \"Tester\"");
            await RunGitAsync(stagingDir, "config user.email \"tester@example.com\"");
            await RunGitAsync(stagingDir, "add file1.txt");
            await RunGitAsync(stagingDir, "commit -m \"v1\"");
            await RunGitAsync(stagingDir, "branch -M main");
            await RunGitAsync(stagingDir, "push -u origin main");

            await RunGitAsync(tempRoot, $"clone -b main \"{remoteDir}\" \"{localDir}\"");
            await RunGitAsync(localDir, "config user.name \"Tester\"");
            await RunGitAsync(localDir, "config user.email \"tester@example.com\"");

            // Push v2 to remote
            File.WriteAllText(Path.Combine(stagingDir, "file2.txt"), "v2\n");
            await RunGitAsync(stagingDir, "add file2.txt");
            await RunGitAsync(stagingDir, "commit -m \"v2\"");
            await RunGitAsync(stagingDir, "push origin main");

            var migrator = new SchemaMigrator();
            migrator.EnsureInitialized(tempDb);
            var store = new SqliteIndexStore(tempDb);

            var repoId = await store.UpsertLocalRepoAsync(new LocalRepo(0, localDir, "localhost:22/remote", null, GitDirKind.Normal, false, false, false, null));
            // Passed in target list as Clean Behind (Simulating stale selection in UI)
            var staleCleanBehindStatus = new LocalStatus(repoId, "main", "origin/main", 0, 1, 0, 0, "hash1", RepoStateCode.Behind, DateTime.UtcNow);
            await store.UpsertLocalStatusAsync(staleCleanBehindStatus);

            // User makes local directory DIRTY right before clicking execute!
            File.WriteAllText(Path.Combine(localDir, "file1.txt"), "UNSAVED WORK BEFORE PULL");

            var pullEngine = new PullEngine(_gitRunner, store);
            var targets = new List<(LocalRepo Repo, LocalStatus? Status)> { (new LocalRepo(repoId, localDir, "localhost:22/remote", null, GitDirKind.Normal, false, false, false, null), staleCleanBehindStatus) };

            // Execute
            var report = await pullEngine.ExecuteBatchPullAsync(targets);

            // Re-evaluation should detect dirty state, drop it, and report "선택 후 상태 변경" (§5-4, Criterion 13, 14)
            Assert.Equal(1, report.TotalSelected);
            Assert.Equal(0, report.SuccessCount);
            Assert.Equal(1, report.SkippedCount);
            Assert.Equal(1, report.ReevaluatedOutCount);

            var itemResult = Assert.Single(report.Results);
            Assert.True(itemResult.WasReevaluatedOut);
            Assert.Contains("선택 후 상태 변경", itemResult.SkipReason);

            // Verify working tree preserved
            Assert.Equal("UNSAVED WORK BEFORE PULL", File.ReadAllText(Path.Combine(localDir, "file1.txt")));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot))
            {
                try { Directory.Delete(tempRoot, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task ExecuteBatchPull_IsolatesFailures_ContinuesBatch()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "reporecon_isolate_test_" + Guid.NewGuid().ToString("N"));
        var remoteDir = Path.Combine(tempRoot, "remote.git");
        var goodLocalDir = Path.Combine(tempRoot, "good_local");
        var badLocalDir = Path.Combine(tempRoot, "bad_local");
        var tempDb = Path.Combine(tempRoot, "index.db");

        try
        {
            Directory.CreateDirectory(tempRoot);
            await RunGitAsync(tempRoot, $"init --bare \"{remoteDir}\"");

            var stagingDir = Path.Combine(tempRoot, "staging");
            await RunGitAsync(tempRoot, $"clone \"{remoteDir}\" \"{stagingDir}\"");
            File.WriteAllText(Path.Combine(stagingDir, "f.txt"), "v1\n");
            await RunGitAsync(stagingDir, "config user.name \"Tester\"");
            await RunGitAsync(stagingDir, "config user.email \"tester@example.com\"");
            await RunGitAsync(stagingDir, "add f.txt");
            await RunGitAsync(stagingDir, "commit -m \"v1\"");
            await RunGitAsync(stagingDir, "branch -M main");
            await RunGitAsync(stagingDir, "push -u origin main");

            // Good repo
            await RunGitAsync(tempRoot, $"clone -b main \"{remoteDir}\" \"{goodLocalDir}\"");
            await RunGitAsync(goodLocalDir, "config user.name \"Tester\"");
            await RunGitAsync(goodLocalDir, "config user.email \"tester@example.com\"");

            // Push remote update
            File.WriteAllText(Path.Combine(stagingDir, "f2.txt"), "v2\n");
            await RunGitAsync(stagingDir, "add f2.txt");
            await RunGitAsync(stagingDir, "commit -m \"v2\"");
            await RunGitAsync(stagingDir, "push origin main");

            // Bad repo (cloned, but remote broken to invalid address)
            await RunGitAsync(tempRoot, $"clone -b main \"{remoteDir}\" \"{badLocalDir}\"");
            await RunGitAsync(badLocalDir, "config user.name \"Tester\"");
            await RunGitAsync(badLocalDir, "config user.email \"tester@example.com\"");
            await RunGitAsync(badLocalDir, "remote set-url origin https://invalid.domain.12345.test/fake/repo.git");

            var migrator = new SchemaMigrator();
            migrator.EnsureInitialized(tempDb);
            var store = new SqliteIndexStore(tempDb);

            var id1 = await store.UpsertLocalRepoAsync(new LocalRepo(0, goodLocalDir, "localhost:22/remote", null, GitDirKind.Normal, false, false, false, null));
            var id2 = await store.UpsertLocalRepoAsync(new LocalRepo(0, badLocalDir, "invalid.domain.12345.test:443/fake/repo", null, GitDirKind.Normal, false, false, false, null));

            await store.UpsertLocalStatusAsync(new LocalStatus(id1, "main", "origin/main", 0, 1, 0, 0, "h1", RepoStateCode.Behind, DateTime.UtcNow));
            await store.UpsertLocalStatusAsync(new LocalStatus(id2, "main", "origin/main", 0, 1, 0, 0, "h2", RepoStateCode.Behind, DateTime.UtcNow));

            var targets = await store.GetAllLocalReposWithStatusAsync();
            var pullEngine = new PullEngine(_gitRunner, store);

            // Execute Batch Pull
            var report = await pullEngine.ExecuteBatchPullAsync(targets);

            // Batch should not stop on bad repo failure! (§7.3, Criterion 12)
            Assert.Equal(2, report.TotalSelected);
            Assert.Equal(1, report.SuccessCount);
            Assert.Equal(1, report.FailedCount);

            // Good repo succeeded
            Assert.True(File.Exists(Path.Combine(goodLocalDir, "f2.txt")));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot))
            {
                try { Directory.Delete(tempRoot, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task ExecuteBatchPull_ThrottlesConcurrencyCorrectly_ObservedWithTimestamps()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "concurrency_test_" + Guid.NewGuid().ToString("N"));
        var tempDb = Path.Combine(tempRoot, "index.db");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var migrator = new SchemaMigrator();
            migrator.EnsureInitialized(tempDb);
            var store = new SqliteIndexStore(tempDb);

            var maxObservedGlobal = 0;
            var maxObservedHost1 = 0;
            var maxObservedHost2 = 0;
            var maxObservedLfs = 0;

            var currentGlobal = 0;
            var currentHost1 = 0;
            var currentHost2 = 0;
            var currentLfs = 0;

            var syncLock = new object();
            var timelineLogs = new List<string>();

            // Create fake GitRunner with realistic async execution delay and concurrency measurement
            var mockGitRunner = new ConcurrencyTestingGitRunner(async (workDir, args) =>
            {
                var isLfs = workDir.Contains("lfs", StringComparison.OrdinalIgnoreCase);
                var isHost1 = workDir.Contains("host1", StringComparison.OrdinalIgnoreCase);

                int curG, curH1, curH2, curL;
                lock (syncLock)
                {
                    if (isLfs) Interlocked.Increment(ref currentLfs);
                    else Interlocked.Increment(ref currentGlobal);

                    if (isHost1) Interlocked.Increment(ref currentHost1);
                    else Interlocked.Increment(ref currentHost2);

                    curG = currentGlobal;
                    curH1 = currentHost1;
                    curH2 = currentHost2;
                    curL = currentLfs;

                    if (curG > maxObservedGlobal) maxObservedGlobal = curG;
                    if (curH1 > maxObservedHost1) maxObservedHost1 = curH1;
                    if (curH2 > maxObservedHost2) maxObservedHost2 = curH2;
                    if (curL > maxObservedLfs) maxObservedLfs = curL;

                    timelineLogs.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] ENTER {Path.GetFileName(workDir)}: Global={curG}, Host1={curH1}, Host2={curH2}, Lfs={curL}");
                }

                await Task.Delay(40); // Simulate work

                lock (syncLock)
                {
                    if (isLfs) Interlocked.Decrement(ref currentLfs);
                    else Interlocked.Decrement(ref currentGlobal);

                    if (isHost1) Interlocked.Decrement(ref currentHost1);
                    else Interlocked.Decrement(ref currentHost2);

                    timelineLogs.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] EXIT  {Path.GetFileName(workDir)}: Global={currentGlobal}, Host1={currentHost1}, Host2={currentHost2}, Lfs={currentLfs}");
                }

                if (args.Contains("rev-parse"))
                    return new GitCommandResult(0, "dummy_hash_123\n", string.Empty, TimeSpan.FromMilliseconds(5));
                return new GitCommandResult(0, "Already up to date.\n", string.Empty, TimeSpan.FromMilliseconds(5));
            });

            var pullEngine = new PullEngine(
                mockGitRunner,
                store,
                lockManager: new RepoLockManager(),
                fetchEngine: null,
                maxConcurrency: 4,
                maxConcurrencyPerHost: 4,
                maxLfsConcurrency: 1);

            // 12 repos total: 5 normal on host 1, 5 normal on host 2, 2 LFS on host 1
            var targets = new List<(LocalRepo Repo, LocalStatus? Status)>();
            for (int i = 1; i <= 5; i++)
            {
                var r = new LocalRepo(0, Path.Combine(tempRoot, $"host1_repo_{i}"), "host1.test:22/r" + i, null, GitDirKind.Normal, false, false, false, null);
                var id = await store.UpsertLocalRepoAsync(r);
                r = r with { Id = id };
                var s = new LocalStatus(id, "main", "origin/main", 0, 1, 0, 0, "hash", RepoStateCode.Behind, DateTime.UtcNow);
                await store.UpsertLocalStatusAsync(s);
                targets.Add((r, s));
            }
            for (int i = 1; i <= 5; i++)
            {
                var r = new LocalRepo(0, Path.Combine(tempRoot, $"host2_repo_{i}"), "host2.test:22/r" + i, null, GitDirKind.Normal, false, false, false, null);
                var id = await store.UpsertLocalRepoAsync(r);
                r = r with { Id = id };
                var s = new LocalStatus(id, "main", "origin/main", 0, 1, 0, 0, "hash", RepoStateCode.Behind, DateTime.UtcNow);
                await store.UpsertLocalStatusAsync(s);
                targets.Add((r, s));
            }
            for (int i = 1; i <= 2; i++)
            {
                var r = new LocalRepo(0, Path.Combine(tempRoot, $"host1_lfs_repo_{i}"), "host1.test:22/lfs" + i, null, GitDirKind.Normal, HasLfs: true);
                var id = await store.UpsertLocalRepoAsync(r);
                r = r with { Id = id };
                var s = new LocalStatus(id, "main", "origin/main", 0, 1, 0, 0, "hash", RepoStateCode.Behind, DateTime.UtcNow);
                await store.UpsertLocalStatusAsync(s);
                targets.Add((r, s));
            }

            var report = await pullEngine.ExecuteBatchPullAsync(targets);

            Assert.Equal(12, report.TotalSelected);

            // CRITERION 18: Global Network pool <= 4, Host pool <= 4
            Assert.InRange(maxObservedGlobal, 1, 4);
            Assert.InRange(maxObservedHost1, 1, 4);
            Assert.InRange(maxObservedHost2, 1, 4);

            // CRITERION 19: LFS pool <= 1
            Assert.InRange(maxObservedLfs, 1, 1);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot))
            {
                try { Directory.Delete(tempRoot, true); } catch { }
            }
        }
    }

    private class ConcurrencyTestingGitRunner : IGitRunner
    {
        private readonly Func<string, string, Task<GitCommandResult>> _handler;
        public ConcurrencyTestingGitRunner(Func<string, string, Task<GitCommandResult>> handler) => _handler = handler;
        public Task<GitCommandResult> ExecuteAsync(string workingDirectory, string arguments, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            _handler(workingDirectory, arguments);
        public Task<GitEnvironmentInfo> CheckEnvironmentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitEnvironmentInfo(true, new Version(2, 45, 0), "git version 2.45.0.windows.1", "git.exe", null));
    }

    private static LocalRepo CreateRepo(long id, string path, string? normalizedKey = "host:22/group/repo", bool isMissing = false, GitDirKind kind = GitDirKind.Normal) =>
        new(id, path, normalizedKey, null, kind, false, false, isMissing, null);

    private static async Task RunGitAsync(string workingDir, string arguments)
    {
        var runner = new GitRunner();
        var res = await runner.ExecuteAsync(workingDir, arguments);
        if (!res.IsSuccess)
        {
            throw new InvalidOperationException($"Git command '{arguments}' in '{workingDir}' failed: {res.StandardError}");
        }
    }
}
