using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoRecon.Core.Concurrency;
using RepoRecon.Core.Data;
using RepoRecon.Core.Fetch;
using RepoRecon.Core.Git;
using RepoRecon.Core.Models;
using RepoRecon.Core.Security;

namespace RepoRecon.Core.Push;

public class PushEngine : IPushEngine
{
    private const int DefaultMaxConcurrency = 4;
    private const int DefaultMaxConcurrencyPerHost = 4;
    private const int DefaultMaxLfsConcurrency = 1;
    private static readonly TimeSpan DefaultPushTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DryRunTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

    private readonly IGitRunner _gitRunner;
    private readonly SqliteIndexStore _indexStore;
    private readonly IRepoLockManager _lockManager;
    private readonly IFetchEngine? _fetchEngine;
    private readonly ISecureTokenStore? _tokenStore;
    private readonly ILogger<PushEngine> _logger;

    private readonly SemaphoreSlim _networkPool;
    private readonly SemaphoreSlim _lfsPool;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostPools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _activeByHost = new(StringComparer.OrdinalIgnoreCase);
    private int _activeNetworkOperations;
    private int _activeLfsOperations;

    private readonly int _maxConcurrency;
    private readonly int _maxConcurrencyPerHost;
    private readonly int _maxLfsConcurrency;
    private readonly TimeSpan _pushTimeout;

    private int _isExecutingFlag;
    public bool IsExecuting => Volatile.Read(ref _isExecutingFlag) == 1;

    public PushEngine(
        IGitRunner gitRunner,
        SqliteIndexStore indexStore,
        IRepoLockManager? lockManager = null,
        IFetchEngine? fetchEngine = null,
        ISecureTokenStore? tokenStore = null,
        int maxConcurrency = DefaultMaxConcurrency,
        int maxConcurrencyPerHost = DefaultMaxConcurrencyPerHost,
        int maxLfsConcurrency = DefaultMaxLfsConcurrency,
        TimeSpan? pushTimeout = null,
        ILogger<PushEngine>? logger = null)
    {
        _gitRunner = gitRunner;
        _indexStore = indexStore;
        _lockManager = lockManager ?? new RepoLockManager();
        _fetchEngine = fetchEngine;
        _tokenStore = tokenStore;
        _maxConcurrency = maxConcurrency > 0 ? maxConcurrency : DefaultMaxConcurrency;
        _maxConcurrencyPerHost = maxConcurrencyPerHost > 0 ? maxConcurrencyPerHost : DefaultMaxConcurrencyPerHost;
        _maxLfsConcurrency = maxLfsConcurrency > 0 ? maxLfsConcurrency : DefaultMaxLfsConcurrency;
        _pushTimeout = pushTimeout ?? DefaultPushTimeout;
        _logger = logger ?? NullLogger<PushEngine>.Instance;

        _networkPool = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
        _lfsPool = new SemaphoreSlim(_maxLfsConcurrency, _maxLfsConcurrency);
    }

