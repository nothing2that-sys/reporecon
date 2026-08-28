using RepoRecon.Core.Models;

namespace RepoRecon.Core.Git;

/// <summary>
/// git stderr 및 예외 메시지를 7가지 표준 오류 분류로 매핑하는 분류기 (§10.2).
/// </summary>
public static class ErrorClassifier
{
    public static ErrorCategory Classify(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return ErrorCategory.Other;

        // 1. Timeout (타임아웃 - 최우선 순위 판정)
        if (ContainsAny(error,
            "Connection timed out",
            "Operation timed out",
            "Execution timed out",
            "timed out after",
            "Operation was canceled",
            "Timeout exceeded",
            "RPC failed; curl 28"))
        {
            return ErrorCategory.Timeout;
        }

        // 2. Path / Local Filesystem (경로/로컬 환경 - 서버 재시도 제외 대상)
        if (ContainsAny(error,
            "Filename too long",
            "File name too long",
            "index.lock",
            "Another git process seems to be running",
            "No space left on device",
            "Disk quota exceeded",
            "cannot lock ref",
            "unable to create directory",
            "cannot open .git",
            "Corrupt",
            "not a valid repository",
            "not a git repository",
            "Permission denied: unable to create"))
        {
            return ErrorCategory.Path;
        }

        // 3. Conflict (충돌 - 로컬 머지/체크아웃 불가)
        if (ContainsAny(error,
            "Not possible to fast-forward",
            "non-fast-forward",
            "Automatic merge failed",
            "fix conflicts",
            "would be overwritten by merge",
            "would be overwritten by checkout",
            "refusing to merge unrelated histories",
            "needs merge",
            "CONFLICT ("))
        {
            return ErrorCategory.Conflict;
        }

        // 4. Authentication (인증 - 토큰/비밀번호/계정/SSH 키 만료/오류 -> 사용자 키/자격증명 갱신으로 해결)
        if (ContainsAny(error,
            "Authentication failed",
            "HTTP Basic: Access denied",
            "could not read Username",
            "could not read Password",
            "terminal prompts disabled",
            "Password for",
            "Invalid username or password",
            "Permission denied (publickey",
            "401 Unauthorized",
            "error: 401",
            "The requested URL returned error: 401",
            "Token is expired",
            "Bad credentials"))
        {
            return ErrorCategory.Authentication;
        }

        // 5. Permission (권한 - 403 Forbidden, 권한 없음 -> 저장소 관리자 요청 필요)
        if (ContainsAny(error,
            "403 Forbidden",
            "error: 403",
            "The requested URL returned error: 403",
            "Permission denied",
            "Permission to ",
            "denied to ",
            "Access denied",
            "You are not allowed to",
            "Repository not found or access denied",
            "insufficient permission",
            "write access required",
            "read access required"))
        {
            return ErrorCategory.Permission;
        }

        // 6. Network (네트워크 - 일시적 연결 장애 -> 재시도 가능)
        if (ContainsAny(error,
            "Could not resolve host",
            "Failed to connect",
            "Connection refused",
            "Connection reset",
            "Connection closed",
            "Network is unreachable",
            "No route to host",
            "SSL certificate problem",
            "SSL connect error",
            "GnuTLS recv error",
            "OpenSSL SSL_read",
            "RPC failed; curl 56",
            "RPC failed; curl 18",
            "RPC failed; curl 55",
            "The remote end hung up unexpectedly",
            "early EOF",
            "fetch-pack: invalid index-pack output"))
        {
            return ErrorCategory.Network;
        }

        // 7. Other (기타 미분류)
        return ErrorCategory.Other;
    }

    public static string ClassifyToString(string? error) => Classify(error).ToString();

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
}
