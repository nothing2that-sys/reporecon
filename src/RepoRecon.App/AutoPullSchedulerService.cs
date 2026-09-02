using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using RepoRecon.Core.Data;
using RepoRecon.Core.Git;
using RepoRecon.Core.Models;
using RepoRecon.Core.Pull;

namespace RepoRecon.App;

/// <summary>§8.1.1: 예약 자동 pull의 App-layer 구동부. 스케줄 판정(AutoPullScheduleCalculator),
/// 대상 해석(AutoPullTargetResolver), 호스트별 연속 인증 실패 추적(HostFailureTracker)은 전부
/// Core의 순수 로직을 그대로 쓰고, 여기서는 DispatcherTimer로 주기 폴링하며 그 결과를
/// 알림(§10.5)으로 내보내는 조립만 한다.</summary>
public sealed class AutoPullSchedulerService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly SqliteIndexStore _indexStore;
    private readonly IPullEngine _pullEngine;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _timer;
    private readonly HostFailureTracker _hostFailureTracker = new();
    private readonly PathRetryScheduler _pathRetryScheduler = new();
    private bool _isRunning;

    /// <summary>(title, body) — 호출자가 트레이 풍선 알림 등으로 표시한다.</summary>
    public event Action<string, string>? NotificationRequested;

    /// <summary>IsEnabled/스케줄 시각이 바뀌었을 때 설정 창이 갱신할 수 있도록 알린다.</summary>
    public event Action? StateChanged;

    public AutoPullSchedulerService(SqliteIndexStore indexStore, IPullEngine pullEngine, ILogger logger)
    {
        _indexStore = indexStore;
        _pullEngine = pullEngine;
        _logger = logger;

        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += async (_, _) => await TickAsync();
    }

    public bool IsEnabled
    {
        get => AutoPullScheduleSettingsStore.Load().IsEnabled;
        set
        {
            var data = AutoPullScheduleSettingsStore.Load();
            data.IsEnabled = value;
            AutoPullScheduleSettingsStore.Save(data);
            StateChanged?.Invoke();
        }
    }

    public IReadOnlyList<string> ScheduleTimes => AutoPullScheduleSettingsStore.Load().ScheduleTimes;

    public bool CatchUpMissedRun
    {
        get => AutoPullScheduleSettingsStore.Load().CatchUpMissedRun;
        set
        {
            var data = AutoPullScheduleSettingsStore.Load();
            data.CatchUpMissedRun = value;
            AutoPullScheduleSettingsStore.Save(data);
        }
    }

    public void AddScheduleTime(string hhmm)
    {
        if (!TryParseTime(hhmm, out _)) return;

        var data = AutoPullScheduleSettingsStore.Load();
        if (!data.ScheduleTimes.Contains(hhmm))
        {
            data.ScheduleTimes.Add(hhmm);
            data.ScheduleTimes.Sort(StringComparer.Ordinal);
            AutoPullScheduleSettingsStore.Save(data);
            StateChanged?.Invoke();
        }
    }

    public void RemoveScheduleTime(string hhmm)
    {
        var data = AutoPullScheduleSettingsStore.Load();
        if (data.ScheduleTimes.Remove(hhmm))
        {
            AutoPullScheduleSettingsStore.Save(data);
            StateChanged?.Invoke();
        }
    }

    public void Start()
    {
        var settings = AutoPullScheduleSettingsStore.Load();
        if (settings.IsEnabled && !settings.CatchUpMissedRun)
        {
            // §8.1.1(e) "놓친 실행 보충 여부" 기본 켜짐이지만 꺼둔 경우: 앱이 꺼져 있던 동안
            // 지나간 슬롯을 조용히 기준선 너머로 넘겨서 소급 실행하지 않게 한다.
            settings.LastRunUtc = DateTime.UtcNow;
            AutoPullScheduleSettingsStore.Save(settings);
        }

        _timer.Start();
    }

    public void Dispose()
    {
        _timer.Stop();
    }

    private async Task TickAsync()
    {
        try
        {
            await CheckPendingPathRetriesAsync();

            if (_isRunning || _pullEngine.IsExecuting) return;

            var settings = AutoPullScheduleSettingsStore.Load();
            if (!settings.IsEnabled) return;

            var scheduleTimes = ParseTimes(settings.ScheduleTimes);
            var lastRunLocal = settings.LastRunUtc?.ToLocalTime();
            if (!AutoPullScheduleCalculator.IsSlotDue(DateTime.Now, scheduleTimes, lastRunLocal)) return;

            await RunNowAsync(isRetryPass: false, explicitTargets: null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "예약 자동 pull 틱 처리 실패");
        }
    }

    private async Task CheckPendingPathRetriesAsync()
    {
        if (_pathRetryScheduler.PendingCount == 0 || _isRunning || _pullEngine.IsExecuting) return;

        var due = _pathRetryScheduler.ConsumeDue(DateTime.UtcNow);
        if (due.Count == 0) return;

        var allRepos = await _indexStore.GetAllLocalReposWithStatusAsync();
        var dueIds = due.ToHashSet();
        var targets = allRepos.Where(r => dueIds.Contains(r.Repo.Id)).ToList();
        if (targets.Count == 0) return;

        await RunNowAsync(isRetryPass: true, explicitTargets: targets);
    }

    private async Task RunNowAsync(bool isRetryPass, IReadOnlyList<(LocalRepo Repo, LocalStatus? Status)>? explicitTargets)
    {
        _isRunning = true;
        var startedAtUtc = DateTime.UtcNow;
        try
        {
            IReadOnlyList<(LocalRepo Repo, LocalStatus? Status)> eligible;
            IReadOnlyList<(LocalRepo Repo, string SkipReason)> preSkipped = Array.Empty<(LocalRepo, string)>();
            IReadOnlyList<AutoPullTarget> activeTargets = Array.Empty<AutoPullTarget>();

            if (explicitTargets != null)
            {
                eligible = explicitTargets;
            }
            else
            {
                activeTargets = (await _indexStore.GetAutoPullTargetsAsync()).Where(t => t.IsActive).ToList();
                if (activeTargets.Count == 0)
                {
                    MarkRanNow(startedAtUtc);
                    return;
                }

                var allRepos = await _indexStore.GetAllLocalReposWithStatusAsync();
                var scanRoots = await _indexStore.GetScanRootsAsync();
                var resolved = AutoPullTargetResolver.Resolve(activeTargets, allRepos, scanRoots);
                eligible = resolved.Eligible;
                preSkipped = resolved.PreSkipped;
            }

            if (eligible.Count == 0)
            {
                if (!isRetryPass) MarkRanNow(startedAtUtc);
                await NotifyIfWarrantedAsync(
                    new PullBatchReport(0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero, Array.Empty<PullJobResult>(), string.Empty),
                    preSkipped,
                    isRetryPass);
                return;
            }

            var repoHostMap = eligible.ToDictionary(
                t => t.Repo.Id,
                t => !string.IsNullOrWhiteSpace(t.Repo.NormalizedKey)
                    ? HostFailureTracker.ExtractHostKey(t.Repo.NormalizedKey!)
                    : "local");

            var report = await _pullEngine.ExecuteBatchPullAsync(eligible);

            if (!isRetryPass)
            {
                MarkRanNow(startedAtUtc);
                foreach (var target in activeTargets)
                {
                    try { await _indexStore.UpdateAutoPullTargetLastRunAsync(target.Id, startedAtUtc); }
                    catch { /* informational column only -- not worth failing the run over */ }
                }
            }

            await ProcessHostFailuresAsync(report, repoHostMap);

            if (!isRetryPass)
            {
                SchedulePathRetries(report);
            }

            await NotifyIfWarrantedAsync(report, preSkipped, isRetryPass);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "자동 pull 실행 실패");
        }
        finally
        {
            _isRunning = false;
        }
    }

    private async Task ProcessHostFailuresAsync(PullBatchReport report, IReadOnlyDictionary<long, string> repoHostMap)
    {
        string? pausedHost = null;

        foreach (var hostGroup in report.Results.GroupBy(r => repoHostMap.TryGetValue(r.RepoId, out var h) ? h : "local"))
        {
            var hadAuthFailure = hostGroup.Any(r =>
                r.Status == PullJobStatus.Failed &&
                ErrorClassifier.Classify(r.ErrorMessage) == ErrorCategory.Authentication);

            var result = _hostFailureTracker.RecordResult(hostGroup.Key, hadAuthFailure);
            if (result != null) pausedHost = result;
        }

        if (pausedHost == null) return;

        var data = AutoPullScheduleSettingsStore.Load();
        data.IsEnabled = false;
        AutoPullScheduleSettingsStore.Save(data);
        StateChanged?.Invoke();

        NotificationRequested?.Invoke(
            "자동 pull 일시 중지됨",
            $"호스트 '{pausedHost}'에서 인증 실패가 연속 {HostFailureTracker.PauseThreshold}회 발생해 예약 자동 pull을 껐습니다.\n자격 증명을 확인한 뒤 설정에서 다시 켜주세요.");

        await Task.CompletedTask;
    }

    private void SchedulePathRetries(PullBatchReport report)
    {
        var now = DateTime.UtcNow;
        foreach (var failed in report.Results.Where(r => r.Status == PullJobStatus.Failed))
        {
            if (ErrorClassifier.Classify(failed.ErrorMessage) == ErrorCategory.Path)
            {
                _pathRetryScheduler.Schedule(failed.RepoId, now);
            }
        }
    }

    private async Task NotifyIfWarrantedAsync(
        PullBatchReport report,
        IReadOnlyList<(LocalRepo Repo, string SkipReason)> preSkipped,
        bool isRetryPass)
    {
        // §8.1.1(g): "변경 없음 + 실패 없음"이면 알림을 띄우지 않는다.
        if (report.SuccessCount == 0 && report.FailedCount == 0 && preSkipped.Count == 0)
        {
            return;
        }

        var chronicCount = 0;
        var individualFailureLines = new List<string>();

        foreach (var failed in report.Results.Where(r => r.Status == PullJobStatus.Failed))
        {
            var category = ErrorClassifier.ClassifyToString(failed.ErrorMessage);
            if (await IsChronicFailureAsync(failed.AbsPath, category))
            {
                chronicCount++;
            }
            else
            {
                individualFailureLines.Add($"- {System.IO.Path.GetFileName(failed.AbsPath.TrimEnd('\\', '/'))}: {failed.ErrorMessage}");
            }
        }

        var lines = new List<string>
        {
            $"성공 {report.SuccessCount} / 실패 {report.FailedCount} / 스킵 {report.SkippedCount + preSkipped.Count}"
        };

        // §8.1.1(g), v4.2 "만성 실패는 강등한다": 같은 오류로 3연속 실패한 레포는 개별 나열하지
        // 않고 요약 한 줄로 내린다.
        if (chronicCount > 0)
        {
            lines.Add($"만성 실패 {chronicCount}건 (작업 이력에서 상세 확인)");
        }

        const int maxIndividualLines = 5;
        lines.AddRange(individualFailureLines.Take(maxIndividualLines));
        if (individualFailureLines.Count > maxIndividualLines)
        {
            lines.Add($"... 외 {individualFailureLines.Count - maxIndividualLines}건");
        }

        var title = isRetryPass ? "자동 pull 재시도 완료" : "자동 pull 완료";
        NotificationRequested?.Invoke(title, string.Join("\n", lines));
    }

    private async Task<bool> IsChronicFailureAsync(string absPath, string category)
    {
        var history = await _indexStore.GetJobHistoryFilteredAsync(targetPath: absPath, jobType: "pull", limit: 3);
        if (history.Count < 3) return false;
        return history.All(h => string.Equals(h.Result, "Failed", StringComparison.OrdinalIgnoreCase) &&
                                 string.Equals(h.ErrorCategory, category, StringComparison.OrdinalIgnoreCase));
    }

    private static void MarkRanNow(DateTime nowUtc)
    {
        var data = AutoPullScheduleSettingsStore.Load();
        data.LastRunUtc = nowUtc;
        AutoPullScheduleSettingsStore.Save(data);
    }

    private static List<TimeOnly> ParseTimes(IEnumerable<string> raw) =>
        raw.Select(s => TryParseTime(s, out var t) ? (TimeOnly?)t : null)
           .Where(t => t != null)
           .Select(t => t!.Value)
           .ToList();

    // §8.1.1(e): "하루 N개 시각(HH:mm)"은 24시간제 고정 형식으로 저장·비교해야 한다.
    // TimeOnly.TryParse(string)은 스레드의 현재 문화권(예: ko-KR)의 짧은 시간 형식을 따르는데,
    // 그 형식이 "HH:mm"과 다르면 "09:18" 같은 값조차 파싱에 실패한다.
    public static bool TryParseTime(string text, out TimeOnly value) =>
        TimeOnly.TryParseExact(
            text.Trim(),
            "HH:mm",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out value);
}
