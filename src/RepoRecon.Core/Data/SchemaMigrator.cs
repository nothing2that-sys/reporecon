using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RepoRecon.Core.Data;

public class SchemaMigrator
{
    public const int CurrentSchemaVersion = 2;
    private readonly ILogger<SchemaMigrator> _logger;

    public SchemaMigrator(ILogger<SchemaMigrator>? logger = null)
    {
        _logger = logger ?? NullLogger<SchemaMigrator>.Instance;
    }

    public bool EnsureInitialized(string dbPath)
    {
        var wasRecreated = false;
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            InitializeSchema(dbPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SQLite DB schema initialization failed on {DbPath}. Recreating database.", dbPath);
            RecreateDatabase(dbPath);
            wasRecreated = true;
        }

        return wasRecreated;
    }

    private void InitializeSchema(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;";
            pragmaCmd.ExecuteNonQuery();
        }

        var existingVersion = GetExistingVersion(connection);

        if (existingVersion == 0)
        {
            CreateSchemaV2(connection);
        }
        else if (existingVersion == CurrentSchemaVersion)
        {
            // Already current
        }
        else
        {
            // Version mismatch (older or newer version B-5) -> discard and recreate
            _logger.LogInformation("Database version mismatch ({ExistingVersion} != {CurrentVersion}). Recreating schema.", existingVersion, CurrentSchemaVersion);
            throw new InvalidOperationException($"Schema version {existingVersion} requires recreation to {CurrentSchemaVersion}.");
        }
    }

    private static int GetExistingVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='schema_version';";
        var exists = cmd.ExecuteScalar();
        if (exists == null)
            return 0;

        using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = "SELECT version FROM schema_version LIMIT 1;";
        var versionObj = versionCmd.ExecuteScalar();
        return versionObj != null && int.TryParse(versionObj.ToString(), out var v) ? v : 0;
    }

    private static void CreateSchemaV2(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;

        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS schema_version (
    version INTEGER PRIMARY KEY
);

INSERT OR REPLACE INTO schema_version (version) VALUES (2);

CREATE TABLE IF NOT EXISTS scan_roots (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    path TEXT NOT NULL UNIQUE,
    max_depth INTEGER NOT NULL DEFAULT 10,
    is_active INTEGER NOT NULL DEFAULT 1,
    last_scanned_at TEXT,
    is_offline INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS local_repos (
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

CREATE TABLE IF NOT EXISTS local_status (
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

CREATE INDEX IF NOT EXISTS idx_local_repos_normalized_key ON local_repos(normalized_key);
CREATE INDEX IF NOT EXISTS idx_local_repos_parent_id ON local_repos(parent_repo_id);
";
        cmd.ExecuteNonQuery();
        transaction.Commit();
    }

    public void RecreateDatabase(string dbPath)
    {
        try
        {
            SqliteConnection.ClearAllPools();

            // Clean main DB and sidecar files (B-7)
            DeleteFileIfExists(dbPath);
            DeleteFileIfExists($"{dbPath}-wal");
            DeleteFileIfExists($"{dbPath}-shm");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while deleting old SQLite database files for {DbPath}", dbPath);
        }

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        CreateSchemaV2(connection);
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
