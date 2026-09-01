using System.Collections.Concurrent;
using RepoRecon.Core.Models;

namespace RepoRecon.Core.Urls;

public class HostAliasTable
{
    // 별칭 항목 형태 (§2.3, v4.1):
    //   "host:port" 키 -> authority 전체를 엄격 매칭
    //   "host" 키(포트 없음) -> 포트를 무시하고 host만 매칭
    // 값(canonicalAuthority)은 "host" 또는 "host:port" 둘 다 허용한다.
    // 값에 포트가 없으면 원본 포트를 그대로 쓴다.
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

    public void SetAlias(string aliasOrIp, string canonicalAuthority)
    {
        if (string.IsNullOrWhiteSpace(aliasOrIp) || string.IsNullOrWhiteSpace(canonicalAuthority))
            return;

        _aliases[aliasOrIp.Trim()] = canonicalAuthority.Trim();
    }

    public void RemoveAlias(string aliasOrIp)
    {
        if (string.IsNullOrWhiteSpace(aliasOrIp))
            return;

        _aliases.TryRemove(aliasOrIp.Trim(), out _);
    }

    public (string Host, int Port) ResolveAuthority(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host))
            return (host, port);

        var trimmedHost = host.Trim();

        if (_aliases.TryGetValue($"{trimmedHost}:{port}", out var authorityCanonical))
            return ParseCanonical(authorityCanonical, port);

        if (_aliases.TryGetValue(trimmedHost, out var hostCanonical))
            return ParseCanonical(hostCanonical, port);

        return (trimmedHost, port);
    }

    private static (string Host, int Port) ParseCanonical(string canonical, int fallbackPort)
    {
        var separatorIndex = canonical.LastIndexOf(':');
        if (separatorIndex > 0 && int.TryParse(canonical[(separatorIndex + 1)..], out var canonicalPort))
        {
            return (canonical[..separatorIndex], canonicalPort);
        }

        return (canonical, fallbackPort);
    }

    // 프로파일별 host + aliases_json으로부터 별칭 테이블을 재구성한다 (§2.3, §4.2 (4)).
    // 프로파일의 정본 authority는 profile.Host에서 뽑고, profile.Aliases의 각 항목이 그리로 치환된다.
    public void ReloadFromProfiles(IEnumerable<ServerProfile> profiles)
    {
        _aliases.Clear();

        foreach (var profile in profiles)
        {
            var canonicalAuthority = RemoteUrlNormalizer.ExtractAuthority(profile.Host);
            if (canonicalAuthority == null)
                continue;

            foreach (var alias in profile.Aliases)
            {
                if (string.IsNullOrWhiteSpace(alias))
                    continue;

                SetAlias(alias.Trim(), canonicalAuthority);
            }
        }
    }

    public IReadOnlyDictionary<string, string> GetAllAliases() => _aliases;
}
