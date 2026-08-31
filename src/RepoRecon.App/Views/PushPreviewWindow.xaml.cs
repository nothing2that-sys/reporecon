using System.Windows;
using RepoRecon.App.ViewModels;

namespace RepoRecon.App.Views;

public partial class PushPreviewWindow : Window
{
    public bool? UserConfirmed { get; private set; }

    public PushPreviewWindow(PushPreviewViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.RequestClose += result =>
        {
            UserConfirmed = result;
            try
            {
                DialogResult = result;
            }
            catch
            {
                // In case opened via non-modal Show()
            }
            Close();
        };
    }
}
