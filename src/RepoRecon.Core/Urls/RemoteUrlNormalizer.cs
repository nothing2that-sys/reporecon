using System.Text.RegularExpressions;

namespace RepoRecon.Core.Urls;

public static class RemoteUrlNormalizer
{
    private static readonly Regex ScpPattern = new(
        @"^(?:(?<user>[^@]+)@)?(?<host>[^:]+):(?<path>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string? Normalize(string? rawUrl, HostAliasTable? aliasTable = null)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
            return null;

        var trimmed = rawUrl.Trim();

        // Check for local file paths first to prevent Windows drive letters (e.g. D:\...) from matching SCP host
        if (IsLocalFilePath(trimmed))
        {
            return null;
        }

        string host;
        int port;
        string path;

        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            if (trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
                return null;

            host = uri.Host;
            port = uri.Port > 0 ? uri.Port : GetDefaultPortForScheme(uri.Scheme);
            path = Uri.UnescapeDataString(uri.AbsolutePath);
        }
        else
        {
            var match = ScpPattern.Match(trimmed);
            if (match.Success)
            {
                host = match.Groups["host"].Value;
                port = 22; // SSH default port
                path = match.Groups["path"].Value;
            }
            else
            {
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(host))
            return null;

        // Clean, unescape and lowercase path
        path = CleanPath(path);
        if (string.IsNullOrWhiteSpace(path))
            return null;

        host = host.Trim().ToLowerInvariant();

        // Apply Host Alias Table as the final step
        if (aliasTable != null)
        {
            host = aliasTable.ResolveCanonical(host).ToLowerInvariant();
        }

        return $"{host}:{port}/{path}";
    }

    public static string MaskCredentials(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        // Mask http(s)://user:password@host or https://token@host
        var masked = Regex.Replace(
            input,
            @"(https?://)(?:[^:@/]+(?::[^@/]+)?@)",
            "$1***:***@",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Mask ssh://user:password@host
        masked = Regex.Replace(
            masked,
            @"(ssh://)(?:[^:@/]+(?::[^@/]+)?@)",
            "$1***:***@",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return masked;
    }

    private static bool IsLocalFilePath(string path)
    {
        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith("./", StringComparison.Ordinal) ||
            path.StartsWith(@".\", StringComparison.Ordinal) ||
            path.StartsWith("../", StringComparison.Ordinal) ||
            path.StartsWith(@"..\", StringComparison.Ordinal) ||
            path.StartsWith("/", StringComparison.Ordinal))
        {
            return true;
        }

        // Windows Drive letter pattern: D:\..., C:/...
        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
        {
            if (path.Length == 2 || path[2] == '\\' || path[2] == '/')
                return true;
        }

        return false;
    }

    private static int GetDefaultPortForScheme(string scheme) => scheme.ToLowerInvariant() switch
    {
        "http" => 80,
        "https" => 443,
        "ssh" => 22,
        "git" => 9418,
        _ => 443
    };

    private static string CleanPath(string path)
    {
        var unescaped = Uri.UnescapeDataString(path);
        var cleaned = unescaped.Trim().Replace('\\', '/');

        while (cleaned.StartsWith('/'))
            cleaned = cleaned[1..];

        while (cleaned.EndsWith('/'))
            cleaned = cleaned[..^1];

        if (cleaned.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[..^4];

        return cleaned.ToLowerInvariant();
    }
}
