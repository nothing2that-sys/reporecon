using System.Windows;

namespace RepoRecon.App.Views;

public partial class ForcePushConfirmWindow : Window
{
    public ForcePushConfirmWindow(string targetPath, string branch, string remoteCommit)
    {
        InitializeComponent();
        TxtTargetPath.Text = targetPath;
        TxtBranch.Text = branch;
        TxtRemoteCommit.Text = string.IsNullOrWhiteSpace(remoteCommit) ? "(원격 커밋 없음 / 신규 생성)" : remoteCommit;
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
