using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoRecon.Core.Adapters;
using RepoRecon.Core.Clone;
using RepoRecon.Core.Data;
using RepoRecon.Core.Models;
using RepoRecon.Core.Pull;
using RepoRecon.Core.Scanning;
using RepoRecon.Core.Security;
using RepoRecon.Core.Urls;

namespace RepoRecon.App.ViewModels;

public partial class CatalogRepoItemViewModel : ObservableObject
{
    public ServerRepo Repo { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionChanged))]
    private bool _isSelected;

    public int ClonedLocalCount { get; set; }
    public bool IsAlreadyCloned => ClonedLocalCount > 0;
    public string AlreadyClonedBadgeText => ClonedLocalCount > 0 ? $"이미 있음 ({ClonedLocalCount}곳)" : string.Empty;
    public bool IsArchived => Repo.IsArchived;

    public Action? SelectionChanged { get; set; }

    public CatalogRepoItemViewModel(ServerRepo repo, int clonedLocalCount = 0)
    {
        Repo = repo;
        ClonedLocalCount = clonedLocalCount;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        SelectionChanged?.Invoke();
    }
}

public partial class CloneTabViewModel : ObservableObject
{
    private readonly SqliteIndexStore _indexStore;
    private readonly ISecureTokenStore _tokenStore;
    private readonly IServerAdapterFactory _adapterFactory;
    private readonly ICloneEngine _cloneEngine;
    private readonly ScanRootManager _scanRootManager;
    private readonly ILogger<CloneTabViewModel> _logger;

    public ObservableCollection<ServerProfile> ServerProfiles { get; } = new();
    public ObservableCollection<CatalogRepoItemViewModel> AllCatalogItems { get; } = new();
    public ObservableCollection<CatalogRepoItemViewModel> FilteredCatalogItems { get; } = new();

    [ObservableProperty]
    private ServerProfile? _selectedProfile;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _showArchived;

    [ObservableProperty]
    private string _targetRootDirectory = string.Empty;

    [ObservableProperty]
    private int _scanRootCount;

    [ObservableProperty]
    private string _scanRootBannerText = string.Empty;

    [ObservableProperty]
    private bool _isSyncing;

    [ObservableProperty]
    private bool _isCloning;

    [ObservableProperty]
    private string _statusMessage = "준비 완료";

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private string _selectionSummaryText = "선택 0개 / 전체 0개";

    public Action<string>? OnJumpToLocalRepoRequested { get; set; }
    public Func<IReadOnlyList<ClonePreviewItem>, string, Task<bool>>? ShowPreviewDialogFunc { get; set; }
    public Func<string, Task<bool>>? PromptAddScanRootFunc { get; set; }
    public Action? OpenProfileManagementFunc { get; set; }
    public Func<Task>? OnCloneCompletedFunc { get; set; }

    public CloneTabViewModel(
        SqliteIndexStore indexStore,
        ISecureTokenStore tokenStore,
        IServerAdapterFactory adapterFactory,
        ICloneEngine cloneEngine,
        ScanRootManager scanRootManager,
        ILogger<CloneTabViewModel>? logger = null)
    {
        _indexStore = indexStore;
        _tokenStore = tokenStore;
        _adapterFactory = adapterFactory;
        _cloneEngine = cloneEngine;
        _scanRootManager = scanRootManager;
        _logger = logger ?? NullLogger<CloneTabViewModel>.Instance;
    }

    public async Task InitializeAsync()
    {
        await LoadProfilesAsync();
        await UpdateScanRootBannerAsync();
    }

    public async Task UpdateScanRootBannerAsync()
    {
        var allRoots = await _scanRootManager.GetRootsAsync();
        var roots = allRoots.Where(r => r.IsActive).ToList();
        ScanRootCount = roots.Count;
        ScanRootBannerText = $"ℹ️ 현재 스캔 루트 {ScanRootCount}개 기준 (스캔 루트 밖의 클론은 '이미 있음' 감지에서 제외될 수 있습니다)";

        if (string.IsNullOrWhiteSpace(TargetRootDirectory) && roots.Count > 0)
        {
            TargetRootDirectory = roots[0].Path;
        }
    }

