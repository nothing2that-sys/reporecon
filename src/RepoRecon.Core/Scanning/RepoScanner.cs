using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoRecon.Core.Data;
using RepoRecon.Core.Git;
using RepoRecon.Core.Models;
using RepoRecon.Core.Urls;

namespace RepoRecon.Core.Scanning;

public class RepoScanner
{
    public static readonly HashSet<string> DefaultExcludePatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules",
        ".venv",
        "venv",
        "dist",
        "build",
        "$RECYCLE.BIN",
        ".svn"
    };

    private readonly IGitRunner _gitRunner;
    private readonly SqliteIndexStore _indexStore;
    private readonly HostAliasTable _aliasTable;
    private readonly HashSet<string> _excludePatterns;
    private readonly ILogger<RepoScanner> _logger;

    public RepoScanner(
        IGitRunner gitRunner,
        SqliteIndexStore indexStore,
        HostAliasTable? aliasTable = null,
        IEnumerable<string>? customExcludePatterns = null,
        ILogger<RepoScanner>? logger = null)
    {
        _gitRunner = gitRunner;
        _indexStore = indexStore;
        _aliasTable = aliasTable ?? new HostAliasTable();
        _excludePatterns = customExcludePatterns != null
            ? new HashSet<string>(customExcludePatterns, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(DefaultExcludePatterns, StringComparer.OrdinalIgnoreCase);
        _logger = logger ?? NullLogger<RepoScanner>.Instance;
    }

    public async Task<ScanReport> ScanAsync(
        IReadOnlyList<ScanRoot> roots,
        IProgress<(string CurrentDirectory, int FoundCount)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var inaccessiblePaths = new List<string>();
        var discoveredRepoPaths = new List<string>();
        var visitedCanonicalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gitCallCount = 0;

        async Task<GitCommandResult> RunGitAsync(string workingDirectory, string arguments, TimeSpan timeout)
        {
            gitCallCount++;
            return await _gitRunner.ExecuteAsync(workingDirectory, arguments, timeout, cancellationToken).ConfigureAwait(false);
        }

        var existingReposWithStatus = await _indexStore.GetAllLocalReposWithStatusAsync(cancellationToken).ConfigureAwait(false);
        var existingRepoMap = existingReposWithStatus.ToDictionary(x => x.Repo.AbsPath, StringComparer.OrdinalIgnoreCase);

        // 1. Recursive directory walk across all active & online scan roots
        foreach (var root in roots.Where(r => r.IsActive && !r.IsOffline))
        {
            if (!Directory.Exists(root.Path))
                continue;

            await TraverseDirectoryAsync(
                root.Path,
                currentDepth: 0,
                maxDepth: root.MaxDepth,
                discoveredRepoPaths,
                inaccessiblePaths,
                visitedCanonicalPaths,
                progress,
                cancellationToken).ConfigureAwait(false);
        }

        // Sort discovered paths by length so parents appear before children
        discoveredRepoPaths.Sort((a, b) => a.Length.CompareTo(b.Length));

        var addedCount = 0;
        var updatedCount = 0;
        var unchangedCount = 0;

        // Path to DB ID map for parent-child relationship tracking
        var pathToIdMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (repo, _) in existingReposWithStatus)
        {
            pathToIdMap[repo.AbsPath] = repo.Id;
        }

        // 2. Process each discovered repo
        foreach (var repoPath in discoveredRepoPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report((repoPath, discoveredRepoPaths.Count));

            // Find closest parent repo (B-9: longest path)
            long? parentRepoId = null;
            var matchingParents = pathToIdMap
                .Where(p => ScanRootManager.IsSubdirectory(p.Key, repoPath) && !string.Equals(p.Key, repoPath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Key.Length)
                .ToList();

            if (matchingParents.Count > 0)
            {
                parentRepoId = matchingParents[0].Value;
            }

            var hasLfs = CheckHasLfs(repoPath);
            var hasSubmodule = File.Exists(Path.Combine(repoPath, ".gitmodules"));
            var gitDirKind = DetectGitDirKind(repoPath);

            var isNew = !existingRepoMap.TryGetValue(repoPath, out var existing);
            var existingRepo = existing.Repo;
            var existingStatus = existing.Status;

            // A-7 & C-2: Remote URL / normalized key caching.
            // If already indexed with a valid (non-null) key, use cached key to avoid redundant git config.
            // If NULL key (remote added later, local path, etc.), re-query on scan/refresh (C-2).
            string? normalizedKey;
            bool hasRemote;

            if (!isNew && existingRepo != null && existingRepo.NormalizedKey != null)
            {
                normalizedKey = existingRepo.NormalizedKey;
                hasRemote = true;
            }
            else
            {
                var originUrl = await QueryRemoteUrlAsync(repoPath, RunGitAsync).ConfigureAwait(false);
                hasRemote = !string.IsNullOrWhiteSpace(originUrl);
                normalizedKey = RemoteUrlNormalizer.Normalize(originUrl, _aliasTable);
            }

            var repoModel = new LocalRepo(
                Id: isNew || existingRepo == null ? 0 : existingRepo.Id,
                AbsPath: repoPath,
                NormalizedKey: normalizedKey,
                ParentRepoId: parentRepoId,
                GitDirKind: gitDirKind,
                HasLfs: hasLfs,
                HasSubmodule: hasSubmodule,
                IsMissing: false,
                MissingSince: null);

            var repoId = await _indexStore.UpsertLocalRepoAsync(repoModel, cancellationToken).ConfigureAwait(false);
            pathToIdMap[repoPath] = repoId;

            // C-1: Bare and worktree repos do not call git status --porcelain=v2 --branch (avoids failure in non-worktrees)
            LocalStatus parsedStatus;
            if (gitDirKind == GitDirKind.Bare || gitDirKind == GitDirKind.Worktree)
            {
                parsedStatus = new LocalStatus(
                    LocalRepoId: repoId,
                    Branch: null,
                    Upstream: null,
                    Ahead: 0,
                    Behind: 0,
                    DirtyCount: 0,
                    ConflictedCount: 0,
                    HeadHash: null,
                    StateCode: hasRemote ? RepoStateCode.UnknownUnfetched : RepoStateCode.Untracked,
                    LastFetchedAt: null);
            }
            else
            {
                // Fetch porcelain=v2 status (single git call per repo, A-7)
                var statusResult = await RunGitAsync(repoPath, "status --porcelain=v2 --branch", TimeSpan.FromSeconds(15)).ConfigureAwait(false);

                if (statusResult.IsSuccess)
                {
                    parsedStatus = GitStatusParser.Parse(repoId, statusResult.StandardOutput, hasRemote);
                }
                else
                {
                    // B-11: Git status execution failure recorded explicitly
                    parsedStatus = new LocalStatus(
                        LocalRepoId: repoId,
                        Branch: null,
                        Upstream: null,
                        Ahead: 0,
                        Behind: 0,
                        DirtyCount: 0,
                        ConflictedCount: 0,
                        HeadHash: null,
                        StateCode: RepoStateCode.UnknownUnfetched,
                        LastFetchedAt: null);
                }
            }

            await _indexStore.UpsertLocalStatusAsync(parsedStatus, cancellationToken).ConfigureAwait(false);

            if (isNew || existingRepo == null)
            {
                addedCount++;
            }
            else if (existingRepo.IsMissing ||
                     existingStatus == null ||
                     existingStatus.Branch != parsedStatus.Branch ||
                     existingStatus.Upstream != parsedStatus.Upstream ||
                     existingStatus.Ahead != parsedStatus.Ahead ||
                     existingStatus.Behind != parsedStatus.Behind ||
                     existingStatus.DirtyCount != parsedStatus.DirtyCount ||
                     existingStatus.ConflictedCount != parsedStatus.ConflictedCount ||
                     existingStatus.HeadHash != parsedStatus.HeadHash ||
                     existingStatus.StateCode != parsedStatus.StateCode)
            {
                updatedCount++;
            }
            else
            {
                unchangedCount++;
            }
        }

        // 3. A-5: Preserve undiscovered repos (DO NOT DELETE). Mark is_missing instead.
        var activeRoots = roots.Where(r => r.IsActive && !r.IsOffline).ToList();
        var now = DateTime.UtcNow;
        var newlyMissingCount = 0;

        foreach (var (existingRepo, _) in existingReposWithStatus)
        {
            var isUnderActiveRoot = activeRoots.Any(root => ScanRootManager.IsSubdirectory(root.Path, existingRepo.AbsPath));
            if (isUnderActiveRoot && !discoveredRepoPaths.Contains(existingRepo.AbsPath, StringComparer.OrdinalIgnoreCase))
            {
                // Check if this repo is inside an inaccessible directory subtree (A-5)
                var wasUnderInaccessibleFolder = inaccessiblePaths.Any(inaccessible => ScanRootManager.IsSubdirectory(inaccessible, existingRepo.AbsPath));
                if (!wasUnderInaccessibleFolder && !existingRepo.IsMissing)
                {
                    await _indexStore.MarkRepoMissingAsync(existingRepo.Id, now, cancellationToken).ConfigureAwait(false);
                    newlyMissingCount++;
                }
            }
        }

        // Update last scanned timestamp on roots
        foreach (var root in activeRoots)
        {
            await _indexStore.UpdateScanRootAsync(root with { LastScannedAt = now }, cancellationToken).ConfigureAwait(false);
        }

        stopwatch.Stop();

        // L-1: post-scan summary at INF so post-mortem diagnosis does not require DBG
        _logger.LogInformation(
            "Scan complete: discovered={Discovered} added={Added} updated={Updated} unchanged={Unchanged} newlyMissing={NewlyMissing} inaccessible={InaccessibleCount} gitCalls={GitCalls} durationMs={DurationMs}",
            discoveredRepoPaths.Count, addedCount, updatedCount, unchangedCount, newlyMissingCount, inaccessiblePaths.Count, gitCallCount, stopwatch.ElapsedMilliseconds);

        if (inaccessiblePaths.Count > 0)
        {
            _logger.LogInformation("Inaccessible paths ({Count}): {Paths}", inaccessiblePaths.Count, string.Join(" | ", inaccessiblePaths));
        }

        return new ScanReport(
            TotalDiscovered: discoveredRepoPaths.Count,
            AddedCount: addedCount,
            UpdatedCount: updatedCount,
            UnchangedCount: unchangedCount,
            InaccessiblePaths: inaccessiblePaths,
            Duration: stopwatch.Elapsed);
    }

    private async Task TraverseDirectoryAsync(
        string directoryPath,
        int currentDepth,
        int maxDepth,
        List<string> discoveredRepos,
        List<string> inaccessiblePaths,
        HashSet<string> visitedCanonicalPaths,
        IProgress<(string CurrentDirectory, int FoundCount)>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (currentDepth > maxDepth)
            return;

        string canonicalPath;
        DirectoryInfo dirInfo;
        try
        {
            dirInfo = new DirectoryInfo(directoryPath);
            canonicalPath = dirInfo.FullName.ToLowerInvariant();

            // A-6: Check if directory is a reparse point (junction / symlink)
            if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                _logger.LogDebug("Skipping reparse point directory: {Path}", directoryPath);
                return;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or PathTooLongException or IOException)
        {
            _logger.LogWarning("Inaccessible directory during scan: {Path} ({Message})", directoryPath, ex.Message);
            inaccessiblePaths.Add(directoryPath);
            return;
        }

        // A-6: Avoid cyclic duplicate visits
        if (!visitedCanonicalPaths.Add(canonicalPath))
        {
            return;
        }

        // Check if this directory is a git repository (Normal, Worktree, or Bare)
        var gitDirPath = Path.Combine(directoryPath, ".git");
        var isNormalOrWorktree = Directory.Exists(gitDirPath) || File.Exists(gitDirPath);
        var isBare = !isNormalOrWorktree &&
                     Directory.Exists(Path.Combine(directoryPath, "refs")) &&
                     Directory.Exists(Path.Combine(directoryPath, "objects")) &&
                     File.Exists(Path.Combine(directoryPath, "HEAD"));

        if (isNormalOrWorktree || isBare)
        {
            discoveredRepos.Add(directoryPath);
            progress?.Report((directoryPath, discoveredRepos.Count));
            // IMPORTANT: Do NOT return here if normal/worktree to allow discovering nested repositories.
            if (isBare)
                return;
        }

        // B-14: Wrap directory enumeration and foreach in try-catch for exception isolation
        try
        {
            foreach (var subDirInfo in dirInfo.EnumerateDirectories())
            {
                var dirName = subDirInfo.Name;

                if (string.Equals(dirName, ".git", StringComparison.OrdinalIgnoreCase) ||
                    _excludePatterns.Contains(dirName))
                {
                    continue;
                }

                await TraverseDirectoryAsync(
                    subDirInfo.FullName,
                    currentDepth + 1,
                    maxDepth,
                    discoveredRepos,
                    inaccessiblePaths,
                    visitedCanonicalPaths,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or PathTooLongException or IOException)
        {
            _logger.LogWarning("Inaccessible directory enumeration: {Path} ({Message})", directoryPath, ex.Message);
            inaccessiblePaths.Add(directoryPath);
        }
    }

    private static async Task<string?> QueryRemoteUrlAsync(string repoPath, Func<string, string, TimeSpan, Task<GitCommandResult>> runGit)
    {
        // 1. Try remote.origin.url
        var result = await runGit(repoPath, "config --get remote.origin.url", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return result.StandardOutput.Trim();
        }

        // 2. Try first remote from git remote -v
        var remoteResult = await runGit(repoPath, "remote -v", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (remoteResult.IsSuccess && !string.IsNullOrWhiteSpace(remoteResult.StandardOutput))
        {
            var lines = remoteResult.StandardOutput.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    return parts[1];
                }
            }
        }

        return null;
    }

    private static bool CheckHasLfs(string repoPath)
    {
        var attributesPath = Path.Combine(repoPath, ".gitattributes");
        if (File.Exists(attributesPath))
        {
            try
            {
                var content = File.ReadAllText(attributesPath);
                if (content.Contains("filter=lfs", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
            }
        }

        var gitLfsDir = Path.Combine(repoPath, ".git", "lfs");
        return Directory.Exists(gitLfsDir);
    }

    private static GitDirKind DetectGitDirKind(string repoPath)
    {
        var gitPath = Path.Combine(repoPath, ".git");
        if (File.Exists(gitPath))
            return GitDirKind.Worktree;

        if (Directory.Exists(Path.Combine(repoPath, "refs")) &&
            File.Exists(Path.Combine(repoPath, "HEAD")) &&
            !Directory.Exists(gitPath))
        {
            return GitDirKind.Bare;
        }

        return GitDirKind.Normal;
    }
}
