using System.IO;
using System.Text.Json;
using RepoRecon.Core.Data;

namespace RepoRecon.App;

/// <summary>§8.1.1(e)/§9.1: 예약 자동 pull의 순수 설정값(활성 여부, 시각 목록, 놓친 실행 보충
/// 여부, 마지막 실행 시각). §2.4가 명시한 대로 인덱스와 조인할 일이 없는 값이라 auto_pull_targets
/// 테이블이 아니라 이 JSON에 둔다 — window-state.json/app-settings.json과 같은 위치·포터블 규칙(§9.3).</summary>
public static class AutoPullScheduleSettingsStore
{
    public class Data
    {
        public bool IsEnabled { get; set; }
        public List<string> ScheduleTimes { get; set; } = new();
        public bool CatchUpMissedRun { get; set; } = true;
        public DateTime? LastRunUtc { get; set; }
    }

    private static string SettingsPath()
    {
        var dbPath = DatabasePathResolver.ResolveDatabasePath();
        return Path.Combine(Path.GetDirectoryName(dbPath) ?? AppDomain.CurrentDomain.BaseDirectory, "auto-pull-schedule.json");
    }

    public static Data Load()
    {
        try
        {
            var path = SettingsPath();
            if (!File.Exists(path)) return new Data();
            return JsonSerializer.Deserialize<Data>(File.ReadAllText(path)) ?? new Data();
        }
        catch
        {
            return new Data();
        }
    }

    public static void Save(Data data)
    {
        try
        {
            File.WriteAllText(SettingsPath(), JsonSerializer.Serialize(data));
        }
        catch
        {
            // Best-effort, same as the other settings stores.
        }
    }
}
