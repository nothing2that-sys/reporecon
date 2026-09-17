namespace RepoRecon.Core.Fetch;

public interface IFetchEngine
{
    event EventHandler<RepoFetchResult>? RepoFetched;
    event EventHandler<bool>? FetchingStateChanged;
    event EventHandler? CooldownsChanged;
    event EventHandler<bool>? PausedStateChanged;

    bool IsFetching { get; }
    bool IsPaused { get; }
    int PendingCount { get; }
    IReadOnlyList<HostFetchCooldown> ActiveCooldowns { get; }

    Task EnqueueReposAsync(IEnumerable<(long RepoId, string AbsPath, string NormalizedKey)> repos, CancellationToken cancellationToken = default);
    void BoostVisibleRepos(IEnumerable<long> visibleRepoIds);
    Task<RepoFetchResult> FetchSingleRepoAsync(long repoId, string absPath, CancellationToken cancellationToken = default);
    void SetAuthenticationCooldown(string hostKey, string reason);
    void ClearCooldowns();

    // 진행 중인 fetch 프로세스를 즉시 중단하고 대기열 처리를 멈춘다. 대기열 자체는
    // 비우지 않으므로 Resume()으로 멈췄던 지점부터 이어서 처리할 수 있다. Stop()과
    // 달리 되돌릴 수 있는 일시정지다 (Stop()은 엔진 종료 전용, 재개 불가).
    void Pause();
    void Resume();
    void Stop();
}
