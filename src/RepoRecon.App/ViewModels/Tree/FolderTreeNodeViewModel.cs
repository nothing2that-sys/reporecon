using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoRecon.Core.Tree;

namespace RepoRecon.App.ViewModels.Tree;

public partial class FolderTreeNodeViewModel : TreeNodeViewModelBase
{
    /// <summary>
    /// Selection surface used by the folder CheckBox. A direct binding to
    /// IsSelected bypasses SetSelected and therefore cannot cascade to the
    /// descendant repositories.
    /// </summary>
    public bool? CascadeSelection
    {
        get => IsSelected;
        set
        {
            if (IsSelected == value)
                return;

            SetSelected(value, updateChildren: true, updateParent: true);
        }
    }

    [ObservableProperty]
    private string _compressedDisplayPath = string.Empty;

    [ObservableProperty]
    private int _rollupBehind;

    [ObservableProperty]
    private int _rollupAhead;

    [ObservableProperty]
    private int _rollupWarning;

    [ObservableProperty]
    private int _rollupTotalRepos;

    [ObservableProperty]
    private string _rollupSummaryText = string.Empty;

    public override bool IsFolder => true;

    public FolderTreeNodeViewModel(TreeFolderNode node)
    {
        Name = node.Name;
        FullPath = node.FullPath;
        CompressedDisplayPath = node.CompressedDisplayPath;
        RollupBehind = node.RollupBehind;
        RollupAhead = node.RollupAhead;
        RollupWarning = node.RollupWarning;
        RollupTotalRepos = node.RollupTotalRepos;
        RollupSummaryText = node.RollupSummaryText;
    }

    [RelayCommand]
    private void OpenInExplorer()
    {
        try
        {
            if (Directory.Exists(FullPath))
            {
                var isShiftPressed = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) == System.Windows.Input.ModifierKeys.Shift;
                var args = isShiftPressed ? $"/select,\"{FullPath}\"" : $"\"{FullPath}\"";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
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
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
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
    private void CopyPath()
    {
        try
        {
            System.Windows.Clipboard.SetText(FullPath);
        }
        catch { }
    }

    public override bool ApplyFilter(Func<RepoTreeNodeViewModel, bool> predicate)
    {
        var hasVisibleDescendant = false;

        foreach (var child in Children)
        {
            var childMatches = child.ApplyFilter(predicate);
            if (childMatches)
            {
                hasVisibleDescendant = true;
            }
        }

        IsVisible = hasVisibleDescendant;
        if (IsVisible)
        {
            // Auto-expand intermediate folders on matching filter (설계 §5.3-f)
            IsExpanded = true;
        }

        UpdateSelectionFromChildren();
        return IsVisible;
    }

    public void RecalculateRollup()
    {
        var behind = 0;
        var ahead = 0;
        var warning = 0;
        var total = 0;

        foreach (var child in Children)
        {
            if (child is FolderTreeNodeViewModel folderChild)
            {
                folderChild.RecalculateRollup();
                behind += folderChild.RollupBehind;
                ahead += folderChild.RollupAhead;
                warning += folderChild.RollupWarning;
                total += folderChild.RollupTotalRepos;
            }
            else if (child is RepoTreeNodeViewModel repoChild)
            {
                if (!repoChild.IsMissing)
                {
                    total++;
                    if (repoChild.Behind > 0 || repoChild.StateCode == Core.Models.RepoStateCode.Behind)
                        behind++;
                    if (repoChild.Ahead > 0 || repoChild.StateCode == Core.Models.RepoStateCode.Ahead)
                        ahead++;
                    if (repoChild.DirtyCount > 0 ||
                        repoChild.ConflictedCount > 0 ||
                        repoChild.StateCode == Core.Models.RepoStateCode.Diverged ||
                        repoChild.StateCode == Core.Models.RepoStateCode.NoUpstream ||
                        repoChild.StateCode == Core.Models.RepoStateCode.UpstreamGone ||
                        repoChild.StateCode == Core.Models.RepoStateCode.DetachedHead ||
                        repoChild.StateCode == Core.Models.RepoStateCode.Untracked)
                    {
                        warning++;
                    }
                }
            }
        }

        RollupBehind = behind;
        RollupAhead = ahead;
        RollupWarning = warning;
        RollupTotalRepos = total;

        var parts = new List<string>();
        if (RollupBehind > 0) parts.Add($"↓{RollupBehind}");
        if (RollupAhead > 0) parts.Add($"↑{RollupAhead}");
        if (RollupWarning > 0) parts.Add($"⚠{RollupWarning}");
        RollupSummaryText = parts.Count > 0 ? string.Join(" ", parts) : string.Empty;
    }
}
