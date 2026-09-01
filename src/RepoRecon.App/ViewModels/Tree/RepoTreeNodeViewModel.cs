using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoRecon.App.ViewModels;
using RepoRecon.Core.Models;
using RepoRecon.Core.Tree;
using RepoRecon.Core.Urls;

namespace RepoRecon.App.ViewModels.Tree;

public partial class RepoTreeNodeViewModel : TreeNodeViewModelBase
{
    private readonly Action<long>? _onNavigateToSibling;
    private string? _fetchFailureLabel;

    public TreeRepoNode Node { get; }

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
    private string _hostDotColor = AppColors.NeutralMuted;

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

    // English -- feeds CSV/Markdown export (§3.2.2 (c)) via MainViewModel's
    // ExportItem construction. Never bind this to the UI.
    [ObservableProperty]
    private string _stateDisplay = "Unknown (Not Fetched)";

    [ObservableProperty]
    private string _threeAxisSummary = string.Empty;

    [ObservableProperty]
    private string? _missingBadgeText;

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

    // Badges (§9.4, 완료 기준 33)
    public bool HasLfsBadge => Repo.HasLfs;
    public bool HasWorktreeBadge => Repo.GitDirKind != GitDirKind.Normal;
    public string WorktreeBadgeText => Repo.GitDirKind == GitDirKind.Bare ? "Bare" : (Repo.GitDirKind == GitDirKind.Worktree ? "Worktree" : string.Empty);

    // Browser Open (§10.1, 완료 기준 3: 카탈로그에 없는 레포는 비활성)
    public ServerRepo? MatchedServerRepo => Node.MatchedServerRepo;
    public ServerProfile? MatchedProfile => Node.MatchedProfile;
    public bool CanOpenInBrowser => MatchedServerRepo != null && !string.IsNullOrWhiteSpace(MatchedServerRepo.CloneUrl);

    // Suggestions (§3.4, §3.5, 완료 기준 14, 16)
    public bool HasUpstreamGoneSuggestion => StateCode == RepoStateCode.UpstreamGone && MatchedServerRepo != null && !string.IsNullOrWhiteSpace(MatchedServerRepo.DefaultBranch);
    public string UpstreamGoneSuggestionText => MatchedServerRepo != null ? $"origin/{MatchedServerRepo.DefaultBranch}으로 전환?" : string.Empty;

    [ObservableProperty]
    private bool _hasUrlChangeSuggestion;

    [ObservableProperty]
    private string? _urlChangeOldUrl;

    [ObservableProperty]
    private string? _urlChangeNewHostUrl;

    [ObservableProperty]
    private string? _urlChangeSuggestionText;

    public override bool IsFolder => false;

    public List<long> DuplicateSiblingIds { get; } = new();

    // Context Menu Action delegates (injected from MainViewModel)
    public Action<RepoTreeNodeViewModel>? OnChangeRemoteUrlRequested { get; set; }
    public Action<RepoTreeNodeViewModel>? OnForcePushRequested { get; set; }
    public Action<RepoTreeNodeViewModel>? OnPushNoUpstreamRequested { get; set; }
    public Action<RepoTreeNodeViewModel>? OnSetUpstreamGoneRequested { get; set; }
    public Action<RepoTreeNodeViewModel>? OnApplyUrlChangeRequested { get; set; }

    public RepoTreeNodeViewModel(
        TreeRepoNode node,
        Action<long>? onNavigateToSibling = null)
    {
        Node = node;
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
            ServerStatusBadge = StateBadgeText.Orphaned;
        }
        else if (IsExternal)
        {
            ServerStatusBadge = StateBadgeText.External;
        }
        else if (IsUntracked)
        {
            ServerStatusBadge = StateBadgeText.Untracked;
        }

        if (IsArchived)
        {
            ServerStatusBadge = ServerStatusBadge != null ? $"{ServerStatusBadge} · {StateBadgeText.Archived}" : StateBadgeText.Archived;
        }

        HasServerStatusBadge = !string.IsNullOrWhiteSpace(ServerStatusBadge);
        MissingBadgeText = IsMissing ? StateBadgeText.MissingConfirmation : null;
        HostDotColor = DetermineHostColor(node.HostName);

