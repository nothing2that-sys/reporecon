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

    // §5.4.2: every local instance matching this server repo's normalized_key,
    // present or is_missing. The old v3 "이미 있음 (N곳)" badge is absorbed
    // into this column entirely -- no separate badge for it anymore.
    public List<(string AbsPath, bool IsMissing)> LocalMatches { get; set; } = new();

    public int PresentLocalCount => LocalMatches.Count(m => !m.IsMissing);
    public bool HasAnyLocalRecord => LocalMatches.Count > 0;

    // §5.4.2 완료 기준 7: is_missing 레포도 "로컬에 없는 것만" 필터에 포함시킨다
    // -- 실제로 안 지워진 것처럼 영영 안 받아지는 사고를 막는다.
    public bool HasNoPresentLocalCopy => PresentLocalCount == 0;

    public bool IsArchived => Repo.IsArchived;

    // §3.2.2: "빈칸 금지". 로컬 기록이 전혀 없으면 "로컬 없음", 있었지만 전부
    // is_missing이면 "확인 안 됨"(취소선 경로와 함께) -- 둘 다 HasNoPresentLocalCopy
    // 이지만 배지 문구와 표시할 경로가 다르므로 구분한다.
    public bool IsNotCloned => LocalMatches.Count == 0;
    public string NotClonedBadgeText => StateBadgeText.NotCloned;
    public bool IsAllLocalMissing => LocalMatches.Count > 0 && PresentLocalCount == 0;
    public string MissingBadgeText => StateBadgeText.MissingConfirmation;

    // 1곳이면 경로, 2곳 이상이면 첫 경로 + "외 N곳" (§5.4.2). 존재하는 사본을
    // 우선 앞세우고, 전부 is_missing이면 그중 하나를 취소선으로 보여준다.
    private List<(string AbsPath, bool IsMissing)> OrderedMatches =>
        LocalMatches.OrderBy(m => m.IsMissing).ThenBy(m => m.AbsPath, StringComparer.OrdinalIgnoreCase).ToList();

    public string LocalPathPrimaryDisplay
    {
        get
        {
            if (LocalMatches.Count == 0) return string.Empty;
            var ordered = OrderedMatches;
            return ordered.Count == 1 ? ordered[0].AbsPath : $"{ordered[0].AbsPath}  외 {ordered.Count - 1}곳";
        }
    }

    public bool PrimaryPathIsMissing => LocalMatches.Count > 0 && OrderedMatches[0].IsMissing;

    public string LocalPathTooltip => string.Join("\n", OrderedMatches.Select(m => m.IsMissing ? $"{m.AbsPath} (확인 안 됨)" : m.AbsPath));

    public Action? SelectionChanged { get; set; }

    public CatalogRepoItemViewModel(ServerRepo repo, List<(string AbsPath, bool IsMissing)>? localMatches = null)
    {
        Repo = repo;
        LocalMatches = localMatches ?? new();
    }

    partial void OnIsSelectedChanged(bool value)
    {
        SelectionChanged?.Invoke();
    }
}

public partial class NamespaceTreeNodeViewModel : ObservableObject
{
    public string Segment { get; }
    public string FullNamespace { get; }
    public ObservableCollection<NamespaceTreeNodeViewModel> Children { get; } = new();
    public List<CatalogRepoItemViewModel> DirectItems { get; } = new();

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private int _rollupRepoCount;

    [ObservableProperty]
    private int _rollupNotClonedCount;

    [ObservableProperty]
    private string _rollupSummaryText = string.Empty;

    public NamespaceTreeNodeViewModel(string segment, string fullNamespace)
    {
        Segment = segment;
        FullNamespace = fullNamespace;
    }

    public IEnumerable<CatalogRepoItemViewModel> GetScopedItems()
    {
        foreach (var item in DirectItems)
        {
            yield return item;
        }

        foreach (var child in Children)
        {
            foreach (var item in child.GetScopedItems())
            {
                yield return item;
            }
        }
    }

    public void RecalculateRollup()
    {
        foreach (var child in Children)
        {
            child.RecalculateRollup();
        }

        var scoped = GetScopedItems().ToList();
        RollupRepoCount = scoped.Count;
        RollupNotClonedCount = scoped.Count(i => i.HasNoPresentLocalCopy);
        RollupSummaryText = $"레포 {RollupRepoCount} · 로컬 없음 {RollupNotClonedCount}";
    }
}

public partial class CloneTabViewModel : ObservableObject
{
    private readonly SqliteIndexStore _indexStore;
    private readonly ISecureTokenStore _tokenStore;
    private readonly IServerAdapterFactory _adapterFactory;
    private readonly ICloneEngine _cloneEngine;
    private readonly ScanRootManager _scanRootManager;
    private readonly HostAliasTable _aliasTable;
    private readonly ILogger<CloneTabViewModel> _logger;

