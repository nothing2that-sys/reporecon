using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoRecon.Core.Concurrency;
using RepoRecon.Core.Data;
using RepoRecon.Core.Fetch;
using RepoRecon.Core.Git;
using RepoRecon.Core.Models;

namespace RepoRecon.Core.Pull;

public class PullEngine : IPullEngine
{
    private const int DefaultMaxConcurrency = 4;
    private const int DefaultMaxConcurrencyPerHost = 4;
    private const int DefaultMaxLfsConcurrency = 1;
    private static readonly TimeSpan DefaultPullTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

    private readonly IGitRunner _gitRunner;
    private readonly SqliteIndexStore _indexStore;
    private readonly IRepoLockManager _lockManager;
    private readonly IFetchEngine? _fetchEngine;
    private readonly ILogger<PullEngine> _logger;

    private readonly SemaphoreSlim _networkPool;
    private readonly SemaphoreSlim _lfsPool;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostPools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _activeByHost = new(StringComparer.OrdinalIgnoreCase);
    private int _activeNetworkOperations;
    private int _activeLfsOperations;

    private readonly int _maxConcurrency;
    private readonly int _maxConcurrencyPerHost;
    private readonly int _maxLfsConcurrency;
    private readonly TimeSpan _pullTimeout;

    private int _isExecutingFlag;
    public bool IsExecuting => Volatile.Read(ref _isExecutingFlag) == 1;

    public PullEngine(
        IGitRunner gitRunner,
        SqliteIndexStore indexStore,
        IRepoLockManager? lockManager = null,
        IFetchEngine? fetchEngine = null,
        int maxConcurrency = DefaultMaxConcurrency,
        int maxConcurrencyPerHost = DefaultMaxConcurrencyPerHost,
        int maxLfsConcurrency = DefaultMaxLfsConcurrency,
        TimeSpan? pullTimeout = null,
        ILogger<PullEngine>? logger = null)
    {
        _gitRunner = gitRunner;
        _indexStore = indexStore;
        _lockManager = lockManager ?? new RepoLockManager();
        _fetchEngine = fetchEngine;
        _maxConcurrency = maxConcurrency > 0 ? maxConcurrency : DefaultMaxConcurrency;
        _maxConcurrencyPerHost = maxConcurrencyPerHost > 0 ? maxConcurrencyPerHost : DefaultMaxConcurrencyPerHost;
        _maxLfsConcurrency = maxLfsConcurrency > 0 ? maxLfsConcurrency : DefaultMaxLfsConcurrency;
        _pullTimeout = pullTimeout ?? DefaultPullTimeout;
        _logger = logger ?? NullLogger<PullEngine>.Instance;

        _networkPool = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
        _lfsPool = new SemaphoreSlim(_maxLfsConcurrency, _maxLfsConcurrency);
    }

    public IReadOnlyList<PullPreviewItem> GeneratePreview(IEnumerable<(LocalRepo Repo, LocalStatus? Status)> targets)
    {
        var list = new List<PullPreviewItem>();
        foreach (var (repo, status) in targets)
        {
            var hostKey = !string.IsNullOrWhiteSpace(repo.NormalizedKey)
                ? FetchEngine.ExtractHostKey(repo.NormalizedKey)
                : "local";

            var (isEligible, skipReason) = EvaluateEligibility(repo, status, hostKey);
            var command = isEligible
                ? "git -c core.quotepath=false -c i18n.logOutputEncoding=UTF-8 pull --ff-only"
                : string.Empty;

            list.Add(new PullPreviewItem(
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
                isEligible,
                skipReason,
                repo.HasLfs,
                hostKey
            ));
        }

        return list;
    }

