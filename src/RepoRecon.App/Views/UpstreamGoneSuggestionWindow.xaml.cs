using System.Windows;

namespace RepoRecon.App.Views;

public partial class UpstreamGoneSuggestionWindow : Window
{
    public UpstreamGoneSuggestionWindow(string targetPath, string defaultBranch)
    {
        InitializeComponent();
        TxtTargetPath.Text = targetPath;
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
