using RepoRecon.Core.Models;

namespace RepoRecon.Core.Clone;

public interface ICloneEngine
{
    Task<IReadOnlyList<ClonePreviewItem>> PreviewClonesAsync(
        IReadOnlyList<ServerRepo> repos,
        string targetRoot,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CloneResult>> ExecuteBatchCloneAsync(
        IReadOnlyList<ServerRepo> repos,
        string targetRoot,
        IProgress<BatchCloneProgress>? progress = null,
        CancellationToken cancellationToken = default);

    IReadOnlyList<ResidualTempFolder> FindResidualTempFolders(IEnumerable<string> scanRoots);

    bool CleanResidualTempFolder(string tempPath);
}
