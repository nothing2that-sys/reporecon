using Microsoft.Data.Sqlite;
using RepoRecon.Core.Concurrency;
using RepoRecon.Core.Data;
using RepoRecon.Core.Git;
using RepoRecon.Core.Models;
using RepoRecon.Core.Push;
using RepoRecon.Core.Security;
using Xunit;

namespace RepoRecon.Core.Tests;

public class PushEngineTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly SqliteIndexStore _indexStore;
    private readonly MockGitRunner _gitRunner;
    private readonly MockSecureTokenStore _tokenStore;

    public PushEngineTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"reporecon_push_test_{Guid.NewGuid():N}.db");
        new SchemaMigrator().EnsureInitialized(_tempDbPath);
        _indexStore = new SqliteIndexStore(_tempDbPath);
        _gitRunner = new MockGitRunner();
        _tokenStore = new MockSecureTokenStore();
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_tempDbPath))
                File.Delete(_tempDbPath);
        }
        catch { }
    }

    private LocalRepo CreateRepo(long id, string absPath, string host = "github.com")
    {
        return new LocalRepo(
            Id: id,
            AbsPath: absPath,
            NormalizedKey: $"{host}/org/repo{id}",
            GitDirKind: GitDirKind.Normal,
            HasLfs: false,
            IsMissing: false
        );
    }

    [Fact]
    public async Task GeneratePreviewAsync_AheadWithCleanStatus_IsEligibleAndDryRuns()
    {
        var repo = CreateRepo(1, @"C:\repos\repo1");
        var status = new LocalStatus(1, "main", "origin/main", Ahead: 2, Behind: 0, DirtyCount: 0, ConflictedCount: 0, "hash123", RepoStateCode.Ahead, DateTime.UtcNow);

        _gitRunner.SetResult("push --dry-run origin main", new GitCommandResult(0, "To https://github.com/org/repo1.git\n   aaa..bbb  main -> main", "", TimeSpan.FromMilliseconds(5)));

        var engine = new PushEngine(_gitRunner, _indexStore, tokenStore: _tokenStore);
        var preview = await engine.GeneratePreviewAsync(new (LocalRepo Repo, LocalStatus? Status)[] { (repo, status) });

        Assert.Single(preview);
        var item = preview[0];
        Assert.True(item.IsEligible);
        Assert.Equal("통과", item.DryRunStatus);
        Assert.Null(item.DirtyWarning);
        Assert.Contains("push origin main", item.Command);
    }

    [Fact]
    public async Task GeneratePreviewAsync_DirtyAndAhead_IsEligibleWithDirtyWarning()
    {
        var repo = CreateRepo(1, @"C:\repos\repo1");
        var status = new LocalStatus(1, "feature", "origin/feature", Ahead: 3, Behind: 0, DirtyCount: 4, ConflictedCount: 0, "hash123", RepoStateCode.Ahead, DateTime.UtcNow);

        _gitRunner.SetResult("push --dry-run origin feature", new GitCommandResult(0, "Everything up-to-date", "", TimeSpan.FromMilliseconds(5)));

        var engine = new PushEngine(_gitRunner, _indexStore, tokenStore: _tokenStore);
        var preview = await engine.GeneratePreviewAsync(new (LocalRepo Repo, LocalStatus? Status)[] { (repo, status) });

        Assert.Single(preview);
        var item = preview[0];
        Assert.True(item.IsEligible);
        Assert.Equal("통과", item.DryRunStatus);
        Assert.Equal("미커밋 4개 파일은 제외됨", item.DirtyWarning);
    }

    [Theory]
    [InlineData(RepoStateCode.Diverged, 1, 1, "pull 먼저 필요 — 서버가 거부함")]
    [InlineData(RepoStateCode.Behind, 0, 2, "pull 먼저 필요")]
    [InlineData(RepoStateCode.UpToDate, 0, 0, "올릴 커밋 없음")]
    [InlineData(RepoStateCode.NoUpstream, 1, 0, "추적 브랜치 없음 — 개별 처리 필요")]
    [InlineData(RepoStateCode.UpstreamGone, 0, 0, "upstream이 서버에서 삭제됨")]
    [InlineData(RepoStateCode.DetachedHead, 0, 0, "브랜치 미체크아웃")]
    [InlineData(RepoStateCode.Conflicted, 0, 0, "머지 충돌 — 해결 필요")]
    public async Task GeneratePreviewAsync_IneligibleStates_AreSkippedWithExpectedReasons(
        RepoStateCode state, int ahead, int behind, string expectedReason)
    {
        var repo = CreateRepo(1, @"C:\repos\repo1");
        var upstream = state == RepoStateCode.NoUpstream ? null : "origin/main";
        var branch = state == RepoStateCode.DetachedHead ? "(detached)" : "main";
        var conflicted = state == RepoStateCode.Conflicted ? 1 : 0;

        var status = new LocalStatus(1, branch, upstream, Ahead: ahead, Behind: behind, DirtyCount: 0, ConflictedCount: conflicted, "hash123", state, DateTime.UtcNow);

        var engine = new PushEngine(_gitRunner, _indexStore, tokenStore: _tokenStore);
        var preview = await engine.GeneratePreviewAsync(new (LocalRepo Repo, LocalStatus? Status)[] { (repo, status) });

        Assert.Single(preview);
        var item = preview[0];
        Assert.False(item.IsEligible);
        Assert.Equal(expectedReason, item.SkipReason);
        Assert.Equal("미실행", item.DryRunStatus);
    }

    [Fact]
    public async Task GeneratePreviewAsync_DryRunProtectedBranchRejection_IsMarkedIneligible()
    {
        var repo = CreateRepo(1, @"C:\repos\repo1");
        var status = new LocalStatus(1, "main", "origin/main", Ahead: 1, Behind: 0, DirtyCount: 0, ConflictedCount: 0, "hash123", RepoStateCode.Ahead, DateTime.UtcNow);

        _gitRunner.SetResult("push --dry-run origin main", new GitCommandResult(
            1, "", "remote: error: branch main is protected; cannot push directly\nTo http://localhost:18988/org/repo1.git\n ! [remote rejected] main -> main (protected branch hook declined)\nerror: failed to push some refs", TimeSpan.FromMilliseconds(5)));

        var engine = new PushEngine(_gitRunner, _indexStore, tokenStore: _tokenStore);
        var preview = await engine.GeneratePreviewAsync(new (LocalRepo Repo, LocalStatus? Status)[] { (repo, status) });

        Assert.Single(preview);
        var item = preview[0];
        Assert.False(item.IsEligible);
        Assert.Equal("거부", item.DryRunStatus);
        Assert.Contains("protected branch", item.SkipReason);
    }

    [Fact]
    public async Task ExecuteAsync_StrictBranchArguments_NeverUsesAllOrForce()
    {
        var repo = CreateRepo(1, @"C:\repos\repo1");
        await _indexStore.UpsertLocalRepoAsync(repo);
        var status = new LocalStatus(1, "main", "origin/main", Ahead: 1, Behind: 0, DirtyCount: 0, ConflictedCount: 0, "hash123", RepoStateCode.Ahead, DateTime.UtcNow);

        _gitRunner.SetResult("push --dry-run origin main", new GitCommandResult(0, "ok", "", TimeSpan.FromMilliseconds(5)));
        _gitRunner.SetResult("status --porcelain=v2 --branch", new GitCommandResult(0, "# branch.oid hash123\n# branch.head main\n# branch.upstream origin/main\n# branch.ab +1 -0\n", "", TimeSpan.FromMilliseconds(5)));
        _gitRunner.SetResult("rev-parse HEAD", new GitCommandResult(0, "hash123", "", TimeSpan.FromMilliseconds(5)));
        _gitRunner.SetResult("push origin main", new GitCommandResult(0, "pushed", "", TimeSpan.FromMilliseconds(5)));

        var engine = new PushEngine(_gitRunner, _indexStore, tokenStore: _tokenStore);
        var preview = await engine.GeneratePreviewAsync(new (LocalRepo Repo, LocalStatus? Status)[] { (repo, status) });
        var result = await engine.ExecuteAsync(preview);

        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(0, result.FailedCount);

        var executedCommands = _gitRunner.ExecutedCommands;
        Assert.Contains(executedCommands, c => c.Contains("push origin main"));
        Assert.DoesNotContain(executedCommands, c => c.Contains("--all"));
        Assert.DoesNotContain(executedCommands, c => c.Contains("--mirror"));
        Assert.DoesNotContain(executedCommands, c => c.Contains("--force"));
        Assert.DoesNotContain(executedCommands, c => c.Contains("+refs"));
    }

    [Fact]
    public async Task ExecuteAsync_PrePushHeadRecording_SavedInJobHistory()
    {
        var repo = CreateRepo(1, @"C:\repos\repo1");
        await _indexStore.UpsertLocalRepoAsync(repo);
        var status = new LocalStatus(1, "main", "origin/main", Ahead: 1, Behind: 0, DirtyCount: 0, ConflictedCount: 0, "hash123", RepoStateCode.Ahead, DateTime.UtcNow);

        _gitRunner.SetResult("push --dry-run origin main", new GitCommandResult(0, "ok", "", TimeSpan.FromMilliseconds(5)));
        _gitRunner.SetResult("status --porcelain=v2 --branch", new GitCommandResult(0, "# branch.oid hash123\n# branch.head main\n# branch.upstream origin/main\n# branch.ab +1 -0\n", "", TimeSpan.FromMilliseconds(5)));
        _gitRunner.SetResult("rev-parse HEAD", new GitCommandResult(0, "hash123", "", TimeSpan.FromMilliseconds(5)));
        _gitRunner.SetResult("push origin main", new GitCommandResult(0, "pushed", "", TimeSpan.FromMilliseconds(5)));

        var engine = new PushEngine(_gitRunner, _indexStore, tokenStore: _tokenStore);
        var preview = await engine.GeneratePreviewAsync(new (LocalRepo Repo, LocalStatus? Status)[] { (repo, status) });
        var result = await engine.ExecuteAsync(preview);

        Assert.Equal(1, result.SuccessCount);

        // Verify SQLite job_history
        using var conn = new SqliteConnection($"Data Source={_tempDbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT job_type, pre_head, post_head, result FROM job_history WHERE target_path = @path ORDER BY id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("@path", @"C:\repos\repo1");
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("push", reader.GetString(0));
        Assert.Equal("hash123", reader.GetString(1));
        Assert.Equal("hash123", reader.GetString(2));
        Assert.Equal("Success", reader.GetString(3));
    }

    private class MockSecureTokenStore : ISecureTokenStore
    {
        public Task<string> SaveTokenAsync(string plainToken, CancellationToken cancellationToken = default) => Task.FromResult("tok_123");
        public Task<string?> GetTokenAsync(string tokenRef, CancellationToken cancellationToken = default) => Task.FromResult<string?>("mock_token_123");
        public Task DeleteTokenAsync(string tokenRef, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private class MockGitRunner : IGitRunner
    {
        private readonly Dictionary<string, GitCommandResult> _results = new(StringComparer.OrdinalIgnoreCase);
        public List<string> ExecutedCommands { get; } = new();

        public void SetResult(string arguments, GitCommandResult result)
        {
            _results[arguments] = result;
        }

        public Task<GitCommandResult> ExecuteAsync(
            string workingDirectory,
            string arguments,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(workingDirectory, arguments, timeout, cancellationToken, null);
        }

        public Task<GitCommandResult> ExecuteAsync(
            string workingDirectory,
            string arguments,
            TimeSpan? timeout,
            CancellationToken cancellationToken,
            IDictionary<string, string>? environmentVariables)
        {
            ExecutedCommands.Add(arguments);

            foreach (var kvp in _results.OrderByDescending(k => k.Key.Length))
            {
                if (arguments.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(kvp.Value);
                }
            }

            return Task.FromResult(new GitCommandResult(0, "default output", "", TimeSpan.FromMilliseconds(5)));
        }

        public Task<GitEnvironmentInfo> CheckEnvironmentAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GitEnvironmentInfo(true, new Version(2, 44, 0), "git version 2.44.0.windows.1", "git.exe", null));
        }
    }
}
