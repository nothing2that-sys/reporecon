using System.Windows;
using RepoRecon.App.ViewModels;

namespace RepoRecon.App.Views;

public partial class AutoPullSettingsWindow : Window
{
    private readonly MainViewModel _mainViewModel;

    public AutoPullSettingsWindow(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        ChkEnabled.IsChecked = _mainViewModel.AutoPullScheduler.IsEnabled;
        ChkCatchUp.IsChecked = _mainViewModel.AutoPullScheduler.CatchUpMissedRun;
        TxtTrayAutoEnabledNotice.Visibility = ChkEnabled.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        ScheduleTimesList.ItemsSource = _mainViewModel.AutoPullScheduler.ScheduleTimes.ToList();

        var targets = await _mainViewModel.GetAutoPullTargetsAsync();
        TargetsList.ItemsSource = targets.Select(t => t.FolderPath).ToList();
    }

    private void ChkEnabled_CheckedChanged(object sender, RoutedEventArgs e)
    {
        var enabling = ChkEnabled.IsChecked == true;
        _mainViewModel.AutoPullScheduler.IsEnabled = enabling;

        if (enabling)
        {
            // §8.1.1(e): 스케줄을 켜면 트레이 상주를 자동으로 켜고 그 사실을 화면에서 알린다 —
            // 그러지 않으면 창을 닫는 순간(기본값=종료) 프로세스가 죽어 예약이 절대 실행되지 않는다.
            AppSettingsStore.SetCloseBehavior(CloseBehavior.MinimizeToTray);
            TxtTrayAutoEnabledNotice.Visibility = Visibility.Visible;
        }
        else
        {
            TxtTrayAutoEnabledNotice.Visibility = Visibility.Collapsed;
        }
    }

    private void ChkCatchUp_CheckedChanged(object sender, RoutedEventArgs e)
    {
        _mainViewModel.AutoPullScheduler.CatchUpMissedRun = ChkCatchUp.IsChecked == true;
    }

    private void BtnAddTime_Click(object sender, RoutedEventArgs e)
    {
        var text = TxtNewTime.Text.Trim();
        if (!AutoPullSchedulerService.TryParseTime(text, out _))
        {
            MessageBox.Show("HH:mm 형식으로 입력하세요 (예: 09:00)", "형식 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _mainViewModel.AutoPullScheduler.AddScheduleTime(text);
        TxtNewTime.Text = string.Empty;
        _ = RefreshAsync();
    }

    private void BtnRemoveTime_Click(object sender, RoutedEventArgs e)
    {
        if (ScheduleTimesList.SelectedItem is string time)
        {
            _mainViewModel.AutoPullScheduler.RemoveScheduleTime(time);
            _ = RefreshAsync();
        }
    }

    private async void BtnRemoveTarget_Click(object sender, RoutedEventArgs e)
    {
        if (TargetsList.SelectedItem is string folderPath)
        {
            await _mainViewModel.RemoveAutoPullTargetAsync(folderPath);
            await RefreshAsync();
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
