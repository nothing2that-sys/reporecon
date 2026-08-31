using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoRecon.App.ViewModels.Tree;
using RepoRecon.App.Views;
using RepoRecon.Core.Clone;
using RepoRecon.Core.Data;
using RepoRecon.Core.Export;
using RepoRecon.Core.Fetch;
using RepoRecon.Core.Git;
using RepoRecon.Core.Models;
using RepoRecon.Core.Pull;
using RepoRecon.Core.Push;
using RepoRecon.Core.Scanning;
using RepoRecon.Core.Tree;
using RepoRecon.Core.Urls;

namespace RepoRecon.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IGitRunner _gitRunner;
    private readonly SqliteIndexStore _indexStore;
    private readonly ScanRootManager _scanRootManager;
    private readonly RepoScanner _repoScanner;
    private readonly IFetchEngine _fetchEngine;
    private readonly IPullEngine _pullEngine;
    private readonly IPushEngine _pushEngine;
    private readonly ICloneEngine _cloneEngine;
    private readonly IRemoteUrlService _remoteUrlService;
    private readonly IRollbackService _rollbackService;
    private readonly IExportService _exportService;
    private readonly HostAliasTable _aliasTable;
    private readonly ILogger<MainViewModel> _logger;
    private readonly DispatcherTimer _freshnessTimer;
    private CancellationTokenSource? _pullCts;
    private CancellationTokenSource? _pushCts;

    private readonly Dictionary<long, RepoTreeNodeViewModel> _repoViewModelMap = new();

    public CloneTabViewModel CloneTab { get; }
    public ProfileManagementViewModel ProfileManagement { get; }

    // Error Grouping Collections (§10.2, 완료 기준 4, 5)
    public ObservableCollection<ErrorGroupSummaryViewModel> PullErrorGroups { get; } = new();
    public ObservableCollection<ErrorGroupSummaryViewModel> PushErrorGroups { get; } = new();

    [ObservableProperty]
    private bool _hasPullErrorGroups;

    [ObservableProperty]
    private bool _hasPushErrorGroups;

    // Job History Properties (§10.3, 완료 기준 8, 9)
    public ObservableCollection<JobHistoryItemViewModel> JobHistoryItems { get; } = new();
    public ObservableCollection<string> AvailableJobTypes { get; } = new() { "전체", "pull", "push", "push_force", "rollback", "remote_url_change" };

    [ObservableProperty]
    private string _historySearchText = string.Empty;

    [ObservableProperty]
    private string _selectedJobTypeFilter = "전체";

    [ObservableProperty]
    private bool _isFullHistoryMode;

    [ObservableProperty]
    private bool _isJobHistoryLoading;

    [ObservableProperty]
    private bool _isHistoryPanelVisible;

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private bool _hasResidualTempFolders;

    [ObservableProperty]
    private string _residualTempFoldersNotice = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGitDiagnosticError))]
    private bool _isGitAvailable = true;

    public bool HasGitDiagnosticError => !IsGitAvailable;

    [ObservableProperty]
    private string? _gitDiagnosticError;

    [ObservableProperty]
    private string? _gitVersionString;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotScanning))]
    private bool _isScanning;

    public bool IsNotScanning => !IsScanning;

    [ObservableProperty]
    private bool _isFetching;

    [ObservableProperty]
    private int _pendingFetchCount;

    [ObservableProperty]
    private bool _hasAuthenticationCooldowns;

    [ObservableProperty]
    private string _authenticationCooldownSummary = string.Empty;

    [ObservableProperty]
    private bool _wasDatabaseRecreated;

    [ObservableProperty]
    private string? _databaseRecreatedNotice;

    [ObservableProperty]
    private string _statusMessage = "준비 완료";

    [ObservableProperty]
    private string _scanReportSummary = string.Empty;

    [ObservableProperty]
    private string _newScanRootPath = string.Empty;

    [ObservableProperty]
    private int _newScanRootDepth = 10;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoRepos))]
    private int _totalRepoCount;

    public bool HasNoRepos => TotalRepoCount == 0;

    [ObservableProperty]
    private bool _hasInaccessiblePaths;

    [ObservableProperty]
    private bool _isInaccessiblePathsExpanded;

    // Filters (설계 §5.3-f)
    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _filterBehindOnly;

    [ObservableProperty]
    private bool _filterAheadOnly;

    [ObservableProperty]
    private bool _filterDivergedOnly;

    [ObservableProperty]
    private bool _filterDirtyOnly;

    [ObservableProperty]
    private bool _filterConflictedOnly;

    [ObservableProperty]
    private string _selectedHostFilter = "전체 호스트";

    public ObservableCollection<string> AvailableHosts { get; } = new() { "전체 호스트" };

    // Execution Count and Skip Breakdown (설계 §5.3-h, 지시서 §4-3)
    [ObservableProperty]
    private int _totalSelectedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExecutePull))]
    private int _pullableCount;

    [ObservableProperty]
    private int _skippedCount;

    [ObservableProperty]
    private string _executionSummaryText = "선택 0개 (Pull 가능 0 / 스킵 0)";

    [ObservableProperty]
    private string _skipReasonSummary = string.Empty;

    [ObservableProperty]
    private bool _hasSkipReasons;

    [ObservableProperty]
    private bool _isSkipDetailsExpanded;

    // Push Execution Properties (§7.5, §7.6)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExecutePush))]
    private int _pushableCount;

    [ObservableProperty]
    private int _pushSkippedCount;

    [ObservableProperty]
    private string _pushExecutionSummaryText = "선택 0개 (Push 가능 0 / 스킵 0)";

    [ObservableProperty]
    private string _pushSkipReasonSummary = string.Empty;

    [ObservableProperty]
    private bool _hasPushSkipReasons;

    [ObservableProperty]
    private bool _isPushSkipDetailsExpanded;

    // Pull Execution State & Panel (§8, §5.5, §7.3)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExecutePull))]
    private bool _isPullExecuting;

    public bool CanExecutePull => PullableCount > 0 && !IsPullExecuting;

    [ObservableProperty]
    private bool _isPullPanelVisible;

    [ObservableProperty]
    private bool _isPullCompleted;

    [ObservableProperty]
    private string _pullHeaderTitle = "🚀 일괄 Pull 실행 진행 상황";

    [ObservableProperty]
    private string _pullHeaderColor = "#0F172A";

    [ObservableProperty]
    private string _pullPanelBorderBrush = "#CBD5E1";

    [ObservableProperty]
    private string _pullPanelBackground = "#F8FAFC";

    [ObservableProperty]
    private int _pullProgressPercent;

    [ObservableProperty]
    private string _pullProgressText = string.Empty;

    [ObservableProperty]
    private bool _hasPullReport;

    [ObservableProperty]
    private string _pullReportSummary = string.Empty;

    public ObservableCollection<PullExecutionItemViewModel> PullExecutionItems { get; } = new();
    public ObservableCollection<string> SkipReasonList { get; } = new();

    // Push Execution State & Panel (§8.2, §8.3)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExecutePush))]
    private bool _isPushExecuting;

    public bool CanExecutePush => PushableCount > 0 && !IsPushExecuting;

    [ObservableProperty]
    private bool _isPushPanelVisible;

    [ObservableProperty]
    private bool _isPushCompleted;

    [ObservableProperty]
    private string _pushHeaderTitle = "🚀 일괄 Push 실행 진행 상황";

    [ObservableProperty]
    private string _pushHeaderColor = "#1E3A8A";

    [ObservableProperty]
    private string _pushPanelBorderBrush = "#BFDBFE";

    [ObservableProperty]
    private string _pushPanelBackground = "#F8FAFC";

    [ObservableProperty]
    private int _pushProgressPercent;

    [ObservableProperty]
    private string _pushProgressText = string.Empty;

    [ObservableProperty]
    private bool _hasPushReport;

    [ObservableProperty]
    private string _pushReportSummary = string.Empty;

    public ObservableCollection<PushExecutionItemViewModel> PushExecutionItems { get; } = new();
    public ObservableCollection<string> PushSkipReasonList { get; } = new();

    public ObservableCollection<string> InaccessiblePaths { get; } = new();
    public ObservableCollection<ScanRootItemViewModel> ScanRoots { get; } = new();
    public ObservableCollection<TreeNodeViewModelBase> RootTreeNodes { get; } = new();

    public MainViewModel(
        IGitRunner gitRunner,
        SqliteIndexStore indexStore,
        ScanRootManager scanRootManager,
        RepoScanner repoScanner,
        IFetchEngine fetchEngine,
        IPullEngine pullEngine,
        IPushEngine pushEngine,
        ICloneEngine cloneEngine,
        IRemoteUrlService remoteUrlService,
        IRollbackService rollbackService,
        IExportService exportService,
        HostAliasTable aliasTable,
        CloneTabViewModel cloneTab,
        ProfileManagementViewModel profileManagement,
        ILogger<MainViewModel>? logger = null)
    {
        _gitRunner = gitRunner;
        _indexStore = indexStore;
        _scanRootManager = scanRootManager;
        _repoScanner = repoScanner;
        _fetchEngine = fetchEngine;
        _pullEngine = pullEngine;
        _pushEngine = pushEngine;
        _cloneEngine = cloneEngine;
        _remoteUrlService = remoteUrlService;
        _rollbackService = rollbackService;
        _exportService = exportService;
        _aliasTable = aliasTable;
        CloneTab = cloneTab;
        ProfileManagement = profileManagement;
        _logger = logger ?? NullLogger<MainViewModel>.Instance;

        _fetchEngine.RepoFetched += OnRepoFetched;
        _fetchEngine.FetchingStateChanged += OnFetchingStateChanged;
        _fetchEngine.CooldownsChanged += OnCooldownsChanged;

        _freshnessTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _freshnessTimer.Tick += (_, _) =>
        {
            UpdateAllFreshnessLabels();
            UpdateCooldownSummary();
        };
        _freshnessTimer.Start();

        WireCloneTabEvents();
    }

    private void WireCloneTabEvents()
    {
        CloneTab.OnJumpToLocalRepoRequested = normKey =>
        {
            SelectedTabIndex = 0; // Switch to Local Management Tab
            var target = _repoViewModelMap.Values.FirstOrDefault(r => string.Equals(r.Repo.NormalizedKey, normKey, StringComparison.OrdinalIgnoreCase));
            if (target != null)
            {
                var p = target.Parent;
                while (p != null)
                {
                    p.IsExpanded = true;
                    p = p.Parent;
                }
                target.IsSelected = true;
            }
        };

        CloneTab.ShowPreviewDialogFunc = (items, targetRoot) =>
        {
            var previewVm = new ClonePreviewViewModel();
            previewVm.LoadPreview(items, targetRoot);
            var dlg = new ClonePreviewWindow(previewVm);
            if (System.Windows.Application.Current?.MainWindow != null)
            {
                dlg.Owner = System.Windows.Application.Current.MainWindow;
            }
            var result = dlg.ShowDialog();
            return Task.FromResult(result == true);
        };

        CloneTab.PromptAddScanRootFunc = targetRoot =>
        {
            var res = System.Windows.MessageBox.Show(
                $"클론 대상 경로 '{targetRoot}'이(가) 현재 스캔 루트에 포함되어 있지 않습니다.\n스캔 루트에 추가하시겠습니까?",
                "스캔 루트 추가 확인",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            return Task.FromResult(res == System.Windows.MessageBoxResult.Yes);
        };

        CloneTab.OpenProfileManagementFunc = () =>
        {
            var dlg = new ProfileManagementWindow(ProfileManagement);
            if (System.Windows.Application.Current?.MainWindow != null)
            {
                dlg.Owner = System.Windows.Application.Current.MainWindow;
            }
            dlg.Closed += (_, _) =>
            {
                _ = CloneTab.LoadProfilesAsync();
            };
            dlg.Show();
        };

        CloneTab.OnCloneCompletedFunc = async () =>
        {
            await LoadDataAsync();
        };

        CloneTab.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(CloneTabViewModel.StatusMessage) && !string.IsNullOrWhiteSpace(CloneTab.StatusMessage))
            {
                StatusMessage = CloneTab.StatusMessage;
            }
        };
    }

    [RelayCommand]
    public async Task CleanResidualTempFoldersAsync()
    {
        var allRoots = await _scanRootManager.GetRootsAsync();
        var roots = allRoots.Where(r => r.IsActive).ToList();
        var residuals = _cloneEngine.FindResidualTempFolders(roots.Select(r => r.Path));
        var count = 0;
        foreach (var res in residuals)
        {
            if (_cloneEngine.CleanResidualTempFolder(res.TempPath))
            {
                count++;
            }
        }
        HasResidualTempFolders = false;
        ResidualTempFoldersNotice = string.Empty;
        StatusMessage = $"잔존 임시 폴더 {count}개가 정리되었습니다.";
    }

    public void NotifyDatabaseRecreated()
    {
        WasDatabaseRecreated = true;
        DatabaseRecreatedNotice = "⚠️ 데이터베이스 스키마 버전 불일치 또는 손상으로 인해 인덱스가 초기화되었습니다. [스캔 시작]을 눌러 재스캔하세요.";
        StatusMessage = "데이터베이스가 재생성되었습니다.";
        _logger.LogInformation("데이터베이스 스키마 재초기화 통보가 UI에 표시되었습니다.");
    }

    [RelayCommand]
    private void ToggleInaccessiblePaths()
    {
        IsInaccessiblePathsExpanded = !IsInaccessiblePathsExpanded;
    }

    [RelayCommand]
    private void ToggleSkipDetails()
    {
        IsSkipDetailsExpanded = !IsSkipDetailsExpanded;
    }

    partial void OnFilterTextChanged(string value) => ApplyFilters();
    partial void OnFilterBehindOnlyChanged(bool value) => ApplyFilters();
    partial void OnFilterAheadOnlyChanged(bool value) => ApplyFilters();
    partial void OnFilterDivergedOnlyChanged(bool value) => ApplyFilters();
    partial void OnFilterDirtyOnlyChanged(bool value) => ApplyFilters();
    partial void OnFilterConflictedOnlyChanged(bool value) => ApplyFilters();
    partial void OnSelectedHostFilterChanged(string value) => ApplyFilters();

    private void ApplyFilters()
    {
        var hasSearch = !string.IsNullOrWhiteSpace(FilterText);
        var query = FilterText.Trim();
        var hostFilter = SelectedHostFilter;
        var isAllHosts = string.IsNullOrWhiteSpace(hostFilter) || hostFilter == "전체 호스트";

        bool Predicate(RepoTreeNodeViewModel repo)
        {
            // Text Search
            if (hasSearch)
            {
                var matchesText = (repo.FolderName != null && repo.FolderName.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                                  (repo.FullPath != null && repo.FullPath.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                                  (repo.DisplayRepoPath != null && repo.DisplayRepoPath.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                                  (repo.Branch != null && repo.Branch.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                                  (repo.ThreeAxisSummary != null && repo.ThreeAxisSummary.Contains(query, StringComparison.OrdinalIgnoreCase));

                if (!matchesText)
                    return false;
            }

            // Host filter
            if (!isAllHosts && !string.Equals(repo.HostName, hostFilter, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Sync state filter
            var hasSyncStateFilter = FilterBehindOnly || FilterAheadOnly || FilterDivergedOnly;
            if (hasSyncStateFilter)
            {
                var matchesSync = false;
                var hasConfirmedFetch = repo.LastFetchedAt != null && !repo.HasFetchFailure;
                if (FilterBehindOnly && hasConfirmedFetch && (repo.Behind > 0 || repo.StateCode == RepoStateCode.Behind)) matchesSync = true;
                if (FilterAheadOnly && hasConfirmedFetch && (repo.Ahead > 0 || repo.StateCode == RepoStateCode.Ahead)) matchesSync = true;
                if (FilterDivergedOnly && hasConfirmedFetch && repo.StateCode == RepoStateCode.Diverged) matchesSync = true;

                if (!matchesSync)
                    return false;
            }

            // Worktree dirty filter (3축 분리)
            if (FilterDirtyOnly && repo.DirtyCount == 0)
            {
                return false;
            }

            // Conflicted filter (3축 분리)
            if (FilterConflictedOnly && repo.ConflictedCount == 0)
            {
                return false;
            }

            return true;
        }

        foreach (var root in RootTreeNodes)
        {
            root.ApplyFilter(Predicate);
        }

        UpdateExecutionCounts();

        // Boost priority for visible repos in the fetch queue (§4.1)
        var visibleIds = _repoViewModelMap.Values.Where(r => r.IsVisible).Select(r => r.Repo.Id).ToList();
        if (visibleIds.Count > 0)
        {
            BoostVisibleRepoPriority(visibleIds);
        }
    }

    [RelayCommand]
    public void SelectAllVisible()
    {
        foreach (var repo in _repoViewModelMap.Values)
        {
            if (repo.IsVisible)
            {
                repo.SetSelected(true, updateChildren: true, updateParent: true);
            }
        }
        UpdateExecutionCounts();
    }

    [RelayCommand]
    public void DeselectAll()
    {
        foreach (var repo in _repoViewModelMap.Values)
        {
            repo.SetSelected(false, updateChildren: true, updateParent: true);
        }
        UpdateExecutionCounts();
    }

    public void UpdateExecutionCounts()
    {
        var selectedRepos = _repoViewModelMap.Values.Where(r => r.IsSelected == true).ToList();
        TotalSelectedCount = selectedRepos.Count;

        // Pull calculation
        var pullable = 0;
        var pullSkipped = 0;

        var dirtySkip = 0;
        var divergedSkip = 0;
        var noUpstreamSkip = 0;
        var upstreamGoneSkip = 0;
        var detachedHeadSkip = 0;
        var untrackedSkip = 0;
        var conflictedSkip = 0;
        var upToDateSkip = 0;
        var missingSkip = 0;
        var unfetchedSkip = 0;
        var otherSkip = 0;

        // Push calculation (§7.5, §7.6)
        var pushable = 0;
        var pushSkipped = 0;

        var pushMissingSkip = 0;
        var pushConflictedSkip = 0;
        var pushNoUpstreamSkip = 0;
        var pushUpstreamGoneSkip = 0;
        var pushDetachedHeadSkip = 0;
        var pushDivergedSkip = 0;
        var pushBehindSkip = 0;
        var pushUpToDateSkip = 0;
        var pushUntrackedSkip = 0;
        var pushOtherSkip = 0;
        var pushDirtyWarningCount = 0;

        foreach (var r in selectedRepos)
        {
            // Pull evaluation
            if (r.IsMissing)
            {
                pullSkipped++;
                missingSkip++;
            }
            else if (r.ConflictedCount > 0 || r.StateCode == RepoStateCode.Conflicted)
            {
                pullSkipped++;
                conflictedSkip++;
            }
            else if (r.DirtyCount > 0)
            {
                pullSkipped++;
                dirtySkip++;
            }
            else if (r.StateCode == RepoStateCode.Diverged)
            {
                pullSkipped++;
                divergedSkip++;
            }
            else if (r.StateCode == RepoStateCode.NoUpstream)
            {
                pullSkipped++;
                noUpstreamSkip++;
            }
            else if (r.StateCode == RepoStateCode.UpstreamGone)
            {
                pullSkipped++;
                upstreamGoneSkip++;
            }
            else if (r.StateCode == RepoStateCode.DetachedHead)
            {
                pullSkipped++;
                detachedHeadSkip++;
            }
            else if (r.StateCode == RepoStateCode.Untracked)
            {
                pullSkipped++;
                untrackedSkip++;
            }
            else if (r.LastFetchedAt == null || r.HasFetchFailure)
            {
                pullSkipped++;
                unfetchedSkip++;
            }
            else if (r.Behind > 0 || r.StateCode == RepoStateCode.Behind)
            {
                pullable++;
            }
            else if (r.StateCode == RepoStateCode.UpToDate)
            {
                pullSkipped++;
                upToDateSkip++;
            }
            else
            {
                pullSkipped++;
                otherSkip++;
            }

            // Push evaluation (§7.5, §7.6)
            if (r.IsMissing)
            {
                pushSkipped++;
                pushMissingSkip++;
            }
            else if (r.Repo.GitDirKind != GitDirKind.Normal)
            {
                pushSkipped++;
                pushOtherSkip++;
            }
            else if (r.ConflictedCount > 0 || r.StateCode == RepoStateCode.Conflicted)
            {
                pushSkipped++;
                pushConflictedSkip++;
            }
            else if (r.StateCode == RepoStateCode.NoUpstream || string.IsNullOrWhiteSpace(r.Upstream))
            {
                pushSkipped++;
                pushNoUpstreamSkip++;
            }
            else if (r.StateCode == RepoStateCode.UpstreamGone)
            {
                pushSkipped++;
                pushUpstreamGoneSkip++;
            }
            else if (r.StateCode == RepoStateCode.DetachedHead || r.Branch == "(detached)" || string.IsNullOrWhiteSpace(r.Branch))
            {
                pushSkipped++;
                pushDetachedHeadSkip++;
            }
            else if (r.StateCode == RepoStateCode.Untracked)
            {
                pushSkipped++;
                pushUntrackedSkip++;
            }
            else if (r.StateCode == RepoStateCode.Diverged || (r.Ahead > 0 && r.Behind > 0))
            {
                pushSkipped++;
                pushDivergedSkip++;
            }
            else if (r.StateCode == RepoStateCode.Behind || (r.Ahead == 0 && r.Behind > 0))
            {
                pushSkipped++;
                pushBehindSkip++;
            }
            else if (r.Ahead > 0 && r.Behind == 0)
            {
                pushable++;
                if (r.DirtyCount > 0)
                {
                    pushDirtyWarningCount++;
                }
            }
            else
            {
                pushSkipped++;
                pushUpToDateSkip++;
            }
        }

        PullableCount = pullable;
        SkippedCount = pullSkipped;
        ExecutionSummaryText = $"선택 {TotalSelectedCount}개 (Pull 가능 {PullableCount} / 스킵 {SkippedCount})";

        SkipReasonList.Clear();
        if (dirtySkip > 0) SkipReasonList.Add($"• 미커밋 변경 (Dirty): {dirtySkip}건");
        if (divergedSkip > 0) SkipReasonList.Add($"• 브랜치 갈라짐 (Diverged): {divergedSkip}건");
        if (noUpstreamSkip > 0) SkipReasonList.Add($"• Upstream 없음: {noUpstreamSkip}건");
        if (upstreamGoneSkip > 0) SkipReasonList.Add($"• Upstream 삭제됨: {upstreamGoneSkip}건");
        if (detachedHeadSkip > 0) SkipReasonList.Add($"• Detached HEAD: {detachedHeadSkip}건");
        if (untrackedSkip > 0) SkipReasonList.Add($"• Remote 없음 (Untracked): {untrackedSkip}건");
        if (conflictedSkip > 0) SkipReasonList.Add($"• 머지 충돌 (Conflicted): {conflictedSkip}건");
        if (upToDateSkip > 0) SkipReasonList.Add($"• 이미 최신 (Up-to-Date): {upToDateSkip}건");
        if (missingSkip > 0) SkipReasonList.Add($"• 디렉토리 미발견 (Missing): {missingSkip}건");
        if (unfetchedSkip > 0) SkipReasonList.Add($"• 상태 미확인 (Fetch 필요): {unfetchedSkip}건");
        if (otherSkip > 0) SkipReasonList.Add($"• 기타 사유: {otherSkip}건");

        HasSkipReasons = SkipReasonList.Count > 0;
        SkipReasonSummary = string.Join(" | ", SkipReasonList);

        // Push Breakdown Summary
        PushableCount = pushable;
        PushSkippedCount = pushSkipped;
        PushExecutionSummaryText = $"Push 가능 {PushableCount} / 스킵 {PushSkippedCount}";

        PushSkipReasonList.Clear();
        if (pushDivergedSkip > 0) PushSkipReasonList.Add($"• pull 먼저 필요 (Diverged): {pushDivergedSkip}건");
        if (pushBehindSkip > 0) PushSkipReasonList.Add($"• pull 먼저 필요 (Behind): {pushBehindSkip}건");
        if (pushUpToDateSkip > 0) PushSkipReasonList.Add($"• 올릴 커밋 없음 (Up-to-Date): {pushUpToDateSkip}건");
        if (pushNoUpstreamSkip > 0) PushSkipReasonList.Add($"• 추적 브랜치 없음: {pushNoUpstreamSkip}건");
        if (pushUpstreamGoneSkip > 0) PushSkipReasonList.Add($"• Upstream 삭제됨: {pushUpstreamGoneSkip}건");
        if (pushDetachedHeadSkip > 0) PushSkipReasonList.Add($"• Detached HEAD: {pushDetachedHeadSkip}건");
        if (pushConflictedSkip > 0) PushSkipReasonList.Add($"• 머지 충돌 (Conflicted): {pushConflictedSkip}건");
        if (pushUntrackedSkip > 0) PushSkipReasonList.Add($"• Remote 없음 (Untracked): {pushUntrackedSkip}건");
        if (pushMissingSkip > 0) PushSkipReasonList.Add($"• 디렉토리 미발견 (Missing): {pushMissingSkip}건");
        if (pushOtherSkip > 0) PushSkipReasonList.Add($"• 기타 사유: {pushOtherSkip}건");

        if (pushDirtyWarningCount > 0)
        {
            PushSkipReasonList.Add($"⚠️ Push 대상 중 {pushDirtyWarningCount}개에 미커밋 파일 있음 (미커밋 파일은 제외됨)");
        }

        HasPushSkipReasons = PushSkipReasonList.Count > 0;
        PushSkipReasonSummary = string.Join(" | ", PushSkipReasonList);
    }

    [RelayCommand]
    public async Task InitializeAsync()
    {
        StatusMessage = "Git 환경 점검 중...";
        var envInfo = await _gitRunner.CheckEnvironmentAsync();

        if (!envInfo.IsAvailable)
        {
            IsGitAvailable = false;
            GitDiagnosticError = envInfo.ErrorMessage;
            StatusMessage = "Git 환경 오류 — 작업을 시작할 수 없습니다.";
            _logger.LogWarning("Git 환경 점검 실패: {Error}", envInfo.ErrorMessage);
            return;
        }

        IsGitAvailable = true;
        GitVersionString = envInfo.VersionString;
        StatusMessage = $"Git 환경 정상 ({GitVersionString})";
        _logger.LogInformation("Git 환경 정상 확인: {Version}", GitVersionString);

        await LoadDataAsync();
        await CloneTab.InitializeAsync();

        // Check residual temp folders on startup (§7.3, §10 Criterion 21)
        try
        {
            var allRoots = await _scanRootManager.GetRootsAsync();
            var activeRoots = allRoots.Where(r => r.IsActive).ToList();
            var residuals = _cloneEngine.FindResidualTempFolders(activeRoots.Select(r => r.Path));
            if (residuals.Count > 0)
            {
                HasResidualTempFolders = true;
                ResidualTempFoldersNotice = $"⚠️ 이전에 중단된 임시 클론 폴더 {residuals.Count}개가 발견되었습니다.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check for residual temp folders on startup");
        }

        // Start initial background fetch on startup (설계 §4.1)
        if (_repoViewModelMap.Count > 0)
        {
            var fetchTargets = _repoViewModelMap.Values
                .Where(r => !r.IsMissing && r.Repo.GitDirKind == GitDirKind.Normal && !string.IsNullOrWhiteSpace(r.Repo.NormalizedKey))
                .Select(r => (r.Repo.Id, r.Repo.AbsPath, r.Repo.NormalizedKey!));
            await _fetchEngine.EnqueueReposAsync(fetchTargets);
        }
    }

    [RelayCommand]
    public async Task LoadDataAsync()
    {
        try
        {
            var roots = await _scanRootManager.GetRootsAsync();
            ScanRoots.Clear();
            foreach (var r in roots)
            {
                ScanRoots.Add(new ScanRootItemViewModel(r, async item =>
                {
                    await _scanRootManager.UpdateRootAsync(item.ToModel());
                    _logger.LogInformation("스캔 루트 활성 상태 변경: {Path} -> IsActive={IsActive}", item.Path, item.IsActive);
                }));
            }

            var bulkQueryCount = 0;
            var queryTimer = Stopwatch.StartNew();
            bulkQueryCount++;
            var reposWithStatus = await _indexStore.GetAllLocalReposWithStatusAsync();
            var serverProfiles = await _indexStore.GetServerProfilesAsync();
            var serverRepos = await _indexStore.GetAllServerReposAsync();
            queryTimer.Stop();

            var treeBuildTimer = Stopwatch.StartNew();
            _repoViewModelMap.Clear();

            // Build hierarchical tree using Core RepoTreeBuilder with Server Profiles and Catalog
            var treeFolders = RepoTreeBuilder.BuildTree(roots, reposWithStatus, serverProfiles, serverRepos);

            RootTreeNodes.Clear();
            var distinctHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var folder in treeFolders)
            {
                var folderVm = CreateFolderViewModel(folder, parent: null, distinctHosts);
                RootTreeNodes.Add(folderVm);
            }

            // Populate AvailableHosts filter
            AvailableHosts.Clear();
            AvailableHosts.Add("전체 호스트");
            foreach (var host in distinctHosts.OrderBy(x => x))
            {
                AvailableHosts.Add(host);
            }

            treeBuildTimer.Stop();
            _logger.LogInformation(
                "TreeBuildMetrics RepoCount={RepoCount} BulkQueryCount={BulkQueryCount} QueryDurationMs={QueryDurationMs:F3} TreeBuildDurationMs={TreeBuildDurationMs:F3}",
                reposWithStatus.Count,
                bulkQueryCount,
                queryTimer.Elapsed.TotalMilliseconds,
                treeBuildTimer.Elapsed.TotalMilliseconds);

            // Check gitignore warnings for nested repos
            foreach (var repoVm in _repoViewModelMap.Values)
            {
                if (repoVm.Parent is RepoTreeNodeViewModel parentRepoVm)
                {
                    repoVm.IsNested = true;
                    var isIgnored = await NestedRepoChecker.IsChildIgnoredByParentAsync(_gitRunner, parentRepoVm.FullPath, repoVm.FullPath);
                    repoVm.HasGitIgnoreWarning = !isIgnored;
                }
            }

            // Detect URL change candidates for Orphaned repos (§3.5, §10.7, 완료 기준 16)
            foreach (var repoVm in _repoViewModelMap.Values)
            {
                if (repoVm.IsOrphaned && !string.IsNullOrWhiteSpace(repoVm.Repo.NormalizedKey))
                {
                    var keyParts = repoVm.Repo.NormalizedKey.Split(new[] { '/' }, 2);
                    if (keyParts.Length == 2)
                    {
                        var repoPathPart = keyParts[1].ToLowerInvariant();
                        var candidateRepo = serverRepos.FirstOrDefault(sr =>
                            !sr.NormalizedKey.StartsWith(repoVm.HostName + ":", StringComparison.OrdinalIgnoreCase) &&
                            sr.NormalizedKey.EndsWith("/" + repoPathPart, StringComparison.OrdinalIgnoreCase));

                        if (candidateRepo != null)
                        {
                            repoVm.HasUrlChangeSuggestion = true;
                            repoVm.UrlChangeOldUrl = $"https://{repoVm.Repo.NormalizedKey}.git";
                            repoVm.UrlChangeNewHostUrl = candidateRepo.CloneUrl;
                            repoVm.UrlChangeSuggestionText = $"URL 변경 감지: {candidateRepo.CloneUrl}";
                        }
                    }
                }
            }

            TotalRepoCount = _repoViewModelMap.Count;
            ApplyFilters();
            await LoadJobHistoryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load index data from SQLite store");
            StatusMessage = $"데이터 로드 실패: {ex.Message}";
        }
    }

    private FolderTreeNodeViewModel CreateFolderViewModel(
        TreeFolderNode folderNode,
        TreeNodeViewModelBase? parent,
        HashSet<string> distinctHosts)
    {
        var folderVm = new FolderTreeNodeViewModel(folderNode) { Parent = parent };
        folderVm.SelectionChanged += OnTreeNodeSelectionChanged;

        foreach (var sub in folderNode.SubFolders)
        {
            var subVm = CreateFolderViewModel(sub, folderVm, distinctHosts);
            folderVm.Children.Add(subVm);
        }

        foreach (var repo in folderNode.Repos)
        {
            var repoVm = CreateRepoViewModel(repo, folderVm, distinctHosts);
            folderVm.Children.Add(repoVm);
        }

        return folderVm;
    }

    private RepoTreeNodeViewModel CreateRepoViewModel(
        TreeRepoNode repoNode,
        TreeNodeViewModelBase? parent,
        HashSet<string> distinctHosts)
    {
        var repoVm = new RepoTreeNodeViewModel(repoNode, FocusRepo) { Parent = parent };
        repoVm.SelectionChanged += OnTreeNodeSelectionChanged;

        // Wire context menu and suggestion callbacks (§10.1, §10.7)
        repoVm.OnChangeRemoteUrlRequested = HandleChangeRemoteUrl;
        repoVm.OnForcePushRequested = async vm => await HandleForcePushAsync(vm);
        repoVm.OnPushNoUpstreamRequested = async vm => await HandlePushNoUpstreamAsync(vm);
        repoVm.OnSetUpstreamGoneRequested = async vm => await HandleSetUpstreamGoneAsync(vm);
        repoVm.OnApplyUrlChangeRequested = async vm => await HandleApplyUrlChangeAsync(vm);

        _repoViewModelMap[repoNode.Repo.Id] = repoVm;

        if (!string.IsNullOrWhiteSpace(repoNode.HostName) && repoNode.HostName != "Local")
        {
            distinctHosts.Add(repoNode.HostName);
        }

        // Add nested children recursively
        foreach (var nested in repoNode.NestedChildRepos)
        {
            var nestedVm = CreateRepoViewModel(nested, repoVm, distinctHosts);
            repoVm.Children.Add(nestedVm);
        }

        return repoVm;
    }

    private void OnTreeNodeSelectionChanged(object? sender, EventArgs e) => UpdateExecutionCounts();

    public void FocusRepo(long repoId)
    {
        if (_repoViewModelMap.TryGetValue(repoId, out var repoVm))
        {
            var current = repoVm.Parent;
            while (current != null)
            {
                current.IsExpanded = true;
                current = current.Parent;
            }
            repoVm.IsExpanded = true;
            repoVm.SetSelected(true);
            StatusMessage = $"레포 사본 이동: {repoVm.FolderName} ({repoVm.FullPath})";
        }
    }

    public void BoostVisibleRepoPriority(IEnumerable<long> visibleIds)
    {
        _fetchEngine.BoostVisibleRepos(visibleIds);
    }

    private void OnRepoFetched(object? sender, RepoFetchResult e)
    {
        // UI thread dispatch if needed
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (_repoViewModelMap.TryGetValue(e.LocalRepoId, out var vm))
            {
                if (e.IsSuccess && e.UpdatedStatus != null)
                {
                    vm.UpdateStatus(e.UpdatedStatus);
                }
                else
                {
                    var failureLabel = e.FailureKind switch
                    {
                        FetchFailureKind.Authentication => "미확인 (인증 실패)",
                        FetchFailureKind.Cooldown => "미확인 (인증 쿨오프)",
                        FetchFailureKind.Network => "미확인 (fetch 실패: 네트워크)",
                        _ => "미확인 (fetch 실패)"
                    };
                    vm.SetFetchFailure(failureLabel);
                }
            }
            UpdateExecutionCounts();
        });
    }

    private void OnCooldownsChanged(object? sender, EventArgs e)
    {
        _logger.LogInformation("CooldownsChanged received by MainViewModel");
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(UpdateCooldownSummary);
    }

    private void UpdateCooldownSummary()
    {
        var now = DateTime.UtcNow;
        var cooldowns = _fetchEngine.ActiveCooldowns;
        HasAuthenticationCooldowns = cooldowns.Count > 0;
        AuthenticationCooldownSummary = string.Join(
            Environment.NewLine,
            cooldowns.Select(cooldown =>
            {
                var remaining = cooldown.Remaining(now);
                var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
                return $"{cooldown.Host}: {cooldown.Reason} (재시도까지 약 {minutes}분)";
            }));
        _logger.LogInformation(
            "AuthCooldownUiState Visible={Visible} Count={Count} Summary={Summary}",
            HasAuthenticationCooldowns,
            cooldowns.Count,
            AuthenticationCooldownSummary);
    }

    private void OnFetchingStateChanged(object? sender, bool isFetching)
    {
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            IsFetching = isFetching;
            PendingFetchCount = _fetchEngine.PendingCount;
            if (isFetching)
            {
                StatusMessage = $"백그라운드 Fetch 진행 중 (대기: {PendingFetchCount}개)...";
            }
            else
            {
                StatusMessage = "Fetch 완료";
            }
        });
    }

    private void UpdateAllFreshnessLabels()
    {
        foreach (var repo in _repoViewModelMap.Values)
        {
            repo.UpdateFreshness();
        }
    }

    [RelayCommand]
    public async Task RefreshAllAsync()
    {
        // F5 manual refresh: re-scan and fetch (지시서 §0 C-2, §2-1)
        _fetchEngine.ClearCooldowns();
        UpdateCooldownSummary();
        await StartScanAsync();

        if (_repoViewModelMap.Count > 0)
        {
            var fetchTargets = _repoViewModelMap.Values
                .Where(r => !r.IsMissing && r.Repo.GitDirKind == GitDirKind.Normal && !string.IsNullOrWhiteSpace(r.Repo.NormalizedKey))
                .Select(r => (r.Repo.Id, r.Repo.AbsPath, r.Repo.NormalizedKey!));
            await _fetchEngine.EnqueueReposAsync(fetchTargets);
        }
    }

    [RelayCommand]
    public async Task AddScanRootAsync()
    {
        if (string.IsNullOrWhiteSpace(NewScanRootPath))
            return;

        try
        {
            var (rootId, wasNestedAbsorbed, wasRejected, message) = await _scanRootManager.AddOrAbsorbRootAsync(NewScanRootPath, NewScanRootDepth);

            _logger.LogInformation("스캔 루트 추가 시도: {Path} (결과: RootId={RootId}, Absorbed={Absorbed}, Rejected={Rejected})", NewScanRootPath, rootId, wasNestedAbsorbed, wasRejected);

            NewScanRootPath = string.Empty;
            await LoadDataAsync();

            StatusMessage = message ?? "스캔 루트가 처리되었습니다.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add scan root");
            StatusMessage = $"루트 추가 실패: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task RemoveScanRootAsync(ScanRootItemViewModel? item)
    {
        if (item == null)
            return;

        try
        {
            await _scanRootManager.RemoveRootAsync(item.Id);
            _logger.LogInformation("스캔 루트 제거: {Path} (Id={Id})", item.Path, item.Id);

            await LoadDataAsync();
            StatusMessage = $"스캔 루트 '{item.Path}'가 제거되었습니다.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove scan root");
            StatusMessage = $"루트 제거 실패: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task StartScanAsync()
    {
        if (!IsGitAvailable)
        {
            StatusMessage = "Git 환경 오류로 스캔을 시작할 수 없습니다.";
            return;
        }

        if (IsScanning)
            return;

        IsScanning = true;
        StatusMessage = "스캔 진행 중...";
        ScanReportSummary = string.Empty;

        var progress = new Progress<(string CurrentDirectory, int FoundCount)>(p =>
        {
            StatusMessage = $"스캔 중 ({p.FoundCount}개 발견): {p.CurrentDirectory}";
        });

        try
        {
            var roots = ScanRoots.Select(r => r.ToModel()).ToList();
            var report = await _repoScanner.ScanAsync(roots, progress);

            InaccessiblePaths.Clear();
            foreach (var p in report.InaccessiblePaths)
            {
                InaccessiblePaths.Add(p);
            }
            HasInaccessiblePaths = InaccessiblePaths.Count > 0;
            if (!HasInaccessiblePaths)
            {
                IsInaccessiblePathsExpanded = false;
            }

            await LoadDataAsync();

            // Nested repo gitignore warning check
            foreach (var repoVm in _repoViewModelMap.Values.Where(r => r.IsNested && r.Repo.ParentRepoId.HasValue))
            {
                if (_repoViewModelMap.TryGetValue(repoVm.Repo.ParentRepoId!.Value, out var parentVm))
                {
                    var isIgnored = await NestedRepoChecker.IsChildIgnoredByParentAsync(_gitRunner, parentVm.FullPath, repoVm.FullPath);
                    repoVm.HasGitIgnoreWarning = !isIgnored;
                }
            }

            // Background progressive fetch for normal non-missing repos with remotes (§4.1)
            if (_repoViewModelMap.Count > 0)
            {
                var fetchTargets = _repoViewModelMap.Values
                    .Where(r => !r.IsMissing && r.Repo.GitDirKind == GitDirKind.Normal && !string.IsNullOrWhiteSpace(r.Repo.NormalizedKey))
                    .Select(r => (r.Repo.Id, r.Repo.AbsPath, r.Repo.NormalizedKey!));
                await _fetchEngine.EnqueueReposAsync(fetchTargets);
            }

            var inaccessibleText = report.InaccessiblePaths.Count > 0
                ? $", 접근 실패: {report.InaccessiblePaths.Count}건"
                : string.Empty;

            ScanReportSummary = $"스캔 완료: 총 {report.TotalDiscovered}개 발견 (신규: {report.AddedCount}, 갱신: {report.UpdatedCount}, 유지: {report.UnchangedCount}{inaccessibleText}, 소요시간: {report.Duration.TotalSeconds:F2}초)";
            StatusMessage = ScanReportSummary;

            _logger.LogInformation(
                "스캔 완료: 총 {Total}개 발견 (신규: {Added}, 갱신: {Updated}, 유지: {Unchanged}, 접근실패: {InaccessibleCount}건), 소요시간: {Duration:F2}초.",
                report.TotalDiscovered, report.AddedCount, report.UpdatedCount, report.UnchangedCount,
                report.InaccessiblePaths.Count, report.Duration.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during repository scan");
            StatusMessage = $"스캔 오류 발생: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    #region Pull Execution (§8, §5.5, §7.3)

    [RelayCommand]
    public async Task ExecutePullAsync()
    {
        var selectedRepoVms = _repoViewModelMap.Values.Where(r => r.IsSelected == true).ToList();
        if (selectedRepoVms.Count == 0)
            return;

        var targets = selectedRepoVms.Select(vm => (vm.Repo, vm.Status)).ToList();
        var previewItems = _pullEngine.GeneratePreview(targets);

        var previewVm = new PullPreviewViewModel(previewItems);
        var previewWindow = new PullPreviewWindow(previewVm);
        if (System.Windows.Application.Current?.MainWindow != null)
        {
            previewWindow.Owner = System.Windows.Application.Current.MainWindow;
        }

        var confirmed = previewWindow.ShowDialog();
        if (confirmed != true)
        {
            _logger.LogInformation("일괄 Pull이 사용자에 의해 미리보기 단계에서 취소되었습니다.");
            return;
        }

        _pullCts = new CancellationTokenSource();
        IsPullPanelVisible = true;
        IsPullExecuting = true;
        IsPullCompleted = false;
        HasPullReport = false;
        PullProgressPercent = 0;
        PullHeaderTitle = "🚀 일괄 Pull 실행 진행 중...";
        PullHeaderColor = "#0F172A";
        PullPanelBorderBrush = "#CBD5E1";
        PullPanelBackground = "#F8FAFC";
        PullProgressText = $"일괄 Pull 시작 중 (총 {targets.Count}개)...";

        PullExecutionItems.Clear();
        foreach (var p in previewItems)
        {
            var initialStatus = p.IsEligible ? PullJobStatus.Pending : PullJobStatus.Skipped;
            PullExecutionItems.Add(new PullExecutionItemViewModel(p.RepoId, p.AbsPath, initialStatus, p.SkipReason));
        }

        var progress = new Progress<PullProgressInfo>(info =>
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                var percent = info.TotalCount > 0 ? (int)((info.CompletedCount / (double)info.TotalCount) * 100) : 0;
                PullProgressPercent = percent;
                PullProgressText = $"진행 중: {info.CompletedCount} / {info.TotalCount} ({percent}%)";

                var itemVm = PullExecutionItems.FirstOrDefault(x => x.RepoId == info.CurrentRepoId);
                if (itemVm != null)
                {
                    itemVm.Status = info.CurrentStatus;
                    itemVm.Detail = info.StatusDetail;
                }
            });
        });

        try
        {
            var report = await _pullEngine.ExecuteBatchPullAsync(targets, progress, _pullCts.Token);
            IsPullCompleted = true;
            var durationStr = FormatDuration(report.Duration);
            PullProgressText = $"Pull 완료 — 성공 {report.SuccessCount} / 실패 {report.FailedCount} / 스킵 {report.SkippedCount} (소요 {durationStr})";
            PullReportSummary = PullProgressText;
            HasPullReport = true;
            StatusMessage = PullProgressText;

            if (report.FailedCount > 0)
            {
                PullHeaderTitle = $"⚠️ 일괄 Pull 완료 (실패 {report.FailedCount}건 발생)";
                PullHeaderColor = "#DC2626";
                PullPanelBorderBrush = "#FCA5A5";
                PullPanelBackground = "#FEF2F2";
            }
            else if (report.SkippedCount > 0)
            {
                PullHeaderTitle = $"⚠️ 일괄 Pull 완료 (스킵 {report.SkippedCount}건 발생)";
                PullHeaderColor = "#D97706";
                PullPanelBorderBrush = "#FCD34D";
                PullPanelBackground = "#FFFBEB";
            }
            else
            {
                PullHeaderTitle = "✅ 일괄 Pull 완료 (모든 대상 성공)";
                PullHeaderColor = "#16A34A";
                PullPanelBorderBrush = "#86EFAC";
                PullPanelBackground = "#F0FDF4";
            }

            // Update items with final results
            foreach (var res in report.Results)
            {
                var itemVm = PullExecutionItems.FirstOrDefault(x => x.RepoId == res.RepoId);
                if (itemVm != null)
                {
                    itemVm.Status = res.Status;
                    itemVm.Detail = res.ErrorMessage ?? res.SkipReason ?? (res.Status == PullJobStatus.Success ? "성공" : null);
                }
            }

            // Error Grouping (§10.2, 완료 기준 4, 5)
            UpdatePullErrorGroups(report.Results);

            await LoadDataAsync();

            // Background Completion Notification (§10.5, 완료 기준 30)
            NotifyBatchCompletion("Pull", report.SuccessCount, report.FailedCount, report.SkippedCount);
        }
        catch (OperationCanceledException)
        {
            IsPullCompleted = true;
            PullHeaderTitle = "🛑 일괄 Pull 취소됨";
            PullHeaderColor = "#991B1B";
            PullPanelBorderBrush = "#FCA5A5";
            PullPanelBackground = "#FEF2F2";
            PullProgressText = "일괄 Pull 작업이 사용자에 의해 취소되었습니다.";
            PullReportSummary = PullProgressText;
            HasPullReport = true;
            StatusMessage = PullReportSummary;
        }
        catch (Exception ex)
        {
            IsPullCompleted = true;
            PullHeaderTitle = "🚨 일괄 Pull 중 오류 발생";
            PullHeaderColor = "#DC2626";
            PullPanelBorderBrush = "#FCA5A5";
            PullPanelBackground = "#FEF2F2";
            _logger.LogError(ex, "일괄 Pull 실행 중 예외 발생");
            PullProgressText = $"일괄 Pull 중 오류 발생: {ex.Message}";
            PullReportSummary = PullProgressText;
            HasPullReport = true;
            StatusMessage = PullReportSummary;
        }
        finally
        {
            IsPullExecuting = false;
            _pullCts?.Dispose();
            _pullCts = null;
        }
    }

    private void UpdatePullErrorGroups(IEnumerable<PullJobResult> results)
    {
        PullErrorGroups.Clear();
        var failed = results.Where(r => r.Status == PullJobStatus.Failed).ToList();
        if (failed.Count == 0)
        {
            HasPullErrorGroups = false;
            return;
        }

        var grouped = failed.GroupBy(r => ErrorClassifier.Classify(r.ErrorMessage));
        foreach (var group in grouped)
        {
            var errorGroupVm = new ErrorGroupSummaryViewModel(
                group.Key,
                group.Select(x => x.RepoId),
                RetryPullCategoryAsync);
            PullErrorGroups.Add(errorGroupVm);
        }
        HasPullErrorGroups = true;
    }

    private async Task RetryPullCategoryAsync(ErrorCategory category, IEnumerable<long> repoIds)
    {
        var idSet = new HashSet<long>(repoIds);
        var targetVms = _repoViewModelMap.Values.Where(r => idSet.Contains(r.Repo.Id)).ToList();
        if (targetVms.Count == 0) return;

        var targets = targetVms.Select(vm => (vm.Repo, vm.Status)).ToList();
        _pullCts = new CancellationTokenSource();
        IsPullPanelVisible = true;
        IsPullExecuting = true;
        HasPullReport = false;
        PullProgressPercent = 0;
        PullProgressText = $"카테고리 '{category}' 재시도 실행 중 (총 {targets.Count}개)...";

        var progress = new Progress<PullProgressInfo>(info =>
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                var percent = info.TotalCount > 0 ? (int)((info.CompletedCount / (double)info.TotalCount) * 100) : 0;
                PullProgressPercent = percent;
                PullProgressText = $"재시도 진행 중: {info.CompletedCount} / {info.TotalCount} ({percent}%)";
            });
        });

        try
        {
            var report = await _pullEngine.ExecuteBatchPullAsync(targets, progress, _pullCts.Token);
            PullReportSummary = $"재시도 완료: {report.SummaryText}";
            HasPullReport = true;
            StatusMessage = PullReportSummary;
            UpdatePullErrorGroups(report.Results);
            await LoadDataAsync();
        }
        finally
        {
            IsPullExecuting = false;
            _pullCts?.Dispose();
            _pullCts = null;
        }
    }

    [RelayCommand]
    public void CancelPull()
    {
        if (IsPullExecuting && _pullCts != null)
        {
            _logger.LogInformation("사용자가 일괄 Pull 취소 요청을 전달했습니다.");
            _pullCts.Cancel();
        }
    }

    [RelayCommand]
    public void ClosePullPanel()
    {
        if (!IsPullExecuting)
        {
            IsPullPanelVisible = false;
        }
    }

    #endregion

    [RelayCommand]
    public void TogglePushSkipDetails()
    {
        IsPushSkipDetailsExpanded = !IsPushSkipDetailsExpanded;
    }

    #region Push Execution (§8.2, §8.3, §7.5, §7.6)

    [RelayCommand]
    public async Task ExecutePushAsync()
    {
        var selectedRepoVms = _repoViewModelMap.Values.Where(r => r.IsSelected == true).ToList();
        if (selectedRepoVms.Count == 0)
            return;

        var targets = selectedRepoVms.Select(vm => (vm.Repo, vm.Status)).ToList();

        StatusMessage = "Push 미리보기 생성 중 (실서버 Dry-run 검증)...";
        var previewItems = await _pushEngine.GeneratePreviewAsync(targets).ConfigureAwait(true);

        var previewVm = new PushPreviewViewModel(previewItems);
        var previewWindow = new PushPreviewWindow(previewVm);
        if (System.Windows.Application.Current?.MainWindow != null)
        {
            previewWindow.Owner = System.Windows.Application.Current.MainWindow;
        }

        var tcs = new TaskCompletionSource<bool>();
        previewWindow.Closed += (_, _) =>
        {
            tcs.TrySetResult(previewWindow.UserConfirmed == true);
        };
        previewWindow.Show();
        var confirmed = await tcs.Task.ConfigureAwait(true);
        if (!confirmed)
        {
            _logger.LogInformation("일괄 Push가 사용자에 의해 미리보기 단계에서 취소되었습니다.");
            StatusMessage = "일괄 Push 미리보기 취소됨";
            return;
        }

        _pushCts = new CancellationTokenSource();
        IsPushPanelVisible = true;
        IsPushExecuting = true;
        IsPushCompleted = false;
        HasPushReport = false;
        PushProgressPercent = 0;
        PushHeaderTitle = "🚀 일괄 Push 실행 진행 중...";
        PushHeaderColor = "#1E3A8A";
        PushPanelBorderBrush = "#BFDBFE";
        PushPanelBackground = "#F8FAFC";
        PushProgressText = $"일괄 Push 시작 중 (총 {previewItems.Count}개)...";

        PushExecutionItems.Clear();
        foreach (var p in previewItems)
        {
            var initialStatus = p.IsEligible ? PushItemUiStatus.Pending : PushItemUiStatus.Skipped;
            PushExecutionItems.Add(new PushExecutionItemViewModel(p.RepoId, p.AbsPath, initialStatus, p.SkipReason, p.DirtyWarning));
        }

        var progress = new Progress<BatchPushProgress>(info =>
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                var percent = info.TotalCount > 0 ? (int)((info.ProcessedCount / (double)info.TotalCount) * 100) : 0;
                PushProgressPercent = percent;
                PushProgressText = $"진행 중: {info.ProcessedCount} / {info.TotalCount} ({percent}%) — 성공: {info.SuccessCount}, 실패: {info.FailedCount}, 스킵: {info.SkippedCount}";

                if (!string.IsNullOrWhiteSpace(info.CurrentRepoPath))
                {
                    var itemVm = PushExecutionItems.FirstOrDefault(x => string.Equals(x.AbsPath, info.CurrentRepoPath, StringComparison.OrdinalIgnoreCase));
                    if (itemVm != null)
                    {
                        itemVm.Status = PushItemUiStatus.Running;
                    }
                }
            });
        });

        try
        {
            var report = await _pushEngine.ExecuteAsync(previewItems, progress, _pushCts.Token).ConfigureAwait(true);
            IsPushCompleted = true;
            var durationStr = FormatDuration(report.TotalDuration);
            PushProgressText = $"Push 완료 — 성공 {report.SuccessCount} / 실패 {report.FailedCount} / 스킵 {report.SkippedCount} (소요 {durationStr})";
            PushReportSummary = PushProgressText;
            HasPushReport = true;
            StatusMessage = PushProgressText;

            if (report.FailedCount > 0)
            {
                PushHeaderTitle = $"⚠️ 일괄 Push 완료 (실패 {report.FailedCount}건 발생)";
                PushHeaderColor = "#DC2626";
                PushPanelBorderBrush = "#FCA5A5";
                PushPanelBackground = "#FEF2F2";
            }
            else if (report.SkippedCount > 0)
            {
                PushHeaderTitle = $"⚠️ 일괄 Push 완료 (스킵 {report.SkippedCount}건 발생)";
                PushHeaderColor = "#D97706";
                PushPanelBorderBrush = "#FCD34D";
                PushPanelBackground = "#FFFBEB";
            }
            else
            {
                PushHeaderTitle = "✅ 일괄 Push 완료 (모든 대상 성공)";
                PushHeaderColor = "#16A34A";
                PushPanelBorderBrush = "#86EFAC";
                PushPanelBackground = "#F0FDF4";
            }

            var previewEligibleMap = previewItems.ToDictionary(p => p.RepoId, p => p.IsEligible);

            // Update items with final results
            foreach (var res in report.Results)
            {
                var itemVm = PushExecutionItems.FirstOrDefault(x => x.RepoId == res.RepoId);
                if (itemVm != null)
                {
                    itemVm.Status = res.Success ? PushItemUiStatus.Success : PushItemUiStatus.Failed;

                    // Criterion 7: Dry-run passed but permission failed at execution
                    var passedDryRun = previewEligibleMap.TryGetValue(res.RepoId, out var passed) && passed;
                    if (!res.Success && res.ErrorCategory == ErrorCategory.Permission && passedDryRun)
                    {
                        itemVm.Detail = "dry-run은 통과했으나 실행 시 권한 거부됨";
                    }
                    else
                    {
                        itemVm.Detail = res.ErrorMessage ?? (res.Success ? "성공" : null);
                    }

                    itemVm.DirtyWarning = res.DirtyWarning;
                }
            }

            // Error Grouping (§10.2, 완료 기준 4, 5, 7)
            UpdatePushErrorGroups(report.Results, previewItems);

            await LoadDataAsync().ConfigureAwait(true);

            // Background Completion Notification (§10.5, 완료 기준 30)
            NotifyBatchCompletion("Push", report.SuccessCount, report.FailedCount, report.SkippedCount);
        }
        catch (OperationCanceledException)
        {
            IsPushCompleted = true;
            PushHeaderTitle = "🛑 일괄 Push 취소됨";
            PushHeaderColor = "#991B1B";
            PushPanelBorderBrush = "#FCA5A5";
            PushPanelBackground = "#FEF2F2";
            PushProgressText = "일괄 Push 작업이 사용자에 의해 취소되었습니다.";
            PushReportSummary = PushProgressText;
            HasPushReport = true;
            StatusMessage = PushReportSummary;
        }
        catch (Exception ex)
        {
            IsPushCompleted = true;
            PushHeaderTitle = "🚨 일괄 Push 중 오류 발생";
            PushHeaderColor = "#DC2626";
            PushPanelBorderBrush = "#FCA5A5";
            PushPanelBackground = "#FEF2F2";
            _logger.LogError(ex, "일괄 Push 실행 중 예외 발생");
            PushProgressText = $"일괄 Push 중 오류 발생: {ex.Message}";
            PushReportSummary = PushProgressText;
            HasPushReport = true;
            StatusMessage = PushReportSummary;
        }
        finally
        {
            IsPushExecuting = false;
            _pushCts?.Dispose();
            _pushCts = null;
        }
    }

    private void UpdatePushErrorGroups(
        IEnumerable<PushExecutionItemResult> results,
        IReadOnlyList<PushPreviewItem> previewItems)
    {
        PushErrorGroups.Clear();
        var failed = results.Where(r => !r.Success).ToList();
        if (failed.Count == 0)
        {
            HasPushErrorGroups = false;
            return;
        }

        var previewEligibleIds = new HashSet<long>(previewItems.Where(p => p.IsEligible).Select(p => p.RepoId));

        var grouped = failed.GroupBy(r => r.ErrorCategory ?? ErrorCategory.Other);
        foreach (var group in grouped)
        {
            var hasDryRunPassPermissionFailNotice = group.Key == ErrorCategory.Permission &&
                group.Any(x => previewEligibleIds.Contains(x.RepoId));

            var errorGroupVm = new ErrorGroupSummaryViewModel(
                group.Key,
                group.Select(x => x.RepoId),
                RetryPushCategoryAsync,
                hasDryRunPassPermissionFailNotice);
            PushErrorGroups.Add(errorGroupVm);
        }
        HasPushErrorGroups = true;
    }

    private async Task RetryPushCategoryAsync(ErrorCategory category, IEnumerable<long> repoIds)
    {
        var idSet = new HashSet<long>(repoIds);
        var targetVms = _repoViewModelMap.Values.Where(r => idSet.Contains(r.Repo.Id)).ToList();
        if (targetVms.Count == 0) return;

        var targets = targetVms.Select(vm => (vm.Repo, vm.Status)).ToList();
        var previewItems = await _pushEngine.GeneratePreviewAsync(targets).ConfigureAwait(true);

        _pushCts = new CancellationTokenSource();
        IsPushPanelVisible = true;
        IsPushExecuting = true;
        HasPushReport = false;
        PushProgressPercent = 0;
        PushProgressText = $"카테고리 '{category}' 재시도 실행 중 (총 {previewItems.Count}개)...";

        var progress = new Progress<BatchPushProgress>(info =>
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                var percent = info.TotalCount > 0 ? (int)((info.ProcessedCount / (double)info.TotalCount) * 100) : 0;
                PushProgressPercent = percent;
                PushProgressText = $"재시도 진행 중: {info.ProcessedCount} / {info.TotalCount} ({percent}%)";
            });
        });

        try
        {
            var report = await _pushEngine.ExecuteAsync(previewItems, progress, _pushCts.Token).ConfigureAwait(true);
            PushReportSummary = $"재시도 완료: Push 성공 {report.SuccessCount}개, 실패 {report.FailedCount}개";
            HasPushReport = true;
            StatusMessage = PushReportSummary;
            UpdatePushErrorGroups(report.Results, previewItems);
            await LoadDataAsync().ConfigureAwait(true);
        }
        finally
        {
            IsPushExecuting = false;
            _pushCts?.Dispose();
            _pushCts = null;
        }
    }

    [RelayCommand]
    public void CancelPush()
    {
        if (IsPushExecuting && _pushCts != null)
        {
            _logger.LogInformation("사용자가 일괄 Push 취소 요청을 전달했습니다.");
            _pushCts.Cancel();
        }
    }

    [RelayCommand]
    public void ClosePushPanel()
    {
        if (!IsPushExecuting)
        {
            IsPushPanelVisible = false;
        }
    }

    #endregion

    #region Job History & Rollback (§10.3, 완료 기준 8~13)

    partial void OnHistorySearchTextChanged(string value) => _ = LoadJobHistoryAsync();
    partial void OnSelectedJobTypeFilterChanged(string value) => _ = LoadJobHistoryAsync();

    [RelayCommand]
    public async Task LoadJobHistoryAsync()
    {
        IsJobHistoryLoading = true;
        try
        {
            var limit = IsFullHistoryMode ? 0 : 100;
            var jType = SelectedJobTypeFilter == "전체" ? null : SelectedJobTypeFilter;
            var search = string.IsNullOrWhiteSpace(HistorySearchText) ? null : HistorySearchText.Trim();

            var entries = await _indexStore.GetJobHistoryFilteredAsync(search, jType, limit);
            JobHistoryItems.Clear();
            foreach (var entry in entries)
            {
                JobHistoryItems.Add(new JobHistoryItemViewModel(entry, async item => await HandleRollbackAsync(item)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load job history");
        }
        finally
        {
            IsJobHistoryLoading = false;
        }
    }

    [RelayCommand]
    public async Task ToggleFullHistoryAsync()
    {
        IsFullHistoryMode = !IsFullHistoryMode;
        await LoadJobHistoryAsync();
    }

    [RelayCommand]
    public void ToggleHistoryPanel()
    {
        IsHistoryPanelVisible = !IsHistoryPanelVisible;
        if (IsHistoryPanelVisible)
        {
            _ = LoadJobHistoryAsync();
        }
    }

    public async Task HandleRollbackAsync(JobHistoryItemViewModel item)
    {
        if (string.IsNullOrWhiteSpace(item.PreHead)) return;

        var dlg = new RollbackConfirmWindow(item.TargetPath, item.PreHead);
        if (System.Windows.Application.Current?.MainWindow != null)
        {
            dlg.Owner = System.Windows.Application.Current.MainWindow;
        }

        if (dlg.ShowDialog() == true)
        {
            try
            {
                var repoId = 0L;
                var matched = _repoViewModelMap.Values.FirstOrDefault(r => string.Equals(r.FullPath, item.TargetPath, StringComparison.OrdinalIgnoreCase));
                if (matched != null) repoId = matched.Repo.Id;

                var (success, error) = await _rollbackService.RollbackCommitAsync(repoId, item.TargetPath, item.PreHead);
                if (success)
                {
                    StatusMessage = $"성공적으로 커밋 {item.PreHead[..Math.Min(8, item.PreHead.Length)]} (으)로 되돌렸습니다.";
                    await LoadDataAsync();
                    await LoadJobHistoryAsync();
                }
                else
                {
                    System.Windows.MessageBox.Show($"되돌리기 실패:\n{error}", "되돌리기 오류", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
            }
            catch (InvalidOperationException ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "되돌리기 차단", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"되돌리기 중 예외 발생:\n{ex.Message}", "되돌리기 오류", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
    }

    #endregion

    #region Context Menu Actions & Single Operations (§10.1, §10.4, §10.7)

    public void HandleChangeRemoteUrl(RepoTreeNodeViewModel repoVm)
    {
        _ = OpenChangeRemoteUrlAsync(repoVm);
    }

    public async Task OpenChangeRemoteUrlAsync(RepoTreeNodeViewModel repoVm)
    {
        var profiles = await _indexStore.GetServerProfilesAsync();
        var serverRepos = await _indexStore.GetAllServerReposAsync();
        var currentUrl = await _remoteUrlService.GetRemoteUrlAsync(repoVm.FullPath, "origin") ?? string.Empty;

        var vm = new ChangeRemoteUrlViewModel(
            repoVm.Repo.Id,
            repoVm.FullPath,
            currentUrl,
            _remoteUrlService,
            _aliasTable,
            profiles,
            serverRepos);

        vm.OnSavedCallback = async () =>
        {
            StatusMessage = $"Remote URL이 변경되었습니다: {repoVm.FullPath}";
            await LoadDataAsync();
            await LoadJobHistoryAsync();
        };

        var dlg = new ChangeRemoteUrlWindow(vm);
        if (System.Windows.Application.Current?.MainWindow != null)
        {
            dlg.Owner = System.Windows.Application.Current.MainWindow;
        }
        dlg.ShowDialog();
    }

    public async Task HandleForcePushAsync(RepoTreeNodeViewModel repoVm)
    {
        // Criterion 24: Force push disabled when multiple items selected
        var selectedCount = _repoViewModelMap.Values.Count(r => r.IsSelected == true);
        if (selectedCount > 1)
        {
            System.Windows.MessageBox.Show("개별 Force Push는 단일 저장소 선택 상태에서만 실행할 수 있습니다.", "Force Push 차단", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        // Query remote commit hash
        var remoteCommitResult = await _gitRunner.ExecuteAsync(repoVm.FullPath, $"rev-parse origin/{repoVm.Branch}", TimeSpan.FromSeconds(5));
        var remoteCommit = remoteCommitResult.IsSuccess ? remoteCommitResult.StandardOutput.Trim() : "(원격 커밋 확인 불가 또는 신규 브랜치)";

        var dlg = new ForcePushConfirmWindow(repoVm.FullPath, repoVm.Branch, remoteCommit);
        if (System.Windows.Application.Current?.MainWindow != null)
        {
            dlg.Owner = System.Windows.Application.Current.MainWindow;
        }

        if (dlg.ShowDialog() == true)
        {
            StatusMessage = $"Force Push 실행 중 ({repoVm.FolderName})...";
            var preHeadCheck = await _gitRunner.ExecuteAsync(repoVm.FullPath, "rev-parse HEAD", TimeSpan.FromSeconds(5));
            var preHead = preHeadCheck.IsSuccess ? preHeadCheck.StandardOutput.Trim() : null;

            var pushResult = await _gitRunner.ExecuteAsync(
                repoVm.FullPath,
                $"push --force-with-lease origin {repoVm.Branch}",
                TimeSpan.FromSeconds(30));

            if (pushResult.IsSuccess)
            {
                var postHeadCheck = await _gitRunner.ExecuteAsync(repoVm.FullPath, "rev-parse HEAD", TimeSpan.FromSeconds(5));
                var postHead = postHeadCheck.IsSuccess ? postHeadCheck.StandardOutput.Trim() : preHead;

                await _indexStore.InsertJobHistoryAsync(new JobHistoryEntry(
                    0,
                    "push_force",
                    repoVm.FullPath,
                    preHead,
                    postHead,
                    "Success",
                    null,
                    DateTime.UtcNow));

                StatusMessage = $"개별 Force Push 완료 ({repoVm.FolderName})";
                await LoadDataAsync();
                await LoadJobHistoryAsync();
            }
            else
            {
                var err = pushResult.StandardError.Trim();
                var errorCat = ErrorClassifier.ClassifyToString(err);

                await _indexStore.InsertJobHistoryAsync(new JobHistoryEntry(
                    0,
                    "push_force",
                    repoVm.FullPath,
                    preHead,
                    preHead,
                    "Failed",
                    errorCat,
                    DateTime.UtcNow));

                System.Windows.MessageBox.Show($"Force Push 실패:\n{err}", "Force Push 오류", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
    }

    public async Task HandlePushNoUpstreamAsync(RepoTreeNodeViewModel repoVm)
    {
        var dlg = new PushNoUpstreamConfirmWindow(repoVm.FullPath, repoVm.Branch);
        if (System.Windows.Application.Current?.MainWindow != null)
        {
            dlg.Owner = System.Windows.Application.Current.MainWindow;
        }

        if (dlg.ShowDialog() == true)
        {
            StatusMessage = $"Push (-u) 실행 중 ({repoVm.FolderName})...";
            var preHeadCheck = await _gitRunner.ExecuteAsync(repoVm.FullPath, "rev-parse HEAD", TimeSpan.FromSeconds(5));
            var preHead = preHeadCheck.IsSuccess ? preHeadCheck.StandardOutput.Trim() : null;

            var pushResult = await _gitRunner.ExecuteAsync(
                repoVm.FullPath,
                $"push -u origin {repoVm.Branch}",
                TimeSpan.FromSeconds(30));

            if (pushResult.IsSuccess)
            {
                var postHeadCheck = await _gitRunner.ExecuteAsync(repoVm.FullPath, "rev-parse HEAD", TimeSpan.FromSeconds(5));
                var postHead = postHeadCheck.IsSuccess ? postHeadCheck.StandardOutput.Trim() : preHead;

                await _indexStore.InsertJobHistoryAsync(new JobHistoryEntry(
                    0,
                    "push",
                    repoVm.FullPath,
                    preHead,
                    postHead,
                    "Success",
                    null,
                    DateTime.UtcNow));

                StatusMessage = $"Push (-u) 완료 ({repoVm.FolderName})";
                await LoadDataAsync();
                await LoadJobHistoryAsync();
            }
            else
            {
                var err = pushResult.StandardError.Trim();
                var errorCat = ErrorClassifier.ClassifyToString(err);

                await _indexStore.InsertJobHistoryAsync(new JobHistoryEntry(
                    0,
                    "push",
                    repoVm.FullPath,
                    preHead,
                    preHead,
                    "Failed",
                    errorCat,
                    DateTime.UtcNow));

                System.Windows.MessageBox.Show($"Push (-u) 실패:\n{err}", "Push 오류", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
    }

    public async Task HandleSetUpstreamGoneAsync(RepoTreeNodeViewModel repoVm)
    {
        if (repoVm.MatchedServerRepo == null || string.IsNullOrWhiteSpace(repoVm.MatchedServerRepo.DefaultBranch)) return;

        var dlg = new UpstreamGoneSuggestionWindow(repoVm.FullPath, repoVm.MatchedServerRepo.DefaultBranch);
        if (System.Windows.Application.Current?.MainWindow != null)
        {
            dlg.Owner = System.Windows.Application.Current.MainWindow;
        }

        if (dlg.ShowDialog() == true)
        {
            var (success, error) = await _remoteUrlService.SetUpstreamBranchAsync(
                repoVm.Repo.Id,
                repoVm.FullPath,
                repoVm.MatchedServerRepo.DefaultBranch);

            if (success)
            {
                StatusMessage = $"업스트림이 origin/{repoVm.MatchedServerRepo.DefaultBranch}(으)로 변경되었습니다.";
                await LoadDataAsync();
            }
            else
            {
                System.Windows.MessageBox.Show($"업스트림 변경 실패:\n{error}", "오류", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
    }

    public async Task HandleApplyUrlChangeAsync(RepoTreeNodeViewModel repoVm)
    {
        if (string.IsNullOrWhiteSpace(repoVm.UrlChangeNewHostUrl)) return;

        var dlg = new UrlChangeSuggestionWindow(repoVm.FullPath, repoVm.UrlChangeOldUrl ?? "-", repoVm.UrlChangeNewHostUrl);
        if (System.Windows.Application.Current?.MainWindow != null)
        {
            dlg.Owner = System.Windows.Application.Current.MainWindow;
        }

        if (dlg.ShowDialog() == true)
        {
            var (success, error, _) = await _remoteUrlService.ChangeRemoteUrlAsync(
                repoVm.Repo.Id,
                repoVm.FullPath,
                "origin",
                repoVm.UrlChangeNewHostUrl);

            if (success)
            {
                StatusMessage = $"Remote URL이 '{repoVm.UrlChangeNewHostUrl}'(으)로 갱신되었습니다.";
                await LoadDataAsync();
                await LoadJobHistoryAsync();
            }
            else
            {
                System.Windows.MessageBox.Show($"Remote URL 갱신 실패:\n{error}", "오류", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
    }

    [RelayCommand]
    public async Task OpenBulkHostReplaceAsync()
    {
        var targets = new List<(LocalRepo Repo, string CurrentUrl)>();
        foreach (var repoVm in _repoViewModelMap.Values)
        {
            var url = await _remoteUrlService.GetRemoteUrlAsync(repoVm.FullPath, "origin");
            if (!string.IsNullOrWhiteSpace(url))
            {
                targets.Add((repoVm.Repo, url));
            }
        }

        var vm = new BulkHostReplaceViewModel(_remoteUrlService, targets);
        vm.OnCompletedCallback = async () =>
        {
            StatusMessage = "호스트 일괄 치환이 완료되었습니다.";
            await LoadDataAsync();
            await LoadJobHistoryAsync();
        };

        var dlg = new BulkHostReplaceWindow(vm);
        if (System.Windows.Application.Current?.MainWindow != null)
        {
            dlg.Owner = System.Windows.Application.Current.MainWindow;
        }
        dlg.ShowDialog();
    }

    [RelayCommand]
    public async Task ExportStateAsync()
    {
        var visibleRepos = _repoViewModelMap.Values.Where(r => r.IsVisible).ToList();
        if (visibleRepos.Count == 0)
        {
            System.Windows.MessageBox.Show("내보낼 저장소가 없습니다 (현재 필터 조건에 표시된 저장소 0개).", "내보내기 안내", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "저장소 상태 내보내기",
            Filter = "CSV 파일 (*.csv)|*.csv|Markdown 파일 (*.md)|*.md|모든 파일 (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = $"RepoRecon_Status_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };

        if (dialog.ShowDialog() == true)
        {
            var isMarkdown = dialog.FileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
            var exportItems = visibleRepos.Select(r => new ExportItem(
                r.FolderName,
                r.FullPath,
                r.Repo.NormalizedKey,
                r.Branch,
                r.Upstream,
                r.Ahead,
                r.Behind,
                r.DirtyCount,
                r.ConflictedCount,
                r.StateDisplay,
                r.Freshness,
                r.MatchedServerRepo?.CloneUrl ?? (!string.IsNullOrWhiteSpace(r.Repo.NormalizedKey) ? $"https://{r.Repo.NormalizedKey}.git" : null)
            )).ToList();

            await _exportService.ExportToFileAsync(exportItems, dialog.FileName, isMarkdown);
            StatusMessage = $"상태 리포트가 저장되었습니다: {dialog.FileName}";
        }
    }

    [RelayCommand]
    public void ClosePullPanel()
    {
        IsPullPanelVisible = false;
    }

    [RelayCommand]
    public void ClosePushPanel()
    {
        IsPushPanelVisible = false;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes >= 1)
        {
            return $"{(int)duration.TotalMinutes}분 {duration.Seconds}초";
        }
        return $"{duration.TotalSeconds:F1}초";
    }

    private void NotifyBatchCompletion(string operation, int success, int failed, int skipped)
    {
        var mainWindow = System.Windows.Application.Current?.MainWindow;
        var isActive = mainWindow != null && mainWindow.IsActive;

        if (!isActive)
        {
            _logger.LogInformation("Background Batch {Operation} Completed: Success={Success}, Failed={Failed}, Skipped={Skipped}",
                operation, success, failed, skipped);

            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                var msg = $"일괄 {operation} 작업이 완료되었습니다.\n성공: {success}건 / 실패: {failed}건 / 스킵: {skipped}건";
                System.Windows.MessageBox.Show(msg, $"RepoRecon 일괄 {operation} 완료", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            });
        }
    }

    #endregion

    public void Dispose()
    {
        _freshnessTimer.Stop();
        _pullCts?.Cancel();
        _pullCts?.Dispose();
        _pushCts?.Cancel();
        _pushCts?.Dispose();
        _fetchEngine.RepoFetched -= OnRepoFetched;
        _fetchEngine.FetchingStateChanged -= OnFetchingStateChanged;
        _fetchEngine.CooldownsChanged -= OnCooldownsChanged;
    }
}
