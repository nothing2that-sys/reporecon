namespace RepoRecon.Core.Fetch;

public enum FetchFailureKind
{
    None,
    Authentication,
    Network,
    Other,
    Cooldown
}

public sealed record HostFetchCooldown(
    string Host,
    string Reason,
    DateTime ExpiresAtUtc)
{
    public TimeSpan Remaining(DateTime utcNow) => ExpiresAtUtc > utcNow
        ? ExpiresAtUtc - utcNow
        : TimeSpan.Zero;
}
