using System.Windows;

namespace RepoRecon.App.Views;

public partial class UrlChangeSuggestionWindow : Window
{
    public UrlChangeSuggestionWindow(string targetPath, string oldUrl, string newUrl)
    {
        InitializeComponent();
        TxtTargetPath.Text = targetPath;
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
