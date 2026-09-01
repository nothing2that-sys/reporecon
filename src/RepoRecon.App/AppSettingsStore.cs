using System.IO;
using System.Text.Json;
using RepoRecon.Core.Data;

namespace RepoRecon.App;

public enum CloseBehavior
{
    Exit,
    MinimizeToTray
}

/// <summary>§9.1: 창 닫기 동작 설정. window-state.json과 같은 위치·포터블 규칙(§9.3)을 따르되,
/// 창 크기/위치와는 성격이 다른 "동작 선택" 값이라 별도 파일로 분리했다.</summary>
public static class AppSettingsStore
{
    private class Data
    {
        public bool HasAskedCloseBehavior { get; set; }
        public CloseBehavior CloseBehavior { get; set; } = CloseBehavior.Exit;
    }

    private static string SettingsPath()
    {
        var dbPath = DatabasePathResolver.ResolveDatabasePath();
        return Path.Combine(Path.GetDirectoryName(dbPath) ?? AppDomain.CurrentDomain.BaseDirectory, "app-settings.json");
    }

    private static Data Load()
    {
        try
        {
            var path = SettingsPath();
            if (!File.Exists(path)) return new Data();
            return JsonSerializer.Deserialize<Data>(File.ReadAllText(path)) ?? new Data();
        }
        catch
        {
            // A missing/corrupt settings file must never block startup or closing.
            return new Data();
        }
    }

    private static void Save(Data data)
    {
        try
        {
            File.WriteAllText(SettingsPath(), JsonSerializer.Serialize(data));
        }
        catch
        {
            // Best-effort — losing the remembered choice is not worth crashing on close for.
        }
    }

    public static bool HasAskedCloseBehavior() => Load().HasAskedCloseBehavior;

    public static CloseBehavior GetCloseBehavior() => Load().CloseBehavior;

    public static void SetCloseBehavior(CloseBehavior behavior)
    {
        var data = Load();
        data.HasAskedCloseBehavior = true;
        data.CloseBehavior = behavior;
        Save(data);
    }
}