    public ObservableCollection<ServerProfile> ServerProfiles { get; } = new();
    public ObservableCollection<CatalogRepoItemViewModel> AllCatalogItems { get; } = new();
    public ObservableCollection<CatalogRepoItemViewModel> FilteredCatalogItems { get; } = new();

    // §5.4.1: namespace tree, single "전체" virtual root over path_with_namespace
    // / full_name split by "/" -- no schema change, server_repos.namespace
    // already has what's needed.
    public ObservableCollection<NamespaceTreeNodeViewModel> NamespaceTreeRoots { get; } = new();

    [ObservableProperty]
    private NamespaceTreeNodeViewModel? _selectedNamespaceNode;

    // §3.5 (b): grouped move-detection results from the last sync, for the
    // Local tab's "경로 이동 감지 N건" banner (SS3.5 감지 시점: 카탈로그 동기화 직후).
    public Action<IReadOnlyList<NamespaceMoveDetector.MoveGroup>>? OnPathMoveCandidatesDetected { get; set; }

    [ObservableProperty]
    private ServerProfile? _selectedProfile;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _showArchived;

    // §5.4.2: "로컬에 없는 것만" 필터를 기본 진입점으로 둔다 -- 이 기능의 목적이다.
    [ObservableProperty]
    private bool _showNotClonedOnly = true;

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

    // §5-4 빈 상태: 등록된 서버 프로파일이 하나도 없음
    [ObservableProperty]
    private bool _hasNoProfiles = true;

    public Action<string>? OnJumpToLocalRepoRequested { get; set; }
    public Func<IReadOnlyList<ClonePreviewItem>, string, Task<bool>>? ShowPreviewDialogFunc { get; set; }
    public Func<string, Task<bool>>? PromptAddScanRootFunc { get; set; }
    public Action? OpenProfileManagementFunc { get; set; }
    public Func<Task>? OnCloneCompletedFunc { get; set; }
    public Func<Task>? OnAliasesInvalidatedFunc { get; set; }

    public CloneTabViewModel(
        SqliteIndexStore indexStore,
        ISecureTokenStore tokenStore,
        IServerAdapterFactory adapterFactory,
        ICloneEngine cloneEngine,
        ScanRootManager scanRootManager,
        HostAliasTable aliasTable,
        ILogger<CloneTabViewModel>? logger = null)
    {
        _indexStore = indexStore;
        _tokenStore = tokenStore;
        _adapterFactory = adapterFactory;
        _cloneEngine = cloneEngine;
        _scanRootManager = scanRootManager;
        _aliasTable = aliasTable;
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
        HasNoProfiles = ServerProfiles.Count == 0;
    }

