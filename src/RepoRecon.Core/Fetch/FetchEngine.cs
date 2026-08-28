using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoRecon.Core.Data;
using RepoRecon.Core.Git;
using RepoRecon.Core.Models;

namespace RepoRecon.Core.Fetch;

public class FetchEngine : IFetchEngine, IDisposable
{
    private const int DefaultMaxConcurrency = 4;
    private const int DefaultMaxConcurrencyPerHost = 4;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultAuthenticationCooldown = TimeSpan.FromMinutes(5);

    private readonly IGitRunner _gitRunner;
    private readonly SqliteIndexStore _indexStore;
    private readonly ILogger<FetchEngine> _logger;
    private readonly FetchPriorityQueue _queue = new();
    private readonly SemaphoreSlim _networkPool;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostPools = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly int _maxConcurrency;
    private readonly int _maxConcurrencyPerHost;
    private readonly TimeSpan _authenticationCooldown;
    private readonly object _workerLock = new();
    private readonly List<Task> _activeWorkerTasks = new();
    private readonly object _cooldownLock = new();
    private readonly Dictionary<string, HostFetchCooldown> _cooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _activeByHost = new(StringComparer.OrdinalIgnoreCase);
    private int _activeNetworkOperations;
    private int _runningWorkerCount;
    private bool _isDisposed;

    public event EventHandler<RepoFetchResult>? RepoFetched;
    public event EventHandler<bool>? FetchingStateChanged;
    public event EventHandler? CooldownsChanged;

    public bool IsFetching => _runningWorkerCount > 0 || !_queue.IsEmpty;
    public int PendingCount => _queue.Count;
    public IReadOnlyList<HostFetchCooldown> ActiveCooldowns
    {
        get
        {
            PruneExpiredCooldowns();
            lock (_cooldownLock)
            {
                return _cooldowns.Values.OrderBy(value => value.Host).ToList();
            }
        }
    }

    public FetchEngine(
        IGitRunner gitRunner,
        SqliteIndexStore indexStore,
        int maxConcurrency = DefaultMaxConcurrency,
        ILogger<FetchEngine>? logger = null,
        int maxConcurrencyPerHost = DefaultMaxConcurrencyPerHost,
        TimeSpan? authenticationCooldown = null)
    {
        if (maxConcurrency <= 0) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        if (maxConcurrencyPerHost <= 0) throw new ArgumentOutOfRangeException(nameof(maxConcurrencyPerHost));
        _gitRunner = gitRunner;
        _indexStore = indexStore;
        _maxConcurrency = maxConcurrency;
        _maxConcurrencyPerHost = maxConcurrencyPerHost;
        _authenticationCooldown = authenticationCooldown ?? DefaultAuthenticationCooldown;
        _networkPool = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _logger = logger ?? NullLogger<FetchEngine>.Instance;
    }

    public Task EnqueueReposAsync(
        IEnumerable<(long RepoId, string AbsPath, string NormalizedKey)> repos,
        CancellationToken cancellationToken = default)
    {
        var addedAny = false;
        foreach (var (repoId, absPath, normalizedKey) in repos)
        {
            if (string.IsNullOrWhiteSpace(normalizedKey))
            {
                _logger.LogDebug("Skipping fetch enqueue for repo {RepoId}: normalized key is null/empty", repoId);
                continue;
            }
            addedAny |= _queue.Enqueue(repoId, absPath, ExtractHostKey(normalizedKey));
        }

        if (addedAny)
        {
            EnsureWorkersStarted();
            FetchingStateChanged?.Invoke(this, true);
        }
        return Task.CompletedTask;
    }

    public void BoostVisibleRepos(IEnumerable<long> visibleRepoIds)
    {
        var visibleIds = visibleRepoIds.Distinct().ToList();
        var boostedCount = _queue.BoostPriority(visibleIds);
        _logger.LogInformation(
            "FetchViewportPriorityBoost VisibleRepoCount={VisibleRepoCount} BoostedQueuedCount={BoostedQueuedCount} PendingCount={PendingCount}",
            visibleIds.Count,
            boostedCount,
            _queue.Count);
    }

