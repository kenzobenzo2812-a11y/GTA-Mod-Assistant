using Microsoft.Data.Sqlite;
using GTAModAssistant.Core.Models;

namespace GTAModAssistant.Core.Services;

public sealed class SqliteHistoryRepository
{
    private readonly string _connectionString;
    private readonly object _writeLock = new();

    public event Action<AuditLogEntry>? LogAppended;

    public string DatabasePath { get; }

    public SqliteHistoryRepository(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        };
        _connectionString = csb.ToString();
        InitializeSchema();
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private void InitializeSchema()
    {
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS app_settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS installed_mods (
                    id TEXT PRIMARY KEY,
                    slug TEXT NOT NULL,
                    name TEXT NOT NULL,
                    version TEXT NOT NULL,
                    author TEXT NOT NULL,
                    source_path TEXT NOT NULL,
                    category INTEGER NOT NULL,
                    target_game_build INTEGER NULL,
                    verification_state INTEGER NOT NULL,
                    is_enabled INTEGER NOT NULL DEFAULT 1,
                    backup_id TEXT NULL,
                    generated_oiv_path TEXT NULL,
                    installed_at_utc TEXT NOT NULL,
                    verified_at_utc TEXT NULL,
                    ai_summary TEXT NOT NULL,
                    plan_json TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS installed_mod_files (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    mod_id TEXT NOT NULL REFERENCES installed_mods(id) ON DELETE CASCADE,
                    operation INTEGER NOT NULL,
                    target_relative_path TEXT NOT NULL,
                    rpf_container_relative_path TEXT NOT NULL,
                    rpf_internal_path TEXT NOT NULL,
                    dlc_list_entry TEXT NOT NULL,
                    existed_before INTEGER NOT NULL,
                    sha256_before TEXT NULL,
                    sha256_after TEXT NOT NULL,
                    previous_stash_path TEXT NULL,
                    disabled_stash_path TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS backups (
                    id TEXT PRIMARY KEY,
                    label TEXT NOT NULL,
                    backup_type TEXT NOT NULL,
                    archive_path TEXT NOT NULL,
                    manifest_sha256 TEXT NOT NULL,
                    file_count INTEGER NOT NULL,
                    total_bytes INTEGER NOT NULL,
                    is_verified INTEGER NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    verified_at_utc TEXT NULL,
                    notes TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS profiles (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL UNIQUE,
                    description TEXT NOT NULL,
                    is_active INTEGER NOT NULL DEFAULT 0,
                    created_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS profile_mod_states (
                    profile_id TEXT NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
                    mod_id TEXT NOT NULL REFERENCES installed_mods(id) ON DELETE CASCADE,
                    is_enabled INTEGER NOT NULL,
                    PRIMARY KEY (profile_id, mod_id)
                );

                CREATE TABLE IF NOT EXISTS audit_logs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    correlation_id TEXT NOT NULL,
                    timestamp_utc TEXT NOT NULL,
                    level INTEGER NOT NULL,
                    category TEXT NOT NULL,
                    message TEXT NOT NULL,
                    details TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();

            // Seed default profile if none exist
            using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM profiles;";
            long profileCount = (long)(checkCmd.ExecuteScalar() ?? 0L);
            if (profileCount == 0)
            {
                using var seedCmd = conn.CreateCommand();
                seedCmd.CommandText = """
                    INSERT INTO profiles (id, name, description, is_active, created_at_utc)
                    VALUES ('default', 'Default Single-Player', 'Primary GTA V Single-Player mod profile', 1, $now);
                    """;
                seedCmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                seedCmd.ExecuteNonQuery();
            }
        }
    }

    public string? GetSetting(string key)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM app_settings WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string? value)
    {
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            if (value is null)
            {
                cmd.CommandText = "DELETE FROM app_settings WHERE key = $k;";
                cmd.Parameters.AddWithValue("$k", key);
            }
            else
            {
                cmd.CommandText = """
                    INSERT INTO app_settings (key, value) VALUES ($k, $v)
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                    """;
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$v", value);
            }
            cmd.ExecuteNonQuery();
        }
    }

    public AuditLogEntry AppendLog(
        AuditLogLevel level,
        string category,
        string message,
        string details = "",
        string correlationId = "")
    {
        var now = DateTimeOffset.UtcNow;
        long id;
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO audit_logs (correlation_id, timestamp_utc, level, category, message, details)
                VALUES ($cid, $ts, $lvl, $cat, $msg, $det);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$cid", correlationId ?? string.Empty);
            cmd.Parameters.AddWithValue("$ts", now.ToString("O"));
            cmd.Parameters.AddWithValue("$lvl", (int)level);
            cmd.Parameters.AddWithValue("$cat", category);
            cmd.Parameters.AddWithValue("$msg", message);
            cmd.Parameters.AddWithValue("$det", details ?? string.Empty);
            id = (long)(cmd.ExecuteScalar() ?? 0L);
        }

        var entry = new AuditLogEntry
        {
            Id = id,
            CorrelationId = correlationId ?? string.Empty,
            TimestampUtc = now,
            Level = level,
            Category = category,
            Message = message,
            Details = details ?? string.Empty
        };

        LogAppended?.Invoke(entry);
        return entry;
    }

