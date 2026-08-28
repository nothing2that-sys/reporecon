using RepoRecon.Core.Clone;
using RepoRecon.Core.Concurrency;
using RepoRecon.Core.Data;
using RepoRecon.Core.Git;
using RepoRecon.Core.Models;
using Xunit;

namespace RepoRecon.Core.Tests;

public class CloneEngineTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _dbPath;
    private readonly SqliteIndexStore _store;
    private readonly GitRunner _gitRunner;
    private readonly RepoLockManager _lockManager;
    private readonly CloneEngine _engine;

    public CloneEngineTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"reporecon-clone-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
        _dbPath = Path.Combine(_testRoot, "index.db");

        var migrator = new SchemaMigrator();
        migrator.EnsureInitialized(_dbPath);

        _store = new SqliteIndexStore(_dbPath);
        _gitRunner = new GitRunner();
        _lockManager = new RepoLockManager();
        _engine = new CloneEngine(_gitRunner, _store, _lockManager);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try
            {
                // Remove readonly attributes
                var dir = new DirectoryInfo(_testRoot);
                foreach (var file in dir.GetFiles("*", SearchOption.AllDirectories))
                {
                    file.Attributes = FileAttributes.Normal;
                }
                Directory.Delete(_testRoot, true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void ComputeTargetPath_GeneratesNamespaceAndProjectHierarchy()
    {
        var repo = new ServerRepo(
            Id: 1,
            ProfileId: 1,
            Namespace: "enterprise/core-team/sub-dept",
            Name: "super-app",
            CloneUrl: "https://gitlab.example.com/enterprise/core-team/sub-dept/super-app.git",
            NormalizedKey: "gitlab.example.com/enterprise/core-team/sub-dept/super-app",
            DefaultBranch: "main",
            IsArchived: false,
            CollectedAt: DateTime.UtcNow);

        var targetPath = CloneEngine.ComputeTargetPath(@"D:\Work\Repos", repo);

        var expectedSuffix = Path.Combine("enterprise", "core-team", "sub-dept", "super-app");
        Assert.EndsWith(expectedSuffix, targetPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreviewClonesAsync_DetectsNonEmptyPathCollisions()
    {
        var repo1 = new ServerRepo(1, 1, "group-a", "clean-repo", "https://host/a/clean-repo.git", "host/a/clean-repo", "main", false, DateTime.UtcNow);
        var repo2 = new ServerRepo(2, 1, "group-b", "colliding-repo", "https://host/b/colliding-repo.git", "host/b/colliding-repo", "main", false, DateTime.UtcNow);

        var targetRoot = Path.Combine(_testRoot, "clones");
        var collidingPath = CloneEngine.ComputeTargetPath(targetRoot, repo2);
        Directory.CreateDirectory(collidingPath);
        await File.WriteAllTextAsync(Path.Combine(collidingPath, "existing.txt"), "hello");

        var previews = await _engine.PreviewClonesAsync(new[] { repo1, repo2 }, targetRoot);

        Assert.Equal(2, previews.Count);
        Assert.False(previews[0].HasCollision);
        Assert.True(previews[1].HasCollision);
        Assert.NotNull(previews[1].CollisionReason);
    }

    [Fact]
    public async Task ExecuteBatchCloneAsync_PerformsAtomicCloning_AndRecordsJobHistory()
    {
        // 1. Setup local source bare repository
        var bareDir = Path.Combine(_testRoot, "source-bare.git");
        Directory.CreateDirectory(bareDir);
        await _gitRunner.ExecuteAsync(bareDir, "init --bare");

        // Seed a commit into the bare repo using a temp worktree
        var seedDir = Path.Combine(_testRoot, "seed-worktree");
        Directory.CreateDirectory(seedDir);
        await _gitRunner.ExecuteAsync(seedDir, "init");
        await _gitRunner.ExecuteAsync(seedDir, "config user.name \"Test\"");
        await _gitRunner.ExecuteAsync(seedDir, "config user.email \"test@test.com\"");
        await File.WriteAllTextAsync(Path.Combine(seedDir, "README.md"), "# Hello Phase 4");
        await _gitRunner.ExecuteAsync(seedDir, "add README.md");
        await _gitRunner.ExecuteAsync(seedDir, "commit -m \"Initial commit\"");
        await _gitRunner.ExecuteAsync(seedDir, $"remote add origin \"{bareDir}\"");
        await _gitRunner.ExecuteAsync(seedDir, "push origin master");

        // 2. Execute Clone via CloneEngine
        var serverRepo = new ServerRepo(
            Id: 10,
            ProfileId: 1,
            Namespace: "mycorp/team",
            Name: "project-x",
            CloneUrl: bareDir, // local file path clone
            NormalizedKey: "mycorp/team/project-x",
            DefaultBranch: "master",
            IsArchived: false,
            CollectedAt: DateTime.UtcNow);

        var targetRoot = Path.Combine(_testRoot, "cloned-workspace");
        var results = await _engine.ExecuteBatchCloneAsync(new[] { serverRepo }, targetRoot);

        Assert.Single(results);
        var result = results[0];
        Assert.True(result.IsSuccess);
        Assert.True(Directory.Exists(result.TargetPath));
        Assert.True(File.Exists(Path.Combine(result.TargetPath, "README.md")));

        // Atomic temp folder must NOT exist (§10 Criterion 20)
        Assert.False(Directory.Exists($"{result.TargetPath}.reporecon-tmp"));

        // Check Job History in SQLite
        var history = await _store.GetJobHistoryAsync(result.TargetPath);
        Assert.Single(history);
        Assert.Equal("clone", history[0].JobType);
        Assert.Equal("SUCCESS", history[0].Result);
        Assert.Null(history[0].PreHead);
        Assert.NotNull(history[0].PostHead);
    }

    [Fact]
    public async Task ExecuteBatchCloneAsync_CleansUpTempFolder_OnCloneFailure()
    {
        var badRepo = new ServerRepo(
            Id: 20,
            ProfileId: 1,
            Namespace: "fail-group",
            Name: "invalid-repo",
            CloneUrl: "https://invalid-non-existent-host-1234567.com/invalid.git",
            NormalizedKey: "invalid/fail",
            DefaultBranch: "main",
            IsArchived: false,
            CollectedAt: DateTime.UtcNow);

        var targetRoot = Path.Combine(_testRoot, "fail-clones");
        var expectedTargetPath = CloneEngine.ComputeTargetPath(targetRoot, badRepo);
        var expectedTempPath = $"{expectedTargetPath}.reporecon-tmp";

        var results = await _engine.ExecuteBatchCloneAsync(new[] { badRepo }, targetRoot);

        Assert.Single(results);
        Assert.False(results[0].IsSuccess);
        Assert.NotNull(results[0].ErrorMessage);

        // Verification for §10 Criterion 20:
        // Neither target path nor .reporecon-tmp folder must exist!
        Assert.False(Directory.Exists(expectedTargetPath));
        Assert.False(Directory.Exists(expectedTempPath));

        // Verify failure was logged to job history
        var history = await _store.GetJobHistoryAsync(expectedTargetPath);
        Assert.Single(history);
        Assert.Equal("FAILED", history[0].Result);
        Assert.NotNull(history[0].ErrorCategory);
    }

    [Fact]
    public void FindResidualTempFolders_DetectsAndCleansTempFolders_OnStartup()
    {
        var scanRoot = Path.Combine(_testRoot, "scan-root-temp");
        var residualFolder = Path.Combine(scanRoot, "group", "orphaned-clone.reporecon-tmp");
        Directory.CreateDirectory(residualFolder);
        File.WriteAllText(Path.Combine(residualFolder, "half-downloaded.pack"), "12345");

        var residuals = _engine.FindResidualTempFolders(new[] { scanRoot });

        Assert.Single(residuals);
        Assert.Equal(residualFolder, residuals[0].TempPath);

        // Clean
        var cleaned = _engine.CleanResidualTempFolder(residualFolder);
        Assert.True(cleaned);
        Assert.False(Directory.Exists(residualFolder));
    }
}
