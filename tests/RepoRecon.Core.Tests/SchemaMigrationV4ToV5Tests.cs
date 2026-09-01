using Microsoft.Data.Sqlite;
using RepoRecon.Core.Data;
using Xunit;

namespace RepoRecon.Core.Tests;

/// <summary>
/// SS2.4 warns this in-place path has, until now, only ever run against a
/// freshly created DB. These tests build a genuine v4 database by hand (the
/// shape 1.0.0 shipped, with no auto_pull_targets table) and assert the
/// upgrade preserves scan_roots / server_profiles / job_history instead of
/// falling back to "discard and rescan".
/// </summary>
public class SchemaMigrationV4ToV5Tests
{
    [Fact]
    public void EnsureInitialized_UpgradesRealV4Database_PreservesExistingDataWithoutFallback()
    {
        var tempDbPath = Path.Combine(Path.GetTempPath(), "reporecon_test_v4tov5_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            CreateLegacyV4DatabaseWithSampleData(tempDbPath);

            var migrator = new SchemaMigrator();
            var wasRecreated = migrator.EnsureInitialized(tempDbPath);

            Assert.False(wasRecreated); // migration path taken, not the discard-and-rescan fallback

            using var connection = new SqliteConnection($"Data Source={tempDbPath}");
            connection.Open();

            Assert.Equal(5, GetSchemaVersion(connection));

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='auto_pull_targets';";
                Assert.NotNull(cmd.ExecuteScalar());
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT path FROM scan_roots;";
                Assert.Equal(@"D:\Work", cmd.ExecuteScalar());
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT host FROM server_profiles;";
                Assert.Equal("gitlab.test", cmd.ExecuteScalar());
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM job_history;";
                Assert.Equal(1L, cmd.ExecuteScalar());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(tempDbPath);
            File.Delete($"{tempDbPath}-wal");
            File.Delete($"{tempDbPath}-shm");
        }
    }

    private static int GetSchemaVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT version FROM schema_version LIMIT 1;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void CreateLegacyV4DatabaseWithSampleData(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE schema_version (
    version INTEGER PRIMARY KEY
);
INSERT INTO schema_version (version) VALUES (4);

CREATE TABLE scan_roots (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    path TEXT NOT NULL UNIQUE,
    max_depth INTEGER NOT NULL DEFAULT 10,
    is_active INTEGER NOT NULL DEFAULT 1,
    last_scanned_at TEXT,
    is_offline INTEGER NOT NULL DEFAULT 0
);
INSERT INTO scan_roots (path, max_depth, is_active) VALUES ('D:\Work', 10, 1);

CREATE TABLE local_repos (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    abs_path TEXT NOT NULL UNIQUE,
    normalized_key TEXT,
    parent_repo_id INTEGER,
    git_dir_kind INTEGER NOT NULL DEFAULT 0,
    has_lfs INTEGER NOT NULL DEFAULT 0,
    has_submodule INTEGER NOT NULL DEFAULT 0,
    is_missing INTEGER NOT NULL DEFAULT 0,
    missing_since TEXT,
    FOREIGN KEY(parent_repo_id) REFERENCES local_repos(id) ON DELETE SET NULL
);

CREATE TABLE local_status (
    local_repo_id INTEGER PRIMARY KEY,
    branch TEXT,
    upstream TEXT,
    ahead INTEGER NOT NULL DEFAULT 0,
    behind INTEGER NOT NULL DEFAULT 0,
    dirty_count INTEGER NOT NULL DEFAULT 0,
    conflicted_count INTEGER NOT NULL DEFAULT 0,
    head_hash TEXT,
    state_code INTEGER NOT NULL DEFAULT 12,
    last_fetched_at TEXT,
    FOREIGN KEY(local_repo_id) REFERENCES local_repos(id) ON DELETE CASCADE
);

CREATE TABLE job_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    job_type TEXT NOT NULL,
    target_path TEXT NOT NULL,
    pre_head TEXT,
    post_head TEXT,
    result TEXT NOT NULL,
    error_category TEXT,
    created_at TEXT NOT NULL
);
INSERT INTO job_history (job_type, target_path, result, created_at)
VALUES ('pull', 'D:\Work\ProjectA', 'success', '2026-08-01T00:00:00Z');

CREATE TABLE server_profiles (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    host TEXT NOT NULL UNIQUE,
    api_kind TEXT NOT NULL,
    aliases_json TEXT NOT NULL DEFAULT '[]',
    token_ref TEXT NOT NULL,
    api_version TEXT,
    created_at TEXT NOT NULL,
    last_synced_at TEXT
);
INSERT INTO server_profiles (host, api_kind, token_ref, created_at)
VALUES ('gitlab.test', 'GitLab', 'ref-1', '2026-08-01T00:00:00Z');

CREATE TABLE server_repos (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    profile_id INTEGER NOT NULL,
    namespace TEXT NOT NULL,
    name TEXT NOT NULL,
    clone_url TEXT NOT NULL,
    normalized_key TEXT NOT NULL,
    default_branch TEXT,
    is_archived INTEGER NOT NULL DEFAULT 0,
    collected_at TEXT NOT NULL,
    FOREIGN KEY(profile_id) REFERENCES server_profiles(id) ON DELETE CASCADE
);

CREATE INDEX idx_local_repos_normalized_key ON local_repos(normalized_key);
CREATE INDEX idx_local_repos_parent_id ON local_repos(parent_repo_id);
CREATE INDEX idx_job_history_target ON job_history(target_path);
CREATE INDEX idx_job_history_created ON job_history(created_at);
CREATE INDEX idx_server_repos_profile_key ON server_repos(profile_id, normalized_key);
CREATE INDEX idx_server_repos_normalized_key ON server_repos(normalized_key);
CREATE INDEX idx_server_repos_namespace ON server_repos(namespace);
";
        cmd.ExecuteNonQuery();
    }
}
