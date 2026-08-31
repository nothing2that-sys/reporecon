using System.Windows;

namespace RepoRecon.App.Views;

public partial class PushNoUpstreamConfirmWindow : Window
{
    public PushNoUpstreamConfirmWindow(string targetPath, string branch)
    {
        InitializeComponent();
        TxtTargetPath.Text = targetPath;
        TxtRemoteBranch.Text = $"origin/{branch}";
    }

    private void BtnConfirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
