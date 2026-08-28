namespace RepoRecon.Core.Security;

/// <summary>
/// API 토큰을 암호화하여 안전하게 보관하는 인터페이스 (§9.2).
/// 평문 토큰은 절대 DB, 로그, 설정 파일에 남지 않습니다.
/// </summary>
public interface ISecureTokenStore
{
    Task<string> SaveTokenAsync(string plainToken, CancellationToken cancellationToken = default);
    Task<string?> GetTokenAsync(string tokenRef, CancellationToken cancellationToken = default);
    Task DeleteTokenAsync(string tokenRef, CancellationToken cancellationToken = default);
}
