using System.IO;
using System.Text.Json;
using System.Windows;
using RepoRecon.Core.Data;

namespace RepoRecon.App;

/// <summary>§5-5: remembers MainWindow size/position/maximized-state and the last
/// selected tab across restarts. Stored next to index.db so it follows the same
/// portable-mode rule as everything else (design §9.3).</summary>
public static class WindowStateStore
{
    private class Data
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }
        public bool IsMaximized { get; set; }
        public int SelectedTabIndex { get; set; }
    }

    private static string SettingsPath()
    {
        var dbPath = DatabasePathResolver.ResolveDatabasePath();
        return Path.Combine(Path.GetDirectoryName(dbPath) ?? AppDomain.CurrentDomain.BaseDirectory, "window-state.json");
    }

    public static void Restore(Window window, Action<int> applySelectedTabIndex)
    {
        try
        {
            var path = SettingsPath();
            if (!File.Exists(path)) return;

            var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(path));
            if (data == null) return;

            if (data.Width > 0 && data.Height > 0)
            {
                window.Width = data.Width;
                window.Height = data.Height;
            }

            if (IsPositionOnScreen(data.Left, data.Top))
            {
                window.Left = data.Left;
                window.Top = data.Top;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
            }

            if (data.IsMaximized)
            {
                window.WindowState = WindowState.Maximized;
            }

            applySelectedTabIndex(data.SelectedTabIndex);
        }
        catch
        {
            // A missing/corrupt settings file must never block startup — fall back to defaults.
        }
    }

    public static void Save(Window window, int selectedTabIndex)
    {
        try
        {
            var isMaximized = window.WindowState == WindowState.Maximized;
            var bounds = isMaximized ? window.RestoreBounds : new Rect(window.Left, window.Top, window.Width, window.Height);

            var data = new Data
            {
                Width = bounds.Width,
                Height = bounds.Height,
                Left = bounds.Left,
                Top = bounds.Top,
                IsMaximized = isMaximized,
                SelectedTabIndex = selectedTabIndex
            };

            File.WriteAllText(SettingsPath(), JsonSerializer.Serialize(data));
        }
        catch
        {
            // Best-effort — losing the remembered window state is not worth crashing on exit for.
        }
    }

    private static bool IsPositionOnScreen(double left, double top)
    {
        const double margin = 50;
        return left >= SystemParameters.VirtualScreenLeft - margin &&
               top >= SystemParameters.VirtualScreenTop - margin &&
               left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
               top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
    }
}
