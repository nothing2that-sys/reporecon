using System.Globalization;
using Microsoft.Data.Sqlite;
using RepoRecon.Core.Models;

namespace RepoRecon.Core.Data;

public class SqliteIndexStore
{
    private readonly string _connectionString;

    public SqliteIndexStore(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
    }

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    #region Scan Roots

    public async Task<IReadOnlyList<ScanRoot>> GetScanRootsAsync(CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, path, max_depth, is_active, last_scanned_at, is_offline FROM scan_roots ORDER BY id ASC;";

        var list = new List<ScanRoot>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetInt64(0);
            var path = reader.GetString(1);
            var maxDepth = reader.GetInt32(2);
            var isActive = reader.GetInt32(3) != 0;
            var lastScannedAt = reader.IsDBNull(4) ? (DateTime?)null : DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var isOffline = reader.GetInt32(5) != 0;

            list.Add(new ScanRoot(id, path, maxDepth, isActive, lastScannedAt, isOffline));
        }

        return list;
    }

    public async Task<long> AddScanRootAsync(string path, int maxDepth = 10, CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO scan_roots (path, max_depth, is_active, is_offline)
VALUES (@path, @max_depth, 1, 0);
SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@max_depth", maxDepth);

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public async Task UpdateScanRootAsync(ScanRoot root, CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE scan_roots
SET max_depth = @max_depth,
    is_active = @is_active,
    last_scanned_at = @last_scanned_at,
    is_offline = @is_offline
WHERE id = @id;";
        cmd.Parameters.AddWithValue("@max_depth", root.MaxDepth);
        cmd.Parameters.AddWithValue("@is_active", root.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("@last_scanned_at", root.LastScannedAt.HasValue ? root.LastScannedAt.Value.ToString("o") : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@is_offline", root.IsOffline ? 1 : 0);
        cmd.Parameters.AddWithValue("@id", root.Id);

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteScanRootAsync(long id, CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM scan_roots WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    #endregion

    #region Local Repos & Status

    public async Task<IReadOnlyList<(LocalRepo Repo, LocalStatus? Status)>> GetAllLocalReposWithStatusAsync(CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT r.id, r.abs_path, r.normalized_key, r.parent_repo_id, r.git_dir_kind, r.has_lfs, r.has_submodule, r.is_missing, r.missing_since,
       s.branch, s.upstream, s.ahead, s.behind, s.dirty_count, s.conflicted_count, s.head_hash, s.state_code, s.last_fetched_at
FROM local_repos r
LEFT JOIN local_status s ON r.id = s.local_repo_id
ORDER BY r.abs_path ASC;";

        var list = new List<(LocalRepo Repo, LocalStatus? Status)>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetInt64(0);
            var absPath = reader.GetString(1);
            var normalizedKey = reader.IsDBNull(2) ? null : reader.GetString(2);
            var parentRepoId = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3);
            var gitDirKind = (GitDirKind)reader.GetInt32(4);
            var hasLfs = reader.GetInt32(5) != 0;
            var hasSubmodule = reader.GetInt32(6) != 0;
            var isMissing = reader.GetInt32(7) != 0;
            var missingSince = reader.IsDBNull(8) ? (DateTime?)null : DateTime.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

            var repo = new LocalRepo(id, absPath, normalizedKey, parentRepoId, gitDirKind, hasLfs, hasSubmodule, isMissing, missingSince);

            LocalStatus? status = null;
            if (!reader.IsDBNull(9) || !reader.IsDBNull(10) || !reader.IsDBNull(16))
            {
                var branch = reader.IsDBNull(9) ? null : reader.GetString(9);
                var upstream = reader.IsDBNull(10) ? null : reader.GetString(10);
                var ahead = reader.IsDBNull(11) ? 0 : reader.GetInt32(11);
                var behind = reader.IsDBNull(12) ? 0 : reader.GetInt32(12);
                var dirtyCount = reader.IsDBNull(13) ? 0 : reader.GetInt32(13);
                var conflictedCount = reader.IsDBNull(14) ? 0 : reader.GetInt32(14);
                var headHash = reader.IsDBNull(15) ? null : reader.GetString(15);
                var stateCode = reader.IsDBNull(16) ? RepoStateCode.UnknownUnfetched : (RepoStateCode)reader.GetInt32(16);
                var lastFetchedAt = reader.IsDBNull(17) ? (DateTime?)null : DateTime.Parse(reader.GetString(17), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

                status = new LocalStatus(id, branch, upstream, ahead, behind, dirtyCount, conflictedCount, headHash, stateCode, lastFetchedAt);
            }

            list.Add((repo, status));
        }

        return list;
    }

    public async Task<long> UpsertLocalRepoAsync(LocalRepo repo, CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO local_repos (abs_path, normalized_key, parent_repo_id, git_dir_kind, has_lfs, has_submodule, is_missing, missing_since)
VALUES (@abs_path, @normalized_key, @parent_repo_id, @git_dir_kind, @has_lfs, @has_submodule, @is_missing, @missing_since)
ON CONFLICT(abs_path) DO UPDATE SET
    normalized_key = excluded.normalized_key,
    parent_repo_id = excluded.parent_repo_id,
    git_dir_kind = excluded.git_dir_kind,
    has_lfs = excluded.has_lfs,
    has_submodule = excluded.has_submodule,
    is_missing = excluded.is_missing,
    missing_since = excluded.missing_since;
SELECT id FROM local_repos WHERE abs_path = @abs_path;";

        cmd.Parameters.AddWithValue("@abs_path", repo.AbsPath);
        cmd.Parameters.AddWithValue("@normalized_key", repo.NormalizedKey ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@parent_repo_id", repo.ParentRepoId.HasValue ? repo.ParentRepoId.Value : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@git_dir_kind", (int)repo.GitDirKind);
        cmd.Parameters.AddWithValue("@has_lfs", repo.HasLfs ? 1 : 0);
        cmd.Parameters.AddWithValue("@has_submodule", repo.HasSubmodule ? 1 : 0);
        cmd.Parameters.AddWithValue("@is_missing", repo.IsMissing ? 1 : 0);
        cmd.Parameters.AddWithValue("@missing_since", repo.MissingSince.HasValue ? repo.MissingSince.Value.ToString("o") : (object)DBNull.Value);

        var idResult = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(idResult, CultureInfo.InvariantCulture);
    }

    public async Task UpsertLocalStatusAsync(LocalStatus status, CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO local_status (local_repo_id, branch, upstream, ahead, behind, dirty_count, conflicted_count, head_hash, state_code, last_fetched_at)
VALUES (@local_repo_id, @branch, @upstream, @ahead, @behind, @dirty_count, @conflicted_count, @head_hash, @state_code, @last_fetched_at)
ON CONFLICT(local_repo_id) DO UPDATE SET
    branch = excluded.branch,
    upstream = excluded.upstream,
    ahead = excluded.ahead,
    behind = excluded.behind,
    dirty_count = excluded.dirty_count,
    conflicted_count = excluded.conflicted_count,
    head_hash = excluded.head_hash,
    state_code = excluded.state_code,
    last_fetched_at = excluded.last_fetched_at;";

        cmd.Parameters.AddWithValue("@local_repo_id", status.LocalRepoId);
        cmd.Parameters.AddWithValue("@branch", status.Branch ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@upstream", status.Upstream ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ahead", status.Ahead);
        cmd.Parameters.AddWithValue("@behind", status.Behind);
        cmd.Parameters.AddWithValue("@dirty_count", status.DirtyCount);
        cmd.Parameters.AddWithValue("@conflicted_count", status.ConflictedCount);
        cmd.Parameters.AddWithValue("@head_hash", status.HeadHash ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@state_code", (int)status.StateCode);
        cmd.Parameters.AddWithValue("@last_fetched_at", status.LastFetchedAt.HasValue ? status.LastFetchedAt.Value.ToString("o") : (object)DBNull.Value);

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkRepoMissingAsync(long repoId, DateTime missingSince, CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE local_repos SET is_missing = 1, missing_since = @missing_since WHERE id = @id;";
        cmd.Parameters.AddWithValue("@missing_since", missingSince.ToString("o"));
        cmd.Parameters.AddWithValue("@id", repoId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearRepoMissingAsync(long repoId, CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE local_repos SET is_missing = 0, missing_since = NULL WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", repoId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteLocalRepoAsync(long id, CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM local_repos WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    #endregion

    #region Job History

    public async Task<long> InsertJobHistoryAsync(JobHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO job_history (job_type, target_path, pre_head, post_head, result, error_category, created_at)
VALUES (@job_type, @target_path, @pre_head, @post_head, @result, @error_category, @created_at);
SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@job_type", entry.JobType);
        cmd.Parameters.AddWithValue("@target_path", entry.TargetPath);
        cmd.Parameters.AddWithValue("@pre_head", entry.PreHead ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@post_head", entry.PostHead ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@result", entry.Result);
        cmd.Parameters.AddWithValue("@error_category", entry.ErrorCategory ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@created_at", entry.CreatedAt.ToString("o"));

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<JobHistoryEntry>> GetJobHistoryAsync(string? targetPath = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        return await GetJobHistoryFilteredAsync(targetPath, jobType: null, limit: limit, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<JobHistoryEntry>> GetJobHistoryFilteredAsync(string? targetPath = null, string? jobType = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        var whereClauses = new List<string>();
        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            whereClauses.Add("(target_path LIKE @target_path_like OR target_path = @target_path)");
            cmd.Parameters.AddWithValue("@target_path", targetPath);
            cmd.Parameters.AddWithValue("@target_path_like", $"%{targetPath}%");
        }
        if (!string.IsNullOrWhiteSpace(jobType) && !string.Equals(jobType, "전체", StringComparison.OrdinalIgnoreCase) && !string.Equals(jobType, "All", StringComparison.OrdinalIgnoreCase))
        {
            whereClauses.Add("job_type = @job_type");
            cmd.Parameters.AddWithValue("@job_type", jobType);
        }

        var whereSql = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : string.Empty;
        cmd.CommandText = $"SELECT id, job_type, target_path, pre_head, post_head, result, error_category, created_at FROM job_history {whereSql} ORDER BY id DESC LIMIT @limit;";
        cmd.Parameters.AddWithValue("@limit", limit > 0 ? limit : int.MaxValue);

        var list = new List<JobHistoryEntry>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetInt64(0);
            var jType = reader.GetString(1);
            var path = reader.GetString(2);
            var preHead = reader.IsDBNull(3) ? null : reader.GetString(3);
            var postHead = reader.IsDBNull(4) ? null : reader.GetString(4);
            var result = reader.GetString(5);
            var errorCat = reader.IsDBNull(6) ? null : reader.GetString(6);
            var createdAt = DateTime.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

            list.Add(new JobHistoryEntry(id, jType, path, preHead, postHead, result, errorCat, createdAt));
        }

        return list;
    }

    #endregion

    #region Server Profiles

    public async Task<IReadOnlyList<ServerProfile>> GetServerProfilesAsync(CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, host, api_kind, aliases_json, token_ref, api_version, created_at, last_synced_at FROM server_profiles ORDER BY id ASC;";

        var list = new List<ServerProfile>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetInt64(0);
            var host = reader.GetString(1);
            var apiKind = Enum.TryParse<ServerApiKind>(reader.GetString(2), true, out var parsedKind) ? parsedKind : ServerApiKind.GitLab;
            var aliasesJson = reader.GetString(3);
            var tokenRef = reader.GetString(4);
            var apiVersion = reader.IsDBNull(5) ? null : reader.GetString(5);
            var createdAt = DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var lastSyncedAt = reader.IsDBNull(7) ? (DateTime?)null : DateTime.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

            var aliases = System.Text.Json.JsonSerializer.Deserialize<List<string>>(aliasesJson) ?? new List<string>();
            list.Add(new ServerProfile(id, host, apiKind, aliases, tokenRef, apiVersion, createdAt, lastSyncedAt));
        }

        return list;
    }

    public async Task<ServerProfile?> GetServerProfileByIdAsync(long profileId, CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, host, api_kind, aliases_json, token_ref, api_version, created_at, last_synced_at FROM server_profiles WHERE id = @id LIMIT 1;";
        cmd.Parameters.AddWithValue("@id", profileId);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetInt64(0);
            var host = reader.GetString(1);
            var apiKind = Enum.TryParse<ServerApiKind>(reader.GetString(2), true, out var parsedKind) ? parsedKind : ServerApiKind.GitLab;
            var aliasesJson = reader.GetString(3);
            var tokenRef = reader.GetString(4);
            var apiVersion = reader.IsDBNull(5) ? null : reader.GetString(5);
            var createdAt = DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var lastSyncedAt = reader.IsDBNull(7) ? (DateTime?)null : DateTime.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

            var aliases = System.Text.Json.JsonSerializer.Deserialize<List<string>>(aliasesJson) ?? new List<string>();
            return new ServerProfile(id, host, apiKind, aliases, tokenRef, apiVersion, createdAt, lastSyncedAt);
        }

        return null;
    }

    public async Task<long> UpsertServerProfileAsync(ServerProfile profile, CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        var aliasesJson = System.Text.Json.JsonSerializer.Serialize(profile.Aliases);
        var now = profile.CreatedAt == default ? DateTime.UtcNow : profile.CreatedAt;

        if (profile.Id == 0)
        {
            cmd.CommandText = @"
INSERT INTO server_profiles (host, api_kind, aliases_json, token_ref, api_version, created_at, last_synced_at)
VALUES (@host, @api_kind, @aliases_json, @token_ref, @api_version, @created_at, @last_synced_at);
SELECT last_insert_rowid();";
        }
        else
        {
            cmd.CommandText = @"
INSERT INTO server_profiles (id, host, api_kind, aliases_json, token_ref, api_version, created_at, last_synced_at)
VALUES (@id, @host, @api_kind, @aliases_json, @token_ref, @api_version, @created_at, @last_synced_at)
ON CONFLICT(id) DO UPDATE SET
    host = excluded.host,
    api_kind = excluded.api_kind,
    aliases_json = excluded.aliases_json,
    token_ref = excluded.token_ref,
    api_version = excluded.api_version,
    last_synced_at = excluded.last_synced_at;
SELECT @id;";
            cmd.Parameters.AddWithValue("@id", profile.Id);
        }

        cmd.Parameters.AddWithValue("@host", profile.Host);
        cmd.Parameters.AddWithValue("@api_kind", profile.ApiKind.ToString());
        cmd.Parameters.AddWithValue("@aliases_json", aliasesJson);
        cmd.Parameters.AddWithValue("@token_ref", profile.TokenRef);
        cmd.Parameters.AddWithValue("@api_version", profile.ApiVersion ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@created_at", now.ToString("o"));
        cmd.Parameters.AddWithValue("@last_synced_at", profile.LastSyncedAt.HasValue ? profile.LastSyncedAt.Value.ToString("o") : (object)DBNull.Value);

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public async Task DeleteServerProfileAsync(long profileId, CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM server_profiles WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", profileId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    #endregion

    #region Server Repos (Catalog)

    public async Task ReplaceServerReposAsync(long profileId, IEnumerable<ServerRepo> repos, CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        using var transaction = conn.BeginTransaction();

        // 1. Delete existing repos for profile
        using (var deleteCmd = conn.CreateCommand())
        {
            deleteCmd.Transaction = transaction;
            deleteCmd.CommandText = "DELETE FROM server_repos WHERE profile_id = @profile_id;";
            deleteCmd.Parameters.AddWithValue("@profile_id", profileId);
            await deleteCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // 2. Insert new repos
        foreach (var repo in repos)
        {
            using var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = @"
INSERT INTO server_repos (profile_id, namespace, name, clone_url, normalized_key, default_branch, is_archived, collected_at)
VALUES (@profile_id, @namespace, @name, @clone_url, @normalized_key, @default_branch, @is_archived, @collected_at);";
            insertCmd.Parameters.AddWithValue("@profile_id", profileId);
            insertCmd.Parameters.AddWithValue("@namespace", repo.Namespace);
            insertCmd.Parameters.AddWithValue("@name", repo.Name);
            insertCmd.Parameters.AddWithValue("@clone_url", repo.CloneUrl);
            insertCmd.Parameters.AddWithValue("@normalized_key", repo.NormalizedKey);
            insertCmd.Parameters.AddWithValue("@default_branch", repo.DefaultBranch ?? (object)DBNull.Value);
            insertCmd.Parameters.AddWithValue("@is_archived", repo.IsArchived ? 1 : 0);
            insertCmd.Parameters.AddWithValue("@collected_at", repo.CollectedAt.ToString("o"));
            await insertCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // 3. Update last_synced_at on server_profiles
        using (var updateProfileCmd = conn.CreateCommand())
        {
            updateProfileCmd.Transaction = transaction;
            updateProfileCmd.CommandText = "UPDATE server_profiles SET last_synced_at = @now WHERE id = @profile_id;";
            updateProfileCmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
            updateProfileCmd.Parameters.AddWithValue("@profile_id", profileId);
            await updateProfileCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    public async Task<IReadOnlyList<ServerRepo>> GetServerReposByProfileAsync(long profileId, CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, profile_id, namespace, name, clone_url, normalized_key, default_branch, is_archived, collected_at FROM server_repos WHERE profile_id = @profile_id ORDER BY namespace ASC, name ASC;";
        cmd.Parameters.AddWithValue("@profile_id", profileId);

        var list = new List<ServerRepo>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetInt64(0);
            var pId = reader.GetInt64(1);
            var ns = reader.GetString(2);
            var name = reader.GetString(3);
            var cloneUrl = reader.GetString(4);
            var normKey = reader.GetString(5);
            var defBranch = reader.IsDBNull(6) ? null : reader.GetString(6);
            var isArchived = reader.GetInt32(7) != 0;
            var collectedAt = DateTime.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

            list.Add(new ServerRepo(id, pId, ns, name, cloneUrl, normKey, defBranch, isArchived, collectedAt));
        }

        return list;
    }

    public async Task<IReadOnlyList<ServerRepo>> GetAllServerReposAsync(CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, profile_id, namespace, name, clone_url, normalized_key, default_branch, is_archived, collected_at FROM server_repos ORDER BY namespace ASC, name ASC;";

        var list = new List<ServerRepo>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetInt64(0);
            var pId = reader.GetInt64(1);
            var ns = reader.GetString(2);
            var name = reader.GetString(3);
            var cloneUrl = reader.GetString(4);
            var normKey = reader.GetString(5);
            var defBranch = reader.IsDBNull(6) ? null : reader.GetString(6);
            var isArchived = reader.GetInt32(7) != 0;
            var collectedAt = DateTime.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

            list.Add(new ServerRepo(id, pId, ns, name, cloneUrl, normKey, defBranch, isArchived, collectedAt));
        }

        return list;
    }

    #endregion
}
