using System.Windows;
using RepoRecon.App.ViewModels;

namespace RepoRecon.App.Views;

public partial class ProfileManagementWindow : Window
{
    public ProfileManagementWindow(ProfileManagementViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.InitializeAsync();
    }

    private void PbToken_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is ProfileManagementViewModel vm)
        {
            vm.EditToken = PbToken.Password;
        }
    }
}
