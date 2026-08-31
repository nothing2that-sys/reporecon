using System.Windows;
using RepoRecon.App.Converters;

namespace RepoRecon.App.Views;

public partial class RollbackConfirmWindow : Window
{
    public RollbackConfirmWindow(string targetPath, string commitHash)
    {
        InitializeComponent();
        TxtTargetPath.Text = PathDisplay.Truncate(targetPath);
        TxtTargetPath.ToolTip = targetPath;
        TxtCommitHash.Text = commitHash;

        Loaded += (_, _) => BtnCancelRollback.Focus();
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
