using System.Windows;

namespace RepoRecon.App.Views;

public partial class CloseBehaviorPromptWindow : Window
{
    // §9.1 기본값은 종료 — 사용자가 창의 X(무응답 종료)로 닫아도 안전한 기본값을 그대로 쓴다.
    public CloseBehavior SelectedBehavior { get; private set; } = CloseBehavior.Exit;

    public CloseBehaviorPromptWindow()
    {
        InitializeComponent();
    }

    private void BtnExit_Click(object sender, RoutedEventArgs e)
    {
        SelectedBehavior = CloseBehavior.Exit;
        Close();
    }

    private void BtnMinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        SelectedBehavior = CloseBehavior.MinimizeToTray;
        Close();
    }
}
