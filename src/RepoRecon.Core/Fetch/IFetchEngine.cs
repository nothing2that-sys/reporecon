namespace RepoRecon.Core.Fetch;

public interface IFetchEngine
{
    event EventHandler<RepoFetchResult>? RepoFetched;
    event EventHandler<bool>? FetchingStateChanged;
    event EventHandler? CooldownsChanged;

    bool IsFetching { get; }
    int PendingCount { get; }
    IReadOnlyList<HostFetchCooldown> ActiveCooldowns { get; }

    Task EnqueueReposAsync(IEnumerable<(long RepoId, string AbsPath, string NormalizedKey)> repos, CancellationToken cancellationToken = default);
    void BoostVisibleRepos(IEnumerable<long> visibleRepoIds);
    Task<RepoFetchResult> FetchSingleRepoAsync(long repoId, string absPath, CancellationToken cancellationToken = default);
    void SetAuthenticationCooldown(string hostKey, string reason);
    void ClearCooldowns();
    void Stop();
}
