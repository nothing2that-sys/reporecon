namespace RepoRecon.Core.Models;

/// <summary>§8.1.1(a): 자동 pull 대상은 레포 ID가 아니라 폴더 경로 단위다 — 그래야 나중에
/// 그 아래 새로 클론한 레포가 재등록 없이 자동 포함된다.</summary>
public record AutoPullTarget(
    long Id,
    string FolderPath,
    bool Recurse,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? LastRunAt);
