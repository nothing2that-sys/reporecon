using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoRecon.Core.Models;
using RepoRecon.Core.Tree;

namespace RepoRecon.App.ViewModels.Tree;

public partial class RepoTreeNodeViewModel : TreeNodeViewModelBase
{
    private readonly Action<long>? _onNavigateToSibling;
    private string? _fetchFailureLabel;

    [ObservableProperty]
    private bool _hasFetchFailure;

    [ObservableProperty]
    private LocalRepo _repo;

    [ObservableProperty]
    private LocalStatus? _status;

    [ObservableProperty]
    private string _folderName = string.Empty;

    [ObservableProperty]
    private string _displayRepoPath = string.Empty;

    [ObservableProperty]
    private string _hostName = "Local";

    [ObservableProperty]
    private string _hostDotColor = "#6B7280";

    [ObservableProperty]
    private string _branch = "(알 수 없음)";

    [ObservableProperty]
    private string _upstream = "(없음)";

    [ObservableProperty]
    private string _branchDisplay = string.Empty;

    [ObservableProperty]
    private int _ahead;

    [ObservableProperty]
    private int _behind;

    [ObservableProperty]
    private int _dirtyCount;

    [ObservableProperty]
    private int _conflictedCount;

    [ObservableProperty]
    private RepoStateCode _stateCode = RepoStateCode.UnknownUnfetched;

    [ObservableProperty]
    private string _stateDisplay = "미확인 (fetch 전)";

    [ObservableProperty]
    private string _threeAxisSummary = string.Empty;

    [ObservableProperty]
    private DateTime? _lastFetchedAt;

    [ObservableProperty]
    private string _freshness = "미확인 (fetch 전)";

    [ObservableProperty]
    private int _duplicateCloneCount = 1;

    [ObservableProperty]
    private string? _duplicateBadgeText;

    [ObservableProperty]
    private bool _hasDuplicateClones;

    [ObservableProperty]
    private bool _isNested;

    [ObservableProperty]
    private bool _hasGitIgnoreWarning;

    [ObservableProperty]
    private bool _isOrphaned;

    [ObservableProperty]
    private bool _isExternal;

    [ObservableProperty]
    private bool _isUntracked;

    [ObservableProperty]
    private bool _isArchived;

    [ObservableProperty]
    private bool _isMissing;

    [ObservableProperty]
    private string? _serverStatusBadge;

    [ObservableProperty]
    private bool _hasServerStatusBadge;

    public override bool IsFolder => false;

    public List<long> DuplicateSiblingIds { get; } = new();

    public RepoTreeNodeViewModel(
        TreeRepoNode node,
        Action<long>? onNavigateToSibling = null)
    {
        _repo = node.Repo;
        _status = node.Status;
        _onNavigateToSibling = onNavigateToSibling;

        Name = node.FolderName;
        FullPath = node.Repo.AbsPath;
        FolderName = node.FolderName;
        DisplayRepoPath = node.DisplayRepoPath;
        HostName = node.HostName;
        DuplicateCloneCount = node.DuplicateCloneCount;
        DuplicateSiblingIds.AddRange(node.DuplicateSiblingIds);
        HasDuplicateClones = node.DuplicateCloneCount > 1;
        DuplicateBadgeText = HasDuplicateClones ? $"이 URL 사본 {DuplicateCloneCount}개" : null;
        IsNested = node.IsNested;
        HasGitIgnoreWarning = node.HasGitIgnoreWarning;
        IsMissing = node.Repo.IsMissing;

        IsOrphaned = node.IsOrphaned;
        IsExternal = node.IsExternal;
        IsUntracked = node.IsUntracked;
        IsArchived = node.IsArchived;

        if (IsOrphaned)
        {
            ServerStatusBadge = "Orphaned";
        }
        else if (IsExternal)
        {
            ServerStatusBadge = "External";
        }
        else if (IsUntracked)
        {
            ServerStatusBadge = "Untracked";
        }

        if (IsArchived)
        {
            ServerStatusBadge = ServerStatusBadge != null ? $"{ServerStatusBadge} · 보관됨" : "보관됨";
        }

        HasServerStatusBadge = !string.IsNullOrWhiteSpace(ServerStatusBadge);

        HostDotColor = DetermineHostColor(node.HostName);

        ApplyStatus(node.Status);
    }

    public void UpdateStatus(LocalStatus? status)
    {
        HasFetchFailure = false;
        _fetchFailureLabel = null;
        Status = status;
        ApplyStatus(status);
        Parent?.UpdateSelectionFromChildren();
        if (Parent is FolderTreeNodeViewModel folderParent)
        {
            folderParent.RecalculateRollup();
        }
    }

