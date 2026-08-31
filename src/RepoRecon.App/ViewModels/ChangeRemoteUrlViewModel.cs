using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoRecon.Core.Git;
using RepoRecon.Core.Models;
using RepoRecon.Core.Urls;

namespace RepoRecon.App.ViewModels;

public partial class ChangeRemoteUrlViewModel : ObservableObject
{
    private readonly IRemoteUrlService _remoteUrlService;
    private readonly HostAliasTable _aliasTable;
    private readonly IReadOnlyList<ServerProfile> _profiles;
    private readonly IReadOnlyList<ServerRepo> _serverRepos;

    public long RepoId { get; }
    public string RepoPath { get; }

    [ObservableProperty]
    private string _remoteName = "origin";

    [ObservableProperty]
    private string _currentUrl = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _newUrl = string.Empty;

    [ObservableProperty]
    private bool _isCheckingReachability;

    [ObservableProperty]
    private bool _hasCheckedReachability;

    [ObservableProperty]
    private bool _isReachable;

    [ObservableProperty]
    private string _reachabilityStatus = "검사 전";

    [ObservableProperty]
    private string _reachabilityStatusColor = AppColors.NeutralMuted;

    [ObservableProperty]
    private string _reevaluatedMappingStatus = "미확인";

    [ObservableProperty]
    private string _reevaluatedMappingColor = AppColors.NeutralMuted;

    public bool CanSave => !string.IsNullOrWhiteSpace(NewUrl) && !string.Equals(NewUrl.Trim(), CurrentUrl.Trim(), StringComparison.OrdinalIgnoreCase);

    public Func<Task>? OnSavedCallback { get; set; }

    public ChangeRemoteUrlViewModel(
        long repoId,
        string repoPath,
        string currentUrl,
        IRemoteUrlService remoteUrlService,
        HostAliasTable aliasTable,
        IReadOnlyList<ServerProfile> profiles,
        IReadOnlyList<ServerRepo> serverRepos)
    {
        RepoId = repoId;
        RepoPath = repoPath;
        CurrentUrl = currentUrl;
        NewUrl = currentUrl;
        _remoteUrlService = remoteUrlService;
        _aliasTable = aliasTable;
        _profiles = profiles;
        _serverRepos = serverRepos;

        UpdateReevaluatedMapping(NewUrl);
    }

    partial void OnNewUrlChanged(string value)
    {
        HasCheckedReachability = false;
        ReachabilityStatus = "검사 전";
        ReachabilityStatusColor = AppColors.NeutralMuted;
        UpdateReevaluatedMapping(value);
    }

    private void UpdateReevaluatedMapping(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            ReevaluatedMappingStatus = "URL 없음 (Untracked)";
            ReevaluatedMappingColor = AppColors.NeutralMuted;
            return;
        }

        var normKey = RemoteUrlNormalizer.Normalize(url, _aliasTable);
        if (string.IsNullOrWhiteSpace(normKey))
        {
            ReevaluatedMappingStatus = "정규화 불가 (로컬 경로 또는 파싱 실패)";
            ReevaluatedMappingColor = AppColors.Warning;
            return;
        }

        // Check against catalog (§10.7-a Criterion 18)
        var matchedRepo = _serverRepos.FirstOrDefault(r => string.Equals(r.NormalizedKey, normKey, StringComparison.OrdinalIgnoreCase));
        if (matchedRepo != null)
        {
            ReevaluatedMappingStatus = $"정상 (카탈로그 매칭: {matchedRepo.Namespace}/{matchedRepo.Name})";
            ReevaluatedMappingColor = AppColors.Success;
            return;
        }

        // Check if host matches any registered server profile
        var hostPart = normKey.Split(new[] { ':', '/' }, 2)[0];
        var matchedProfile = _profiles.FirstOrDefault(p =>
            p.Host.Equals(hostPart, StringComparison.OrdinalIgnoreCase) ||
            p.Aliases.Any(a => a.Equals(hostPart, StringComparison.OrdinalIgnoreCase)));

        if (matchedProfile != null)
        {
            ReevaluatedMappingStatus = $"Orphaned (프로파일 호스트 '{matchedProfile.Host}'이나 카탈로그에 없음)";
            ReevaluatedMappingColor = AppColors.Warning;
        }
        else
        {
            ReevaluatedMappingStatus = "External (미등록 외부 호스트)";
            ReevaluatedMappingColor = AppColors.NeutralMuted;
        }
    }

    [RelayCommand]
    public async Task CheckReachabilityAsync()
    {
        if (string.IsNullOrWhiteSpace(NewUrl)) return;

        IsCheckingReachability = true;
        ReachabilityStatus = "접근성 검사 중 (ls-remote)...";
        ReachabilityStatusColor = AppColors.Info;

        try
        {
            var (reachable, error) = await _remoteUrlService.TestRemoteReachableAsync(RepoPath, NewUrl.Trim());
            HasCheckedReachability = true;
            IsReachable = reachable;

            if (reachable)
            {
                ReachabilityStatus = "✅ 연결 및 접근 검증 완료 (정상)";
                ReachabilityStatusColor = AppColors.Success;
            }
            else
            {
                ReachabilityStatus = $"⚠️ 접근 확인 실패: {error} (오프라인일 수 있으며 저장은 가능합니다)";
                ReachabilityStatusColor = AppColors.Error;
            }
        }
        finally
        {
            IsCheckingReachability = false;
        }
    }

    [RelayCommand]
    public async Task SaveAsync(Action? closeWindowAction = null)
    {
        if (!CanSave) return;

        var (success, error, _) = await _remoteUrlService.ChangeRemoteUrlAsync(
            RepoId,
            RepoPath,
            RemoteName,
            NewUrl.Trim());

        if (success)
        {
            if (OnSavedCallback != null)
            {
                await OnSavedCallback();
            }
            closeWindowAction?.Invoke();
        }
        else
        {
            System.Windows.MessageBox.Show(
                $"Remote URL 변경에 실패했습니다:\n{error}",
                "변경 실패",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }
}
