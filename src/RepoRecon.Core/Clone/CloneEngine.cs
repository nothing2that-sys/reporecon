using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoRecon.Core.Concurrency;
using RepoRecon.Core.Data;
using RepoRecon.Core.Git;
using RepoRecon.Core.Models;
using RepoRecon.Core.Pull;
using RepoRecon.Core.Security;

namespace RepoRecon.Core.Clone;

public class CloneEngine : ICloneEngine
{
    private readonly IGitRunner _gitRunner;
    private readonly SqliteIndexStore _indexStore;
    private readonly IRepoLockManager _lockManager;
    private readonly ISecureTokenStore? _tokenStore;
    private readonly ILogger<CloneEngine> _logger;

    private readonly SemaphoreSlim _globalNetworkPool;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostPools = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lfsPool = new(1, 1);

    public CloneEngine(
        IGitRunner gitRunner,
        SqliteIndexStore indexStore,
        IRepoLockManager lockManager,
        ISecureTokenStore? tokenStore = null,
        ILogger<CloneEngine>? logger = null,
        int maxGlobalConcurrency = 4,
        int maxPerHostConcurrency = 4)
    {
        _gitRunner = gitRunner;
        _indexStore = indexStore;
        _lockManager = lockManager;
        _tokenStore = tokenStore;
        _logger = logger ?? NullLogger<CloneEngine>.Instance;
        _globalNetworkPool = new SemaphoreSlim(maxGlobalConcurrency, maxGlobalConcurrency);
    }

    public static string ComputeTargetPath(string targetRoot, ServerRepo repo)
    {
        var cleanRoot = targetRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var nsParts = repo.Namespace.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        var combined = Path.Combine(new[] { cleanRoot }.Concat(nsParts).Concat(new[] { repo.Name }).ToArray());
        return Path.GetFullPath(combined);
    }

    public Task<IReadOnlyList<ClonePreviewItem>> PreviewClonesAsync(
        IReadOnlyList<ServerRepo> repos,
        string targetRoot,
        CancellationToken cancellationToken = default)
    {
        var previewList = new List<ClonePreviewItem>();

        foreach (var repo in repos)
        {
            var targetPath = ComputeTargetPath(targetRoot, repo);
            var hasCollision = false;
            string? collisionReason = null;
            var exceedsLongPath = targetPath.Length >= 240;

            if (Directory.Exists(targetPath))
            {
                try
                {
                    if (Directory.EnumerateFileSystemEntries(targetPath).Any())
                    {
                        hasCollision = true;
                        collisionReason = "대상 폴더가 이미 존재하며 파일/폴더가 있습니다.";
                    }
                }
                catch (Exception ex)
                {
                    hasCollision = true;
                    collisionReason = $"폴더 접근 확인 실패: {ex.Message}";
                }
            }

            previewList.Add(new ClonePreviewItem(repo, targetPath, hasCollision, collisionReason, exceedsLongPath));
        }

        return Task.FromResult<IReadOnlyList<ClonePreviewItem>>(previewList);
    }