    public void SetFetchFailure(string label)
    {
        HasFetchFailure = true;
        _fetchFailureLabel = label;
        ApplyStatus(Status);
    }

    private void ApplyStatus(LocalStatus? status)
    {
        Branch = status?.Branch ?? "(알 수 없음)";
        Upstream = status?.Upstream ?? "(없음)";
        Ahead = status?.Ahead ?? 0;
        Behind = status?.Behind ?? 0;
        DirtyCount = status?.DirtyCount ?? 0;
        ConflictedCount = status?.ConflictedCount ?? 0;
        LastFetchedAt = status?.LastFetchedAt;

        BranchDisplay = string.IsNullOrWhiteSpace(status?.Upstream)
            ? Branch
            : $"{Branch} → {status.Upstream}";

        // If not fetched yet and has remote, keep state as UnknownUnfetched / Ahead / Behind / etc.
        StateCode = status?.StateCode ?? RepoStateCode.UnknownUnfetched;

        if ((LastFetchedAt == null || HasFetchFailure) && (StateCode == RepoStateCode.Ahead || StateCode == RepoStateCode.Behind || StateCode == RepoStateCode.UpToDate || StateCode == RepoStateCode.Diverged))
        {
            StateDisplay = HasFetchFailure ? "미확인 (fetch 실패)" : "미확인 (fetch 전)";
        }
        else
        {
            StateDisplay = StateCode switch
            {
                RepoStateCode.NoUpstream => "No Upstream",
                RepoStateCode.UpstreamGone => "Upstream Gone",
                RepoStateCode.DetachedHead => "Detached HEAD",
                RepoStateCode.Untracked => "Untracked · No Upstream",
                RepoStateCode.Conflicted => "Conflicted",
                RepoStateCode.Diverged => $"Diverged (↑{Ahead} ↓{Behind})",
                RepoStateCode.Ahead => $"Ahead (↑{Ahead})",
                RepoStateCode.Behind => $"Behind (↓{Behind})",
                RepoStateCode.UpToDate => "Up-to-Date",
                _ => "미확인 (fetch 전)"
            };
        }

        var parts = new List<string> { StateDisplay };
        if (DirtyCount > 0)
        {
            parts.Add($"수정 {DirtyCount}");
        }
        if (ConflictedCount > 0)
        {
            parts.Add($"충돌 {ConflictedCount}");
        }
        if (IsMissing)
        {
            parts.Add("(미발견)");
        }

        ThreeAxisSummary = string.Join(" · ", parts);
        UpdateFreshness();
    }

    public void UpdateFreshness()
    {
        if (HasFetchFailure && _fetchFailureLabel != null)
        {
            Freshness = _fetchFailureLabel;
            return;
        }
        if (LastFetchedAt == null)
        {
            Freshness = "미확인 (fetch 전)";
            return;
        }

        var elapsed = DateTime.UtcNow - LastFetchedAt.Value;
        if (elapsed.TotalSeconds < 60)
        {
            Freshness = "방금 전 기준";
        }
        else if (elapsed.TotalMinutes < 60)
        {
            Freshness = $"{(int)elapsed.TotalMinutes}분 전 기준";
        }
        else if (elapsed.TotalHours < 24)
        {
            Freshness = $"{(int)elapsed.TotalHours}시간 전 기준";
        }
        else
        {
            Freshness = $"{(int)elapsed.TotalDays}일 전 기준";
        }
    }

    [RelayCommand]
    private void JumpToNextClone()
    {
        if (DuplicateSiblingIds.Count > 0 && _onNavigateToSibling != null)
        {
            _onNavigateToSibling(DuplicateSiblingIds[0]);
        }
    }

    public override bool ApplyFilter(Func<RepoTreeNodeViewModel, bool> predicate)
    {
        var directMatches = predicate(this);
        var hasVisibleDescendant = false;

        foreach (var child in Children)
        {
            if (child.ApplyFilter(predicate))
            {
                hasVisibleDescendant = true;
            }
        }

        IsVisible = directMatches || hasVisibleDescendant;
        if (IsVisible && hasVisibleDescendant)
        {
            IsExpanded = true;
        }

        return IsVisible;
    }

    private static string DetermineHostColor(string host)
    {
        var h = host.ToLowerInvariant();
        if (h.Contains("gitlab")) return "#FC6D26"; // GitLab Orange
        if (h.Contains("github")) return "#24292F"; // GitHub Dark
        if (h.Contains("gitea")) return "#609926";  // Gitea Green
        return "#6B7280";                           // Neutral Gray
    }
}
