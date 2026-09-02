namespace RepoRecon.Core.Pull;

/// <summary>§10.2: "자동 pull 경로에 한해 경로 분류에 1회 재시도를 허용한다. 10분 후 1회,
/// 그 이후는 재시도하지 않는다." 예약(Schedule)과 소비(ConsumeDue)를 분리해 실제 시각 대신
/// 주입된 now로 테스트할 수 있게 한다(AutoPullScheduleCalculator와 같은 패턴).</summary>
public sealed class PathRetryScheduler
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(10);

    private readonly List<(long RepoId, DateTime RetryAtUtc)> _pending = new();

    public int PendingCount => _pending.Count;

    /// <summary>지금(nowUtc) 기준 10분 뒤로 재시도를 예약한다. 같은 repoId를 중복 예약해도
    /// 그대로 별도 항목으로 쌓인다 — 호출자가 "이미 한 번 재시도했다"는 것을 스스로 보장해야
    /// 한다(AutoPullSchedulerService는 예약된 실행 결과에서만 새로 예약하고, 재시도 자체의
    /// 결과로는 다시 예약하지 않아 이를 지킨다).</summary>
    public void Schedule(long repoId, DateTime nowUtc)
    {
        _pending.Add((repoId, nowUtc + RetryDelay));
    }

    /// <summary>지금(nowUtc) 기준 기한이 된 항목을 꺼내고 목록에서 제거한다 — 다시 호출해도
    /// 같은 항목이 두 번 나오지 않는다(1회만 재시도).</summary>
    public IReadOnlyList<long> ConsumeDue(DateTime nowUtc)
    {
        var due = _pending.Where(p => p.RetryAtUtc <= nowUtc).Select(p => p.RepoId).ToList();
        if (due.Count > 0)
        {
            _pending.RemoveAll(p => p.RetryAtUtc <= nowUtc);
        }

        return due;
    }
}
