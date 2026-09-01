using Microsoft.Win32;

namespace RepoRecon.App;

/// <summary>§9.1: Windows 시작 시 자동 실행. 사용자 범위 HKCU\...\Run 키만 쓴다 — HKLM은 관리자
/// 권한을 요구하므로 §13.2("관리자 권한을 요구하지 않는다")와 충돌해 금지.</summary>
public static class AutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RepoRecon";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) != null;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            // 값을 매번 새로 쓴다 — 앱이 이동된 뒤 재활성화하면 경로가 갱신된다.
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
