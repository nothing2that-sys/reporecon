using System.Windows;
using RepoRecon.App.Converters;

namespace RepoRecon.App.Views;

public partial class ForcePushConfirmWindow : Window
{
    public ForcePushConfirmWindow(string targetPath, string branch, string remoteCommit)
    {
        InitializeComponent();
        TxtTargetPath.Text = PathDisplay.Truncate(targetPath);
        TxtTargetPath.ToolTip = targetPath;
        TxtBranch.Text = branch;
        TxtRemoteCommit.Text = string.IsNullOrWhiteSpace(remoteCommit) ? "(원격 커밋 없음 / 신규 생성)" : remoteCommit;

        // §5-1: 위험 동작 대화상자는 첫 포커스도 취소 버튼에 둔다(포커스된 버튼이
        // Enter를 직접 처리하므로, 아래 IsDefault 지정과 이중으로 안전판이 된다).
        Loaded += (_, _) => BtnCancelForcePush.Focus();
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
