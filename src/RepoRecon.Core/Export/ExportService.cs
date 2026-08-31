using System.Text;
using RepoRecon.Core.Urls;

namespace RepoRecon.Core.Export;

public class ExportService : IExportService
{
    public string ExportToCsv(IEnumerable<ExportItem> items)
    {
        var sb = new StringBuilder();
        // Header
        sb.AppendLine("FolderName,AbsPath,NormalizedKey,Branch,Upstream,Ahead,Behind,DirtyCount,ConflictedCount,State,Freshness,CloneUrl");

        foreach (var item in items)
        {
            var maskedUrl = !string.IsNullOrWhiteSpace(item.CloneUrl)
                ? RemoteUrlNormalizer.MaskCredentials(item.CloneUrl)
                : string.Empty;

            sb.Append(EscapeCsv(item.FolderName)).Append(',');
            sb.Append(EscapeCsv(item.AbsPath)).Append(',');
            sb.Append(EscapeCsv(item.NormalizedKey ?? string.Empty)).Append(',');
            sb.Append(EscapeCsv(item.Branch)).Append(',');
            sb.Append(EscapeCsv(item.Upstream)).Append(',');
            sb.Append(item.Ahead).Append(',');
            sb.Append(item.Behind).Append(',');
            sb.Append(item.DirtyCount).Append(',');
            sb.Append(item.ConflictedCount).Append(',');
            sb.Append(EscapeCsv(item.StateDisplay)).Append(',');
            sb.Append(EscapeCsv(item.Freshness)).Append(',');
            sb.Append(EscapeCsv(maskedUrl));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public string ExportToMarkdown(IEnumerable<ExportItem> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# RepoRecon 상태 리포트");
        sb.AppendLine();
        sb.AppendLine($"생성 시각: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine("| 폴더명 | 정규화 키 | 브랜치 → Upstream | 상태 | 수정/충돌 | 신선도 | Clone URL |");
        sb.AppendLine("|---|---|---|---|---|---|---|");

        foreach (var item in items)
        {
            var branchText = string.IsNullOrWhiteSpace(item.Upstream) || item.Upstream == "(없음)"
                ? item.Branch
                : $"{item.Branch} → {item.Upstream}";

            var dirtyText = $"수정:{item.DirtyCount} / 충돌:{item.ConflictedCount}";
            var maskedUrl = !string.IsNullOrWhiteSpace(item.CloneUrl)
                ? RemoteUrlNormalizer.MaskCredentials(item.CloneUrl)
                : "-";

            sb.AppendLine($"| {EscapeMd(item.FolderName)} | {EscapeMd(item.NormalizedKey ?? "-")} | {EscapeMd(branchText)} | {EscapeMd(item.StateDisplay)} | {EscapeMd(dirtyText)} | {EscapeMd(item.Freshness)} | {EscapeMd(maskedUrl)} |");
        }

        return sb.ToString();
    }

    public async Task ExportToFileAsync(
        IEnumerable<ExportItem> items,
        string filePath,
        bool isMarkdown,
        CancellationToken cancellationToken = default)
    {
        var content = isMarkdown ? ExportToMarkdown(items) : ExportToCsv(items);
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(filePath, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    private static string EscapeCsv(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r'))
        {
            return $"\"{text.Replace("\"", "\"\"")}\"";
        }
        return text;
    }

    private static string EscapeMd(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Replace("|", "\\|").Replace("\n", " ").Replace("\r", "");
    }
}