    public IReadOnlyList<AuditLogEntry> GetRecentLogs(int limit = 500)
    {
        var list = new List<AuditLogEntry>();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, correlation_id, timestamp_utc, level, category, message, details
            FROM audit_logs
            ORDER BY id DESC
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$lim", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new AuditLogEntry
            {
                Id = r.GetInt64(0),
                CorrelationId = r.GetString(1),
                TimestampUtc = DateTimeOffset.Parse(r.GetString(2)),
                Level = (AuditLogLevel)r.GetInt32(3),
                Category = r.GetString(4),
                Message = r.GetString(5),
                Details = r.GetString(6)
            });
        }
        list.Reverse();
        return list;
    }

    public void SaveInstalledMod(InstalledModRecord mod, IReadOnlyList<InstalledModFileRecord> files)
    {
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO installed_mods (
                        id, slug, name, version, author, source_path, category, target_game_build,
                        verification_state, is_enabled, backup_id, generated_oiv_path,
                        installed_at_utc, verified_at_utc, ai_summary, plan_json
                    ) VALUES (
                        $id, $slug, $name, $ver, $author, $src, $cat, $build,
                        $vstate, $enabled, $bid, $oiv,
                        $inst, $verat, $aisum, $plan
                    );
                    """;
                cmd.Parameters.AddWithValue("$id", mod.Id);
                cmd.Parameters.AddWithValue("$slug", mod.Slug);
                cmd.Parameters.AddWithValue("$name", mod.Name);
                cmd.Parameters.AddWithValue("$ver", mod.Version);
                cmd.Parameters.AddWithValue("$author", mod.Author);
                cmd.Parameters.AddWithValue("$src", mod.SourcePath);
                cmd.Parameters.AddWithValue("$cat", (int)mod.Category);
                cmd.Parameters.AddWithValue("$build", mod.TargetGameBuild.HasValue ? mod.TargetGameBuild.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("$vstate", (int)mod.VerificationState);
                cmd.Parameters.AddWithValue("$enabled", mod.IsEnabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$bid", (object?)mod.BackupId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$oiv", (object?)mod.GeneratedOivPath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$inst", mod.InstalledAtUtc.ToString("O"));
                cmd.Parameters.AddWithValue("$verat", mod.VerifiedAtUtc.HasValue ? mod.VerifiedAtUtc.Value.ToString("O") : DBNull.Value);
                cmd.Parameters.AddWithValue("$aisum", mod.AiSummary);
                cmd.Parameters.AddWithValue("$plan", mod.PlanJson);
                cmd.ExecuteNonQuery();
            }

            foreach (var f in files)
            {
                using var fCmd = conn.CreateCommand();
                fCmd.Transaction = tx;
                fCmd.CommandText = """
                    INSERT INTO installed_mod_files (
                        mod_id, operation, target_relative_path, rpf_container_relative_path,
                        rpf_internal_path, dlc_list_entry, existed_before, sha256_before,
                        sha256_after, previous_stash_path, disabled_stash_path
                    ) VALUES (
                        $mid, $op, $trel, $rpfcont,
                        $rpfint, $dlc, $exb, $shab,
                        $shaa, $prevstash, $disstash
                    );
                    """;
                fCmd.Parameters.AddWithValue("$mid", mod.Id);
                fCmd.Parameters.AddWithValue("$op", (int)f.Operation);
                fCmd.Parameters.AddWithValue("$trel", f.TargetRelativePath);
                fCmd.Parameters.AddWithValue("$rpfcont", f.RpfContainerRelativePath);
                fCmd.Parameters.AddWithValue("$rpfint", f.RpfInternalPath);
                fCmd.Parameters.AddWithValue("$dlc", f.DlcListEntry);
                fCmd.Parameters.AddWithValue("$exb", f.ExistedBefore ? 1 : 0);
                fCmd.Parameters.AddWithValue("$shab", (object?)f.Sha256Before ?? DBNull.Value);
                fCmd.Parameters.AddWithValue("$shaa", f.Sha256After);
                fCmd.Parameters.AddWithValue("$prevstash", (object?)f.PreviousFileStashPath ?? DBNull.Value);
                fCmd.Parameters.AddWithValue("$disstash", (object?)f.DisabledStashPath ?? DBNull.Value);
                fCmd.ExecuteNonQuery();
            }

            // Record enabled state in active profile
            using (var pCmd = conn.CreateCommand())
            {
                pCmd.Transaction = tx;
                pCmd.CommandText = """
                    INSERT OR REPLACE INTO profile_mod_states (profile_id, mod_id, is_enabled)
                    SELECT id, $mid, $en FROM profiles WHERE is_active = 1;
                    """;
                pCmd.Parameters.AddWithValue("$mid", mod.Id);
                pCmd.Parameters.AddWithValue("$en", mod.IsEnabled ? 1 : 0);
                pCmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public IReadOnlyList<InstalledModRecord> GetInstalledMods()
    {
        var list = new List<InstalledModRecord>();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT m.id, m.slug, m.name, m.version, m.author, m.source_path, m.category,
                   m.target_game_build, m.verification_state, m.is_enabled, m.backup_id,
                   m.generated_oiv_path, m.installed_at_utc, m.verified_at_utc, m.ai_summary, m.plan_json,
                   (SELECT COUNT(*) FROM installed_mod_files f WHERE f.mod_id = m.id)
            FROM installed_mods m
            ORDER BY m.installed_at_utc DESC;
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new InstalledModRecord
            {
                Id = r.GetString(0),
                Slug = r.GetString(1),
                Name = r.GetString(2),
                Version = r.GetString(3),
                Author = r.GetString(4),
                SourcePath = r.GetString(5),
                Category = (ModCategory)r.GetInt32(6),
                TargetGameBuild = r.IsDBNull(7) ? null : r.GetInt32(7),
                VerificationState = (InstallVerificationState)r.GetInt32(8),
                IsEnabled = r.GetInt32(9) != 0,
                BackupId = r.IsDBNull(10) ? null : r.GetString(10),
                GeneratedOivPath = r.IsDBNull(11) ? null : r.GetString(11),
                InstalledAtUtc = DateTimeOffset.Parse(r.GetString(12)),
                VerifiedAtUtc = r.IsDBNull(13) ? null : DateTimeOffset.Parse(r.GetString(13)),
                AiSummary = r.GetString(14),
                PlanJson = r.GetString(15),
                FileCount = r.GetInt32(16)
            });
        }
        return list;
    }

    public InstalledModRecord? GetInstalledModById(string modId) =>
        GetInstalledMods().FirstOrDefault(m => string.Equals(m.Id, modId, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<InstalledModFileRecord> GetModFiles(string? modId = null, bool onlyEnabledMods = false)
    {
        var list = new List<InstalledModFileRecord>();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        var sql = """
            SELECT f.id, f.mod_id, f.operation, f.target_relative_path, f.rpf_container_relative_path,
                   f.rpf_internal_path, f.dlc_list_entry, f.existed_before, f.sha256_before,
                   f.sha256_after, f.previous_stash_path, f.disabled_stash_path
            FROM installed_mod_files f
            INNER JOIN installed_mods m ON m.id = f.mod_id
            WHERE 1 = 1
            """;
        if (modId is not null)
        {
            sql += " AND f.mod_id = $mid";
            cmd.Parameters.AddWithValue("$mid", modId);
        }
        if (onlyEnabledMods)
        {
            sql += " AND m.is_enabled = 1";
        }
        sql += " ORDER BY f.id ASC;";
        cmd.CommandText = sql;

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new InstalledModFileRecord
            {
                Id = r.GetInt64(0),
                ModId = r.GetString(1),
                Operation = (InstallOperationKind)r.GetInt32(2),
                TargetRelativePath = r.GetString(3),
                RpfContainerRelativePath = r.GetString(4),
                RpfInternalPath = r.GetString(5),
                DlcListEntry = r.GetString(6),
                ExistedBefore = r.GetInt32(7) != 0,
                Sha256Before = r.IsDBNull(8) ? null : r.GetString(8),
                Sha256After = r.GetString(9),
                PreviousFileStashPath = r.IsDBNull(10) ? null : r.GetString(10),
                DisabledStashPath = r.IsDBNull(11) ? null : r.GetString(11)
            });
        }
        return list;
    }

    public void UpdateModEnabledState(string modId, bool isEnabled, IReadOnlyDictionary<long, string?> updatedDisabledPaths)
    {
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE installed_mods SET is_enabled = $en WHERE id = $id;";
                cmd.Parameters.AddWithValue("$en", isEnabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$id", modId);
                cmd.ExecuteNonQuery();
            }

            foreach (var kv in updatedDisabledPaths)
            {
                using var fCmd = conn.CreateCommand();
                fCmd.Transaction = tx;
                fCmd.CommandText = "UPDATE installed_mod_files SET disabled_stash_path = $p WHERE id = $fid;";
                fCmd.Parameters.AddWithValue("$p", (object?)kv.Value ?? DBNull.Value);
                fCmd.Parameters.AddWithValue("$fid", kv.Key);
                fCmd.ExecuteNonQuery();
            }

            using (var pCmd = conn.CreateCommand())
            {
                pCmd.Transaction = tx;
                pCmd.CommandText = """
                    INSERT OR REPLACE INTO profile_mod_states (profile_id, mod_id, is_enabled)
                    SELECT id, $mid, $en FROM profiles WHERE is_active = 1;
                    """;
                pCmd.Parameters.AddWithValue("$mid", modId);
                pCmd.Parameters.AddWithValue("$en", isEnabled ? 1 : 0);
                pCmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public void UpdateModVerificationState(string modId, InstallVerificationState state)
    {
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE installed_mods
                SET verification_state = $s,
                    verified_at_utc = $vat
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$s", (int)state);
            cmd.Parameters.AddWithValue("$vat", state == InstallVerificationState.VerifiedComplete ? DateTimeOffset.UtcNow.ToString("O") : DBNull.Value);
            cmd.Parameters.AddWithValue("$id", modId);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteInstalledMod(string modId)
    {
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM installed_mods WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", modId);
            cmd.ExecuteNonQuery();
        }
    }

    public void SaveBackupRecord(BackupRecord backup)
    {
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO backups (
                    id, label, backup_type, archive_path, manifest_sha256,
                    file_count, total_bytes, is_verified, created_at_utc, verified_at_utc, notes
                ) VALUES (
                    $id, $lbl, $btype, $arch, $msha,
                    $fc, $tb, $ver, $cat, $vat, $notes
                );
                """;
            cmd.Parameters.AddWithValue("$id", backup.Id);
            cmd.Parameters.AddWithValue("$lbl", backup.Label);
            cmd.Parameters.AddWithValue("$btype", backup.BackupType);
            cmd.Parameters.AddWithValue("$arch", backup.ArchivePath);
            cmd.Parameters.AddWithValue("$msha", backup.ManifestSha256);
            cmd.Parameters.AddWithValue("$fc", backup.FileCount);
            cmd.Parameters.AddWithValue("$tb", backup.TotalBytes);
            cmd.Parameters.AddWithValue("$ver", backup.IsVerified ? 1 : 0);
            cmd.Parameters.AddWithValue("$cat", backup.CreatedAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$vat", backup.VerifiedAtUtc.HasValue ? backup.VerifiedAtUtc.Value.ToString("O") : DBNull.Value);
            cmd.Parameters.AddWithValue("$notes", backup.Notes);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<BackupRecord> GetBackups()
    {
        var list = new List<BackupRecord>();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, label, backup_type, archive_path, manifest_sha256,
                   file_count, total_bytes, is_verified, created_at_utc, verified_at_utc, notes
            FROM backups
            ORDER BY created_at_utc DESC;
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new BackupRecord
            {
                Id = r.GetString(0),
                Label = r.GetString(1),
                BackupType = r.GetString(2),
                ArchivePath = r.GetString(3),
                ManifestSha256 = r.GetString(4),
                FileCount = r.GetInt32(5),
                TotalBytes = r.GetInt64(6),
                IsVerified = r.GetInt32(7) != 0,
                CreatedAtUtc = DateTimeOffset.Parse(r.GetString(8)),
                VerifiedAtUtc = r.IsDBNull(9) ? null : DateTimeOffset.Parse(r.GetString(9)),
                Notes = r.GetString(10)
            });
        }
        return list;
    }

    public void DeleteBackupRecord(string backupId)
    {
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM backups WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", backupId);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<ModProfile> GetProfiles()
    {
        var list = new List<ModProfile>();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, description, is_active, created_at_utc FROM profiles ORDER BY created_at_utc ASC;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ModProfile
            {
                Id = r.GetString(0),
                Name = r.GetString(1),
                Description = r.GetString(2),
                IsActive = r.GetInt32(3) != 0,
                CreatedAtUtc = DateTimeOffset.Parse(r.GetString(4))
            });
        }
        return list;
    }

    public ModProfile CreateProfile(string name, string description, bool copyCurrentEnabledStates = true)
    {
        string id = "prof_" + Guid.NewGuid().ToString("N")[..8];
        var now = DateTimeOffset.UtcNow;

        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO profiles (id, name, description, is_active, created_at_utc)
                    VALUES ($id, $n, $d, 0, $now);
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$n", name.Trim());
                cmd.Parameters.AddWithValue("$d", description?.Trim() ?? string.Empty);
                cmd.Parameters.AddWithValue("$now", now.ToString("O"));
                cmd.ExecuteNonQuery();
            }

            if (copyCurrentEnabledStates)
            {
                using var copyCmd = conn.CreateCommand();
                copyCmd.Transaction = tx;
                copyCmd.CommandText = """
                    INSERT INTO profile_mod_states (profile_id, mod_id, is_enabled)
                    SELECT $pid, id, is_enabled FROM installed_mods;
                    """;
                copyCmd.Parameters.AddWithValue("$pid", id);
                copyCmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        return new ModProfile
        {
            Id = id,
            Name = name.Trim(),
            Description = description?.Trim() ?? string.Empty,
            IsActive = false,
            CreatedAtUtc = now
        };
    }

    public Dictionary<string, bool> GetProfileModStates(string profileId)
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT mod_id, is_enabled FROM profile_mod_states WHERE profile_id = $pid;";
        cmd.Parameters.AddWithValue("$pid", profileId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            map[r.GetString(0)] = r.GetInt32(1) != 0;
        }
        return map;
    }

    public void SetActiveProfileRecord(string profileId)
    {
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            using (var saveCur = conn.CreateCommand())
            {
                saveCur.Transaction = tx;
                saveCur.CommandText = """
                    INSERT OR REPLACE INTO profile_mod_states (profile_id, mod_id, is_enabled)
                    SELECT p.id, m.id, m.is_enabled
                    FROM profiles p CROSS JOIN installed_mods m
                    WHERE p.is_active = 1;
                    """;
                saveCur.ExecuteNonQuery();
            }

            using (var clearCmd = conn.CreateCommand())
            {
                clearCmd.Transaction = tx;
                clearCmd.CommandText = "UPDATE profiles SET is_active = CASE WHEN id = $pid THEN 1 ELSE 0 END;";
                clearCmd.Parameters.AddWithValue("$pid", profileId);
                clearCmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public void DeleteProfile(string profileId)
    {
        if (string.Equals(profileId, "default", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The Default Single-Player profile cannot be deleted.");
        }

        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM profiles WHERE id = $pid AND is_active = 0;";
            cmd.Parameters.AddWithValue("$pid", profileId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Non-destructively checkpoints the WAL journal and runs SQLite VACUUM to reclaim unused disk pages
    /// while preserving 100% of installed mods, profiles, backups, and audit logs.
    /// </summary>
    public long CompactDatabase()
    {
        lock (_writeLock)
        {
            long beforeBytes = GetDatabaseFootprintBytes();
            using (var conn = OpenConnection())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); VACUUM;";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            long afterBytes = GetDatabaseFootprintBytes();
            return Math.Max(0L, beforeBytes - afterBytes);
        }
    }

    public long GetDatabaseFootprintBytes()
    {
        long total = 0;
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            string p = DatabasePath + suffix;
            if (File.Exists(p))
            {
                try { total += new FileInfo(p).Length; } catch { /* ignore */ }
            }
        }
        return total;
    }
}
