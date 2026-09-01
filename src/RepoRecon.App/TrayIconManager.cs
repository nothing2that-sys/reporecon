using System.Windows;
using System.Windows.Forms;

namespace RepoRecon.App;

/// <summary>§9.1 트레이 상주: 창이 숨겨져 있는 동안만 트레이 아이콘을 노출한다.
/// WPF에는 트레이 아이콘 API가 없어 System.Windows.Forms.NotifyIcon을 그대로 쓴다
/// (csproj의 UseWindowsForms=true가 이 참조를 가능하게 한다).</summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly Window _window;
    private readonly NotifyIcon _notifyIcon;
    private bool _disposed;

    public event EventHandler? ExitRequested;

    public TrayIconManager(Window window)
    {
        _window = window;

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("열기", null, (_, _) => Restore());
        contextMenu.Items.Add("종료", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        _notifyIcon = new NotifyIcon
        {
            // 실행 중인 exe 자체에 embedded된 아이콘(csproj의 ApplicationIcon)을 그대로 재사용한다 —
            // 별도 리소스 스트림을 다시 열 필요가 없다.
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Text = "RepoRecon",
            Visible = false,
            ContextMenuStrip = contextMenu
        };
        _notifyIcon.DoubleClick += (_, _) => Restore();
    }

    public void MinimizeToTray()
    {
        _notifyIcon.Visible = true;
        _window.Hide();
    }

    private void Restore()
    {
        _notifyIcon.Visible = false;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
