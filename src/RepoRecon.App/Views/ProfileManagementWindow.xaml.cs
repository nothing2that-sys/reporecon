using System.Windows;
using RepoRecon.App.ViewModels;

namespace RepoRecon.App.Views;

public partial class ProfileManagementWindow : Window
{
    public ProfileManagementWindow(ProfileManagementViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ProfileManagementViewModel.SelectedProfile))
            {
                PbToken.Password = string.Empty;
            }
        };
        Loaded += async (_, _) =>
        {
            await viewModel.InitializeAsync();
            Activate();
        };
    }

    private void PbToken_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is ProfileManagementViewModel vm)
        {
            vm.EditToken = PbToken.Password;
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
