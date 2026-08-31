using RepoRecon.Core.Data;
using RepoRecon.Core.Export;
using RepoRecon.Core.Git;
using RepoRecon.Core.Models;
using RepoRecon.Core.Urls;
using Xunit;

namespace RepoRecon.Core.Tests;

public class Phase6CoreTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly SqliteIndexStore _store;

    public Phase6CoreTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"reporecon_phase6_test_{Guid.NewGuid():N}.db");
        var migrator = new SchemaMigrator();
        migrator.EnsureInitialized(_testDbPath);
        _store = new SqliteIndexStore(_testDbPath);
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_testDbPath)) File.Delete(_testDbPath);
            if (File.Exists($"{_testDbPath}-wal")) File.Delete($"{_testDbPath}-wal");
            if (File.Exists($"{_testDbPath}-shm")) File.Delete($"{_testDbPath}-shm");
        }
        catch { }
    }

    [Fact]
    public async Task JobHistory_FilteredQuery_WorksCorrectly()
    {
        // Arrange
        await _store.InsertJobHistoryAsync(new JobHistoryEntry(0, "pull", "D:/repos/repo1", "aaa", "bbb", "Success", null, DateTime.UtcNow.AddMinutes(-10)));
        await _store.InsertJobHistoryAsync(new JobHistoryEntry(0, "push", "D:/repos/repo2", "ccc", "ddd", "Success", null, DateTime.UtcNow.AddMinutes(-5)));
        await _store.InsertJobHistoryAsync(new JobHistoryEntry(0, "rollback", "D:/repos/repo1", "bbb", "aaa", "Success", null, DateTime.UtcNow.AddMinutes(-2)));
        await _store.InsertJobHistoryAsync(new JobHistoryEntry(0, "remote_url_change", "D:/repos/repo3", "https://old.com/r3", "https://new.com/r3", "Success", null, DateTime.UtcNow));

        // Act & Assert 1: Filter by path
        var repo1History = await _store.GetJobHistoryFilteredAsync(targetPath: "repo1");
        Assert.Equal(2, repo1History.Count);

        // Act & Assert 2: Filter by job_type
        var pushHistory = await _store.GetJobHistoryFilteredAsync(jobType: "push");
        Assert.Single(pushHistory);
        Assert.Equal("D:/repos/repo2", pushHistory[0].TargetPath);

        // Act & Assert 3: Limit
        var limited = await _store.GetJobHistoryFilteredAsync(limit: 2);
        Assert.Equal(2, limited.Count);
    }

    [Fact]
    public void BulkHostReplace_Preview_MatchesHostAndPreservesPath()
    {
        // Arrange
        var service = new RemoteUrlService(new GitRunner(), _store);
        var repo1 = new LocalRepo(1, "D:/repos/p1", "gitlab.old.test:443/g1/p1", null, GitDirKind.Normal, false, false, false, null);
        var repo2 = new LocalRepo(2, "D:/repos/p2", "gitlab.other.test:443/g2/p2", null, GitDirKind.Normal, false, false, false, null);
        var targets = new List<(LocalRepo, string)>
        {
            (repo1, "https://gitlab.old.test/g1/p1.git"),
            (repo2, "git@gitlab.other.test:g2/p2.git")
        };

        // Act
        var preview = service.PreviewBulkHostReplace("gitlab.old.test", "gitlab.new.test", targets);

        // Assert
        Assert.Equal(2, preview.Count);
        var item1 = preview.First(p => p.Repo.Id == 1);
        Assert.True(item1.IsEligible);
        Assert.Equal("https://gitlab.new.test/g1/p1.git", item1.NewUrl);

        var item2 = preview.First(p => p.Repo.Id == 2);
        Assert.False(item2.IsEligible);
        Assert.Contains("불일치", item2.SkipReason);
    }

    [Fact]
    public void BulkHostReplace_ScpFormat_ReplacesHostCorrectly()
    {
        // Arrange
        var scpUrl = "git@gitlab.old.test:group/project.git";

        // Act
        var replaced = RemoteUrlService.ReplaceHost(scpUrl, "gitlab.old.test", "gitlab.new.test");

        // Assert
        Assert.Equal("git@gitlab.new.test:group/project.git", replaced);
    }

    [Fact]
    public void ExportService_CsvAndMarkdown_MasksCredentials()
    {
        // Arrange
        var service = new ExportService();
        var items = new List<ExportItem>
        {
            new("Repo1", "D:/work/repo1", "gitlab.test:443/grp/r1", "main", "origin/main", 0, 0, 0, 0, "Up-to-Date", "방금 전 기준", "https://oauth2:secret_token_12345@gitlab.test/grp/r1.git"),
            new("Repo2", "D:/work/repo2", "github.com:443/org/r2", "feature", "origin/feature", 1, 0, 2, 0, "Ahead (↑1)", "5분 전 기준", "https://user:password999@github.com/org/r2.git")
        };

        // Act
        var csv = service.ExportToCsv(items);
        var md = service.ExportToMarkdown(items);

        // Assert
        Assert.DoesNotContain("secret_token_12345", csv);
        Assert.DoesNotContain("password999", csv);
        Assert.Contains("gitlab.test/grp/r1.git", csv);
        Assert.DoesNotContain("secret_token_12345", md);
        Assert.DoesNotContain("password999", md);
        Assert.Contains("gitlab.test/grp/r1.git", md);
        Assert.Contains("github.com/org/r2.git", md);
    }

    [Fact]
    public async Task RollbackService_CleanRepo_RollsBackHeadAndRecordsHistory()
    {
        // Arrange
        var tempRepoDir = Path.Combine(Path.GetTempPath(), $"reporecon_rollback_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRepoDir);
        var runner = new GitRunner();

        try
        {
            await runner.ExecuteAsync(tempRepoDir, "init", TimeSpan.FromSeconds(5));
            await runner.ExecuteAsync(tempRepoDir, "config user.name \"Test User\"", TimeSpan.FromSeconds(5));
            await runner.ExecuteAsync(tempRepoDir, "config user.email \"test@test.test\"", TimeSpan.FromSeconds(5));

            await File.WriteAllTextAsync(Path.Combine(tempRepoDir, "file1.txt"), "v1");
            await runner.ExecuteAsync(tempRepoDir, "add file1.txt", TimeSpan.FromSeconds(5));
            await runner.ExecuteAsync(tempRepoDir, "commit -m \"commit 1\"", TimeSpan.FromSeconds(5));
            var commit1Result = await runner.ExecuteAsync(tempRepoDir, "rev-parse HEAD", TimeSpan.FromSeconds(5));
            var commit1 = commit1Result.StandardOutput.Trim();

            await File.WriteAllTextAsync(Path.Combine(tempRepoDir, "file1.txt"), "v2");
            await runner.ExecuteAsync(tempRepoDir, "commit -am \"commit 2\"", TimeSpan.FromSeconds(5));
            var commit2Result = await runner.ExecuteAsync(tempRepoDir, "rev-parse HEAD", TimeSpan.FromSeconds(5));
            var commit2 = commit2Result.StandardOutput.Trim();

            var rollbackService = new RollbackService(runner, _store);

            // Act: Rollback from commit2 to commit1
            var (success, error) = await rollbackService.RollbackCommitAsync(1, tempRepoDir, commit1);

            // Assert
            Assert.True(success, error);
            var headCheck = await runner.ExecuteAsync(tempRepoDir, "rev-parse HEAD", TimeSpan.FromSeconds(5));
            Assert.Equal(commit1, headCheck.StandardOutput.Trim());

            var history = await _store.GetJobHistoryFilteredAsync(jobType: "rollback");
            Assert.Single(history);
            Assert.Equal("rollback", history[0].JobType);
            Assert.Equal(commit2, history[0].PreHead);
            Assert.Equal(commit1, history[0].PostHead);
            Assert.Equal("Success", history[0].Result);
        }
        finally
        {
            try { Directory.Delete(tempRepoDir, true); } catch { }
        }
    }

    [Fact]
    public async Task RollbackService_DirtyRepo_BlocksRollbackAndThrows()
    {
        // Arrange
        var tempRepoDir = Path.Combine(Path.GetTempPath(), $"reporecon_rollback_dirty_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRepoDir);
        var runner = new GitRunner();

        try
        {
            await runner.ExecuteAsync(tempRepoDir, "init", TimeSpan.FromSeconds(5));
            await runner.ExecuteAsync(tempRepoDir, "config user.name \"Test User\"", TimeSpan.FromSeconds(5));
            await runner.ExecuteAsync(tempRepoDir, "config user.email \"test@test.test\"", TimeSpan.FromSeconds(5));

            await File.WriteAllTextAsync(Path.Combine(tempRepoDir, "file1.txt"), "v1");
            await runner.ExecuteAsync(tempRepoDir, "add file1.txt", TimeSpan.FromSeconds(5));
            await runner.ExecuteAsync(tempRepoDir, "commit -m \"commit 1\"", TimeSpan.FromSeconds(5));
            var commit1Result = await runner.ExecuteAsync(tempRepoDir, "rev-parse HEAD", TimeSpan.FromSeconds(5));
            var commit1 = commit1Result.StandardOutput.Trim();

            await File.WriteAllTextAsync(Path.Combine(tempRepoDir, "file1.txt"), "v2");
            await runner.ExecuteAsync(tempRepoDir, "commit -am \"commit 2\"", TimeSpan.FromSeconds(5));

            // Introduce dirty working tree modification
            await File.WriteAllTextAsync(Path.Combine(tempRepoDir, "file1.txt"), "dirty uncommitted modification");

            var rollbackService = new RollbackService(runner, _store);

            // Act & Assert: Rollback MUST throw InvalidOperationException
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                rollbackService.RollbackCommitAsync(1, tempRepoDir, commit1));

            Assert.Contains("작업트리에 커밋되지 않은 변경 사항이 있어 되돌리기가 차단되었습니다", ex.Message);

            // Verify working tree still has dirty modification intact
            var content = await File.ReadAllTextAsync(Path.Combine(tempRepoDir, "file1.txt"));
            Assert.Equal("dirty uncommitted modification", content);
        }
        finally
        {
            try { Directory.Delete(tempRepoDir, true); } catch { }
        }
    }
}
