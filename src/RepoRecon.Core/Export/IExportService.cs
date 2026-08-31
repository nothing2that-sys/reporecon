namespace RepoRecon.Core.Export;

public record ExportItem(
    string FolderName,
    string AbsPath,
    string? NormalizedKey,
    string Branch,
    string Upstream,
    int Ahead,
    int Behind,
    int DirtyCount,
    int ConflictedCount,
    string StateDisplay,
    string Freshness,
    string? CloneUrl);

public interface IExportService
{
    string ExportToCsv(IEnumerable<ExportItem> items);

    string ExportToMarkdown(IEnumerable<ExportItem> items);

    Task ExportToFileAsync(IEnumerable<ExportItem> items, string filePath, bool isMarkdown, CancellationToken cancellationToken = default);
}
