using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using GTAModAssistant.Core.Models;

namespace GTAModAssistant.Core.Services;

/// <summary>
/// Safely cleans temporary modding staging folders, orphaned crash dumps, stale archive temp leftovers,
/// SQLite WAL bloat, and idle user-space RAM working sets — while strictly NEVER touching GTA V game files,
/// the mods\ folder, active mod backups/stashes, or Windows boot / system memory configuration settings.
/// </summary>
public sealed class SafeCleanupService
{
    private static readonly string[] SafeTempEntryPrefixes =
    {
        "gtama-",
        "gtama_",
        "OpenIV",
        "7zO",
        "Rar$",
        ".oiv_temp"
    };

    private static readonly HashSet<string> ProtectedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "GTA5",
        "GTA5_BE",
        "PlayGTAV",
        "GTAVLauncher",
        "RockstarService",
        "RockstarErrorHandler",
        "SocialClubHelper",
        "Launcher",
        "OpenIV",
        "dwm",
        "explorer",
        "csrss",
        "lsass",
        "services",
        "svchost",
        "wininit",
        "winlogon",
        "smss",
        "System",
        "Idle",
        "Memory Compression",
        "audiodg",
        "nvcontainer",
        "amdrsserv"
    };

    private readonly AppWorkspacePaths _paths;
    private readonly SqliteHistoryRepository _repository;

    public SafeCleanupService(AppWorkspacePaths paths, SqliteHistoryRepository repository)
    {
        _paths = paths;
        _repository = repository;
    }

    public SystemMemorySnapshot CaptureMemorySnapshot(string? gtaRootPath)
    {
        var (totalRam, availRam) = QueryPhysicalMemory();
        long freeDriveBytes = 0;
        string driveLabel = string.Empty;

        string probePath = !string.IsNullOrWhiteSpace(gtaRootPath) && Directory.Exists(gtaRootPath)
            ? gtaRootPath
            : _paths.RootDataDirectory;

        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(probePath));
            if (!string.IsNullOrWhiteSpace(root))
            {
                var di = new DriveInfo(root);
                if (di.IsReady)
                {
                    freeDriveBytes = di.AvailableFreeSpace;
                    driveLabel = root.TrimEnd('\\');
                }
            }
        }
        catch
        {
            // Ignore drive telemetry failure on virtual mounts
        }

        return new SystemMemorySnapshot(totalRam, availRam, freeDriveBytes, driveLabel);
    }

    /// <summary>
    /// Returns true if a candidate path is inside any protected mod, backup, active stash, database, or GTA V directory.
    /// Hard-blocks deletion of anything inside these paths under all circumstances.
    /// </summary>
    public bool IsProtectedPath(
        string candidatePath,
        string? gtaRootPath,
        string? activePlanStagingDirectory,
        ISet<string> activeModIds,
        ISet<string> referencedStashFiles)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return true;
        }

        string fullCandidate = NormalizeFullPath(candidatePath);

        // 1. NEVER touch anything inside the GTA V installation directory (vanilla files, mods\, scripts\, .asi, etc.)
        if (!string.IsNullOrWhiteSpace(gtaRootPath))
        {
            string fullGta = NormalizeFullPath(gtaRootPath);
            if (IsSameOrSubPath(fullCandidate, fullGta) || IsSameOrSubPath(fullGta, fullCandidate))
            {
                return true;
            }
        }

        // 2. NEVER touch verified backups directory, profile_stash, database, or DPAPI secret vault
        string backupsRoot = NormalizeFullPath(_paths.BackupsDirectory);
        string profileStashRoot = NormalizeFullPath(Path.Combine(_paths.RootDataDirectory, "profile_stash"));
        string dbPath = NormalizeFullPath(_paths.DatabasePath);
        string secretPath = NormalizeFullPath(Path.Combine(_paths.RootDataDirectory, "ai_secret.dpapi"));

        if (IsSameOrSubPath(fullCandidate, backupsRoot) ||
            IsSameOrSubPath(fullCandidate, profileStashRoot) ||
            fullCandidate.StartsWith(dbPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullCandidate, secretPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 3. NEVER delete the currently open ModInstallationPlan's staging folder
        if (!string.IsNullOrWhiteSpace(activePlanStagingDirectory))
        {
            string activeStaging = NormalizeFullPath(activePlanStagingDirectory);
            if (IsSameOrSubPath(fullCandidate, activeStaging) || IsSameOrSubPath(activeStaging, fullCandidate))
            {
                return true;
            }
        }

        // 4. NEVER touch conflict_stash folders belonging to any installed mod or referenced stash file
        string conflictStashRoot = NormalizeFullPath(Path.Combine(_paths.RootDataDirectory, "conflict_stash"));
        if (string.Equals(fullCandidate, conflictStashRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsSameOrSubPath(fullCandidate, conflictStashRoot))
        {
            string dirName = Path.GetFileName(fullCandidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (activeModIds.Contains(dirName))
            {
                return true;
            }

            foreach (var stashRef in referencedStashFiles)
            {
                if (IsSameOrSubPath(stashRef, fullCandidate) || IsSameOrSubPath(fullCandidate, stashRef))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public IReadOnlyList<CleanupCategoryItem> ScanCleanupCandidates(
        string? gtaRootPath,
        string? activePlanStagingDirectory = null,
        IReadOnlyList<string>? extraCrashDumpDirectoriesForTest = null,
        string? customSystemTempDirectoryForTest = null,
        TimeSpan? staleTempThresholdOverride = null)
    {
        var activeModIds = _repository.GetInstalledMods()
            .Select(m => m.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var referencedStashFiles = _repository.GetModFiles(onlyEnabledMods: false)
            .SelectMany(f => new[] { f.PreviousFileStashPath, f.DisabledStashPath })
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => NormalizeFullPath(p!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var items = new List<CleanupCategoryItem>();

        // 1. Leftover Staging Cache Folders
        var stagingTargets = new List<string>();
        long stagingBytes = 0;
        if (Directory.Exists(_paths.StagingDirectory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(_paths.StagingDirectory))
            {
                if (!IsProtectedPath(entry, gtaRootPath, activePlanStagingDirectory, activeModIds, referencedStashFiles))
                {
                    stagingTargets.Add(entry);
                    stagingBytes += ComputePathSizeBytes(entry);
                }
            }
        }

        items.Add(new CleanupCategoryItem
        {
            Kind = CleanupCategoryKind.StagingCache,
            Title = "Leftover Mod Extraction Staging Cache",
            Description = "Temporary unpacked .zip / .rar / .7z / .oiv folders left over after analyzing or installing mods.",
            SafetyGuarantee = "Installed mods, mods\\ folder, and any currently previewed mod remain 100% untouched.",
            ItemCount = stagingTargets.Count,
            EstimatedReclaimableBytes = stagingBytes,
            TargetPaths = stagingTargets,
            IsSelected = true
        });

        // 2. Orphaned Stash Folders From Previously Uninstalled Mods
        var orphanStashTargets = new List<string>();
        long orphanStashBytes = 0;
        string conflictStashRoot = Path.Combine(_paths.RootDataDirectory, "conflict_stash");
        if (Directory.Exists(conflictStashRoot))
        {
            foreach (var subDir in Directory.EnumerateDirectories(conflictStashRoot))
            {
                if (!IsProtectedPath(subDir, gtaRootPath, activePlanStagingDirectory, activeModIds, referencedStashFiles))
                {
                    orphanStashTargets.Add(subDir);
                    orphanStashBytes += ComputePathSizeBytes(subDir);
                }
            }
        }

        items.Add(new CleanupCategoryItem
        {
            Kind = CleanupCategoryKind.OrphanedModStash,
            Title = "Orphaned Uninstalled Mod Stash Leftovers",
            Description = "Old rollback fragments from mods that have already been completely uninstalled.",
            SafetyGuarantee = "Every currently installed or disabled mod's backups and stashes are strictly protected.",
            ItemCount = orphanStashTargets.Count,
            EstimatedReclaimableBytes = orphanStashBytes,
            TargetPaths = orphanStashTargets,
            IsSelected = true
        });

        // 3. GTA V & Modding Crash Dumps (*.dmp / *.mdmp)
        var crashTargets = new List<string>();
        long crashBytes = 0;
        var crashDirs = new List<string>();
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            crashDirs.Add(Path.Combine(localAppData, "Rockstar Games", "GTA V", "CrashDumps"));
            crashDirs.Add(Path.Combine(localAppData, "CrashDumps"));
        }
        if (extraCrashDumpDirectoriesForTest is not null)
        {
            crashDirs.AddRange(extraCrashDumpDirectoriesForTest);
        }

        foreach (var dir in crashDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }
            bool isDedicatedGtaDumpDir = dir.Contains("GTA V", StringComparison.OrdinalIgnoreCase);
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    string ext = Path.GetExtension(file);
                    if (!ext.Equals(".dmp", StringComparison.OrdinalIgnoreCase) &&
                        !ext.Equals(".mdmp", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string fileName = Path.GetFileName(file);
                    bool isRelevantDump = isDedicatedGtaDumpDir ||
                        fileName.StartsWith("GTA5", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("PlayGTAV", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("OpenIV", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("GTAModAssistant", StringComparison.OrdinalIgnoreCase);

                    if (isRelevantDump &&
                        !IsProtectedPath(file, gtaRootPath, activePlanStagingDirectory, activeModIds, referencedStashFiles))
                    {
                        crashTargets.Add(file);
                        crashBytes += ComputePathSizeBytes(file);
                    }
                }
            }
            catch
            {
                // Ignore inaccessible system dump dirs
            }
        }

        items.Add(new CleanupCategoryItem
        {
            Kind = CleanupCategoryKind.CrashDumps,
            Title = "GTA V & OpenIV Crash Dump Bloat (.dmp / .mdmp)",
            Description = "Large Windows/Rockstar memory dump files generated by past crashes in AppData.",
            SafetyGuarantee = "Never touches game saves, settings, logs, or any file inside your GTA V folder.",
            ItemCount = crashTargets.Count,
            EstimatedReclaimableBytes = crashBytes,
            TargetPaths = crashTargets,
            IsSelected = true
        });

        // 4. Stale Archive & OpenIV Temp Leftovers in %TEMP%
        var tempTargets = new List<string>();
        long tempBytes = 0;
        string sysTemp = !string.IsNullOrWhiteSpace(customSystemTempDirectoryForTest)
            ? customSystemTempDirectoryForTest
            : Path.GetTempPath();
        TimeSpan staleAge = staleTempThresholdOverride ?? TimeSpan.FromMinutes(30);
        DateTime cutoffUtc = DateTime.UtcNow - staleAge;

        if (Directory.Exists(sysTemp))
        {
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(sysTemp))
                {
                    string name = Path.GetFileName(entry);
                    if (!SafeTempEntryPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    DateTime lastWriteUtc = Directory.Exists(entry)
                        ? Directory.GetLastWriteTimeUtc(entry)
                        : File.GetLastWriteTimeUtc(entry);

                    if (lastWriteUtc <= cutoffUtc &&
                        !IsProtectedPath(entry, gtaRootPath, activePlanStagingDirectory, activeModIds, referencedStashFiles))
                    {
                        tempTargets.Add(entry);
                        tempBytes += ComputePathSizeBytes(entry);
                    }
                }
            }
            catch
            {
                // Ignore locked system temp entries
            }
        }

        items.Add(new CleanupCategoryItem
        {
            Kind = CleanupCategoryKind.ArchiveTempLeftovers,
            Title = "Stale Archive & OpenIV Temp Leftovers (%TEMP%)",
            Description = "Old RAR / 7-Zip / OpenIV temporary extraction folders left behind in Windows Temp.",
            SafetyGuarantee = "Only cleans modding archive temp prefixes older than 30 minutes; skips any file in use.",
            ItemCount = tempTargets.Count,
            EstimatedReclaimableBytes = tempBytes,
            TargetPaths = tempTargets,
            IsSelected = true
        });

        // 5. SQLite WAL & Database Compaction
        long walBytes = 0;
        string walPath = _paths.DatabasePath + "-wal";
        if (File.Exists(walPath))
        {
            try { walBytes = new FileInfo(walPath).Length; } catch { /* ignore */ }
        }

        items.Add(new CleanupCategoryItem
        {
            Kind = CleanupCategoryKind.DatabaseWalBloat,
            Title = "Compact Mod History Database (SQLite VACUUM)",
            Description = "Checkpoints SQLite WAL journal and compacts unused database pages.",
            SafetyGuarantee = "100% lossless — keeps all installed mod records, profiles, backups, and logs intact.",
            ItemCount = 1,
            EstimatedReclaimableBytes = walBytes,
            TargetPaths = Array.Empty<string>(),
            IsSelected = true
        });

        // 6. Live Idle RAM Working-Set Trim (User-Space Only — Zero System Memory/Boot Changes)
        int eligibleIdleApps = CountEligibleUserProcessesForRamTrim();
        items.Add(new CleanupCategoryItem
        {
            Kind = CleanupCategoryKind.IdleRamWorkingSet,
            Title = "Trim Idle App & Background RAM Working Sets",
            Description = "Frees cached .NET Large Object Heap memory and asks Windows to trim idle pages from user apps before playing GTA V.",
            SafetyGuarantee = "Closes 0 apps and NEVER changes Windows boot config, pagefile, or system memory settings.",
            ItemCount = eligibleIdleApps,
            EstimatedReclaimableBytes = 0,
            TargetPaths = Array.Empty<string>(),
            IsSelected = true
        });

        return items;
    }

    public CleanupExecutionResult ExecuteCleanup(
        IReadOnlyList<CleanupCategoryItem> categories,
        string? gtaRootPath,
        string? activePlanStagingDirectory = null)
    {
        var memBefore = CaptureMemorySnapshot(gtaRootPath);

        var activeModIds = _repository.GetInstalledMods()
            .Select(m => m.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var referencedStashFiles = _repository.GetModFiles(onlyEnabledMods: false)
            .SelectMany(f => new[] { f.PreviousFileStashPath, f.DisabledStashPath })
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => NormalizeFullPath(p!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int deletedItems = 0;
        long reclaimedDiskBytes = 0;
        int skippedLocked = 0;
        int trimmedProcs = 0;

        foreach (var cat in categories.Where(c => c.IsSelected))
        {
            if (cat.Kind is CleanupCategoryKind.StagingCache
                or CleanupCategoryKind.OrphanedModStash
                or CleanupCategoryKind.CrashDumps
                or CleanupCategoryKind.ArchiveTempLeftovers)
            {
                foreach (var target in cat.TargetPaths)
                {
                    if (IsProtectedPath(target, gtaRootPath, activePlanStagingDirectory, activeModIds, referencedStashFiles))
                    {
                        skippedLocked++;
                        continue;
                    }

                    long sizeBefore = ComputePathSizeBytes(target);
                    try
                    {
                        if (Directory.Exists(target))
                        {
                            Directory.Delete(target, recursive: true);
                            deletedItems++;
                            reclaimedDiskBytes += sizeBefore;
                        }
                        else if (File.Exists(target))
                        {
                            File.Delete(target);
                            deletedItems++;
                            reclaimedDiskBytes += sizeBefore;
                        }
                    }
                    catch
                    {
                        skippedLocked++;
                    }
                }
            }
            else if (cat.Kind == CleanupCategoryKind.DatabaseWalBloat)
            {
                try
                {
                    reclaimedDiskBytes += _repository.CompactDatabase();
                }
                catch
                {
                    // Ignore busy DB compaction error
                }
            }
            else if (cat.Kind == CleanupCategoryKind.IdleRamWorkingSet)
            {
                trimmedProcs = TrimIdleRamWorkingSets();
            }
        }

        // Ensure staging root directory still exists cleanly after wiping subfolders
        Directory.CreateDirectory(_paths.StagingDirectory);

        var memAfter = CaptureMemorySnapshot(gtaRootPath);
        long freedRamBytes = Math.Max(0L, memAfter.AvailablePhysicalBytes - memBefore.AvailablePhysicalBytes);

        string diskPart = SystemMemorySnapshot.FormatSize(reclaimedDiskBytes);
        string ramPart = freedRamBytes > 0
            ? $"{SystemMemorySnapshot.FormatSize(freedRamBytes)} physical RAM freed"
            : $"trimmed {trimmedProcs} app working set(s)";

        string summary = $"Cleaned {diskPart} of disk junk ({deletedItems} item(s)) + {ramPart} — 0 mod files or system settings touched.";

        return new CleanupExecutionResult(
            deletedItems,
            reclaimedDiskBytes,
            trimmedProcs,
            freedRamBytes,
            skippedLocked,
            memBefore,
            memAfter,
            summary);
    }

    /// <summary>
    /// Compacts GTA Mod Assistant's own managed heap and, on Windows, requests standard user-mode working-set
    /// trimming (EmptyWorkingSet) on idle user-owned processes without closing any apps or altering system settings.
    /// </summary>
    public int TrimIdleRamWorkingSets()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        int trimmedCount = 1; // Current managed process compacted

        if (!OperatingSystem.IsWindows())
        {
            return trimmedCount;
        }

        try
        {
            using var self = Process.GetCurrentProcess();
            _ = EmptyWorkingSet(self.Handle);
            int currentSession = self.SessionId;

            foreach (var proc in Process.GetProcesses())
            {
                using (proc)
                {
                    try
                    {
                        if (proc.Id == self.Id || proc.SessionId != currentSession)
                        {
                            continue;
                        }

                        if (ProtectedProcessNames.Contains(proc.ProcessName))
                        {
                            continue;
                        }

                        // Only trim processes holding >= 32 MB working set that our standard user token owns
                        if (proc.WorkingSet64 < 32L * 1024 * 1024)
                        {
                            continue;
                        }

                        if (EmptyWorkingSet(proc.Handle))
                        {
                            trimmedCount++;
                        }
                    }
                    catch
                    {
                        // Normal access denied on elevated/system processes — skip silently
                    }
                }
            }
        }
        catch
        {
            // Ignore process enumeration issues
        }

        return trimmedCount;
    }

    private static int CountEligibleUserProcessesForRamTrim()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 1;
        }

        int count = 1;
        try
        {
            using var self = Process.GetCurrentProcess();
            int currentSession = self.SessionId;
            foreach (var proc in Process.GetProcesses())
            {
                using (proc)
                {
                    try
                    {
                        if (proc.Id != self.Id &&
                            proc.SessionId == currentSession &&
                            !ProtectedProcessNames.Contains(proc.ProcessName) &&
                            proc.WorkingSet64 >= 32L * 1024 * 1024)
                        {
                            count++;
                        }
                    }
                    catch
                    {
                        // Ignore inaccessible processes
                    }
                }
            }
        }
        catch
        {
            // Ignore enumeration errors
        }

        return count;
    }

    private static (long TotalBytes, long AvailableBytes) QueryPhysicalMemory()
    {
        if (OperatingSystem.IsWindows())
        {
            var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref memStatus))
            {
                return ((long)memStatus.ullTotalPhys, (long)memStatus.ullAvailPhys);
            }
        }
        else if (File.Exists("/proc/meminfo"))
        {
            try
            {
                long totalKb = 0;
                long availKb = 0;
                foreach (var line in File.ReadAllLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                    {
                        totalKb = ParseMemInfoKb(line);
                    }
                    else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    {
                        availKb = ParseMemInfoKb(line);
                    }
                }
                if (totalKb > 0)
                {
                    return (totalKb * 1024L, availKb * 1024L);
                }
            }
            catch
            {
                // Ignore /proc read errors
            }
        }

        var gcInfo = GC.GetGCMemoryInfo();
        return (gcInfo.TotalAvailableMemoryBytes, Math.Max(0L, gcInfo.TotalAvailableMemoryBytes - Environment.WorkingSet));
    }

    private static long ParseMemInfoKb(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out long kb) ? kb : 0L;
    }

    private static long ComputePathSizeBytes(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return new FileInfo(path).Length;
            }

            if (Directory.Exists(path))
            {
                long sum = 0;
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { sum += new FileInfo(file).Length; } catch { /* ignore */ }
                }
                return sum;
            }
        }
        catch
        {
            // Ignore inaccessible entry
        }

        return 0L;
    }

    private static string NormalizeFullPath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsSameOrSubPath(string candidateFullPath, string parentFullPath)
    {
        if (string.Equals(candidateFullPath, parentFullPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string withSep = parentFullPath + Path.DirectorySeparatorChar;
        return candidateFullPath.StartsWith(withSep, StringComparison.OrdinalIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);
}