    public void ClearCooldowns()
    {
        var changed = false;
        lock (_cooldownLock)
        {
            changed = _cooldowns.Count > 0;
            _cooldowns.Clear();
        }
        if (changed)
        {
            _logger.LogInformation("All authentication cooldowns were explicitly cleared by the user");
            CooldownsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void EnsureWorkersStarted()
    {
        lock (_workerLock)
        {
            if (_isDisposed) return;
            _activeWorkerTasks.RemoveAll(task => task.IsCompleted);
            while (_activeWorkerTasks.Count < _maxConcurrency && !_queue.IsEmpty)
            {
                _activeWorkerTasks.Add(Task.Run(WorkerLoopAsync));
            }
        }
    }

    private async Task WorkerLoopAsync()
    {
        Interlocked.Increment(ref _runningWorkerCount);
        try
        {
            while (!_cts.IsCancellationRequested && _queue.TryDequeue(out var task))
            {
                _logger.LogInformation(
                    "FetchDequeued RepoId={RepoId} Path={Path} Host={Host} Priority={Priority}",
                    task.LocalRepoId, task.AbsPath, task.HostKey, task.Priority);
                await ProcessFetchTaskAsync(task, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "Unexpected exception in FetchEngine worker loop"); }
        finally
        {
            Interlocked.Decrement(ref _runningWorkerCount);
            lock (_workerLock)
            {
                _activeWorkerTasks.RemoveAll(task => task.IsCompleted);
                if (_runningWorkerCount == 0 && _queue.IsEmpty)
                    FetchingStateChanged?.Invoke(this, false);
            }
        }
    }

    private async Task ProcessFetchTaskAsync(FetchTask task, CancellationToken cancellationToken)
    {
        if (TryGetCooldown(task.HostKey, out var cooldown))
        {
            RepoFetched?.Invoke(this, CreateCooldownResult(task, cooldown));
            return;
        }

        var hostPool = _hostPools.GetOrAdd(task.HostKey, _ => new SemaphoreSlim(_maxConcurrencyPerHost, _maxConcurrencyPerHost));
        await _networkPool.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await hostPool.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (TryGetCooldown(task.HostKey, out cooldown))
                {
                    RepoFetched?.Invoke(this, CreateCooldownResult(task, cooldown));
                    return;
                }
                var globalActive = Interlocked.Increment(ref _activeNetworkOperations);
                var hostActive = _activeByHost.AddOrUpdate(task.HostKey, 1, (_, current) => current + 1);
                _logger.LogInformation(
                    "FetchConcurrency Host={Host} GlobalActive={GlobalActive} HostActive={HostActive} GlobalLimit={GlobalLimit} HostLimit={HostLimit}",
                    task.HostKey, globalActive, hostActive, _maxConcurrency, _maxConcurrencyPerHost);
                try
                {
                    var result = await FetchSingleRepoCoreAsync(task.LocalRepoId, task.AbsPath, task.HostKey, cancellationToken).ConfigureAwait(false);
                    if (result.FailureKind == FetchFailureKind.Authentication)
                        SetAuthenticationCooldown(task.HostKey, result.ErrorMessage ?? "인증 실패");
                    RepoFetched?.Invoke(this, result);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeNetworkOperations);
                    _activeByHost.AddOrUpdate(task.HostKey, 0, (_, current) => Math.Max(0, current - 1));
                }
            }
            finally { hostPool.Release(); }
        }
        finally { _networkPool.Release(); }
    }

    public Task<RepoFetchResult> FetchSingleRepoAsync(long repoId, string absPath, CancellationToken cancellationToken = default) =>
        FetchSingleRepoCoreAsync(repoId, absPath, hostKey: null, cancellationToken);

