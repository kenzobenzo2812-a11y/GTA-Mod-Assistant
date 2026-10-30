using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GTAModAssistant.Core.Models;
using GTAModAssistant.Core.Security;

namespace GTAModAssistant.Core.Services;

public sealed class BackupService
{
    private readonly string _backupsRootDirectory;
    private readonly SqliteHistoryRepository _repository;

    public BackupService(string backupsRootDirectory, SqliteHistoryRepository repository)
    {
        _backupsRootDirectory = Path.GetFullPath(backupsRootDirectory);
        _repository = repository;
        Directory.CreateDirectory(_backupsRootDirectory);
    }

    public BackupRecord CreateVerifiedBackup(
        string gtaRootPath,
        string label,
        string backupType,
        IEnumerable<string> targetRelativePaths,
        IEnumerable<string>? newlyCreatedPathsToTrack = null,
        string correlationId = "")
    {
        string backupId = $"bkp_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}";
        string folder = Path.Combine(_backupsRootDirectory, backupId);
        Directory.CreateDirectory(folder);
        string zipPath = Path.Combine(folder, $"{backupId}.zip");

        var distinctRelPaths = targetRelativePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(PathSafetyGuard.NormalizeRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var createdTrack = (newlyCreatedPathsToTrack ?? Enumerable.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(PathSafetyGuard.NormalizeRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var entries = new List<BackupFileEntry>();
        var filesToWrite = new List<(string ZipEntryName, string SourceFullPath, string ExpectedSha256)>();
        long totalBytes = 0;
        int idx = 0;

        foreach (var rel in distinctRelPaths)
        {
            string fullPath = PathSafetyGuard.ResolveSafeChildPath(gtaRootPath, rel);
            if (File.Exists(fullPath))
            {
                idx++;
                var fi = new FileInfo(fullPath);
                string sha = HashVerifier.ComputeFileSha256(fullPath);
                string entryName = $"files/{idx:D4}_{Path.GetFileName(fullPath)}";
                entries.Add(new BackupFileEntry(rel, entryName, fi.Length, sha, ExistedBefore: true));
                filesToWrite.Add((entryName, fullPath, sha));
                totalBytes += fi.Length;
            }
            else
            {
                entries.Add(new BackupFileEntry(rel, string.Empty, 0, string.Empty, ExistedBefore: false));
                if (!createdTrack.Contains(rel, StringComparer.OrdinalIgnoreCase))
                {
                    createdTrack.Add(rel);
                }
            }
        }

        var manifest = new BackupManifest
        {
            BackupId = backupId,
            Label = label,
            GtaRootPath = Path.GetFullPath(gtaRootPath),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Entries = entries,
            CreatedPathsToRemoveOnRollback = createdTrack
        };

        string manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        byte[] manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
        string manifestSha256 = HashVerifier.ComputeBytesSha256(manifestBytes);

        using (var fs = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var mEntry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
            using (var ms = mEntry.Open())
            {
                ms.Write(manifestBytes, 0, manifestBytes.Length);
            }

            foreach (var (zipEntryName, srcPath, _) in filesToWrite)
            {
                zip.CreateEntryFromFile(srcPath, zipEntryName, CompressionLevel.Fastest);
            }

            fs.Flush(flushToDisk: true);
        }

        // Immediate cryptographic read-back verification!
        if (!VerifyBackupArchiveFile(zipPath, manifestSha256, out string verifyDetails))
        {
            throw new InvalidDataException($"Backup verification failed for '{backupId}': {verifyDetails}");
        }

        var now = DateTimeOffset.UtcNow;
        var record = new BackupRecord
        {
            Id = backupId,
            Label = label,
            BackupType = backupType,
            ArchivePath = zipPath,
            ManifestSha256 = manifestSha256,
            FileCount = filesToWrite.Count,
            TotalBytes = totalBytes,
            IsVerified = true,
            CreatedAtUtc = manifest.CreatedAtUtc,
            VerifiedAtUtc = now,
            Notes = verifyDetails
        };

        _repository.SaveBackupRecord(record);
        _repository.AppendLog(
            AuditLogLevel.Verify,
            "Backup",
            $"Created and SHA-256 verified backup '{label}' ({backupId})",
            verifyDetails,
            correlationId);

        return record;
    }

    public bool VerifyExistingBackup(BackupRecord record, out string details) =>
        VerifyBackupArchiveFile(record.ArchivePath, record.ManifestSha256, out details);

    public static bool VerifyBackupArchiveFile(string zipPath, string expectedManifestSha256, out string details)
    {
        if (!File.Exists(zipPath))
        {
            details = $"Backup archive missing on disk: '{zipPath}'.";
            return false;
        }

        try
        {
            using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

            var mEntry = zip.GetEntry("manifest.json");
            if (mEntry is null)
            {
                details = "Backup archive is missing manifest.json.";
                return false;
            }

            byte[] manifestBytes;
            using (var ms = new MemoryStream())
            using (var s = mEntry.Open())
            {
                s.CopyTo(ms);
                manifestBytes = ms.ToArray();
            }

            string actualManifestSha = HashVerifier.ComputeBytesSha256(manifestBytes);
            if (!string.IsNullOrEmpty(expectedManifestSha256) &&
                !string.Equals(actualManifestSha, expectedManifestSha256, StringComparison.OrdinalIgnoreCase))
            {
                details = $"Manifest SHA-256 mismatch (expected {expectedManifestSha256[..12]}..., got {actualManifestSha[..12]}...).";
                return false;
            }

            var manifest = JsonSerializer.Deserialize<BackupManifest>(Encoding.UTF8.GetString(manifestBytes))
                           ?? throw new InvalidDataException("Unable to deserialize backup manifest.json.");

            int verifiedCount = 0;
            foreach (var entry in manifest.Entries.Where(e => e.ExistedBefore))
            {
                var zEntry = zip.GetEntry(entry.ArchiveEntryName);
                if (zEntry is null)
                {
                    details = $"Missing archived payload '{entry.ArchiveEntryName}' for '{entry.RelativePath}'.";
                    return false;
                }

                using var entryStream = zEntry.Open();
                string entrySha = HashVerifier.ComputeStreamSha256(entryStream);
                if (!string.Equals(entrySha, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    details = $"Payload SHA-256 mismatch for '{entry.RelativePath}'.";
                    return false;
                }
                verifiedCount++;
            }

            details = $"Verified manifest SHA-256 ({actualManifestSha[..12]}...) and {verifiedCount} archived file(s).";
            return true;
        }
        catch (Exception ex)
        {
            details = $"Archive verification exception: {ex.Message}";
            return false;
        }
    }

    public void RestoreVerifiedBackup(
        BackupRecord record,
        string? gtaRootOverride = null,
        IProgress<InstallProgressReport>? progress = null,
        string correlationId = "")
    {
        progress?.Report(new InstallProgressReport(10, "Verifying Backup", $"Checking SHA-256 integrity of {record.Id}..."));
        if (!VerifyExistingBackup(record, out string verifyDetails))
        {
            _repository.AppendLog(AuditLogLevel.Error, "Restore", $"Refused to restore unverified backup {record.Id}", verifyDetails, correlationId);
            throw new InvalidOperationException($"Cannot restore backup '{record.Label}': {verifyDetails}");
        }

        using var fs = new FileStream(record.ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        BackupManifest manifest;
        using (var s = zip.GetEntry("manifest.json")!.Open())
        {
            manifest = JsonSerializer.Deserialize<BackupManifest>(s)!;
        }

        string targetRoot = !string.IsNullOrWhiteSpace(gtaRootOverride) ? gtaRootOverride : manifest.GtaRootPath;
        if (!Directory.Exists(targetRoot))
        {
            throw new DirectoryNotFoundException($"Target GTA V directory '{targetRoot}' does not exist.");
        }

        int totalOps = manifest.Entries.Count + manifest.CreatedPathsToRemoveOnRollback.Count;
        int done = 0;

        // 1. Restore all files that existed before
        foreach (var entry in manifest.Entries.Where(e => e.ExistedBefore))
        {
            done++;
            int pct = 20 + (int)(60.0 * done / Math.Max(1, totalOps));
            progress?.Report(new InstallProgressReport(pct, "Restoring Files", $"Restoring {entry.RelativePath}..."));

            string destFull = PathSafetyGuard.ResolveSafeChildPath(targetRoot, entry.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);

            string tempPath = destFull + ".gtama_restore_tmp";
            var zEntry = zip.GetEntry(entry.ArchiveEntryName)!;
            using (var srcStream = zEntry.Open())
            using (var outFs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                srcStream.CopyTo(outFs);
                outFs.Flush(flushToDisk: true);
            }

            if (!HashVerifier.VerifyFileSha256(tempPath, entry.Sha256, out string actualSha))
            {
                File.Delete(tempPath);
                throw new InvalidDataException($"Restored file '{entry.RelativePath}' failed SHA-256 check (got {actualSha}).");
            }

            File.Move(tempPath, destFull, overwrite: true);
        }

        // 2. Remove files that were newly created after this snapshot
        foreach (var createdRel in manifest.CreatedPathsToRemoveOnRollback)
        {
            done++;
            if (manifest.Entries.Any(e => e.ExistedBefore && string.Equals(e.RelativePath, createdRel, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string fullCreated = PathSafetyGuard.ResolveSafeChildPath(targetRoot, createdRel);
            if (File.Exists(fullCreated))
            {
                File.Delete(fullCreated);
                TryCleanEmptyModDirectories(targetRoot, Path.GetDirectoryName(fullCreated));
            }
        }

        progress?.Report(new InstallProgressReport(100, "Restore Verified", $"Snapshot '{record.Label}' restored and verified."));
        _repository.AppendLog(
            AuditLogLevel.Verify,
            "Restore",
            $"Restored and SHA-256 verified snapshot '{record.Label}' ({record.Id})",
            $"Restored {manifest.Entries.Count(e => e.ExistedBefore)} file(s), removed {manifest.CreatedPathsToRemoveOnRollback.Count} post-snapshot file(s).",
            correlationId);
    }

    public static void TryCleanEmptyModDirectories(string gtaRoot, string? startDir)
    {
        if (string.IsNullOrWhiteSpace(startDir))
        {
            return;
        }

        string fullRoot = Path.GetFullPath(gtaRoot).TrimEnd(Path.DirectorySeparatorChar);
        string modsRoot = Path.Combine(fullRoot, "mods");
        string? cur = Path.GetFullPath(startDir).TrimEnd(Path.DirectorySeparatorChar);

        while (!string.IsNullOrEmpty(cur) &&
               !string.Equals(cur, fullRoot, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(cur, modsRoot, StringComparison.OrdinalIgnoreCase) &&
               cur.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            if (Directory.Exists(cur) && !Directory.EnumerateFileSystemEntries(cur).Any())
            {
                Directory.Delete(cur);
                cur = Path.GetDirectoryName(cur);
            }
            else
            {
                break;
            }
        }
    }
}
