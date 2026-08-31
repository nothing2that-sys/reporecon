using RepoRecon.Core.Models;

namespace RepoRecon.Core.Push;

/// <summary>
/// 현재 체크아웃된 브랜치에 대한 일괄 Push 오케스트레이션 엔진 인터페이스 (§3.3, §7.5, §7.6, §8.2~8.4).
/// </summary>
public interface IPushEngine
{
    /// <summary>
    /// 대상 저장소들에 대해 실서버 dry-run 검증을 포함한 Push 미리보기를 생성합니다.
    /// </summary>
    Task<IReadOnlyList<PushPreviewItem>> GeneratePreviewAsync(
        IEnumerable<(LocalRepo Repo, LocalStatus? Status)> targets,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Push 실행 가능 판정된 대상들에 대해 일괄 Push를 실행합니다.
    /// </summary>
    Task<BatchPushResult> ExecuteAsync(
        IReadOnlyList<PushPreviewItem> targets,
        IProgress<BatchPushProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 현재 Push 배치가 실행 중인지 여부.
    /// </summary>
    bool IsExecuting { get; }
}
