namespace GTAModAssistant.Core.Models;

public enum AuditLogLevel
{
    Info = 0,
    Verify = 1,
    Warning = 2,
    Error = 3,
    Security = 4
}

public sealed record AuditLogEntry
{
    public long Id { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public AuditLogLevel Level { get; init; } = AuditLogLevel.Info;
    public string Category { get; init; } = "System";
    public string Message { get; init; } = string.Empty;
    public string Details { get; init; } = string.Empty;

    public string FormattedTimestamp => TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
}

public sealed record InstalledModRecord
{
    public string Id { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = "1.0";
    public string Author { get; init; } = "Unknown";
    public string SourcePath { get; init; } = string.Empty;
    public ModCategory Category { get; init; }
    public int? TargetGameBuild { get; init; }
    public InstallVerificationState VerificationState { get; init; }
    public bool IsEnabled { get; init; } = true;
    public string? BackupId { get; init; }
    public string? GeneratedOivPath { get; init; }
    public DateTimeOffset InstalledAtUtc { get; init; }
    public DateTimeOffset? VerifiedAtUtc { get; init; }
    public string AiSummary { get; init; } = string.Empty;
    public string PlanJson { get; init; } = string.Empty;
    public int FileCount { get; init; }
}

public sealed record InstalledModFileRecord
{
    public long Id { get; init; }
    public string ModId { get; init; } = string.Empty;
    public InstallOperationKind Operation { get; init; }
    public string TargetRelativePath { get; init; } = string.Empty;
    public string RpfContainerRelativePath { get; init; } = string.Empty;
    public string RpfInternalPath { get; init; } = string.Empty;
    public string DlcListEntry { get; init; } = string.Empty;
    public bool ExistedBefore { get; init; }
    public string? Sha256Before { get; init; }
    public string Sha256After { get; init; } = string.Empty;
    public string? PreviousFileStashPath { get; init; }
    public string? DisabledStashPath { get; init; }

    public string ConflictKey =>
        Operation == InstallOperationKind.InjectIntoRpfArchive
            ? $"rpf::{NormalizePath(RpfContainerRelativePath)}::{NormalizePath(RpfInternalPath)}"
            : Operation == InstallOperationKind.RegisterDlcListEntry
                ? $"dlc::{DlcListEntry.Trim().ToLowerInvariant()}"
                : $"fs::{NormalizePath(TargetRelativePath)}";

    private static string NormalizePath(string p) =>
        p.Replace('\\', '/').Trim('/').ToLowerInvariant();
}

public sealed record BackupFileEntry(
    string RelativePath,
    string ArchiveEntryName,
    long SizeBytes,
    string Sha256,
    bool ExistedBefore);

public sealed record BackupManifest
{
    public string BackupId { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string GtaRootPath { get; init; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<BackupFileEntry> Entries { get; init; } = Array.Empty<BackupFileEntry>();
    public IReadOnlyList<string> CreatedPathsToRemoveOnRollback { get; init; } = Array.Empty<string>();
}

public sealed record BackupRecord
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string BackupType { get; init; } = "PreInstall";
    public string ArchivePath { get; init; } = string.Empty;
    public string ManifestSha256 { get; init; } = string.Empty;
    public int FileCount { get; init; }
    public long TotalBytes { get; init; }
    public bool IsVerified { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? VerifiedAtUtc { get; init; }
    public string Notes { get; init; } = string.Empty;

    public string FormattedSize =>
        TotalBytes >= 1024 * 1024
            ? $"{TotalBytes / (1024.0 * 1024.0):F2} MB"
            : $"{Math.Max(TotalBytes / 1024.0, 0.1):F1} KB";
}

public sealed record ModProfile
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
