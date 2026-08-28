namespace RepoRecon.Core.Models;

/// <summary>
/// Git 작업 실패 시의 오류 분류 7종 (§10.2).
/// Phase 6 사유별 일괄 재시도 및 로컬 문제 필터링의 기준이 됩니다.
/// </summary>
public enum ErrorCategory
{
    /// <summary>
    /// 인증 실패 (401, 토큰 만료, 잘못된 자격 증명 등 - 재로그인/자격증명 갱신으로 해결 가능)
    /// </summary>
    Authentication,

    /// <summary>
    /// 권한 부족 (403 Forbidden, 읽기/쓰기 권한 없음, SSH 키 미등록 등 - 저장소 관리자 승인 필요)
    /// </summary>
    Permission,

    /// <summary>
    /// 네트워크 오류 (DNS 실패, 연결 거부/리셋, 호스트 도달 불가, SSL 핸드셰이크 등 - 일시적 네트워크 문제)
    /// </summary>
    Network,

    /// <summary>
    /// 타임아웃 (작업 실행 시간 초과, curl 28 등)
    /// </summary>
    Timeout,

    /// <summary>
    /// 충돌 (non-fast-forward 거부, 머지 충돌, 로컬 수정 덮어쓰기 충돌 등)
    /// </summary>
    Conflict,

    /// <summary>
    /// 경로 및 로컬 파일시스템 오류 (Long Path 초과, index.lock 존재, 디스크 부족 등 - 서버와 무관한 로컬 문제, 재시도 제외 대상)
    /// </summary>
    Path,

    /// <summary>
    /// 기타 미분류 오류
    /// </summary>
    Other
}
