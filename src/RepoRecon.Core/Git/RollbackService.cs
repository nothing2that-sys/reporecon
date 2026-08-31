using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoRecon.Core.Data;
using RepoRecon.Core.Models;

namespace RepoRecon.Core.Git;

public class RollbackService : IRollbackService
{
    private readonly IGitRunner _gitRunner;
    private readonly SqliteIndexStore _indexStore;
    private readonly ILogger<RollbackService> _logger;

    public RollbackService(
        IGitRunner gitRunner,
        SqliteIndexStore indexStore,
        ILogger<RollbackService>? logger = null)
    {
        _gitRunner = gitRunner;
        _indexStore = indexStore;
        _logger = logger ?? NullLogger<RollbackService>.Instance;
    }

    public async Task<(bool Success, string? ErrorMessage)> RollbackCommitAsync(
        long repoId,
        string repoPath,
        string preHeadHash,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(preHeadHash))
        {
            return (false, "되돌릴 커밋 해시가 없습니다.");
        }

        _logger.LogInformation("Attempting to rollback {Path} to commit {Hash}", repoPath, preHeadHash);

        // 1. Working tree dirty guard (설계 §10.3, 완료 기준 11)
        var statusResult = await _gitRunner.ExecuteAsync(
            repoPath,
            "status --porcelain=v2 --branch",
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);

        if (statusResult.IsSuccess)
        {
            var parsedStatus = GitStatusParser.Parse(repoId, statusResult.StandardOutput, hasRemote: true);
            if (parsedStatus.DirtyCount > 0 || parsedStatus.ConflictedCount > 0)
            {
                _logger.LogWarning("Rollback blocked for {Path}: working tree is dirty (Dirty: {Dirty}, Conflicted: {Conflicted})",
                    repoPath, parsedStatus.DirtyCount, parsedStatus.ConflictedCount);
                throw new InvalidOperationException("작업트리에 커밋되지 않은 변경 사항이 있어 되돌리기가 차단되었습니다.");
            }
        }

        // 2. Query current HEAD before reset
        var currentHeadResult = await _gitRunner.ExecuteAsync(
            repoPath,
            "rev-parse HEAD",
            TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);

        var currentHead = currentHeadResult.IsSuccess ? currentHeadResult.StandardOutput.Trim() : null;

        // 3. Execute git reset --hard <pre_head>
        var resetResult = await _gitRunner.ExecuteAsync(
            repoPath,
            $"reset --hard {preHeadHash}",
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);

        if (resetResult.IsSuccess)
        {
            var postHeadResult = await _gitRunner.ExecuteAsync(
                repoPath,
                "rev-parse HEAD",
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);

            var postHead = postHeadResult.IsSuccess ? postHeadResult.StandardOutput.Trim() : preHeadHash;

            // Record in job_history (완료 기준 13)
            try
            {
                await _indexStore.InsertJobHistoryAsync(new JobHistoryEntry(
                    0,
                    "rollback",
                    repoPath,
                    currentHead,
                    postHead,
                    "Success",
                    null,
                    DateTime.UtcNow
                ), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record rollback job_history for {Path}", repoPath);
            }

            // Update SQLite LocalStatus
            try
            {
                var newStatusResult = await _gitRunner.ExecuteAsync(
                    repoPath,
                    "status --porcelain=v2 --branch",
                    TimeSpan.FromSeconds(10),
                    cancellationToken).ConfigureAwait(false);

                if (newStatusResult.IsSuccess)
                {
                    var newStatus = GitStatusParser.Parse(repoId, newStatusResult.StandardOutput, hasRemote: true) with { LastFetchedAt = DateTime.UtcNow };
                    await _indexStore.UpsertLocalStatusAsync(newStatus, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to refresh LocalStatus after rollback for {Path}", repoPath);
            }

            return (true, null);
        }
        else
        {
            var errorMsg = string.IsNullOrWhiteSpace(resetResult.StandardError)
                ? $"종료 코드 {resetResult.ExitCode}"
                : resetResult.StandardError.Trim();

            var errorCat = ErrorClassifier.ClassifyToString(resetResult.StandardError);

            try
            {
                await _indexStore.InsertJobHistoryAsync(new JobHistoryEntry(
                    0,
                    "rollback",
                    repoPath,
                    currentHead,
                    preHeadHash,
                    "Failed",
                    errorCat,
                    DateTime.UtcNow
                ), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record failed rollback job_history for {Path}", repoPath);
            }

            return (false, errorMsg);
        }
    }
}
