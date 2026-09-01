using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoRecon.Core.Tree;

namespace RepoRecon.App.ViewModels.Tree;

public partial class FolderTreeNodeViewModel : TreeNodeViewModelBase
{
    // §5.3 v4 breaking change: "폴더 노드 체크 = 하위 전체 선택" is discarded.
    // Selecting a folder now navigates the right-pane scope (§5.3.1/5.3.2);
    // bulk selection lives in the right pane's own checkboxes and its
    // "전체 선택" button, not a cascading folder checkbox.
    //
    // Repos that live directly in this folder (RepoTreeBuilder.TreeFolderNode
    // .Repos) -- kept separate from Children, which now holds only sub-folders,
    // so the left tree never renders a repo as a node (§5.3.1).
    public List<RepoTreeNodeViewModel> DirectRepos { get; } = new();

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
        // §5.3.1/5.3.3 v4 breaking change: folders no longer hide themselves
        // or auto-expand based on the filter (that v3 behavior is discarded,
        // per the design's own breaking-changes table). The filter only
        // narrows the right pane's rows -- MainViewModel sets each repo's
        // IsVisible directly -- and the rollup badge below, recomputed via
        // RecalculateRollup() once every repo's IsVisible reflects the filter.
        foreach (var child in Children)
        {
            child.ApplyFilter(predicate);
        }

        IsVisible = true;
        return true;
    }

    public void RecalculateRollup()
    {
        var behind = 0;
        var ahead = 0;
        var warning = 0;
        var total = 0;

        foreach (var repo in DirectRepos.SelectMany(r => r.GetAllDescendantRepos()))
        {
            if (repo.IsMissing || !repo.IsVisible)
                continue;

            total++;
            if (repo.Behind > 0 || repo.StateCode == Core.Models.RepoStateCode.Behind)
                behind++;
            if (repo.Ahead > 0 || repo.StateCode == Core.Models.RepoStateCode.Ahead)
                ahead++;
            if (repo.DirtyCount > 0 ||
                repo.ConflictedCount > 0 ||
                repo.StateCode == Core.Models.RepoStateCode.Diverged ||
                repo.StateCode == Core.Models.RepoStateCode.NoUpstream ||
                repo.StateCode == Core.Models.RepoStateCode.UpstreamGone ||
                repo.StateCode == Core.Models.RepoStateCode.DetachedHead ||
                repo.StateCode == Core.Models.RepoStateCode.Untracked)
            {
                warning++;
            }
        }

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

    // §5.3.2: right-pane scope for this node -- direct repos only ("직속만"
    // toggle) or the full recursive subtree (default). Each repo is expanded
    // via GetAllDescendantRepos() so its own nested child repos (§2.2's
    // nested-repo boundary badge) land as their own flat rows too.
    public IEnumerable<RepoTreeNodeViewModel> GetScopedRepos(bool directOnly)
    {
        foreach (var repo in DirectRepos.SelectMany(r => r.GetAllDescendantRepos()))
        {
            yield return repo;
        }

        if (!directOnly)
        {
            foreach (var child in Children)
            {
                if (child is FolderTreeNodeViewModel folderChild)
                {
                    foreach (var repo in folderChild.GetScopedRepos(directOnly: false))
                    {
                        yield return repo;
                    }
                }
            }
        }
    }
}
