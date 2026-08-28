using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RepoRecon.App.ViewModels.Tree;

public abstract partial class TreeNodeViewModelBase : ObservableObject
{
    public event EventHandler? SelectionChanged;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _fullPath = string.Empty;

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private bool? _isSelected = false;

    partial void OnIsSelectedChanged(bool? value)
    {
        // FolderTreeNodeViewModel exposes CascadeSelection as the UI-facing
        // property. Notify it whenever selection is recomputed from children.
        OnPropertyChanged("CascadeSelection");
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public TreeNodeViewModelBase? Parent { get; set; }
    public ObservableCollection<TreeNodeViewModelBase> Children { get; } = new();

    public abstract bool IsFolder { get; }

    public virtual void SetSelected(bool? value, bool updateChildren = true, bool updateParent = true)
    {
        if (IsSelected == value)
            return;

        IsSelected = value;

        if (updateChildren && value.HasValue)
        {
            foreach (var child in Children)
            {
                if (child.IsVisible)
                {
                    child.SetSelected(value.Value, updateChildren: true, updateParent: false);
                }
            }
        }

        if (updateParent && Parent != null)
        {
            Parent.UpdateSelectionFromChildren();
        }
    }

    public virtual void UpdateSelectionFromChildren()
    {
        var visibleChildren = Children.Where(c => c.IsVisible).ToList();
        if (visibleChildren.Count == 0)
            return;

        var allTrue = visibleChildren.All(c => c.IsSelected == true);
        var allFalse = visibleChildren.All(c => c.IsSelected == false);

        bool? newSelected = allTrue ? true : (allFalse ? false : null);

        if (IsSelected != newSelected)
        {
            IsSelected = newSelected;
            Parent?.UpdateSelectionFromChildren();
        }
    }

    public IEnumerable<RepoTreeNodeViewModel> GetAllDescendantRepos(bool visibleOnly = false)
    {
        if (visibleOnly && !IsVisible)
            yield break;

        if (this is RepoTreeNodeViewModel repoNode)
        {
            yield return repoNode;
        }

        foreach (var child in Children)
        {
            foreach (var r in child.GetAllDescendantRepos(visibleOnly))
            {
                yield return r;
            }
        }
    }

    public abstract bool ApplyFilter(Func<RepoTreeNodeViewModel, bool> predicate);
}
