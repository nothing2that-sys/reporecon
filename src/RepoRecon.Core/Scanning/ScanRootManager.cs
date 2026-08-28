using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoRecon.Core.Data;
using RepoRecon.Core.Models;

namespace RepoRecon.Core.Scanning;

public class ScanRootManager
{
    private readonly SqliteIndexStore _store;
    private readonly ILogger<ScanRootManager> _logger;

    public ScanRootManager(SqliteIndexStore store, ILogger<ScanRootManager>? logger = null)
    {
        _store = store;
        _logger = logger ?? NullLogger<ScanRootManager>.Instance;
    }

    public async Task<IReadOnlyList<ScanRoot>> GetRootsAsync(CancellationToken cancellationToken = default)
    {
        var roots = await _store.GetScanRootsAsync(cancellationToken).ConfigureAwait(false);
        var updatedList = new List<ScanRoot>();

        foreach (var root in roots)
        {
            var exists = Directory.Exists(root.Path);
            var isOffline = !exists;

            if (isOffline != root.IsOffline)
            {
                var updated = root with { IsOffline = isOffline };
                await _store.UpdateScanRootAsync(updated, cancellationToken).ConfigureAwait(false);
                updatedList.Add(updated);
            }
            else
            {
                updatedList.Add(root);
            }
        }

        return updatedList;
    }

    public async Task UpdateRootAsync(ScanRoot root, CancellationToken cancellationToken = default)
    {
        await _store.UpdateScanRootAsync(root, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(long RootId, bool WasNestedAbsorbed, bool WasRejected, string? Message)> AddOrAbsorbRootAsync(
        string rawPath,
        int maxDepth = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            throw new ArgumentException("경로가 비어 있습니다.", nameof(rawPath));

        var fullPath = Path.GetFullPath(rawPath.Trim());
        string? warningMessage = null;

        if (IsNetworkPath(fullPath))
        {
            warningMessage = "네트워크 드라이브/UNC 경로가 감지되었습니다. 스캔 속도가 느릴 수 있습니다.";
        }

        var existingRoots = await _store.GetScanRootsAsync(cancellationToken).ConfigureAwait(false);

        // 1. Check if an existing root already contains this new path (rejected / already covered, B-15)
        foreach (var existing in existingRoots)
        {
            if (IsSubdirectory(existing.Path, fullPath))
            {
                var msg = $"상위 경로 '{existing.Path}'에 이미 포함되어 있어 추가되지 않았습니다. {warningMessage}".Trim();
                _logger.LogInformation("Scan root add rejected: {Path} already covered by existing root {ExistingPath} (id={ExistingId})", fullPath, existing.Path, existing.Id);
                return (existing.Id, WasNestedAbsorbed: false, WasRejected: true, msg);
            }
        }

        // 2. Check if the new path contains any existing roots (absorb existing children, B-15)
        var wasNestedAbsorbed = false;
        var absorbedPaths = new List<string>();
        foreach (var existing in existingRoots)
        {
            if (IsSubdirectory(fullPath, existing.Path))
            {
                await _store.DeleteScanRootAsync(existing.Id, cancellationToken).ConfigureAwait(false);
                wasNestedAbsorbed = true;
                absorbedPaths.Add(existing.Path);
            }
        }

        var newId = await _store.AddScanRootAsync(fullPath, maxDepth, cancellationToken).ConfigureAwait(false);
        var successMsg = wasNestedAbsorbed
            ? $"하위 경로가 흡수되었습니다. {warningMessage}".Trim()
            : $"스캔 루트가 추가되었습니다. {warningMessage}".Trim();

        if (wasNestedAbsorbed)
        {
            _logger.LogInformation("Scan root added: {Path} (id={Id}), absorbed nested roots: {Absorbed}", fullPath, newId, string.Join(", ", absorbedPaths));
        }
        else
        {
            _logger.LogInformation("Scan root added: {Path} (id={Id})", fullPath, newId);
        }

        return (newId, wasNestedAbsorbed, WasRejected: false, successMsg);
    }

    public async Task RemoveRootAsync(long rootId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Scan root removed: id={Id}", rootId);
        await _store.DeleteScanRootAsync(rootId, cancellationToken).ConfigureAwait(false);
    }

    public static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return true;

        try
        {
            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root))
            {
                var driveInfo = new DriveInfo(root);
                return driveInfo.DriveType == DriveType.Network;
            }
        }
        catch
        {
            // Ignore drive info lookup errors
        }

        return false;
    }

    public static bool IsSubdirectory(string parentPath, string childPath)
    {
        var parent = NormalizePath(parentPath);
        var child = NormalizePath(childPath);

        if (string.Equals(parent, child, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!parent.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            parent += Path.DirectorySeparatorChar;

        return child.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