    public async Task<IReadOnlyList<PushPreviewItem>> GeneratePreviewAsync(
        IEnumerable<(LocalRepo Repo, LocalStatus? Status)> targets,
        CancellationToken cancellationToken = default)
    {
        var targetList = targets.ToList();
        var list = new List<PushPreviewItem>();

        // Cache profiles for token lookup
        var profiles = await _indexStore.GetServerProfilesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var (repo, status) in targetList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hostKey = !string.IsNullOrWhiteSpace(repo.NormalizedKey)
                ? FetchEngine.ExtractHostKey(repo.NormalizedKey)
                : "local";

            var (isInitialEligible, initialSkipReason) = EvaluateEligibility(repo, status, hostKey);

            var branch = !string.IsNullOrWhiteSpace(status?.Branch) && status.Branch != "(detached)"
                ? status.Branch
                : null;

            var dirtyWarning = (status?.DirtyCount ?? 0) > 0
                ? $"미커밋 {status!.DirtyCount}개 파일은 제외됨"
                : null;

            var command = isInitialEligible && !string.IsNullOrEmpty(branch)
                ? $"git -c core.quotepath=false -c i18n.logOutputEncoding=UTF-8 push origin {branch}"
                : string.Empty;

            if (!isInitialEligible || string.IsNullOrEmpty(branch))
            {
                list.Add(new PushPreviewItem(
                    repo.Id,
                    repo.AbsPath,
                    repo.NormalizedKey,
                    status?.Branch,
                    status?.Upstream,
                    status?.Ahead ?? 0,
                    status?.Behind ?? 0,
                    status?.DirtyCount ?? 0,
                    status?.ConflictedCount ?? 0,
                    command,
                    IsEligible: false,
                    SkipReason: initialSkipReason ?? "Push 불가 상태",
                    repo.HasLfs,
                    hostKey,
                    DryRunStatus: "미실행",
                    ServerRejectionMessage: null,
                    DirtyWarning: dirtyWarning
                ));
                continue;
            }

            // Run Live git push --dry-run origin <branch>
            var envVars = await GetEnvironmentVariablesForHostAsync(hostKey, profiles, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Executing git push --dry-run for {Path} on branch {Branch}", repo.AbsPath, branch);

            var dryRunResult = await _gitRunner.ExecuteAsync(
                repo.AbsPath,
                $"push --dry-run origin {branch}",
                DryRunTimeout,
                cancellationToken,
                envVars).ConfigureAwait(false);

            if (dryRunResult.IsSuccess)
            {
                list.Add(new PushPreviewItem(
                    repo.Id,
                    repo.AbsPath,
                    repo.NormalizedKey,
                    status?.Branch,
                    status?.Upstream,
                    status?.Ahead ?? 0,
                    status?.Behind ?? 0,
                    status?.DirtyCount ?? 0,
                    status?.ConflictedCount ?? 0,
                    command,
                    IsEligible: true,
                    SkipReason: null,
                    repo.HasLfs,
                    hostKey,
                    DryRunStatus: "통과",
                    ServerRejectionMessage: null,
                    DirtyWarning: dirtyWarning
                ));
            }
            else
            {
                var stderr = dryRunResult.StandardError.Trim();
                var cleanedMsg = CleanServerRejectionMessage(stderr);
                _logger.LogWarning("Push dry-run rejected for {Path}: {Error}", repo.AbsPath, stderr);

                list.Add(new PushPreviewItem(
                    repo.Id,
                    repo.AbsPath,
                    repo.NormalizedKey,
                    status?.Branch,
                    status?.Upstream,
                    status?.Ahead ?? 0,
                    status?.Behind ?? 0,
                    status?.DirtyCount ?? 0,
                    status?.ConflictedCount ?? 0,
                    command,
                    IsEligible: false,
                    SkipReason: $"서버 거부: {cleanedMsg}",
                    repo.HasLfs,
                    hostKey,
                    DryRunStatus: "거부",
                    ServerRejectionMessage: cleanedMsg,
                    DirtyWarning: dirtyWarning
                ));
            }
        }

        return list;
    }