    public async Task<PullBatchReport> ExecuteBatchPullAsync(
        IEnumerable<(LocalRepo Repo, LocalStatus? Status)> targets,
        IProgress<PullProgressInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _isExecutingFlag, 1, 0) != 0)
        {
            throw new InvalidOperationException("A pull batch is already in progress.");
        }

        var stopwatch = Stopwatch.StartNew();
        var targetList = targets.ToList();
        var totalSelected = targetList.Count;
        var results = new ConcurrentBag<PullJobResult>();
        var completedCount = 0;
        var runningCount = 0;

        _logger.LogInformation("Starting pull batch for {Count} repositories", totalSelected);

        try
        {
            var tasks = targetList.Select(async target =>
            {
                var (repo, status) = target;
                var hostKey = !string.IsNullOrWhiteSpace(repo.NormalizedKey)
                    ? FetchEngine.ExtractHostKey(repo.NormalizedKey)
                    : "local";

                // Initial eligibility check
                var (isInitiallyEligible, initialSkipReason) = EvaluateEligibility(repo, status, hostKey);
                if (!isInitiallyEligible)
                {
                    var skippedResult = new PullJobResult(
                        repo.Id,
                        repo.AbsPath,
                        PullJobStatus.Skipped,
                        status?.HeadHash,
                        status?.HeadHash,
                        null,
                        initialSkipReason,
                        false,
                        TimeSpan.Zero,
                        hostKey);

                    results.Add(skippedResult);
                    var done = Interlocked.Increment(ref completedCount);
                    progress?.Report(new PullProgressInfo(totalSelected, done, Volatile.Read(ref runningCount), repo.Id, repo.AbsPath, PullJobStatus.Skipped, initialSkipReason));
                    return;
                }

                // Wait on pools
                var hostPool = _hostPools.GetOrAdd(hostKey, _ => new SemaphoreSlim(_maxConcurrencyPerHost, _maxConcurrencyPerHost));
                var throttlePool = repo.HasLfs ? _lfsPool : _networkPool;

                Interlocked.Increment(ref runningCount);
                progress?.Report(new PullProgressInfo(totalSelected, Volatile.Read(ref completedCount), Volatile.Read(ref runningCount), repo.Id, repo.AbsPath, PullJobStatus.Running, "대기열 진입"));

                try
                {
                    await throttlePool.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await hostPool.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            var globalNetActive = repo.HasLfs
                                ? Interlocked.Increment(ref _activeLfsOperations)
                                : Interlocked.Increment(ref _activeNetworkOperations);
                            var hostActive = _activeByHost.AddOrUpdate(hostKey, 1, (_, current) => current + 1);

                            _logger.LogInformation(
                                "PullConcurrency Host={Host} Lfs={IsLfs} GlobalNetActive={NetActive} GlobalLfsActive={LfsActive} HostActive={HostActive}",
                                hostKey, repo.HasLfs, _activeNetworkOperations, _activeLfsOperations, hostActive);

                            try
                            {
                                var result = await ExecuteSingleRepoPullAsync(repo, status, hostKey, progress, totalSelected, completedCount, runningCount, cancellationToken).ConfigureAwait(false);
                                results.Add(result);
                            }
                            finally
                            {
                                if (repo.HasLfs)
                                    Interlocked.Decrement(ref _activeLfsOperations);
                                else
                                    Interlocked.Decrement(ref _activeNetworkOperations);

                                _activeByHost.AddOrUpdate(hostKey, 0, (_, current) => Math.Max(0, current - 1));
                            }
                        }
                        finally
                        {
                            hostPool.Release();
                        }
                    }
                    finally
                    {
                        throttlePool.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    var canceledResult = new PullJobResult(
                        repo.Id,
                        repo.AbsPath,
                        PullJobStatus.Canceled,
                        status?.HeadHash,
                        status?.HeadHash,
                        "사용자에 의해 작업이 취소되었습니다.",
                        "취소됨",
                        false,
                        TimeSpan.Zero,
                        hostKey);

                    results.Add(canceledResult);

                    // Record cancellation in job_history (§7.6, Criterion 17)
                    try
                    {
                        await _indexStore.InsertJobHistoryAsync(new JobHistoryEntry(
                            0, "pull", repo.AbsPath, status?.HeadHash, status?.HeadHash, "Canceled", "UserCanceled", DateTime.UtcNow), CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to record canceled job history for {Path}", repo.AbsPath);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref runningCount);
                    var done = Interlocked.Increment(ref completedCount);
                    var lastResult = results.FirstOrDefault(r => r.RepoId == repo.Id);
                    progress?.Report(new PullProgressInfo(
                        totalSelected,
                        done,
                        Volatile.Read(ref runningCount),
                        repo.Id,
                        repo.AbsPath,
                        lastResult?.Status ?? PullJobStatus.Running,
                        lastResult?.ErrorMessage ?? lastResult?.SkipReason ?? "완료"));
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            stopwatch.Stop();
            Interlocked.Exchange(ref _isExecutingFlag, 0);
        }

        var resultList = results.OrderBy(r => r.AbsPath).ToList();
        var successCount = resultList.Count(r => r.Status == PullJobStatus.Success);
        var failedCount = resultList.Count(r => r.Status == PullJobStatus.Failed);
        var skippedCount = resultList.Count(r => r.Status == PullJobStatus.Skipped);
        var reevaluatedOutCount = resultList.Count(r => r.WasReevaluatedOut);
        var canceledCount = resultList.Count(r => r.Status == PullJobStatus.Canceled);
        var executedCount = successCount + failedCount;

        var reevalSummary = reevaluatedOutCount > 0 ? $" (재판정 제외: {reevaluatedOutCount}건)" : string.Empty;
        var summaryText = $"일괄 Pull 완료: 총 {totalSelected}개 중 성공 {successCount}개, 실패 {failedCount}개, 스킵 {skippedCount}개{reevalSummary}, 취소 {canceledCount}개 (소요시간: {stopwatch.Elapsed.TotalSeconds:F2}초)";

        _logger.LogInformation(
            "PullBatchCompleted TotalSelected={Total} Executed={Executed} Success={Success} Failed={Failed} Skipped={Skipped} ReevaluatedOut={ReevaluatedOut} Canceled={Canceled} DurationMs={DurationMs}",
            totalSelected, executedCount, successCount, failedCount, skippedCount, reevaluatedOutCount, canceledCount, stopwatch.ElapsedMilliseconds);

        return new PullBatchReport(
            totalSelected,
            executedCount,
            successCount,
            failedCount,
            skippedCount,
            reevaluatedOutCount,
            canceledCount,
            stopwatch.Elapsed,
            resultList,
            summaryText
        );
    }

    private async Task<PullJobResult> ExecuteSingleRepoPullAsync(
        LocalRepo repo,
        LocalStatus? initialStatus,
        string hostKey,
        IProgress<PullProgressInfo>? progress,
        int totalCount,
        int completedCount,
        int runningCount,
        CancellationToken cancellationToken)
    {
        var itemStopwatch = Stopwatch.StartNew();

        // 1. Acquire repo-level lock (§7.2, Criterion 15)
        using var repoLock = await _lockManager.AcquireLockAsync(repo.AbsPath, cancellationToken).ConfigureAwait(false);

        progress?.Report(new PullProgressInfo(totalCount, completedCount, runningCount, repo.Id, repo.AbsPath, PullJobStatus.Running, "실행 직전 재판정 중..."));

        // 2. Pre-execution Re-evaluation (§5-4, §8.1, Criterion 13)
        // Run fetch and re-parse status to ensure freshness
        var fetchResult = await _gitRunner.ExecuteAsync(repo.AbsPath, "fetch --prune", FetchTimeout, cancellationToken).ConfigureAwait(false);
        if (!fetchResult.IsSuccess)
        {
            itemStopwatch.Stop();
            var error = string.IsNullOrWhiteSpace(fetchResult.StandardError)
                ? $"Fetch failed with exit code {fetchResult.ExitCode}"
                : fetchResult.StandardError;
            var errorCategory = ClassifyError(error);

            _logger.LogWarning("Fetch during pre-execution re-evaluation failed for {Path}: {Error}", repo.AbsPath, error);

            try
            {
                await _indexStore.InsertJobHistoryAsync(new JobHistoryEntry(
                    0,
                    "pull",
                    repo.AbsPath,
                    initialStatus?.HeadHash,
                    initialStatus?.HeadHash,
                    "Failed",
                    errorCategory,
                    DateTime.UtcNow
                ), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record failed fetch history for {Path}", repo.AbsPath);
            }

            return new PullJobResult(
                repo.Id,
                repo.AbsPath,
                PullJobStatus.Failed,
                initialStatus?.HeadHash,
                initialStatus?.HeadHash,
                error,
                errorCategory,
                false,
                itemStopwatch.Elapsed,
                hostKey);
        }

        var statusResult = await _gitRunner.ExecuteAsync(repo.AbsPath, "status --porcelain=v2 --branch", StatusTimeout, cancellationToken).ConfigureAwait(false);

        LocalStatus reevaluatedStatus;
        if (statusResult.IsSuccess)
        {
            reevaluatedStatus = GitStatusParser.Parse(repo.Id, statusResult.StandardOutput, hasRemote: true) with { LastFetchedAt = DateTime.UtcNow };
            await _indexStore.UpsertLocalStatusAsync(reevaluatedStatus, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            reevaluatedStatus = initialStatus ?? new LocalStatus(repo.Id, null, null, 0, 0, 0, 0, null, RepoStateCode.UnknownUnfetched, DateTime.UtcNow);
        }

        var (isStillEligible, reevalSkipReason) = EvaluateEligibility(repo, reevaluatedStatus, hostKey);
        if (!isStillEligible)
        {
            itemStopwatch.Stop();
            var skipReasonWithDetail = $"선택 후 상태 변경 ({reevalSkipReason})";
            _logger.LogInformation("Repo {Path} re-evaluated out: {Reason}", repo.AbsPath, skipReasonWithDetail);

            return new PullJobResult(
                repo.Id,
                repo.AbsPath,
                PullJobStatus.Skipped,
                reevaluatedStatus.HeadHash,
                reevaluatedStatus.HeadHash,
                null,
                skipReasonWithDetail,
                true,
                itemStopwatch.Elapsed,
                hostKey);
        }

        // 3. Pre-execution HEAD Recording (§7.6, Criterion 9, 10)
        var preHead = reevaluatedStatus.HeadHash;
        if (string.IsNullOrWhiteSpace(preHead))
        {
            var headResult = await _gitRunner.ExecuteAsync(repo.AbsPath, "rev-parse HEAD", StatusTimeout, cancellationToken).ConfigureAwait(false);
            if (headResult.IsSuccess && !string.IsNullOrWhiteSpace(headResult.StandardOutput))
            {
                preHead = headResult.StandardOutput.Trim();
            }
        }

        try
        {
            // Try recording pre-execution HEAD
            await _indexStore.InsertJobHistoryAsync(new JobHistoryEntry(
                0,
                "pull",
                repo.AbsPath,
                preHead,
                null,
                "Pending",
                null,
                DateTime.UtcNow
            ), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // CRITICAL SAFETY RULE (§7.6, Criterion 10):
            // If HEAD recording fails, DO NOT pull!
            itemStopwatch.Stop();
            _logger.LogError(ex, "CRITICAL: Pre-execution HEAD recording failed for {Path}. Aborting pull.", repo.AbsPath);
            return new PullJobResult(
                repo.Id,
                repo.AbsPath,
                PullJobStatus.Failed,
                preHead,
                null,
                "HEAD 기록 실패 — 복구 근거 확보 실패로 안전을 위해 Pull 중단",
                "HEAD 기록 실패",
                false,
                itemStopwatch.Elapsed,
                hostKey);
        }

        progress?.Report(new PullProgressInfo(totalCount, completedCount, runningCount, repo.Id, repo.AbsPath, PullJobStatus.Running, "git pull --ff-only 실행 중..."));

        // 4. Pull Execution (§5-1, §8.1)
        _logger.LogInformation("Executing git pull --ff-only on {Path} (PreHEAD={PreHead})", repo.AbsPath, preHead);
        var pullCmdResult = await _gitRunner.ExecuteAsync(repo.AbsPath, "pull --ff-only", _pullTimeout, cancellationToken).ConfigureAwait(false);

        itemStopwatch.Stop();
        var now = DateTime.UtcNow;

        if (pullCmdResult.IsSuccess)
        {
            // Fetch post-execution status & HEAD
            var postStatusCmd = await _gitRunner.ExecuteAsync(repo.AbsPath, "status --porcelain=v2 --branch", StatusTimeout, cancellationToken).ConfigureAwait(false);
            string? postHead = null;
            if (postStatusCmd.IsSuccess)
            {
                var postStatus = GitStatusParser.Parse(repo.Id, postStatusCmd.StandardOutput, hasRemote: true) with { LastFetchedAt = now };
                postHead = postStatus.HeadHash;
                await _indexStore.UpsertLocalStatusAsync(postStatus, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var postHeadCmd = await _gitRunner.ExecuteAsync(repo.AbsPath, "rev-parse HEAD", StatusTimeout, cancellationToken).ConfigureAwait(false);
                if (postHeadCmd.IsSuccess) postHead = postHeadCmd.StandardOutput.Trim();
            }

            // Record Success in job_history (§7.6, Criterion 11)
            try
            {
                await _indexStore.InsertJobHistoryAsync(new JobHistoryEntry(
                    0,
                    "pull",
                    repo.AbsPath,
                    preHead,
                    postHead,
                    "Success",
                    null,
                    now
                ), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record post-execution Success history for {Path}", repo.AbsPath);
            }

            _logger.LogInformation("Pull succeeded for {Path} in {Duration}ms (PreHEAD={PreHead}, PostHEAD={PostHead})",
                repo.AbsPath, itemStopwatch.ElapsedMilliseconds, preHead, postHead);

            return new PullJobResult(
                repo.Id,
                repo.AbsPath,
                PullJobStatus.Success,
                preHead,
                postHead,
                null,
                null,
                false,
                itemStopwatch.Elapsed,
                hostKey);
        }
        else
        {
            var error = string.IsNullOrWhiteSpace(pullCmdResult.StandardError)
                ? $"Pull failed with exit code {pullCmdResult.ExitCode}"
                : pullCmdResult.StandardError;

            var errorCategory = ClassifyError(error);

            // If authentication failure, register cooldown
            if (errorCategory == "Authentication" && _fetchEngine is FetchEngine fe)
            {
                _logger.LogWarning("Authentication error detected during pull on host {Host}. Registering cooldown.", hostKey);
            }

            // Record Failed in job_history (§7.6, Criterion 11)
            try
            {
                await _indexStore.InsertJobHistoryAsync(new JobHistoryEntry(
                    0,
                    "pull",
                    repo.AbsPath,
                    preHead,
                    preHead,
                    "Failed",
                    errorCategory,
                    now
                ), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record post-execution Failed history for {Path}", repo.AbsPath);
            }

            _logger.LogWarning("Pull failed for {Path} in {Duration}ms. Category={Category}, Error={Error}",
                repo.AbsPath, itemStopwatch.ElapsedMilliseconds, errorCategory, error);

            return new PullJobResult(
                repo.Id,
                repo.AbsPath,
                PullJobStatus.Failed,
                preHead,
                preHead,
                error,
                errorCategory,
                false,
                itemStopwatch.Elapsed,
                hostKey);
        }
    }

    private (bool IsEligible, string? SkipReason) EvaluateEligibility(LocalRepo repo, LocalStatus? status, string hostKey)
    {
        if (repo.IsMissing)
        {
            return (false, "디스크에서 발견되지 않음");
        }

        if (repo.GitDirKind != GitDirKind.Normal)
        {
            return (false, "일괄 작업 대상 아님 (Bare/Worktree)");
        }

        if (status == null || status.StateCode == RepoStateCode.UnknownUnfetched || status.LastFetchedAt == null)
        {
            return (false, "상태 미확인 (Fetch 필요)");
        }

        if (status.ConflictedCount > 0 || status.StateCode == RepoStateCode.Conflicted)
        {
            return (false, status.ConflictedCount > 0 ? $"머지 충돌 {status.ConflictedCount}개 — 수동 해결 필요" : "머지 충돌 — 수동 해결 필요");
        }

        if (status.DirtyCount > 0)
        {
            return (false, $"미커밋 변경 {status.DirtyCount}개");
        }

        if (status.StateCode == RepoStateCode.Diverged)
        {
            return (false, "pull 먼저 필요 — 수동 병합");
        }

        if (status.StateCode == RepoStateCode.NoUpstream)
        {
            return (false, "추적 브랜치 없음");
        }

        if (status.StateCode == RepoStateCode.UpstreamGone)
        {
            return (false, "upstream이 서버에서 삭제됨");
        }

        if (status.StateCode == RepoStateCode.DetachedHead)
        {
            return (false, "브랜치 미체크아웃");
        }

        if (status.StateCode == RepoStateCode.Untracked)
        {
            return (false, "remote 없음");
        }

        if (status.StateCode == RepoStateCode.UpToDate)
        {
            return (false, "이미 최신");
        }

        if (status.StateCode == RepoStateCode.Ahead)
        {
            return (false, "pull 불필요");
        }

        // Check if host is under cooldown in FetchEngine
        if (_fetchEngine != null && _fetchEngine.ActiveCooldowns.Any(c => string.Equals(c.Host, hostKey, StringComparison.OrdinalIgnoreCase)))
        {
            return (false, "인증 실패 쿨오프 중");
        }

        if (status.Behind > 0 || status.StateCode == RepoStateCode.Behind)
        {
            return (true, null);
        }

        return (false, "Pull 대상 아님");
    }

    private static string ClassifyError(string error) => ErrorClassifier.ClassifyToString(error);

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
}
