using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoRecon.Core.Clone;

namespace RepoRecon.App.ViewModels;

public partial class ClonePreviewViewModel : ObservableObject
{
    public ObservableCollection<ClonePreviewItem> Items { get; } = new();

    [ObservableProperty]
    private string _targetRoot = string.Empty;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _executableCount;

    [ObservableProperty]
    private int _collisionCount;

    public bool HasCollisions => CollisionCount > 0;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    public void LoadPreview(IReadOnlyList<ClonePreviewItem> items, string targetRoot)
    {
        Items.Clear();
        TargetRoot = targetRoot;
        TotalCount = items.Count;

        var executable = 0;
        var collisions = 0;

        foreach (var item in items)
        {
            Items.Add(item);
            if (item.HasCollision)
            {
                collisions++;
            }
            else
            {
                executable++;
            }
        }

        ExecutableCount = executable;
        CollisionCount = collisions;
        SummaryText = $"총 {TotalCount}개 선택 — 실행 가능 {ExecutableCount}개 / 충돌 스킵 {CollisionCount}개";
    }
}
