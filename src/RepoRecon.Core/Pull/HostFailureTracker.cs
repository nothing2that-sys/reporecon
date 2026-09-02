namespace RepoRecon.Core.Pull;

/// <summary>§8.1.1(f): 자동 실행에서 인증 실패가 연속 3회면 스케줄 자체를 일시 정지한다.
/// 카운터는 <c>auto_pull_targets</c>가 아니라 호스트 기준으로 센다(§8.4 쿨오프와 같은 단위) —
/// 대상 폴더가 여러 개라도 같은 호스트면 실패가 합산된다.</summary>
public sealed class HostFailureTracker
{
    public const int PauseThreshold = 3;

    private readonly Dictionary<string, int> _consecutiveAuthFailures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>이번 실행 결과를 반영한다. 임계값에 도달한 순간 그 호스트 키를 반환한다(그 외 null) —
    /// 호출자는 이 반환값으로 스케줄 일시정지 여부를 판단한다.</summary>
    public string? RecordResult(string hostKey, bool wasAuthFailure)
    {
        if (string.IsNullOrWhiteSpace(hostKey)) return null;

        if (!wasAuthFailure)
        {
            _consecutiveAuthFailures.Remove(hostKey);
            return null;
        }

        var count = _consecutiveAuthFailures.TryGetValue(hostKey, out var existing) ? existing + 1 : 1;
        _consecutiveAuthFailures[hostKey] = count;
        return count >= PauseThreshold ? hostKey : null;
    }

    public int GetConsecutiveFailures(string hostKey) =>
        _consecutiveAuthFailures.TryGetValue(hostKey, out var count) ? count : 0;

    /// <summary>같은 정규화(§2.3) 알고리즘을 쓴다 — FetchEngine.ExtractHostKey(internal, 같은 어셈블리 전용)와
    /// 동일한 로직을 여기 공개 버전으로 둔다. §8.4 쿨오프가 쓰는 것과 같은 "호스트" 단위(포트 제외)라야
    /// §8.1.1(f)의 "호스트 기준"이 쿨오프와 일치한다.</summary>
    public static string ExtractHostKey(string normalizedKey)
    {
        var slash = normalizedKey.IndexOf('/');
        var authority = slash >= 0 ? normalizedKey[..slash] : normalizedKey;
        var colon = authority.LastIndexOf(':');
        var host = colon > 0 ? authority[..colon] : authority;
        return host.Trim('[', ']').ToLowerInvariant();
    }
}