    public async Task<IReadOnlyList<CloneResult>> ExecuteBatchCloneAsync(
        IReadOnlyList<ServerRepo> repos,
        string targetRoot,
        IProgress<BatchCloneProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var totalCount = repos.Count;
        var completedCount = 0;
        var results = new ConcurrentBag<CloneResult>();

        _logger.LogInformation("Starting batch clone of {Count} repositories to {TargetRoot}", totalCount, targetRoot);

        var tasks = repos.Select(async repo =>
        {
            var targetPath = ComputeTargetPath(targetRoot, repo);
            var result = await CloneSingleRepoAsync(repo, targetPath, cancellationToken).ConfigureAwait(false);
            results.Add(result);

            var currentCompleted = Interlocked.Increment(ref completedCount);
            progress?.Report(new BatchCloneProgress(
                Completed: currentCompleted,
                Total: totalCount,
                CurrentRepoPath: targetPath,
                Result: result.IsSuccess ? "SUCCESS" : "FAILED",
                ErrorMessage: result.ErrorMessage));
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        return results.OrderBy(r => r.TargetPath).ToList();
    }

    private async Task<CloneResult> CloneSingleRepoAsync(ServerRepo repo, string targetPath, CancellationToken cancellationToken)
    {
        // 1. Acquire repo path lock
        using var repoLock = await _lockManager.AcquireLockAsync(targetPath, cancellationToken).ConfigureAwait(false);

        // 2. Collision pre-check
        if (Directory.Exists(targetPath) && Directory.EnumerateFileSystemEntries(targetPath).Any())
        {
            var error = "대상 경로가 이미 존재하며 비어있지 않습니다.";
            return new CloneResult(repo, targetPath, false, error, ErrorCategory.Path.ToString(), null);
        }

        // 3. Acquire network concurrency semaphores
        var host = ExtractHost(repo.CloneUrl);
        var hostPool = _hostPools.GetOrAdd(host, _ => new SemaphoreSlim(4, 4));

        await _globalNetworkPool.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await hostPool.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ExecuteAtomicCloneAsync(repo, targetPath, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                hostPool.Release();
            }
        }
        finally
        {
            _globalNetworkPool.Release();
        }
    }

    public static string ToExtendedLengthPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) return path;

