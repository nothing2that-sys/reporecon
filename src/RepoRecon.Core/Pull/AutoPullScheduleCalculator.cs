namespace RepoRecon.Core.Pull;

/// <summary>§8.1.1(e): 하루 N개 시각(HH:mm) 지정, cron 없음. 놓친 실행은 앱 기동 후 1회만 보충한다.
///
/// 별도의 "catch-up" 경로를 두지 않는다 — <see cref="IsSlotDue"/> 하나만으로 두 요구사항이 함께
/// 충족된다: 어떤 슬롯이 due로 판정되어 실행되면 호출자는 lastRunLocal을 "실행 시각(now)"으로
/// 갱신한다. 그러면 그 이전에 밀려 있던 다른 슬롯들은 전부 "lastRunLocal 이전"이 되어 다음 틱에서
/// 자동으로 걸러진다 — 여러 슬롯이 밀려 있어도 딱 1번만 보충 실행된다.</summary>
public static class AutoPullScheduleCalculator
{
    /// <summary>지금(로컬 시각) 실행해야 할 슬롯이 있는지 판정한다. 오늘 이미 지난 슬롯 중
    /// lastRunLocal 이후로 아직 실행되지 않은 것이 하나라도 있으면 true.</summary>
    public static bool IsSlotDue(DateTime nowLocal, IReadOnlyList<TimeOnly> scheduleTimes, DateTime? lastRunLocal)
    {
        if (scheduleTimes.Count == 0) return false;

        foreach (var slot in scheduleTimes)
        {
            var slotToday = nowLocal.Date + slot.ToTimeSpan();
            if (slotToday > nowLocal) continue; // 오늘 아직 도래하지 않은 슬롯
            if (lastRunLocal != null && lastRunLocal.Value >= slotToday) continue; // 이미 이 슬롯은 처리됨
            return true;
        }

        return false;
    }
}