    private async Task<RepoFetchResult> FetchSingleRepoCoreAsync(long repoId, string absPath, string? hostKey, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("Starting fetch for repo {RepoId} at {Path} (Host={Host})", repoId, absPath, hostKey ?? "direct");
        try
        {
            var fetchResult = await _gitRunner.ExecuteAsync(absPath, "fetch --prune", FetchTimeout, cancellationToken).ConfigureAwait(false);
            if (!fetchResult.IsSuccess)
            {
                stopwatch.Stop();
                var error = string.IsNullOrWhiteSpace(fetchResult.StandardError)
                    ? $"Fetch failed with exit code {fetchResult.ExitCode}"
                    : fetchResult.StandardError;
                var kind = ClassifyFailure(error);
                _logger.LogWarning("Fetch failed for repo {RepoId} ({Path}), Host={Host}, Kind={Kind}: {Error}", repoId, absPath, hostKey ?? "direct", kind, error);
                return new RepoFetchResult(repoId, absPath, false, error, null, stopwatch.Elapsed, hostKey, kind);
            }

            var statusResult = await _gitRunner.ExecuteAsync(absPath, "status --porcelain=v2 --branch", StatusTimeout, cancellationToken).ConfigureAwait(false);
            var now = DateTime.UtcNow;
            LocalStatus updatedStatus;
            if (statusResult.IsSuccess)
            {
                updatedStatus = GitStatusParser.Parse(repoId, statusResult.StandardOutput, hasRemote: true) with { LastFetchedAt = now };
            }
            else
            {
                updatedStatus = new LocalStatus(repoId, null, null, 0, 0, 0, 0, null, RepoStateCode.UnknownUnfetched, now);
            }
            await _indexStore.UpsertLocalStatusAsync(updatedStatus, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            _logger.LogInformation("Fetch completed for repo {RepoId} ({Path}) in {Duration}ms: State={State}, Ahead={Ahead}, Behind={Behind}, Dirty={Dirty}",
                repoId, absPath, stopwatch.ElapsedMilliseconds, updatedStatus.StateCode, updatedStatus.Ahead, updatedStatus.Behind, updatedStatus.DirtyCount);
            return new RepoFetchResult(repoId, absPath, true, null, updatedStatus, stopwatch.Elapsed, hostKey);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new RepoFetchResult(repoId, absPath, false, "Fetch canceled.", null, stopwatch.Elapsed, hostKey, FetchFailureKind.Other);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Exception during fetch of repo {RepoId} ({Path})", repoId, absPath);
            return new RepoFetchResult(repoId, absPath, false, ex.Message, null, stopwatch.Elapsed, hostKey, FetchFailureKind.Other);
        }
    }

    private static FetchFailureKind ClassifyFailure(string error)
    {
        if (ContainsAny(error, "Authentication failed", "HTTP Basic: Access denied", "could not read Username", "terminal prompts disabled", "Permission denied (publickey)", "401 Unauthorized", "error: 401"))
            return FetchFailureKind.Authentication;
        if (ContainsAny(error, "Could not resolve host", "Failed to connect", "Connection refused", "Connection timed out", "Network is unreachable", "Execution timed out"))
            return FetchFailureKind.Network;
        return FetchFailureKind.Other;
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private void SetAuthenticationCooldown(string hostKey, string reason)
    {
        var firstLine = reason.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "인증 실패";
        var cooldown = new HostFetchCooldown(hostKey, firstLine, DateTime.UtcNow + _authenticationCooldown);
        lock (_cooldownLock) { _cooldowns[hostKey] = cooldown; }
        _logger.LogWarning("Authentication cooldown started for host {Host}. ExpiresAtUtc={ExpiresAtUtc:o}. Reason={Reason}", hostKey, cooldown.ExpiresAtUtc, cooldown.Reason);
        CooldownsChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool TryGetCooldown(string hostKey, out HostFetchCooldown cooldown)
    {
        PruneExpiredCooldowns();
        lock (_cooldownLock) { return _cooldowns.TryGetValue(hostKey, out cooldown!); }
    }

    private void PruneExpiredCooldowns()
    {
        var changed = false;
        lock (_cooldownLock)
        {
            foreach (var host in _cooldowns.Where(pair => pair.Value.ExpiresAtUtc <= DateTime.UtcNow).Select(pair => pair.Key).ToList())
                changed |= _cooldowns.Remove(host);
        }
        if (changed) CooldownsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static RepoFetchResult CreateCooldownResult(FetchTask task, HostFetchCooldown cooldown) =>
        new(task.LocalRepoId, task.AbsPath, false, $"인증 실패 쿨오프: {cooldown.Reason}", null, TimeSpan.Zero,
            task.HostKey, FetchFailureKind.Cooldown, true);

    internal static string ExtractHostKey(string normalizedKey)
    {
        var slash = normalizedKey.IndexOf('/');
        var authority = slash >= 0 ? normalizedKey[..slash] : normalizedKey;
        var colon = authority.LastIndexOf(':');
        var host = colon > 0 ? authority[..colon] : authority;
        return host.Trim('[', ']').ToLowerInvariant();
    }

    public void Stop() { _queue.Clear(); _cts.Cancel(); }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Stop();
        _networkPool.Dispose();
        foreach (var hostPool in _hostPools.Values) hostPool.Dispose();
        _cts.Dispose();
    }
}
