using CommunityToolkit.Mvvm.ComponentModel;
using RepoRecon.Core.Models;

namespace RepoRecon.App.ViewModels;

public partial class LocalRepoItemViewModel : ObservableObject
{
    [ObservableProperty]
    private long _id;

    [ObservableProperty]
    private string _absPath;

    [ObservableProperty]
    private string? _normalizedKey;

    [ObservableProperty]
    private long? _parentRepoId;

    [ObservableProperty]
    private string _gitDirKind;

    [ObservableProperty]
    private bool _hasLfs;

    [ObservableProperty]
    private bool _hasSubmodule;

    [ObservableProperty]
    private bool _isMissing;

    [ObservableProperty]
    private DateTime? _missingSince;

    [ObservableProperty]
    private string _branch;

    [ObservableProperty]
    private string _upstream;

    [ObservableProperty]
    private int _ahead;

    [ObservableProperty]
    private int _behind;

    [ObservableProperty]
    private int _dirtyCount;

    [ObservableProperty]
    private int _conflictedCount;

    [ObservableProperty]
    private string _headHash;

    [ObservableProperty]
    private RepoStateCode _stateCode;

    [ObservableProperty]
    private string _stateDisplay;

    [ObservableProperty]
    private string _branchDisplay;

    [ObservableProperty]
    private string _freshness;

    [ObservableProperty]
    private string _threeAxisSummary;

    public LocalRepoItemViewModel(LocalRepo repo, LocalStatus? status)
    {
        _id = repo.Id;
        _absPath = repo.AbsPath;
        _normalizedKey = repo.NormalizedKey;
        _parentRepoId = repo.ParentRepoId;
        _gitDirKind = repo.GitDirKind.ToString();
        _hasLfs = repo.HasLfs;
        _hasSubmodule = repo.HasSubmodule;
        _isMissing = repo.IsMissing;
        _missingSince = repo.MissingSince;

        _branch = status?.Branch ?? "(알 수 없음)";
        _upstream = status?.Upstream ?? "(없음)";
        _ahead = status?.Ahead ?? 0;
        _behind = status?.Behind ?? 0;
        _dirtyCount = status?.DirtyCount ?? 0;
        _conflictedCount = status?.ConflictedCount ?? 0;
        _headHash = status?.HeadHash ?? "-";
        _stateCode = status?.StateCode ?? RepoStateCode.UnknownUnfetched;

        _branchDisplay = string.IsNullOrWhiteSpace(status?.Upstream)
            ? _branch
            : $"{_branch} → {status.Upstream}";

        _freshness = "미확인 (fetch 전)";

        _stateDisplay = _stateCode switch
        {
            RepoStateCode.NoUpstream => "No Upstream",
            RepoStateCode.UpstreamGone => "Upstream Gone",
            RepoStateCode.DetachedHead => "Detached HEAD",
            RepoStateCode.Untracked => "Untracked",
            RepoStateCode.Conflicted => "Conflicted",
            RepoStateCode.Diverged => $"Diverged (↑{_ahead} ↓{_behind})",
            RepoStateCode.Ahead => $"Ahead (↑{_ahead})",
            RepoStateCode.Behind => $"Behind (↓{_behind})",
            RepoStateCode.UpToDate => "Up-to-Date",
            _ => "미확인 (fetch 전)"
        };

        var parts = new List<string> { _stateDisplay };
        if (_dirtyCount > 0)
        {
            parts.Add($"수정 {_dirtyCount}");
        }
        if (_conflictedCount > 0)
        {
            parts.Add($"충돌 {_conflictedCount}");
        }
        if (_isMissing)
        {
            parts.Add("(미발견)");
        }

        _threeAxisSummary = string.Join(" · ", parts);
    }
}