        var cleanPath = path;
        if (!Path.IsPathRooted(cleanPath))
        {
            cleanPath = Path.Combine(Directory.GetCurrentDirectory(), cleanPath);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (cleanPath.StartsWith(@"\\"))
            {
                return @"\\?\UNC\" + cleanPath.Substring(2);
            }
            return @"\\?\" + cleanPath;
        }
        return cleanPath;
    }

    private async Task<CloneResult> ExecuteAtomicCloneAsync(ServerRepo repo, string targetPath, CancellationToken cancellationToken)
    {
        var tempPath = $"{targetPath}.reporecon-tmp";
        var parentDir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(parentDir))
        {
            Directory.CreateDirectory(ToExtendedLengthPath(parentDir));
        }

        // Clean any residual temp folder before starting
        SafeDeleteDirectory(tempPath);

        _logger.LogInformation("Cloning {NormalizedKey} into atomic temp directory {TempPath}", repo.NormalizedKey, tempPath);

        try
        {
            Dictionary<string, string>? envVars = null;
            if (_tokenStore != null)
            {
                var profiles = await _indexStore.GetServerProfilesAsync(cancellationToken).ConfigureAwait(false);
                var profile = (repo.ProfileId > 0 ? profiles.FirstOrDefault(p => p.Id == repo.ProfileId) : null)
                              ?? profiles.FirstOrDefault(p => !string.IsNullOrEmpty(p.Host) && repo.CloneUrl.Contains(p.Host, StringComparison.OrdinalIgnoreCase));
                var hasToken = false;
                if (profile != null && !string.IsNullOrWhiteSpace(profile.TokenRef))
                {
                    var token = await _tokenStore.GetTokenAsync(profile.TokenRef, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        hasToken = true;
                        var cleanTok = token.Trim();
                        var headerVal = profile.ApiKind == ServerApiKind.GitLab
                            ? $"PRIVATE-TOKEN: {cleanTok}"
                            : $"Authorization: token {cleanTok}";

                        envVars = new Dictionary<string, string>
                        {
                            ["GIT_CONFIG_COUNT"] = "1",
                            ["GIT_CONFIG_KEY_0"] = "http.extraHeader",
                            ["GIT_CONFIG_VALUE_0"] = headerVal
                        };
                    }
                }
                _logger.LogInformation("CloneEngine token lookup for {NormalizedKey}: ProfileId={ProfileId}, FoundProfile={ProfileHost}, HasToken={HasToken}", repo.NormalizedKey, repo.ProfileId, profile?.Host, hasToken);
            }

            // Execute git clone with longpaths=true both globally and in the new repository config (§7.10, §10 Criterion 18)
            // Token is NEVER embedded in clone URL (§4.2, §5)
            var cleanTempPath = tempPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var cloneArgs = $"-c core.longpaths=true clone --config core.longpaths=true \"{repo.CloneUrl}\" \"{cleanTempPath}\"";
            var workDir = Directory.GetCurrentDirectory();
            var result = await _gitRunner.ExecuteAsync(workDir, cloneArgs, TimeSpan.FromMinutes(3), cancellationToken, envVars).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                var isCheckoutWarningOnly = result.StandardError.Contains("Clone succeeded, but checkout failed", StringComparison.OrdinalIgnoreCase) ||
                                           (Directory.Exists(Path.Combine(tempPath, ".git")));

                if (isCheckoutWarningOnly)
                {
                    // Checkout recovery using explicit core.longpaths config & checkout & reset (§7.10, §10 Criterion 18)
                    _logger.LogInformation("Attempting post-clone checkout recovery for {NormalizedKey} at {TempPath}", repo.NormalizedKey, tempPath);
                    await _gitRunner.ExecuteAsync(workDir, $"-C \"{tempPath}\" config core.longpaths true", TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                    var branch = string.IsNullOrWhiteSpace(repo.DefaultBranch) ? "main" : repo.DefaultBranch;
                    await _gitRunner.ExecuteAsync(workDir, $"-c core.longpaths=true -C \"{tempPath}\" checkout -f -B \"{branch}\" \"origin/{branch}\"", TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                    await _gitRunner.ExecuteAsync(workDir, $"-c core.longpaths=true -C \"{tempPath}\" reset --hard", TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    SafeDeleteDirectory(tempPath);
                    var errCategory = ErrorClassifier.ClassifyToString(result.StandardError);
                    _logger.LogWarning("Clone failed for {NormalizedKey}. Error: {Error}. Category: {Category}", repo.NormalizedKey, result.StandardError, errCategory);

                    await _indexStore.InsertJobHistoryAsync(new JobHistoryEntry(
                        Id: 0,
                        JobType: "clone",
                        TargetPath: targetPath,
                        PreHead: null,
                        PostHead: null,
                        Result: "FAILED",
                        ErrorCategory: errCategory,
                        CreatedAt: DateTime.UtcNow), cancellationToken).ConfigureAwait(false);

                    return new CloneResult(repo, targetPath, false, result.StandardError, errCategory, null);
                }
            }

            // Get cloned HEAD hash using --git-dir and --work-tree
            var headResult = await _gitRunner.ExecuteAsync(workDir, $"-c core.longpaths=true --git-dir=\"{tempPath}/.git\" --work-tree=\"{tempPath}\" rev-parse HEAD", TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            var clonedHead = headResult.IsSuccess ? headResult.StandardOutput.Trim() : null;

            // Move tempPath -> targetPath (Atomic promotion §7.10)
            var extTemp = ToExtendedLengthPath(tempPath);
            var extTarget = ToExtendedLengthPath(targetPath);
            if (Directory.Exists(extTarget) || Directory.Exists(targetPath))
            {
                SafeDeleteDirectory(extTarget);
            }
            Directory.Move(extTemp, extTarget);

            _logger.LogInformation("Successfully promoted cloned repo to final path: {TargetPath}", targetPath);

            // Immediate index insertion (§8)
            await InsertClonedRepoToIndexAsync(repo, targetPath, clonedHead, cancellationToken).ConfigureAwait(false);

            // Record job history
            await _indexStore.InsertJobHistoryAsync(new JobHistoryEntry(
                Id: 0,
                JobType: "clone",
                TargetPath: targetPath,
                PreHead: null,
                PostHead: clonedHead,
                Result: "SUCCESS",
                ErrorCategory: null,
                CreatedAt: DateTime.UtcNow), cancellationToken).ConfigureAwait(false);

            return new CloneResult(repo, targetPath, true, null, null, clonedHead);
        }
        catch (Exception ex)
        {
            SafeDeleteDirectory(tempPath);
            var errCategory = ErrorClassifier.ClassifyToString(ex.Message);
            _logger.LogError(ex, "Unexpected error during atomic clone of {NormalizedKey}", repo.NormalizedKey);

            await _indexStore.InsertJobHistoryAsync(new JobHistoryEntry(
                Id: 0,
                JobType: "clone",
                TargetPath: targetPath,
                PreHead: null,
                PostHead: null,
                Result: "FAILED",
                ErrorCategory: errCategory,
                CreatedAt: DateTime.UtcNow), cancellationToken).ConfigureAwait(false);

            return new CloneResult(repo, targetPath, false, ex.Message, errCategory, null);
        }
    }

    private async Task InsertClonedRepoToIndexAsync(ServerRepo repo, string targetPath, string? clonedHead, CancellationToken cancellationToken)
    {
        try
        {
            var hasLfs = File.Exists(Path.Combine(targetPath, ".gitattributes")) &&
                         (await File.ReadAllTextAsync(Path.Combine(targetPath, ".gitattributes"), cancellationToken).ConfigureAwait(false)).Contains("filter=lfs");
            var hasSubmodule = File.Exists(Path.Combine(targetPath, ".gitmodules"));

            var localRepo = new LocalRepo(
                Id: 0,
                AbsPath: targetPath,
                NormalizedKey: repo.NormalizedKey,
                ParentRepoId: null,
                GitDirKind: GitDirKind.Normal,
                HasLfs: hasLfs,
                HasSubmodule: hasSubmodule,
                IsMissing: false,
                MissingSince: null);

            var repoId = await _indexStore.UpsertLocalRepoAsync(localRepo, cancellationToken).ConfigureAwait(false);

            var statusResult = await _gitRunner.ExecuteAsync(Directory.GetCurrentDirectory(), $"-c core.longpaths=true --git-dir=\"{targetPath}/.git\" --work-tree=\"{targetPath}\" status --porcelain=v2 --branch", TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            var status = GitStatusParser.Parse(repoId, statusResult.StandardOutput, hasRemote: true);
            await _indexStore.UpsertLocalStatusAsync(status, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to immediately index newly cloned repo at {TargetPath}", targetPath);
        }
    }

    public IReadOnlyList<ResidualTempFolder> FindResidualTempFolders(IEnumerable<string> scanRoots)
    {
        var residuals = new List<ResidualTempFolder>();

        foreach (var root in scanRoots)
        {
            if (!Directory.Exists(root)) continue;

            try
            {
                var tempDirs = Directory.GetDirectories(root, "*.reporecon-tmp", SearchOption.AllDirectories);
                foreach (var dir in tempDirs)
                {
                    var expectedTarget = dir[..^".reporecon-tmp".Length];
                    var created = Directory.GetCreationTimeUtc(dir);
                    residuals.Add(new ResidualTempFolder(dir, expectedTarget, created));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to scan for residual temp folders in {Root}", root);
            }
        }

        return residuals;
    }

    public bool CleanResidualTempFolder(string tempPath)
    {
        if (!tempPath.EndsWith(".reporecon-tmp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return SafeDeleteDirectory(tempPath);
    }

    private static bool SafeDeleteDirectory(string path)
    {
        var extPath = ToExtendedLengthPath(path);
        if (!Directory.Exists(extPath) && !Directory.Exists(path)) return true;

        try
        {
            // Remove readonly flags before deleting
            var dirInfo = new DirectoryInfo(extPath);
            foreach (var file in dirInfo.GetFiles("*", SearchOption.AllDirectories))
            {
                file.Attributes = FileAttributes.Normal;
            }
            Directory.Delete(extPath, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ExtractHost(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return uri.Host;
            }
            if (url.Contains('@') && url.Contains(':'))
            {
                var at = url.IndexOf('@');
                var colon = url.IndexOf(':', at);
                if (colon > at) return url.Substring(at + 1, colon - at - 1);
            }
        }
        catch
        {
        }
        return "default";
    }
}
