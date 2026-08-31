using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoRecon.Core.Git;
using RepoRecon.Core.Models;

namespace RepoRecon.App.ViewModels;

public partial class BulkHostReplaceViewModel : ObservableObject
{
    private readonly IRemoteUrlService _remoteUrlService;
    private readonly List<(LocalRepo Repo, string CurrentUrl)> _targets = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGeneratePreview))]
    private string _oldHost = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGeneratePreview))]
    private string _newHost = string.Empty;

    [ObservableProperty]
    private bool _isPreviewGenerated;

    [ObservableProperty]
    private bool _isExecuting;

    [ObservableProperty]
    private string _previewSummaryText = string.Empty;

    [ObservableProperty]
    private string _sampleValidationStatus = string.Empty;

    [ObservableProperty]
    private string _sampleValidationColor = AppColors.NeutralMuted;

    public bool CanGeneratePreview => !string.IsNullOrWhiteSpace(OldHost) && !string.IsNullOrWhiteSpace(NewHost) && !string.Equals(OldHost.Trim(), NewHost.Trim(), StringComparison.OrdinalIgnoreCase);

    public ObservableCollection<BulkHostReplacePreviewItem> PreviewItems { get; } = new();

    public Func<Task>? OnCompletedCallback { get; set; }

    public BulkHostReplaceViewModel(
        IRemoteUrlService remoteUrlService,
        IEnumerable<(LocalRepo Repo, string CurrentUrl)> targets)
    {
        _remoteUrlService = remoteUrlService;
        _targets.AddRange(targets);
    }

    [RelayCommand]
    public async Task GeneratePreviewAsync()
    {
        if (!CanGeneratePreview) return;

        PreviewItems.Clear();
        var preview = _remoteUrlService.PreviewBulkHostReplace(OldHost.Trim(), NewHost.Trim(), _targets);
        foreach (var item in preview)
        {
            PreviewItems.Add(item);
        }

        var eligibleCount = PreviewItems.Count(p => p.IsEligible);
        PreviewSummaryText = $"총 {PreviewItems.Count}개 중 {eligibleCount}개 저장소 치환 가능 (Dry-run 미리보기)";
        IsPreviewGenerated = true;

        // Sample ls-remote validation on first eligible item (§10.7-b, Criterion 20)
        var firstEligible = PreviewItems.FirstOrDefault(p => p.IsEligible);
        if (firstEligible != null)
        {
            SampleValidationStatus = "새 호스트 표본 접근성 검사 중 (ls-remote)...";
            SampleValidationColor = AppColors.Info;

            var (reachable, error) = await _remoteUrlService.TestRemoteReachableAsync(
                firstEligible.Repo.AbsPath,
                firstEligible.NewUrl);

            if (reachable)
            {
                SampleValidationStatus = $"✅ 표본 접근 검증 성공 ({firstEligible.Repo.AbsPath})";
                SampleValidationColor = AppColors.Success;
            }
            else
            {
                SampleValidationStatus = $"⚠️ 표본 접근 검증 실패: {error} (오프라인 상태일 수 있음)";
                SampleValidationColor = AppColors.Warning;
            }
        }
        else
        {
            SampleValidationStatus = "치환 가능한 대상이 없습니다.";
            SampleValidationColor = AppColors.NeutralMuted;
        }
    }

    [RelayCommand]
    public async Task ExecuteAsync(Action? closeWindowAction = null)
    {
        if (!IsPreviewGenerated || PreviewItems.Count(p => p.IsEligible) == 0) return;

        IsExecuting = true;
        try
        {
            await _remoteUrlService.ExecuteBulkHostReplaceAsync(
                OldHost.Trim(),
                NewHost.Trim(),
                _targets);

            if (OnCompletedCallback != null)
            {
                await OnCompletedCallback();
            }

            closeWindowAction?.Invoke();
        }
        finally
        {
            IsExecuting = false;
        }
    }
}
