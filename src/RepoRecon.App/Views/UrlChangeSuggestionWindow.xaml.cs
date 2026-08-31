using System.Windows;
using RepoRecon.App.Converters;

namespace RepoRecon.App.Views;

public partial class UrlChangeSuggestionWindow : Window
{
    public UrlChangeSuggestionWindow(string targetPath, string oldUrl, string newUrl)
    {
        InitializeComponent();
        TxtTargetPath.Text = PathDisplay.Truncate(targetPath);
        TxtTargetPath.ToolTip = targetPath;
        TxtOldUrl.Text = oldUrl;
        TxtNewUrl.Text = newUrl;
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
