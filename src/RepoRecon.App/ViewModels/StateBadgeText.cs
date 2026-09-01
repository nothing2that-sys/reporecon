using RepoRecon.Core.Models;

namespace RepoRecon.App.ViewModels;

/// <summary>
/// §3.2.2: on-screen Korean badge text only. `local_status.state_code`
/// storage, logs, and CSV/Markdown export must keep the English label
/// (§3.2.2 (c)) -- never feed this into ExportItem or a log message.
/// Shared by the local tab now and by the clone tab once §5.4 lands (tasks
/// 4/7 reuse the same badge component per the design's own sequencing note).
/// </summary>
public static class StateBadgeText
{
    public const string NotCloned = "로컬 없음";
    public const string Orphaned = "카탈로그에 없음";
    public const string External = "미등록 호스트";
    public const string Untracked = "remote 없음";
    public const string Archived = "보관됨";
    public const string MissingConfirmation = "확인 안 됨";
    public const string UnknownUnfetched = "미확인 (fetch 전)";
    public const string UnknownFetchFailed = "미확인 (fetch 실패)";

    public static string ForDirty(int dirtyCount) => $"미커밋 변경 {dirtyCount}";

    public static string ForConflicted(int conflictedCount) => $"머지 충돌 {conflictedCount}";

    public static string ForSyncState(RepoStateCode code, int ahead, int behind) => code switch
    {
        RepoStateCode.UpToDate => "최신",
        RepoStateCode.Behind => $"받을 것 있음 ↓{behind}",
        RepoStateCode.Ahead => $"올릴 것 있음 ↑{ahead}",
        RepoStateCode.Diverged => "갈라짐 · pull 먼저 필요",
        RepoStateCode.NoUpstream => "추적 브랜치 없음",
        RepoStateCode.UpstreamGone => "서버에서 사라진 브랜치",
        RepoStateCode.DetachedHead => "브랜치 미체크아웃",
        RepoStateCode.NotCloned => NotCloned,
        RepoStateCode.Orphaned => Orphaned,
        RepoStateCode.External => External,
        RepoStateCode.Untracked => Untracked,
        // Not one of §3.2.1's nine sync-state values (conflicted is its own
        // axis, conflicted_count) -- this enum member predates that split.
        // Rendered for display completeness only; not a design-sanctioned state.
        RepoStateCode.Conflicted => "머지 충돌 중",
        _ => UnknownUnfetched
    };
}
