using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoRecon.Core.Pull;

namespace RepoRecon.App.ViewModels;

public partial class PullPreviewViewModel : ObservableObject
{
    [ObservableProperty]
    private int _totalSelectedCount;

    [ObservableProperty]
    private int _eligibleCount;

    [ObservableProperty]
    private int _skippedCount;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    public ObservableCollection<PullPreviewItem> EligibleItems { get; } = new();
    public ObservableCollection<PullPreviewItem> SkippedItems { get; } = new();

    public event Action<bool>? RequestClose;

    public PullPreviewViewModel(IReadOnlyList<PullPreviewItem> items)
    {
        TotalSelectedCount = items.Count;
        EligibleCount = items.Count(x => x.IsEligible);
        SkippedCount = items.Count(x => !x.IsEligible);

        SummaryText = $"총 {TotalSelectedCount}개 선택됨 — 실행 대상: {EligibleCount}개 / 스킵 대상: {SkippedCount}개";

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