    [RelayCommand]
    public async Task LoadProfilesAsync()
    {
        ServerProfiles.Clear();
        var profiles = await _indexStore.GetServerProfilesAsync();
        ServerProfiles.Clear();
        foreach (var p in profiles)
        {
            ServerProfiles.Add(p);
        }

        SelectedProfile = ServerProfiles.FirstOrDefault();
    }

    partial void OnSelectedProfileChanged(ServerProfile? value)
    {
        _ = LoadCatalogForSelectedProfileAsync();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilters();
    partial void OnShowArchivedChanged(bool value) => ApplyFilters();

    public async Task LoadCatalogForSelectedProfileAsync()
    {
        AllCatalogItems.Clear();
        if (SelectedProfile == null)
        {
            ApplyFilters();
            return;
        }

        var repos = await _indexStore.GetServerReposByProfileAsync(SelectedProfile.Id);
        var reposWithStatus = await _indexStore.GetAllLocalReposWithStatusAsync();
        var localKeyCounts = reposWithStatus
            .Where(l => !string.IsNullOrWhiteSpace(l.Repo.NormalizedKey))
            .GroupBy(l => l.Repo.NormalizedKey!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var repo in repos)
        {
            localKeyCounts.TryGetValue(repo.NormalizedKey, out var count);
            var item = new CatalogRepoItemViewModel(repo, count)
            {
                SelectionChanged = UpdateSelectionSummary
            };
            AllCatalogItems.Add(item);
        }

        ApplyFilters();
    }

    public void ApplyFilters()
    {
        FilteredCatalogItems.Clear();
        var query = FilterText.Trim();

        foreach (var item in AllCatalogItems)
        {
            // Filter 1: Archived toggle (hidden by default §7.4, §10 Criterion 25)
            if (item.IsArchived && !ShowArchived)
            {
                continue;
            }

            // Filter 2: Text search (Namespace / Name)
            if (!string.IsNullOrWhiteSpace(query))
            {
                var matchesName = item.Repo.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
                var matchesNs = item.Repo.Namespace.Contains(query, StringComparison.OrdinalIgnoreCase);
                var matchesKey = item.Repo.NormalizedKey.Contains(query, StringComparison.OrdinalIgnoreCase);
                if (!matchesName && !matchesNs && !matchesKey)
                {
                    continue;
                }
            }

            FilteredCatalogItems.Add(item);
        }

        UpdateSelectionSummary();
    }

    [RelayCommand]
    public async Task SyncCatalogAsync()
    {
        if (SelectedProfile == null)
        {
            await LoadProfilesAsync();
        }

        if (SelectedProfile == null)
        {
            StatusMessage = "선택된 서버 프로파일이 없습니다.";
            return;
        }

        IsSyncing = true;
        StatusMessage = $"'{SelectedProfile.Host}' 카탈로그 동기화 시작...";

        try
        {
            string? token = null;
            if (!string.IsNullOrWhiteSpace(SelectedProfile.TokenRef))
            {
                token = await _tokenStore.GetTokenAsync(SelectedProfile.TokenRef);
            }
            _logger.LogInformation("SyncCatalogAsync for {Host}, tokenRef={TokenRef}, tokenIsNull={TokenIsNull}", SelectedProfile.Host, SelectedProfile.TokenRef, token == null);

            var adapter = _adapterFactory.GetAdapter(SelectedProfile.ApiKind);
            var progress = new Progress<CatalogProgressInfo>(p =>
            {
                StatusMessage = p.StatusDetail;
            });

            var discoveredRepos = await adapter.FetchCatalogAsync(SelectedProfile, token, progress);

            // Convert to ServerRepo entities
            var now = DateTime.UtcNow;
            var serverRepos = discoveredRepos.Select(r =>
            {
                var normalizedKey = RemoteUrlNormalizer.Normalize(r.CloneUrl, null) ?? $"{SelectedProfile.Host}/{r.Namespace}/{r.Name}".ToLowerInvariant();
                return new ServerRepo(
                    Id: 0,
                    ProfileId: SelectedProfile.Id,
                    Namespace: r.Namespace,
                    Name: r.Name,
                    CloneUrl: r.CloneUrl,
                    NormalizedKey: normalizedKey,
                    DefaultBranch: r.DefaultBranch,
                    IsArchived: r.IsArchived,
                    CollectedAt: now);
            }).ToList();

            await _indexStore.ReplaceServerReposAsync(SelectedProfile.Id, serverRepos);

            StatusMessage = $"카탈로그 동기화 완료: 총 {serverRepos.Count}개 저장소 수집됨";
            await LoadCatalogForSelectedProfileAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Catalog synchronization failed for {Host}", SelectedProfile.Host);
            StatusMessage = $"동기화 실패: {ex.Message}";
        }
        finally
        {
            IsSyncing = false;
        }
    }

    [RelayCommand]
    public void SelectAll()
    {
        foreach (var item in FilteredCatalogItems)
        {
            item.IsSelected = true;
        }
        UpdateSelectionSummary();
    }

    [RelayCommand]
    public void UnselectAll()
    {
        foreach (var item in FilteredCatalogItems)
        {
            item.IsSelected = false;
        }
        UpdateSelectionSummary();
    }

    private void UpdateSelectionSummary()
    {
        SelectedCount = FilteredCatalogItems.Count(i => i.IsSelected);
        SelectionSummaryText = $"선택 {SelectedCount}개 / 전체 {FilteredCatalogItems.Count}개";
    }

    [RelayCommand]
    public void OpenProfileManagement()
    {
        OpenProfileManagementFunc?.Invoke();
    }

    [RelayCommand]
    public async Task PreviewAndExecuteCloneAsync()
    {
        var selectedRepos = FilteredCatalogItems.Where(i => i.IsSelected).Select(i => i.Repo).ToList();
        if (selectedRepos.Count == 0)
        {
            StatusMessage = "클론할 저장소를 1개 이상 선택하세요.";
            return;
        }

        if (string.IsNullOrWhiteSpace(TargetRootDirectory))
        {
            StatusMessage = "클론 대상 루트 디렉터리를 지정하세요.";
            return;
        }

        try
        {
            // 1. Preview
            var previewItems = await _cloneEngine.PreviewClonesAsync(selectedRepos, TargetRootDirectory);

            if (ShowPreviewDialogFunc != null)
            {
                var confirmed = await ShowPreviewDialogFunc(previewItems, TargetRootDirectory);
                if (!confirmed)
                {
                    StatusMessage = "클론 실행이 취소되었습니다.";
                    return;
                }
            }

            // 2. Execute
            IsCloning = true;
            StatusMessage = "일괄 클론 실행 중...";

            var progress = new Progress<BatchCloneProgress>(p =>
            {
                StatusMessage = $"클론 진행 중 ({p.Completed}/{p.Total}): {Path.GetFileName(p.CurrentRepoPath)} - {p.Result}";
            });

            var results = await _cloneEngine.ExecuteBatchCloneAsync(selectedRepos, TargetRootDirectory, progress);

            var successCount = results.Count(r => r.IsSuccess);
            var failCount = results.Count(r => !r.IsSuccess);

            StatusMessage = $"클론 완료: 성공 {successCount}건 / 실패 {failCount}건";

            if (successCount > 0 && OnCloneCompletedFunc != null)
            {
                await OnCloneCompletedFunc();
            }

            // 3. Prompt add scan root if targetRoot is not in scan roots (§8, §10 Criterion 23)
            var allRoots = await _scanRootManager.GetRootsAsync();
            var activeRoots = allRoots.Where(r => r.IsActive).ToList();
            var isContained = activeRoots.Any(r => TargetRootDirectory.StartsWith(r.Path, StringComparison.OrdinalIgnoreCase));
            if (!isContained && PromptAddScanRootFunc != null && successCount > 0)
            {
                var shouldAdd = await PromptAddScanRootFunc(TargetRootDirectory);
                if (shouldAdd)
                {
                    await _scanRootManager.AddOrAbsorbRootAsync(TargetRootDirectory);
                    await UpdateScanRootBannerAsync();
                }
            }

            // Reload catalog badges
            await LoadCatalogForSelectedProfileAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch clone execution error");
            StatusMessage = $"클론 실행 오류: {ex.Message}";
        }
        finally
        {
            IsCloning = false;
        }
    }

    [RelayCommand]
    public void JumpToLocalRepo(CatalogRepoItemViewModel item)
    {
        if (item == null || !item.IsAlreadyCloned) return;
        OnJumpToLocalRepoRequested?.Invoke(item.Repo.NormalizedKey);
    }
}
