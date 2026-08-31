using System.Windows;
using RepoRecon.App.ViewModels;

namespace RepoRecon.App.Views;

public partial class ChangeRemoteUrlWindow : Window
{
    private readonly ChangeRemoteUrlViewModel _viewModel;

    public ChangeRemoteUrlWindow(ChangeRemoteUrlViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SaveAsync(() =>
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
