namespace RepoRecon.Core.Tree;

public class TreeFolderNode
{
    public string Name { get; set; }
    public string FullPath { get; set; }
    public List<string> CompressedSegments { get; } = new();
    public string CompressedDisplayPath => CompressedSegments.Count > 0 ? string.Join(" / ", CompressedSegments) : Name;

    public List<TreeFolderNode> SubFolders { get; } = new();
    public List<TreeRepoNode> Repos { get; } = new();

    public int RollupBehind { get; set; }
    public int RollupAhead { get; set; }
    public int RollupWarning { get; set; }
    public int RollupTotalRepos { get; set; }

    public string RollupSummaryText
    {
        get
        {
            var parts = new List<string>();
            if (RollupBehind > 0) parts.Add($"↓{RollupBehind}");
            if (RollupAhead > 0) parts.Add($"↑{RollupAhead}");
            if (RollupWarning > 0) parts.Add($"⚠{RollupWarning}");
            return parts.Count > 0 ? string.Join(" ", parts) : string.Empty;
        }
    }

    public TreeFolderNode(string name, string fullPath)
    {
        Name = name;
        FullPath = fullPath;
        CompressedSegments.Add(name);
    }
}
