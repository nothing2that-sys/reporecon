using RepoRecon.Core.Models;

namespace RepoRecon.Core.Pull;

/// <summary>§8.1.1(a)/(c): auto_pull_targets의 폴더 경로를 실제 로컬 레포 목록에 대입해
/// 이번에 시도할 대상과, 시도조차 하지 않고 사전 스킵할 대상을 나눈다.
///
/// is_missing 레포는 여기서 걸러내지 않는다 — PullEngine.EvaluateEligibility가 이미
/// "디스크에서 발견되지 않음"으로 스킵(실패 아님) 처리하므로 중복이다. 하지만 오프라인
/// 스캔 루트는 PullEngine이 전혀 모르는 개념이라 여기서 사전에 걸러야 한다 — 그러지 않으면
/// 연결 안 된 네트워크 드라이브 밑 레포에 fetch/pull을 실제로 시도하다 실패로 계수된다.</summary>
public static class AutoPullTargetResolver
{
    public record ResolvedTargets(
        IReadOnlyList<(LocalRepo Repo, LocalStatus? Status)> Eligible,
        IReadOnlyList<(LocalRepo Repo, string SkipReason)> PreSkipped);

    public static ResolvedTargets Resolve(
        IReadOnlyList<AutoPullTarget> targets,
        IReadOnlyList<(LocalRepo Repo, LocalStatus? Status)> allRepos,
        IReadOnlyList<ScanRoot> scanRoots)
    {
        var activeTargets = targets.Where(t => t.IsActive).ToList();
        var offlineRoots = scanRoots.Where(r => r.IsOffline).Select(r => NormalizeDir(r.Path)).ToList();

        var eligible = new List<(LocalRepo, LocalStatus?)>();
        var preSkipped = new List<(LocalRepo, string)>();

        foreach (var (repo, status) in allRepos)
        {
            if (!activeTargets.Any(t => Matches(t, repo.AbsPath))) continue;

            var repoDir = NormalizeDir(repo.AbsPath);
            if (offlineRoots.Any(root => repoDir.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
            {
                preSkipped.Add((repo, "스캔 루트 오프라인"));
                continue;
            }

            eligible.Add((repo, status));
        }

        return new ResolvedTargets(eligible, preSkipped);
    }

    private static bool Matches(AutoPullTarget target, string repoAbsPath)
    {
        var folder = NormalizeDir(target.FolderPath);
        var repoDir = NormalizeDir(repoAbsPath);

        if (!repoDir.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (target.Recurse)
        {
            return true;
        }

        // 직속만: 레포의 부모 폴더가 대상 폴더와 정확히 같아야 한다.
        var parent = Path.GetDirectoryName(repoAbsPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.Equals(NormalizeDir(parent ?? string.Empty), folder, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDir(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }
}
