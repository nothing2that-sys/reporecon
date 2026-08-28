using RepoRecon.Core.Models;

namespace RepoRecon.Core.Adapters;

/// <summary>
/// 원격 Git 서버(GitLab, GitHub, Gitea)의 API 카탈로그를 조회하는 공통 어댑터 인터페이스 (§4.2).
/// </summary>
public interface IServerAdapter
{
    ServerApiKind ApiKind { get; }

    Task<ServerConnectionResult> TestConnectionAsync(
        string host,
        string? token,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DiscoveredRemoteRepo>> FetchCatalogAsync(
        ServerProfile profile,
        string? token,
        IProgress<CatalogProgressInfo>? progress = null,
        CancellationToken cancellationToken = default);
}