        ApplyStatus(node.Status);
    }

    #region Context Menu Commands (§10.1, 완료 기준 1~3, 17, 23, 27)

    [RelayCommand]
    private void OpenInExplorer()
    {
        try
        {
            if (Directory.Exists(FullPath))
            {
                var isShiftPressed = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) == System.Windows.Input.ModifierKeys.Shift;
                var args = isShiftPressed ? $"/select,\"{FullPath}\"" : $"\"{FullPath}\"";
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = args,
                    UseShellExecute = true
                });
            }
        }
        catch { }
    }

    [RelayCommand]
    private void OpenTerminalHere()
    {
        try
        {
            if (Directory.Exists(FullPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    WorkingDirectory = FullPath,
                    UseShellExecute = true
                });
            }
        }
        catch { }
    }

    [RelayCommand]
    private void OpenInBrowser()
    {
        if (!CanOpenInBrowser || MatchedServerRepo == null) return;
        try
        {
            var url = MatchedServerRepo.CloneUrl;
            if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                url = url[..^4];
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }

    [RelayCommand]
    private void CopyPath()
    {
        try
        {
            Clipboard.SetText(FullPath);
        }
        catch { }
    }

    [RelayCommand]
    private void CopyCloneUrl()
    {
        try
        {
            var url = MatchedServerRepo?.CloneUrl ?? (!string.IsNullOrWhiteSpace(Repo.NormalizedKey) ? $"https://{Repo.NormalizedKey}.git" : string.Empty);
            if (!string.IsNullOrWhiteSpace(url))
            {
                Clipboard.SetText(RemoteUrlNormalizer.MaskCredentials(url));
            }
        }
        catch { }
    }

    [RelayCommand]
    private void ChangeRemoteUrl()
    {
        OnChangeRemoteUrlRequested?.Invoke(this);
    }

    [RelayCommand]
    private void ForcePush()
    {
        OnForcePushRequested?.Invoke(this);
    }

    [RelayCommand]
    private void PushNoUpstream()
    {
        OnPushNoUpstreamRequested?.Invoke(this);
    }

    [RelayCommand]
    private void SetUpstreamGone()
    {
        OnSetUpstreamGoneRequested?.Invoke(this);
    }

    [RelayCommand]
    private void ApplyUrlChange()
    {
        OnApplyUrlChangeRequested?.Invoke(this);
    }

    #endregion

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

        StateCode = status?.StateCode ?? RepoStateCode.UnknownUnfetched;

        var isFetchPending = (LastFetchedAt == null || HasFetchFailure) &&
            (StateCode == RepoStateCode.Ahead || StateCode == RepoStateCode.Behind || StateCode == RepoStateCode.UpToDate || StateCode == RepoStateCode.Diverged);

        string stateDisplayKo;
        if (isFetchPending)
        {
            // §3.2.2 (c): StateDisplay stays English -- it feeds CSV/Markdown
            // export via MainViewModel. stateDisplayKo is screen-only.
            StateDisplay = HasFetchFailure ? "Unknown (Fetch Failed)" : "Unknown (Not Fetched)";
            stateDisplayKo = HasFetchFailure ? StateBadgeText.UnknownFetchFailed : StateBadgeText.UnknownUnfetched;
        }
        else
        {
            StateDisplay = StateCode switch
            {
                RepoStateCode.NoUpstream => "No Upstream",
                RepoStateCode.UpstreamGone => "Upstream Gone",
                RepoStateCode.DetachedHead => "Detached HEAD",
                RepoStateCode.Untracked => "Untracked",
                RepoStateCode.Conflicted => "Conflicted",
                RepoStateCode.Diverged => $"Diverged (↑{Ahead} ↓{Behind})",
                RepoStateCode.Ahead => $"Ahead (↑{Ahead})",
                RepoStateCode.Behind => $"Behind (↓{Behind})",
                RepoStateCode.UpToDate => "Up-to-Date",
                _ => "Unknown (Not Fetched)"
            };
            stateDisplayKo = StateBadgeText.ForSyncState(StateCode, Ahead, Behind);
        }

        var parts = new List<string> { stateDisplayKo };
        if (DirtyCount > 0)
        {
            parts.Add(StateBadgeText.ForDirty(DirtyCount));
        }
        if (ConflictedCount > 0)
        {
            parts.Add(StateBadgeText.ForConflicted(ConflictedCount));
        }

        ThreeAxisSummary = string.Join(" · ", parts);
        MissingBadgeText = IsMissing ? StateBadgeText.MissingConfirmation : null;
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
        // Brand identity colors, not status colors — intentionally outside the
        // 6-role semantic palette (§2). They distinguish *which server*, not
        // *what state*, so folding them into e.g. "정보" would lose information.
        var h = host.ToLowerInvariant();
        if (h.Contains("gitlab")) return "#FC6D26"; // GitLab Orange
        if (h.Contains("github")) return "#24292F"; // GitHub Dark
        if (h.Contains("gitea")) return "#609926";  // Gitea Green
        return AppColors.NeutralMuted;
    }
}