    partial void OnSelectedProfileChanged(ServerProfile? value)
    {
        _ = LoadCatalogForSelectedProfileAsync();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilters();
    partial void OnShowArchivedChanged(bool value) => ApplyFilters();
    partial void OnShowNotClonedOnlyChanged(bool value) => ApplyFilters();
    partial void OnSelectedNamespaceNodeChanged(NamespaceTreeNodeViewModel? value) => ApplyFilters();

    public async Task LoadCatalogForSelectedProfileAsync()
    {
        AllCatalogItems.Clear();
        if (SelectedProfile == null)
        {
            RebuildNamespaceTree();
            ApplyFilters();
            return;
        }

        var repos = await _indexStore.GetServerReposByProfileAsync(SelectedProfile.Id);
        var reposWithStatus = await _indexStore.GetAllLocalReposWithStatusAsync();
        var localMatchesByKey = reposWithStatus
            .Where(l => !string.IsNullOrWhiteSpace(l.Repo.NormalizedKey))
            .GroupBy(l => l.Repo.NormalizedKey!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(l => (l.Repo.AbsPath, l.Repo.IsMissing)).ToList(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var repo in repos)
        {
            localMatchesByKey.TryGetValue(repo.NormalizedKey, out var matches);
            var item = new CatalogRepoItemViewModel(repo, matches)
            {
                SelectionChanged = UpdateSelectionSummary
            };
            AllCatalogItems.Add(item);
        }

        RebuildNamespaceTree();
        ApplyFilters();
    }

    private void RebuildNamespaceTree()
    {
        var previousSelectedNamespace = SelectedNamespaceNode?.FullNamespace;

        NamespaceTreeRoots.Clear();
        var root = new NamespaceTreeNodeViewModel("전체", string.Empty);

        foreach (var item in AllCatalogItems)
        {
            var segments = item.Repo.Namespace.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            var pathSoFar = new List<string>();
            foreach (var seg in segments)
            {
                pathSoFar.Add(seg);
                var existing = current.Children.FirstOrDefault(c => string.Equals(c.Segment, seg, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    existing = new NamespaceTreeNodeViewModel(seg, string.Join("/", pathSoFar));
                    current.Children.Add(existing);
                }
                current = existing;
            }
            current.DirectItems.Add(item);
        }

        root.RecalculateRollup();
        NamespaceTreeRoots.Add(root);

        SelectedNamespaceNode = FindNamespaceNodeByPath(root, previousSelectedNamespace) ?? root;
    }

    private static NamespaceTreeNodeViewModel? FindNamespaceNodeByPath(NamespaceTreeNodeViewModel node, string? fullNamespace)
    {
        if (fullNamespace == null)
            return null;

        if (string.Equals(node.FullNamespace, fullNamespace, StringComparison.OrdinalIgnoreCase))
            return node;

        foreach (var child in node.Children)
        {
            var found = FindNamespaceNodeByPath(child, fullNamespace);
            if (found != null)
                return found;
        }

        return null;
    }

    public void ApplyFilters()
    {
        FilteredCatalogItems.Clear();
        var query = FilterText.Trim();
        var scopedItems = SelectedNamespaceNode != null
            ? new HashSet<CatalogRepoItemViewModel>(SelectedNamespaceNode.GetScopedItems())
            : null;

        foreach (var item in AllCatalogItems)
        {
            // Namespace tree scope (§5.4.1)
            if (scopedItems != null && !scopedItems.Contains(item))
            {
                continue;
            }

            // Archived toggle (hidden by default §7.4, §10 Criterion 25)
            if (item.IsArchived && !ShowArchived)
            {
                continue;
            }

            // §5.4.2: "로컬에 없는 것만" -- 기본 진입점. is_missing만 남은 레포도 포함한다.
            if (ShowNotClonedOnly && !item.HasNoPresentLocalCopy)
            {
                continue;
            }

            // Text search (Namespace / Name)
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

            // clone URL의 authority가 API 접속 authority와 다르면 자동으로 별칭을 등록한다
            // (§4.2 (4), v4.1) — GitLab 등의 clone URL은 external_url 설정을 반영하므로
            // 사용자가 접속한 IP:port와 다른 호스트를 가리킬 수 있다.
            var apiAuthority = RemoteUrlNormalizer.ExtractAuthority(SelectedProfile.Host);
            var cloneAuthority = discoveredRepos
                .Select(r => RemoteUrlNormalizer.ExtractAuthority(r.CloneUrl))
                .FirstOrDefault(a => a != null);

            if (apiAuthority != null && cloneAuthority != null &&
                !string.Equals(apiAuthority, cloneAuthority, StringComparison.OrdinalIgnoreCase) &&
                !SelectedProfile.Aliases.Contains(cloneAuthority, StringComparer.OrdinalIgnoreCase))
            {
                var updatedAliases = SelectedProfile.Aliases.Append(cloneAuthority)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var updatedProfile = SelectedProfile with { Aliases = updatedAliases };
                await _indexStore.UpsertServerProfileAsync(updatedProfile);
                SelectedProfile = updatedProfile;
                _aliasTable.SetAlias(cloneAuthority, apiAuthority);
                _logger.LogInformation("Auto-registered host alias {CloneAuthority} -> {ApiAuthority} for profile {Host}",
                    cloneAuthority, apiAuthority, SelectedProfile.Host);
                StatusMessage = $"별칭 자동 등록: {cloneAuthority} → {apiAuthority}";

                // §2.3/§4.4: repos scanned before this alias existed are still
                // keyed under the old (mismatched) authority. Invalidate just
                // those so the next scan re-resolves them, then run it now --
                // the user is about to look at the local tab for the result.
                var invalidatedCount = await _indexStore.InvalidateNormalizedKeysForAliasAsync(cloneAuthority);
                if (invalidatedCount > 0)
                {
                    _logger.LogInformation(
                        "Invalidated {Count} local_repos.normalized_key rows for newly-registered authority {CloneAuthority}",
                        invalidatedCount, cloneAuthority);

                    if (OnAliasesInvalidatedFunc != null)
                    {
                        await OnAliasesInvalidatedFunc();
                    }
                }
            }

            // Convert to ServerRepo entities
            var now = DateTime.UtcNow;
            var serverRepos = discoveredRepos.Select(r =>
            {
                var normalizedKey = RemoteUrlNormalizer.Normalize(r.CloneUrl, _aliasTable) ?? $"{SelectedProfile.Host}/{r.Namespace}/{r.Name}".ToLowerInvariant();
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

            // §3.5 (b): run right after catalog sync, one shot -- not on every
            // incidental reload (profile switch, post-clone refresh).
            var canonicalAuthority = RemoteUrlNormalizer.ExtractAuthority(SelectedProfile.Host);
            if (canonicalAuthority != null)
            {
                var localReposForDetection = (await _indexStore.GetAllLocalReposWithStatusAsync())
                    .Select(l => l.Repo)
                    .ToList();
                var moveGroups = NamespaceMoveDetector.Detect(localReposForDetection, serverRepos, canonicalAuthority);
                if (moveGroups.Count > 0)
                {
                    OnPathMoveCandidatesDetected?.Invoke(moveGroups);
                }
            }

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
        if (item == null || !item.HasAnyLocalRecord) return;
        OnJumpToLocalRepoRequested?.Invoke(item.Repo.NormalizedKey);
    }
}
