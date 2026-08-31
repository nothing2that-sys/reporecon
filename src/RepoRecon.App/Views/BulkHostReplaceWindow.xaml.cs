using System.Windows;
using RepoRecon.App.ViewModels;

namespace RepoRecon.App.Views;

public partial class BulkHostReplaceWindow : Window
{
    private readonly BulkHostReplaceViewModel _viewModel;

    public BulkHostReplaceWindow(BulkHostReplaceViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void BtnExecute_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ExecuteAsync(() =>
        {
            DialogResult = true;
            Close();
        });
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
