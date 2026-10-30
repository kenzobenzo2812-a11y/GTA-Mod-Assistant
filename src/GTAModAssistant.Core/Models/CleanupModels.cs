namespace GTAModAssistant.Core.Models;

public enum CleanupCategoryKind
{
    StagingCache = 0,
    OrphanedModStash = 1,
    CrashDumps = 2,
    ArchiveTempLeftovers = 3,
    DatabaseWalBloat = 4,
    IdleRamWorkingSet = 5
}

public sealed record SystemMemorySnapshot(
    long TotalPhysicalBytes,
    long AvailablePhysicalBytes,
    long GtaDriveFreeBytes,
    string GtaDriveLabel)
{
    public long UsedPhysicalBytes => Math.Max(0L, TotalPhysicalBytes - AvailablePhysicalBytes);

    public double UsedPercent =>
        TotalPhysicalBytes > 0
            ? Math.Round(UsedPhysicalBytes * 100.0 / TotalPhysicalBytes, 1)
            : 0.0;

    public string RamSummaryDisplay =>
        TotalPhysicalBytes > 0
            ? $"{FormatSize(AvailablePhysicalBytes)} Free / {FormatSize(TotalPhysicalBytes)} Total ({UsedPercent:0.#}% Used)"
            : "Memory telemetry ready";

    public string DriveSummaryDisplay =>
        GtaDriveFreeBytes > 0
            ? $"{FormatSize(GtaDriveFreeBytes)} Free ({GtaDriveLabel})"
            : "Scan GTA V folder to view drive space";

    public static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.00} GB";
        }
        if (bytes >= 1024L * 1024)
        {
            return $"{bytes / (1024.0 * 1024.0):0.0} MB";
        }
        if (bytes >= 1024L)
        {
            return $"{bytes / 1024.0:0.0} KB";
        }
        return $"{bytes} B";
    }
}

public sealed record CleanupCategoryItem
{
    public CleanupCategoryKind Kind { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string SafetyGuarantee { get; init; } = string.Empty;
    public int ItemCount { get; init; }
    public long EstimatedReclaimableBytes { get; init; }
    public IReadOnlyList<string> TargetPaths { get; init; } = Array.Empty<string>();
    public bool IsSelected { get; init; } = true;

    public string SizeDisplay =>
        Kind == CleanupCategoryKind.IdleRamWorkingSet
            ? "Live RAM Trim"
            : SystemMemorySnapshot.FormatSize(EstimatedReclaimableBytes);

    public string CountDisplay =>
        Kind == CleanupCategoryKind.IdleRamWorkingSet
            ? $"{ItemCount} idle app(s)"
            : Kind == CleanupCategoryKind.DatabaseWalBloat
                ? "Non-destructive VACUUM"
                : $"{ItemCount} item(s)";
}

public sealed record CleanupExecutionResult(
    int DeletedDiskItems,
    long ReclaimedDiskBytes,
    int TrimmedProcessesCount,
    long FreedRamBytes,
    int SkippedLockedItems,
    SystemMemorySnapshot MemoryBefore,
    SystemMemorySnapshot MemoryAfter,
    string SummaryHeadline);
