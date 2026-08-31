using System.Windows;

namespace RepoRecon.App.Views;

public partial class RollbackConfirmWindow : Window
{
    public RollbackConfirmWindow(string targetPath, string commitHash)
    {
        InitializeComponent();
        TxtTargetPath.Text = targetPath;
        TxtCommitHash.Text = commitHash;
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
