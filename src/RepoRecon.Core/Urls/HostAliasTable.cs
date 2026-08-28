using System.Collections.Concurrent;

namespace RepoRecon.Core.Urls;

public class HostAliasTable
{
    private readonly ConcurrentDictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

    public HostAliasTable()
    {
    }

    public HostAliasTable(IDictionary<string, string> initialAliases)
    {
        foreach (var (alias, canonical) in initialAliases)
        {
            SetAlias(alias, canonical);
        }
    }

    public void SetAlias(string aliasOrIp, string canonicalHost)
    {
        if (string.IsNullOrWhiteSpace(aliasOrIp) || string.IsNullOrWhiteSpace(canonicalHost))
            return;

        _aliases[aliasOrIp.Trim()] = canonicalHost.Trim();
    }

    public void RemoveAlias(string aliasOrIp)
    {
        if (string.IsNullOrWhiteSpace(aliasOrIp))
            return;

        _aliases.TryRemove(aliasOrIp.Trim(), out _);
    }

    public string ResolveCanonical(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return host;

        var trimmed = host.Trim();
        return _aliases.TryGetValue(trimmed, out var canonical) ? canonical : trimmed;
    }

    public IReadOnlyDictionary<string, string> GetAllAliases() => _aliases;
}
