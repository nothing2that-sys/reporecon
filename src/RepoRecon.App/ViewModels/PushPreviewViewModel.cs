using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoRecon.Core.Push;

namespace RepoRecon.App.ViewModels;

public partial class PushPreviewViewModel : ObservableObject
{
    [ObservableProperty]
    private int _totalSelectedCount;

    [ObservableProperty]
    private int _eligibleCount;

    [ObservableProperty]
    private int _skippedCount;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string? _dirtyNoticeText;

    [ObservableProperty]
    private bool _hasDirtyItems;

    public ObservableCollection<PushPreviewItem> EligibleItems { get; } = new();
    public ObservableCollection<PushPreviewItem> SkippedItems { get; } = new();

    public event Action<bool>? RequestClose;

    public PushPreviewViewModel(IReadOnlyList<PushPreviewItem> items)
    {
        TotalSelectedCount = items.Count;
        EligibleCount = items.Count(x => x.IsEligible);
        SkippedCount = items.Count(x => !x.IsEligible);

        SummaryText = $"총 {TotalSelectedCount}개 선택됨 — Push 가능: {EligibleCount}개 / 스킵 대상: {SkippedCount}개 (서버 dry-run 검증 완료)";

        var dirtyCount = items.Count(x => x.IsEligible && !string.IsNullOrWhiteSpace(x.DirtyWarning));
        if (dirtyCount > 0)
        {
            HasDirtyItems = true;
            DirtyNoticeText = $"⚠️ 주의: 실행 대상 중 {dirtyCount}개 저장소에 커밋되지 않은 로컬 수정사항이 있습니다. 커밋되지 않은 파일은 서버로 전송되지 않습니다 (§3.3).";
        }

        foreach (var item in items)
        {
            if (item.IsEligible)
                EligibleItems.Add(item);
            else
                SkippedItems.Add(item);
        }
    }

    [RelayCommand]
    private void Confirm()
    {
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke(false);
    }
}
