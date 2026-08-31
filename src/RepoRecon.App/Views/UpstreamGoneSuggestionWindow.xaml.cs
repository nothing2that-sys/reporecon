using System.Windows;
using RepoRecon.App.Converters;

namespace RepoRecon.App.Views;

public partial class UpstreamGoneSuggestionWindow : Window
{
    public UpstreamGoneSuggestionWindow(string targetPath, string defaultBranch)
    {
        InitializeComponent();
        TxtTargetPath.Text = PathDisplay.Truncate(targetPath);
        TxtTargetPath.ToolTip = targetPath;
        TxtSuggestedUpstream.Text = $"origin/{defaultBranch}";
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
