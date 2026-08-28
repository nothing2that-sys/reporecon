using System.Windows;
using RepoRecon.App.ViewModels;

namespace RepoRecon.App.Views;

public partial class PullPreviewWindow : Window
{
    public PullPreviewWindow(PullPreviewViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.RequestClose += isConfirmed =>
        {
            DialogResult = isConfirmed;
            Close();
        };
    }
}
