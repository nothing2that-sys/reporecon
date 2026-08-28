using RepoRecon.Core.Models;

namespace RepoRecon.Core.Tree;

public class TreeRepoNode
{
    public LocalRepo Repo { get; }
    public LocalStatus? Status { get; set; }
    public string FolderName { get; }
    public string DisplayRepoPath { get; }
    public string HostName { get; }
    public int DuplicateCloneCount { get; set; } = 1;
    public List<long> DuplicateSiblingIds { get; } = new();
    public bool IsNested => Repo.ParentRepoId.HasValue;
    public bool HasGitIgnoreWarning { get; set; }
    public List<TreeRepoNode> NestedChildRepos { get; } = new();
    public List<TreeFolderNode> SubFolders { get; } = new();

    public TreeRepoNode(LocalRepo repo, LocalStatus? status)
    {
        Repo = repo;
        Status = status;
        FolderName = Path.GetFileName(repo.AbsPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(FolderName))
        {
            FolderName = repo.AbsPath;
        }

        if (!string.IsNullOrWhiteSpace(repo.NormalizedKey))
        {
            var parts = repo.NormalizedKey.Split(new[] { ':', '/' }, 2);
            HostName = parts[0];
            DisplayRepoPath = repo.NormalizedKey.Replace('/', ' ').Replace(':', ' ');
        }
        else
        {
            HostName = "Local";
            DisplayRepoPath = FolderName;
        }
    }
}
