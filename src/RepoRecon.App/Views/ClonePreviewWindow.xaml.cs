using System.Windows;
using RepoRecon.App.ViewModels;

namespace RepoRecon.App.Views;

public partial class ClonePreviewWindow : Window
{
    public ClonePreviewWindow(ClonePreviewViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
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
