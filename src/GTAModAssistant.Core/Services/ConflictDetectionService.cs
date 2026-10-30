using GTAModAssistant.Core.Models;
using GTAModAssistant.Core.Security;

namespace GTAModAssistant.Core.Services;

public static class ConflictDetectionService
{
    public static string BuildStepConflictKey(ModInstallStep step)
    {
        if (step.RequiresUserTargetSelection)
        {
            return $"unresolved::{Normalize(step.SourceRelativePath)}";
        }

        return step.Operation switch
        {
            InstallOperationKind.InjectIntoRpfArchive =>
                $"rpf::{Normalize(step.RpfContainerRelativePath)}::{Normalize(step.RpfInternalPath)}",
            InstallOperationKind.RegisterDlcListEntry =>
                $"dlc::{step.DlcListEntry.Trim().ToLowerInvariant()}",
            _ =>
                $"fs::{Normalize(step.TargetRelativePath)}"
        };
    }

    public static IReadOnlyList<ConflictItem> FindConflicts(
        IReadOnlyList<ModInstallStep> plannedSteps,
        IReadOnlyList<InstalledModFileRecord> activeInstalledFiles,
        string? gtaRootPath,
        IReadOnlyDictionary<string, string>? installedModNamesById = null)
    {
        var selectedSteps = plannedSteps.Where(s => s.IsSelectedForInstall).ToList();
        var conflicts = new List<ConflictItem>();

        // 1. Intra-package duplicate filenames or duplicate target RPF entries!
        var seenByTargetKey = new Dictionary<string, ModInstallStep>(StringComparer.OrdinalIgnoreCase);
        var byFileNameForAssets = selectedSteps
            .Where(s => s.Operation == InstallOperationKind.InjectIntoRpfArchive && !string.IsNullOrWhiteSpace(s.FileName))
            .GroupBy(s => s.FileName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var step in selectedSteps)
        {
            if (!step.RequiresUserTargetSelection)
            {
                string targetKey = BuildStepConflictKey(step);
                if (seenByTargetKey.TryGetValue(targetKey, out var earlierStep))
                {
                    conflicts.Add(new ConflictItem(
                        step.StepId,
                        targetKey,
                        "same-package",
                        "Duplicate in Imported Mod",
                        ExistsOnDiskUnmanaged: false,
                        $"Duplicate replacement inside this mod: '{step.SourceRelativePath}' and '{earlierStep.SourceRelativePath}' both target '{DescribeTarget(step)}'.",
                        ConflictKind.DuplicateFileInPackage));
                    continue;
                }
                seenByTargetKey[targetKey] = step;
            }

            // Even if target RPF is still unresolved (or resolved differently without explicit folder), warn when multiple .yft/.ytd share the exact same filename!
            if (step.Operation == InstallOperationKind.InjectIntoRpfArchive &&
                byFileNameForAssets.TryGetValue(step.FileName, out var dupNameGroup) &&
                dupNameGroup[0].StepId != step.StepId)
            {
                string key = BuildStepConflictKey(step);
                if (!conflicts.Any(c => c.StepId == step.StepId && c.Kind == ConflictKind.DuplicateFileInPackage))
                {
                    conflicts.Add(new ConflictItem(
                        step.StepId,
                        key,
                        "same-package",
                        "Duplicate Filename in Imported Mod",
                        ExistsOnDiskUnmanaged: false,
                        $"Duplicate filename '{step.FileName}' detected in imported mod ('{step.SourceRelativePath}' vs '{dupNameGroup[0].SourceRelativePath}'). Uncheck unwanted variant or keep first.",
                        ConflictKind.DuplicateFileInPackage));
                }
            }
        }

        // 2. Conflicts against already-installed active mods
        var activeByKey = activeInstalledFiles
            .GroupBy(f => f.ConflictKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        foreach (var step in selectedSteps)
        {
            if (step.RequiresUserTargetSelection)
            {
                continue;
            }

            string key = BuildStepConflictKey(step);
            if (activeByKey.TryGetValue(key, out var existingRecord))
            {
                string modName = installedModNamesById is not null &&
                                 installedModNamesById.TryGetValue(existingRecord.ModId, out var resolvedName)
                    ? resolvedName
                    : existingRecord.ModId;

                conflicts.Add(new ConflictItem(
                    step.StepId,
                    key,
                    existingRecord.ModId,
                    modName,
                    ExistsOnDiskUnmanaged: false,
                    $"Conflicts with installed mod '{modName}' ({existingRecord.ModId}) at '{DescribeTarget(step)}'.",
                    ConflictKind.InstalledModConflict));
                continue;
            }

            // 3. Unmanaged pre-existing non-RPF files on disk
            if (step.Operation == InstallOperationKind.CopyFileToModsOrRoot &&
                !string.IsNullOrWhiteSpace(gtaRootPath) &&
                Directory.Exists(gtaRootPath))
            {
                try
                {
                    string diskPath = PathSafetyGuard.ResolveSafeChildPath(gtaRootPath, step.TargetRelativePath);
                    if (File.Exists(diskPath))
                    {
                        conflicts.Add(new ConflictItem(
                            step.StepId,
                            key,
                            "unmanaged",
                            "Existing Unmanaged File",
                            ExistsOnDiskUnmanaged: true,
                            $"Target file '{step.TargetRelativePath}' already exists on disk (installed outside GTA Mod Assistant).",
                            ConflictKind.ExistingUnmanagedFileOnDisk));
                    }
                }
                catch
                {
                    // PathSafetyGuard blocks unsafe paths separately
                }
            }
        }

        return conflicts;
    }

    public static IReadOnlyList<ModInstallStep> EnrichStepsWithPreviewMetadata(
        IReadOnlyList<ModInstallStep> steps,
        IReadOnlyList<ConflictItem> conflicts,
        GameRpfSearchService? rpfIndex,
        string? gtaRootPath)
    {
        var conflictsByStepId = conflicts
            .GroupBy(c => c.StepId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var enriched = new List<ModInstallStep>(steps.Count);
        foreach (var step in steps)
        {
            if (step.RequiresUserTargetSelection)
            {
                string status = conflictsByStepId.TryGetValue(step.StepId, out var uConf)
                    ? string.Join(" | ", uConf.Select(c => c.Description))
                    : "Target archive uncertain — select RPF before installing";
                enriched.Add(step with
                {
                    ReplacesExistingEntry = false,
                    ConflictStatusPreview = status
                });
                continue;
            }

            bool replacesExisting = false;
            if (step.Operation == InstallOperationKind.InjectIntoRpfArchive && rpfIndex is not null)
            {
                replacesExisting = rpfIndex.ContainsExactEntry(step.RpfContainerRelativePath, step.RpfInternalPath);
            }
            else if (step.Operation == InstallOperationKind.CopyFileToModsOrRoot &&
                     !string.IsNullOrWhiteSpace(gtaRootPath) &&
                     Directory.Exists(gtaRootPath))
            {
                try
                {
                    replacesExisting = File.Exists(PathSafetyGuard.ResolveSafeChildPath(gtaRootPath, step.TargetRelativePath));
                }
                catch
                {
                    replacesExisting = false;
                }
            }

            string previewStatus;
            if (conflictsByStepId.TryGetValue(step.StepId, out var stepConflicts))
            {
                var dup = stepConflicts.FirstOrDefault(c => c.Kind == ConflictKind.DuplicateFileInPackage);
                var modConf = stepConflicts.FirstOrDefault(c => c.Kind == ConflictKind.InstalledModConflict);
                if (dup is not null && modConf is not null)
                {
                    previewStatus = $"DUPLICATE + CONFLICT ({modConf.ExistingModName})";
                }
                else if (dup is not null)
                {
                    previewStatus = $"DUPLICATE filename '{step.FileName}' in mod";
                }
                else if (modConf is not null)
                {
                    previewStatus = $"CONFLICT with '{modConf.ExistingModName}'";
                }
                else
                {
                    previewStatus = "CONFLICT with existing unmanaged file";
                }
            }
            else if (replacesExisting)
            {
                previewStatus = "Replaces original entry (auto-backed up)";
            }
            else
            {
                previewStatus = "New entry (clean)";
            }

            enriched.Add(step with
            {
                ReplacesExistingEntry = replacesExisting,
                ConflictStatusPreview = previewStatus
            });
        }

        return enriched;
    }

    private static string DescribeTarget(ModInstallStep step) =>
        step.Operation switch
        {
            InstallOperationKind.InjectIntoRpfArchive => $"{step.RpfContainerRelativePath} -> {step.RpfInternalPath}",
            InstallOperationKind.RegisterDlcListEntry => $"dlclist.xml ({step.DlcListEntry})",
            _ => step.TargetRelativePath
        };

    private static string Normalize(string p) =>
        (p ?? string.Empty).Replace('\\', '/').Trim('/').ToLowerInvariant();
}