    public async Task<BatchPushResult> ExecuteAsync(
        IReadOnlyList<PushPreviewItem> targets,
        IProgress<BatchPushProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _isExecutingFlag, 1, 0) != 0)
        {
            throw new InvalidOperationException("다른 Push 작업이 이미 실행 중입니다.");
        }

        var stopwatch = Stopwatch.StartNew();
        var results = new ConcurrentBag<PushExecutionItemResult>();
        var totalCount = targets.Count;
        var processedCount = 0;
        var successCount = 0;
        var failedCount = 0;
        var skippedCount = 0;

        var profiles = await _indexStore.GetServerProfilesAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var tasks = targets.Select(async item =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!item.IsEligible)
                {
                    Interlocked.Increment(ref processedCount);
                    Interlocked.Increment(ref skippedCount);
                    results.Add(new PushExecutionItemResult(
                        item.RepoId,
                        item.AbsPath,
                        Success: false,
                        PreHead: null,
                        PostHead: null,
                        ErrorMessage: item.SkipReason ?? "스킵 대상",
                        ErrorCategory: null,
                        DirtyWarning: item.DirtyWarning,
                        Duration: TimeSpan.Zero
                    ));
                    ReportProgress();
                    return;
                }

                // Acquire per-repo lock
                using var repoLock = await _lockManager.AcquireLockAsync(item.AbsPath, cancellationToken).ConfigureAwait(false);

                // Acquire throttles
                using var throttle = await AcquireThrottlesAsync(item.HostKey, item.HasLfs, cancellationToken).ConfigureAwait(false);

                var itemSw = Stopwatch.StartNew();
                try
                {
                    var envVars = await GetEnvironmentVariablesForHostAsync(item.HostKey, profiles, cancellationToken).ConfigureAwait(false);

                    // 1. Pre-push Re-evaluation & Fetch
                    if (_fetchEngine != null && !string.IsNullOrWhiteSpace(item.NormalizedKey))
                    {
                        var fetchResult = await _gitRunner.ExecuteAsync(
                            item.AbsPath,
                            "fetch --prune origin",
                            FetchTimeout,
                            cancellationToken,
                            envVars).ConfigureAwait(false);

                        if (!fetchResult.IsSuccess)
                        {
                            _logger.LogWarning("Pre-push fetch failed for {Path}: {Error}", item.AbsPath, fetchResult.StandardError);
                        }
                    }

                    // 2. Re-read local status
                    var statusResult = await _gitRunner.ExecuteAsync(
                        item.AbsPath,
                        "status --porcelain=v2 --branch",
                        StatusTimeout,
                        cancellationToken).ConfigureAwait(false);

                    var currentStatus = GitStatusParser.Parse(item.RepoId, statusResult.StandardOutput, hasRemote: true);
                    var (reEligible, reSkipReason) = EvaluateEligibilityDirect(currentStatus, item.HostKey);

                    if (!reEligible)
                    {
                        Interlocked.Increment(ref processedCount);
                        Interlocked.Increment(ref skippedCount);
                        results.Add(new PushExecutionItemResult(
                            item.RepoId,
                            item.AbsPath,
                            Success: false,
                            PreHead: currentStatus.HeadHash,
                            PostHead: currentStatus.HeadHash,
                            ErrorMessage: $"실행 직전 상태 변경으로 스킵: {reSkipReason}",
                            ErrorCategory: null,
                            DirtyWarning: item.DirtyWarning,
                            Duration: itemSw.Elapsed
                        ));
                        ReportProgress();
                        return;
                    }

                    var branch = currentStatus.Branch;
                    if (string.IsNullOrWhiteSpace(branch) || branch == "(detached)")
                    {
                        Interlocked.Increment(ref processedCount);
                        Interlocked.Increment(ref skippedCount);
                        results.Add(new PushExecutionItemResult(
                            item.RepoId,
                            item.AbsPath,
                            Success: false,
                            PreHead: currentStatus.HeadHash,
                            PostHead: currentStatus.HeadHash,
                            ErrorMessage: "실행 직전 브랜치 미체크아웃 감지",
                            ErrorCategory: null,
                            DirtyWarning: item.DirtyWarning,
                            Duration: itemSw.Elapsed
                        ));
                        ReportProgress();
                        return;
                    }

                    // 3. Pre-push live dry-run confirmation
                    var dryRunCheck = await _gitRunner.ExecuteAsync(
                        item.AbsPath,
                        $"push --dry-run origin {branch}",
                        DryRunTimeout,
                        cancellationToken,
                        envVars).ConfigureAwait(false);

                    if (!dryRunCheck.IsSuccess)
                    {
                        var dryErr = CleanServerRejectionMessage(dryRunCheck.StandardError);
                        Interlocked.Increment(ref processedCount);
                        Interlocked.Increment(ref skippedCount);
                        results.Add(new PushExecutionItemResult(
                            item.RepoId,
                            item.AbsPath,
                            Success: false,
                            PreHead: currentStatus.HeadHash,
                            PostHead: currentStatus.HeadHash,
                            ErrorMessage: $"실행 직전 dry-run 거부: {dryErr}",
                            ErrorCategory: ErrorClassifier.Classify(dryRunCheck.StandardError),
                            DirtyWarning: item.DirtyWarning,
                            Duration: itemSw.Elapsed
                        ));
                        ReportProgress();
                        return;
                    }

                    // 4. Pre-push HEAD Recording (§7.6)
                    var preHead = currentStatus.HeadHash;
                    if (string.IsNullOrWhiteSpace(preHead))
                    {
                        var headCheck = await _gitRunner.ExecuteAsync(item.AbsPath, "rev-parse HEAD", TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                        preHead = headCheck.StandardOutput.Trim();
                    }

                    try
                    {
                        await _indexStore.InsertJobHistoryAsync(new JobHistoryEntry(
                            0,
                            "push",
                            item.AbsPath,
                            preHead,
                            null,
                            "Pending",
                            null,
                            DateTime.UtcNow
                        ), cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to record pre-push job history for {Path}. Aborting push.", item.AbsPath);
                        Interlocked.Increment(ref processedCount);
                        Interlocked.Increment(ref failedCount);
                        results.Add(new PushExecutionItemResult(
                            item.RepoId,
                            item.AbsPath,
                            Success: false,
                            PreHead: preHead,
                            PostHead: preHead,
                            ErrorMessage: $"HEAD 기록 실패로 Push 중단: {ex.Message}",
                            ErrorCategory: ErrorCategory.Path,
                            DirtyWarning: item.DirtyWarning,
                            Duration: itemSw.Elapsed
                        ));
                        ReportProgress();
                        return;
                    }

                    // 5. Execute git push origin <branch> (Strict argument: never --all, --mirror, --force)
                    _logger.LogInformation("Executing git push origin {Branch} for {Path}", branch, item.AbsPath);

                    var pushResult = await _gitRunner.ExecuteAsync(
                        item.AbsPath,
                        $"push origin {branch}",
                        _pushTimeout,
                        cancellationToken,
                        envVars).ConfigureAwait(false);

                    if (pushResult.IsSuccess)
                    {
                        var postHeadCheck = await _gitRunner.ExecuteAsync(item.AbsPath, "rev-parse HEAD", TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                        var postHead = postHeadCheck.StandardOutput.Trim();

                        try
                        {
                            await _indexStore.InsertJobHistoryAsync(new JobHistoryEntry(
                                0,
                                "push",
                                item.AbsPath,
                                preHead,
                                postHead,
                                "Success",
                                null,
                                DateTime.UtcNow
                            ), CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to record success job history for {Path}", item.AbsPath);
                        }

                        // Refresh and update SQLite LocalStatus immediately (§8.2)
                        var postStatusResult = await _gitRunner.ExecuteAsync(item.AbsPath, "status --porcelain=v2 --branch", StatusTimeout, cancellationToken).ConfigureAwait(false);
                        var parsedPostStatus = GitStatusParser.Parse(item.RepoId, postStatusResult.StandardOutput, hasRemote: true) with { LastFetchedAt = DateTime.UtcNow };
                        await _indexStore.UpsertLocalStatusAsync(parsedPostStatus, cancellationToken).ConfigureAwait(false);

                        Interlocked.Increment(ref processedCount);
                        Interlocked.Increment(ref successCount);
                        results.Add(new PushExecutionItemResult(
                            item.RepoId,
                            item.AbsPath,
                            Success: true,
                            PreHead: preHead,
                            PostHead: postHead,
                            ErrorMessage: null,
                            ErrorCategory: null,
                            DirtyWarning: item.DirtyWarning,
                            Duration: itemSw.Elapsed
                        ));
                    }
                    else
                    {
                        var category = ErrorClassifier.Classify(pushResult.StandardError);
                        var categoryStr = category.ToString();

                        try
                        {
                            await _indexStore.InsertJobHistoryAsync(new JobHistoryEntry(
                                0,
                                "push",
                                item.AbsPath,
                                preHead,
                                preHead,
                                "Failed",
                                categoryStr,
                                DateTime.UtcNow
                            ), CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to record failed job history for {Path}", item.AbsPath);
                        }

                        if (category == ErrorCategory.Authentication && _fetchEngine != null)
                        {
                            _fetchEngine.SetAuthenticationCooldown(item.HostKey, $"Push 인증 실패 ({category})");
                        }

                        Interlocked.Increment(ref processedCount);
                        Interlocked.Increment(ref failedCount);
                        results.Add(new PushExecutionItemResult(
                            item.RepoId,
                            item.AbsPath,
                            Success: false,
                            PreHead: preHead,
                            PostHead: preHead,
                            ErrorMessage: pushResult.StandardError.Trim(),
                            ErrorCategory: category,
                            DirtyWarning: item.DirtyWarning,
                            Duration: itemSw.Elapsed
                        ));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected exception during push for {Path}", item.AbsPath);
                    Interlocked.Increment(ref processedCount);
                    Interlocked.Increment(ref failedCount);
                    results.Add(new PushExecutionItemResult(
                        item.RepoId,
                        item.AbsPath,
                        Success: false,
                        PreHead: null,
                        PostHead: null,
                        ErrorMessage: ex.Message,
                        ErrorCategory: ErrorCategory.Other,
                        DirtyWarning: item.DirtyWarning,
                        Duration: itemSw.Elapsed
                    ));
                }
                finally
                {
                    ReportProgress();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _isExecutingFlag, 0);
        }

        return new BatchPushResult(
            totalCount,
            successCount,
            failedCount,
            skippedCount,
            results.OrderBy(r => r.AbsPath).ToList(),
            stopwatch.Elapsed
        );

        void ReportProgress()
        {
            var p = new BatchPushProgress(
                totalCount,
                Volatile.Read(ref processedCount),
                Volatile.Read(ref successCount),
                Volatile.Read(ref failedCount),
                Volatile.Read(ref skippedCount),
                string.Empty,
                $"Push 진행 중 ({processedCount}/{totalCount}): 성공 {successCount} / 실패 {failedCount} / 스킵 {skippedCount}"
            );
            progress?.Report(p);
        }
    }

    public (bool IsEligible, string? SkipReason) EvaluateEligibility(LocalRepo repo, LocalStatus? status, string hostKey)
    {
        if (repo.IsMissing)
            return (false, "디스크에서 저장소를 찾을 수 없음");

        if (repo.GitDirKind != GitDirKind.Normal)
            return (false, "bare / worktree — 일괄 작업 대상 아님");

        if (status == null)
            return (false, "상태 미수집");

        return EvaluateEligibilityDirect(status, hostKey);
    }

    private (bool IsEligible, string? SkipReason) EvaluateEligibilityDirect(LocalStatus status, string hostKey)
    {
        if (status.ConflictedCount > 0 || status.StateCode == RepoStateCode.Conflicted)
            return (false, "머지 충돌 — 해결 필요");

        if (status.StateCode == RepoStateCode.DetachedHead || status.Branch == "(detached)")
            return (false, "브랜치 미체크아웃");

        if (status.StateCode == RepoStateCode.NoUpstream || string.IsNullOrWhiteSpace(status.Upstream))
            return (false, "추적 브랜치 없음 — 개별 처리 필요");

        if (status.StateCode == RepoStateCode.UpstreamGone)
            return (false, "upstream이 서버에서 삭제됨");

        if (status.StateCode == RepoStateCode.Diverged)
            return (false, "pull 먼저 필요 — 서버가 거부함");

        if (status.StateCode == RepoStateCode.Behind)
            return (false, "pull 먼저 필요");

        if (status.StateCode == RepoStateCode.UpToDate || status.Ahead <= 0)
            return (false, "올릴 커밋 없음");

        if (status.StateCode != RepoStateCode.Ahead)
            return (false, $"올릴 커밋 없음 ({status.StateCode})");

        if (_fetchEngine != null && _fetchEngine.ActiveCooldowns.Any(c => string.Equals(c.Host, hostKey, StringComparison.OrdinalIgnoreCase)))
            return (false, "인증 쿨오프 중 — 5분 후 재시도 가능");

        return (true, null);
    }

    private async Task<IDictionary<string, string>?> GetEnvironmentVariablesForHostAsync(
        string hostKey,
        IEnumerable<ServerProfile> profiles,
        CancellationToken cancellationToken)
    {
        if (_tokenStore == null || string.IsNullOrWhiteSpace(hostKey))
            return null;

        var profile = profiles.FirstOrDefault(p =>
            string.Equals(p.Host, hostKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(FetchEngine.ExtractHostKey(p.Host), hostKey, StringComparison.OrdinalIgnoreCase) ||
            p.Aliases.Any(a => string.Equals(a, hostKey, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(FetchEngine.ExtractHostKey(a), hostKey, StringComparison.OrdinalIgnoreCase)));

        if (profile == null || string.IsNullOrWhiteSpace(profile.TokenRef))
            return null;

        var token = await _tokenStore.GetTokenAsync(profile.TokenRef, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var isGitLab = profile.ApiKind == ServerApiKind.GitLab;
        var headerValue = isGitLab ? $"PRIVATE-TOKEN: {token}" : $"Authorization: token {token}";

        return new Dictionary<string, string>
        {
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "http.extraHeader",
            ["GIT_CONFIG_VALUE_0"] = headerValue
        };
    }

    private static string CleanServerRejectionMessage(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return "원격 서버 거부";

        var lines = stderr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith("fatal: Could not read from remote repository", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (lines.Count == 0)
            return stderr.Trim();

        var hookLine = lines.FirstOrDefault(l => l.Contains("hook declined", StringComparison.OrdinalIgnoreCase) ||
                                                 l.Contains("pre-receive", StringComparison.OrdinalIgnoreCase) ||
                                                 l.Contains("protected branch", StringComparison.OrdinalIgnoreCase) ||
                                                 l.Contains("rejected", StringComparison.OrdinalIgnoreCase) ||
                                                 l.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase));

        return hookLine ?? lines.First();
    }

    private async Task<IDisposable> AcquireThrottlesAsync(string hostKey, bool hasLfs, CancellationToken cancellationToken)
    {
        var networkAcquired = false;
        var hostAcquired = false;
        var lfsAcquired = false;

        var hostPool = _hostPools.GetOrAdd(hostKey, _ => new SemaphoreSlim(_maxConcurrencyPerHost, _maxConcurrencyPerHost));

        try
        {
            await _networkPool.WaitAsync(cancellationToken).ConfigureAwait(false);
            networkAcquired = true;
            Interlocked.Increment(ref _activeNetworkOperations);

            await hostPool.WaitAsync(cancellationToken).ConfigureAwait(false);
            hostAcquired = true;
            _activeByHost.AddOrUpdate(hostKey, 1, (_, current) => current + 1);

            if (hasLfs)
            {
                await _lfsPool.WaitAsync(cancellationToken).ConfigureAwait(false);
                lfsAcquired = true;
                Interlocked.Increment(ref _activeLfsOperations);
            }

            return new ThrottleReleaser(() =>
            {
                if (lfsAcquired)
                {
                    Interlocked.Decrement(ref _activeLfsOperations);
                    _lfsPool.Release();
                }

                if (hostAcquired)
                {
                    _activeByHost.AddOrUpdate(hostKey, 0, (_, current) => Math.Max(0, current - 1));
                    hostPool.Release();
                }

                if (networkAcquired)
                {
                    Interlocked.Decrement(ref _activeNetworkOperations);
                    _networkPool.Release();
                }
            });
        }
        catch
        {
            if (lfsAcquired)
            {
                Interlocked.Decrement(ref _activeLfsOperations);
                _lfsPool.Release();
            }
            if (hostAcquired)
            {
                _activeByHost.AddOrUpdate(hostKey, 0, (_, current) => Math.Max(0, current - 1));
                hostPool.Release();
            }
            if (networkAcquired)
            {
                Interlocked.Decrement(ref _activeNetworkOperations);
                _networkPool.Release();
            }
            throw;
        }
    }

    private sealed class ThrottleReleaser : IDisposable
    {
        private Action? _releaseAction;

        public ThrottleReleaser(Action releaseAction)
        {
            _releaseAction = releaseAction;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _releaseAction, null)?.Invoke();
        }
    }
}
