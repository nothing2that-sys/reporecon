using System.Windows;

namespace RepoRecon.App.Views;

public partial class AppSettingsWindow : Window
{
    public AppSettingsWindow()
    {
        InitializeComponent();

        ChkAutoStart.IsChecked = AutoStartManager.IsEnabled();

        var current = AppSettingsStore.GetCloseBehavior();
        RadioCloseExit.IsChecked = current == CloseBehavior.Exit;
        RadioCloseTray.IsChecked = current == CloseBehavior.MinimizeToTray;
    }

    private void ChkAutoStart_CheckedChanged(object sender, RoutedEventArgs e)
    {
        AutoStartManager.SetEnabled(ChkAutoStart.IsChecked == true);
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        AppSettingsStore.SetCloseBehavior(RadioCloseTray.IsChecked == true ? CloseBehavior.MinimizeToTray : CloseBehavior.Exit);
        Close();
    }
}
