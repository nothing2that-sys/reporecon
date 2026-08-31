using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using RepoRecon.App.ViewModels;
using RepoRecon.App.ViewModels.Tree;

namespace RepoRecon.App.Views;

public partial class MainWindow : Window
{
    private readonly ILogger<MainWindow> _logger;
    private readonly DispatcherTimer _scrollSamplingCompletionTimer;
    private readonly List<double> _scrollFrameIntervalsMs = new();
    private bool _isScrollSampling;
    private long _lastRenderingTimestamp;

    public MainWindow(MainViewModel viewModel, ILogger<MainWindow> logger)
    {
        _logger = logger;
        InitializeComponent();
        DataContext = viewModel;

        _scrollSamplingCompletionTimer = new DispatcherTimer
        {
            // Keep the measurement window long enough to collect at least
            // 50 real CompositionTarget.Rendering intervals at 60 Hz after
            // a ScrollChanged event. Repeated scrolls restart this window.
            Interval = TimeSpan.FromMilliseconds(1200)
        };
        _scrollSamplingCompletionTimer.Tick += ScrollSamplingCompletionTimer_Tick;
        MainTreeView.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(MainTreeView_ScrollChanged));
        CompositionTarget.Rendering += CompositionTarget_Rendering;
        Closed += (_, _) =>
        {
            _scrollSamplingCompletionTimer.Stop();
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
        };

        Loaded += async (_, _) =>
        {
            await viewModel.InitializeAsync();
        };

        KeyDown += MainWindow_KeyDown;
    }

    private void MainWindow_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        // F5 -> Refresh all (Criterion 28)
        if (e.Key == System.Windows.Input.Key.F5)
        {
            if (vm.RefreshAllCommand.CanExecute(null))
            {
                vm.RefreshAllCommand.Execute(null);
                e.Handled = true;
            }
            return;
        }

        // Ctrl + A -> Select all visible items ONLY (Criterion 29)
        if (e.Key == System.Windows.Input.Key.A && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control)
        {
            if (System.Windows.Input.Keyboard.FocusedElement is TextBox)
            {
                return;
            }

            vm.SelectAllVisible();
            e.Handled = true;
            return;
        }

        // Space -> Toggle selection of focused tree item (Criterion 28)
        if (e.Key == System.Windows.Input.Key.Space && MainTreeView.IsKeyboardFocusWithin)
        {
            if (MainTreeView.SelectedItem is TreeNodeViewModelBase selectedNode)
            {
                selectedNode.SetSelected(!selectedNode.IsSelected.GetValueOrDefault());
                e.Handled = true;
            }
            return;
        }

        // Type-to-filter on tree (Criterion 28)
        if (MainTreeView.IsKeyboardFocusWithin &&
            (System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None) &&
            ((e.Key >= System.Windows.Input.Key.A && e.Key <= System.Windows.Input.Key.Z) ||
             (e.Key >= System.Windows.Input.Key.D0 && e.Key <= System.Windows.Input.Key.D9) ||
             (e.Key >= System.Windows.Input.Key.NumPad0 && e.Key <= System.Windows.Input.Key.NumPad9)))
        {
            TxtFilter.Focus();
        }
    }

    private void MainTreeView_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.VerticalChange) < double.Epsilon && Math.Abs(e.ViewportHeightChange) < double.Epsilon)
        {
            return;
        }

        if (!_isScrollSampling)
        {
            _scrollFrameIntervalsMs.Clear();
            _lastRenderingTimestamp = 0;
            _isScrollSampling = true;
        }

        _scrollSamplingCompletionTimer.Stop();
        _scrollSamplingCompletionTimer.Start();

        // Reorder only queued work for repo rows that are actually realized
        // and intersect the MainTreeView viewport. Repeated scroll events add
        // another boost, which gives the most recently viewed rows priority.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, BoostViewportRepoPriority);
    }

    private void BoostViewportRepoPriority()
    {
        if (DataContext is not MainViewModel viewModel || MainTreeView.ActualHeight <= 0)
            return;

        var visibleRepoIds = new HashSet<long>();
        CollectVisibleRepoIds(MainTreeView, visibleRepoIds);
        if (visibleRepoIds.Count > 0)
            viewModel.BoostVisibleRepoPriority(visibleRepoIds);
    }

    private void CollectVisibleRepoIds(DependencyObject parent, HashSet<long> visibleRepoIds)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is TreeViewItem item && item.DataContext is RepoTreeNodeViewModel repo)
            {
                try
                {
                    var topLeft = item.TransformToAncestor(MainTreeView).Transform(new Point(0, 0));
                    var bottom = topLeft.Y + item.ActualHeight;
                    if (bottom >= 0 && topLeft.Y <= MainTreeView.ActualHeight)
                        visibleRepoIds.Add(repo.Repo.Id);
                }
                catch (InvalidOperationException)
                {
                    // Layout changed between visual traversal and transform.
                }
            }

            CollectVisibleRepoIds(child, visibleRepoIds);
        }
    }

    private void CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        if (!_isScrollSampling)
        {
            return;
        }

        var timestamp = Stopwatch.GetTimestamp();
        if (_lastRenderingTimestamp != 0)
        {
            var elapsedMs = (timestamp - _lastRenderingTimestamp) * 1000d / Stopwatch.Frequency;
            if (elapsedMs > 0)
            {
                _scrollFrameIntervalsMs.Add(elapsedMs);
            }
        }

        _lastRenderingTimestamp = timestamp;
    }

    private void ScrollSamplingCompletionTimer_Tick(object? sender, EventArgs e)
    {
        _scrollSamplingCompletionTimer.Stop();
        if (!_isScrollSampling)
        {
            return;
        }

        _isScrollSampling = false;
        if (_scrollFrameIntervalsMs.Count == 0)
        {
            _logger.LogInformation(
                "TreeScrollFrameMetrics Control=MainTreeView Method=ScrollChanged+CompositionTarget.Rendering Samples=0");
            return;
        }

        var minimum = _scrollFrameIntervalsMs.Min();
        var maximum = _scrollFrameIntervalsMs.Max();
        var average = _scrollFrameIntervalsMs.Average();
        var overBudget = _scrollFrameIntervalsMs.Count(value => value > 16.6d);

        _logger.LogInformation(
            "TreeScrollFrameMetrics Control=MainTreeView Method=ScrollChanged+CompositionTarget.Rendering Samples={Samples} MinMs={MinMs:F3} MaxMs={MaxMs:F3} AvgMs={AvgMs:F3} Over16_6Ms={OverBudget}",
            _scrollFrameIntervalsMs.Count,
            minimum,
            maximum,
            average,
            overBudget);
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "스캔할 루트 폴더를 선택하세요",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true && DataContext is MainViewModel vm)
        {
            vm.NewScanRootPath = dialog.FolderName;
        }
    }
}
