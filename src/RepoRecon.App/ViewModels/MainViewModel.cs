using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoRecon.App.ViewModels.Tree;
using RepoRecon.Core.Data;
using RepoRecon.Core.Fetch;
using RepoRecon.Core.Git;
using RepoRecon.Core.Models;
using RepoRecon.Core.Scanning;
using RepoRecon.Core.Tree;

namespace RepoRecon.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IGitRunner _gitRunner;
    private readonly SqliteIndexStore _indexStore;
    private readonly ScanRootManager _scanRootManager;
    private readonly RepoScanner _repoScanner;
    private readonly IFetchEngine _fetchEngine;
    private readonly ILogger<MainViewModel> _logger;
    private readonly DispatcherTimer _freshnessTimer;

    private readonly Dictionary<long, RepoTreeNodeViewModel> _repoViewModelMap = new();

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

    public ObservableCollection<string> SkipReasonList { get; } = new();

    public ObservableCollection<string> InaccessiblePaths { get; } = new();
    public ObservableCollection<ScanRootItemViewModel> ScanRoots { get; } = new();
    public ObservableCollection<TreeNodeViewModelBase> RootTreeNodes { get; } = new();

    public MainViewModel(
        IGitRunner gitRunner,
        SqliteIndexStore indexStore,
        ScanRootManager scanRootManager,
        RepoScanner repoScanner,
        IFetchEngine fetchEngine,
        ILogger<MainViewModel>? logger = null)
    {
        _gitRunner = gitRunner;
        _indexStore = indexStore;
        _scanRootManager = scanRootManager;
        _repoScanner = repoScanner;
        _fetchEngine = fetchEngine;
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

        var pullable = 0;
        var skipped = 0;

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

        foreach (var r in selectedRepos)
        {
            if (r.IsMissing)
            {
                skipped++;
                missingSkip++;
                continue;
            }

            if (r.ConflictedCount > 0 || r.StateCode == RepoStateCode.Conflicted)
            {
                skipped++;
                conflictedSkip++;
                continue;
            }

            if (r.DirtyCount > 0)
            {
                skipped++;
                dirtySkip++;
                continue;
            }

            if (r.StateCode == RepoStateCode.Diverged)
            {
                skipped++;
                divergedSkip++;
                continue;
            }

            if (r.StateCode == RepoStateCode.NoUpstream)
            {
                skipped++;
                noUpstreamSkip++;
                continue;
            }

            if (r.StateCode == RepoStateCode.UpstreamGone)
            {
                skipped++;
                upstreamGoneSkip++;
                continue;
            }

            if (r.StateCode == RepoStateCode.DetachedHead)
            {
                skipped++;
                detachedHeadSkip++;
                continue;
            }

            if (r.StateCode == RepoStateCode.Untracked)
            {
                skipped++;
                untrackedSkip++;
                continue;
            }

            if (r.LastFetchedAt == null || r.HasFetchFailure)
            {
                skipped++;
                unfetchedSkip++;
                continue;
            }

            if (r.Behind > 0 || r.StateCode == RepoStateCode.Behind)
            {
                pullable++;
            }
            else if (r.StateCode == RepoStateCode.UpToDate)
            {
                skipped++;
                upToDateSkip++;
            }
            else
            {
                skipped++;
                otherSkip++;
            }
        }

        PullableCount = pullable;
        SkippedCount = skipped;
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
            queryTimer.Stop();

            var treeBuildTimer = Stopwatch.StartNew();
            _repoViewModelMap.Clear();

            // Build hierarchical tree using Core RepoTreeBuilder
            var treeFolders = RepoTreeBuilder.BuildTree(roots, reposWithStatus);

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

            TotalRepoCount = _repoViewModelMap.Count;
            ApplyFilters();
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

    public void Dispose()
    {
        _freshnessTimer.Stop();
        _fetchEngine.RepoFetched -= OnRepoFetched;
        _fetchEngine.FetchingStateChanged -= OnFetchingStateChanged;
        _fetchEngine.CooldownsChanged -= OnCooldownsChanged;
    }
}
