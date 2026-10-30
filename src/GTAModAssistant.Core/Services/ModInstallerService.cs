using System.Text.Json;
using GTAModAssistant.Core.Archives;
using GTAModAssistant.Core.Models;
using GTAModAssistant.Core.Security;

namespace GTAModAssistant.Core.Services;

public sealed class ModInstallerService
{
    private readonly SqliteHistoryRepository _repository;
    private readonly BackupService _backupService;
    private readonly string _stashRootDirectory;
    private readonly string _generatedOivDirectory;
    private readonly IOpenIvPackageInstallerAutomation? _openIvPackageInstallerAutomation;

    public ModInstallerService(
        SqliteHistoryRepository repository,
        BackupService backupService,
        string appDataDirectory,
        IOpenIvPackageInstallerAutomation? openIvPackageInstallerAutomation = null)
    {
        _repository = repository;
        _backupService = backupService;
        _openIvPackageInstallerAutomation = openIvPackageInstallerAutomation;
        _stashRootDirectory = Path.Combine(appDataDirectory, "conflict_stash");
        _generatedOivDirectory = Path.Combine(appDataDirectory, "generated_oiv");
        Directory.CreateDirectory(_stashRootDirectory);
        Directory.CreateDirectory(_generatedOivDirectory);
    }

    public async Task<InstallExecutionResult> InstallAsync(
        ModInstallationPlan plan,
        GameEnvironmentInfo gameEnv,
        ConflictResolutionStrategy conflictStrategy = ConflictResolutionStrategy.BackupAndOverwrite,
        bool autoLaunchOpenIvForEncryptedRpfs = false,
        IProgress<InstallProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string cid = "inst_" + Guid.NewGuid().ToString("N")[..8];

        if (!gameEnv.IsDetected || !GameDetectionService.IsValidGtaDirectory(gameEnv.GtaRootPath))
        {
            _repository.AppendLog(AuditLogLevel.Error, "Installer", "Installation blocked: valid GTA V installation not detected.", gameEnv.GtaRootPath, cid);
            return new InstallExecutionResult
            {
                Succeeded = false,
                VerificationState = InstallVerificationState.FailedVerification,
                SummaryMessage = "Cannot install: valid GTA V directory with GTA5.exe is not configured."
            };
        }

        if (!plan.CanInstall)
        {
            _repository.AppendLog(AuditLogLevel.Security, "Installer", $"Installation blocked by pre-flight safety or compatibility checks for '{plan.ModName}'.", plan.Compatibility.Headline, cid);
            return new InstallExecutionResult
            {
                Succeeded = false,
                VerificationState = InstallVerificationState.FailedVerification,
                SummaryMessage = $"Installation blocked: {plan.Compatibility.Headline}"
            };
        }

        // Enforce explicit user confirmation for uncertain RPF targets — NEVER guess!
        var selectedSteps = plan.Steps.Where(s => s.IsSelectedForInstall).ToList();
        var unresolvedSteps = selectedSteps.Where(s => s.RequiresUserTargetSelection).ToList();
        if (unresolvedSteps.Count > 0)
        {
            string unresolvedNames = string.Join(", ", unresolvedSteps.Select(s => s.FileName));
            _repository.AppendLog(
                AuditLogLevel.Warning,
                "Installer",
                $"Installation halted: {unresolvedSteps.Count} file(s) have uncertain RPF destinations ({unresolvedNames}).",
                "User must select the target RPF archive before installation proceeds.",
                cid);
            return new InstallExecutionResult
            {
                Succeeded = false,
                VerificationState = InstallVerificationState.FailedVerification,
                SummaryMessage =
                    $"Installation halted: uncertain target RPF archive for {unresolvedSteps.Count} file(s) ({unresolvedNames}). " +
                    "Please choose the intended RPF archive instead of guessing."
            };
        }

        // Re-check conflicts live against current DB state
        var activeFiles = _repository.GetModFiles(onlyEnabledMods: true);
        var modNamesById = _repository.GetInstalledMods()
            .ToDictionary(m => m.Id, m => m.Name, StringComparer.OrdinalIgnoreCase);
        var liveConflicts = ConflictDetectionService.FindConflicts(selectedSteps, activeFiles, gameEnv.GtaRootPath, modNamesById);
        if (liveConflicts.Count > 0 && conflictStrategy == ConflictResolutionStrategy.CancelInstall)
        {
            _repository.AppendLog(AuditLogLevel.Info, "Installer", $"User cancelled installation of '{plan.ModName}' due to {liveConflicts.Count} conflict(s).", "", cid);
            return new InstallExecutionResult
            {
                Succeeded = false,
                VerificationState = InstallVerificationState.Cancelled,
                SummaryMessage = "Installation cancelled cleanly with zero changes."
            };
        }

        var conflictStepIds = new HashSet<string>(liveConflicts.Select(c => c.StepId), StringComparer.OrdinalIgnoreCase);
        var duplicateOnlyStepIds = new HashSet<string>(
            liveConflicts.Where(c => c.Kind == ConflictKind.DuplicateFileInPackage).Select(c => c.StepId),
            StringComparer.OrdinalIgnoreCase);

        var stepsToRun = conflictStrategy switch
        {
            ConflictResolutionStrategy.SkipConflictingSteps =>
                selectedSteps.Where(s => !conflictStepIds.Contains(s.StepId)).ToList(),
            ConflictResolutionStrategy.KeepFirstDuplicateOnly =>
                selectedSteps.Where(s => !duplicateOnlyStepIds.Contains(s.StepId)).ToList(),
            _ =>
                selectedSteps
        };

        if (stepsToRun.Count == 0)
        {
            return new InstallExecutionResult
            {
                Succeeded = false,
                VerificationState = InstallVerificationState.Cancelled,
                SummaryMessage = "All installation steps were skipped due to conflict resolution settings."
            };
        }

        string modId = $"mod_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}";
        string modStashDir = Path.Combine(_stashRootDirectory, modId);
        Directory.CreateDirectory(modStashDir);

        _repository.AppendLog(
            AuditLogLevel.Info,
            "Installer",
            $"Starting verified installation of '{plan.ModName}' v{plan.ModVersion} ({stepsToRun.Count} step(s))",
            $"GTA V Root: {gameEnv.GtaRootPath}",
            cid);

        progress?.Report(new InstallProgressReport(8, "Preparing mods/ Copy-on-Write", "Ensuring vanilla Rockstar archives are safely mirrored into mods/..."));

        // 1. Copy-on-write any needed RPF archives from vanilla GTA V into mods/ BEFORE backup/modification!
        foreach (var rpfStep in stepsToRun.Where(s => s.Operation is InstallOperationKind.InjectIntoRpfArchive or InstallOperationKind.RegisterDlcListEntry))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string modsRpfRel = PathSafetyGuard.EnsureSafeModsPath(
                string.IsNullOrWhiteSpace(rpfStep.RpfContainerRelativePath)
                    ? "mods/update/update.rpf"
                    : rpfStep.RpfContainerRelativePath);

            EnsureModsRpfCopyOnWrite(gameEnv.GtaRootPath, modsRpfRel, cid);
        }

        // 2. Create verified pre-install backup (skipping multi-GB RPF7 containers > 64 MB, whose modified entries are stashed & SHA-256 verified individually!)
        const long MaxRpfBytesForFullZipSnapshot = 64L * 1024 * 1024;
        progress?.Report(new InstallProgressReport(20, "Creating Verified Backup", "Capturing SHA-256 pre-install backup..."));
        var targetPathsForBackup = stepsToRun
            .Select(s => s.Operation is InstallOperationKind.InjectIntoRpfArchive or InstallOperationKind.RegisterDlcListEntry
                ? PathSafetyGuard.EnsureSafeModsPath(string.IsNullOrWhiteSpace(s.RpfContainerRelativePath) ? "mods/update/update.rpf" : s.RpfContainerRelativePath)
                : PathSafetyGuard.NormalizeRelativePath(s.TargetRelativePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(rel =>
            {
                if (!rel.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                string full = PathSafetyGuard.ResolveSafeChildPath(gameEnv.GtaRootPath, rel);
                return !File.Exists(full) || new FileInfo(full).Length <= MaxRpfBytesForFullZipSnapshot;
            })
            .ToList();

        BackupRecord backupRecord = _backupService.CreateVerifiedBackup(
            gameEnv.GtaRootPath,
            $"Pre-Install: {plan.ModName} v{plan.ModVersion}",
            "PreInstall",
            targetPathsForBackup,
            newlyCreatedPathsToTrack: null,
            correlationId: cid);

        var fileLedger = new List<InstalledModFileRecord>();
        var deferredEncryptedRpfSteps = new List<ModInstallStep>();
        var verificationProofs = new List<string>
        {
            $"Pre-install backup {backupRecord.Id} SHA-256 verified."
        };

        try
        {
            int stepIndex = 0;
            foreach (var step in stepsToRun)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stepIndex++;
                int pct = 30 + (int)(50.0 * stepIndex / stepsToRun.Count);
                progress?.Report(new InstallProgressReport(pct, "Executing Plan", step.Description));

                if (step.Operation == InstallOperationKind.CopyFileToModsOrRoot)
                {
                    if (PathSafetyGuard.IsProtectedVanillaRootFile(step.TargetRelativePath))
                    {
                        throw new InvalidOperationException(
                            $"Safety violation: refused to overwrite protected vanilla Rockstar file '{step.TargetRelativePath}'.");
                    }

                    string destFull = PathSafetyGuard.ResolveSafeChildPath(gameEnv.GtaRootPath, step.TargetRelativePath);
                    bool existedBefore = File.Exists(destFull);
                    string? shaBefore = existedBefore ? HashVerifier.ComputeFileSha256(destFull) : null;
                    string? prevStashPath = null;

                    if (existedBefore)
                    {
                        prevStashPath = Path.Combine(modStashDir, $"{stepIndex:D3}_{Path.GetFileName(destFull)}");
                        File.Copy(destFull, prevStashPath, overwrite: true);
                        string stashSha = HashVerifier.ComputeFileSha256(prevStashPath);
                        if (!string.Equals(stashSha, shaBefore, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException($"Stash copy SHA-256 verification failed for '{step.TargetRelativePath}'.");
                        }
                    }

                    string expectedSha = HashVerifier.ComputeFileSha256(step.SourceFullPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);

                    string tmpDest = destFull + ".gtama_write_tmp";
                    File.Copy(step.SourceFullPath, tmpDest, overwrite: true);
                    if (!HashVerifier.VerifyFileSha256(tmpDest, expectedSha, out string actualSha))
                    {
                        File.Delete(tmpDest);
                        throw new InvalidDataException($"Disk write SHA-256 mismatch for '{step.TargetRelativePath}' (got {actualSha}).");
                    }

                    if (destFull.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase) &&
                        !Rpf7Archive.InspectHeader(tmpDest).IsValidRpf7)
                    {
                        File.Delete(tmpDest);
                        throw new InvalidDataException($"Target '{step.TargetRelativePath}' failed RPF7 binary header verification.");
                    }

                    File.Move(tmpDest, destFull, overwrite: true);

                    fileLedger.Add(new InstalledModFileRecord
                    {
                        ModId = modId,
                        Operation = InstallOperationKind.CopyFileToModsOrRoot,
                        TargetRelativePath = PathSafetyGuard.NormalizeRelativePath(step.TargetRelativePath),
                        ExistedBefore = existedBefore,
                        Sha256Before = shaBefore,
                        Sha256After = actualSha,
                        PreviousFileStashPath = prevStashPath
                    });

                    verificationProofs.Add($"Verified '{step.TargetRelativePath}' SHA-256 = {actualSha[..12]}...");
                }
                else if (step.Operation == InstallOperationKind.InjectIntoRpfArchive)
                {
                    string safeRpfRel = PathSafetyGuard.EnsureSafeModsPath(step.RpfContainerRelativePath);
                    string rpfFull = PathSafetyGuard.ResolveSafeChildPath(gameEnv.GtaRootPath, safeRpfRel);
                    var hdr = Rpf7Archive.InspectHeader(rpfFull);

                    if (!hdr.IsValidRpf7)
                    {
                        throw new InvalidDataException($"Target container '{safeRpfRel}' is not a valid RPF7 archive.");
                    }

                    if (hdr.CanDirectEditWithoutNgKeys)
                    {
                        var rpf = Rpf7Archive.Open(rpfFull);
                        string innerNorm = Rpf7Archive.NormalizeInternalPath(step.RpfInternalPath);
                        bool existedInRpf = rpf.ContainsFile(innerNorm);
                        string? shaBefore = null;
                        string? prevStashPath = null;

                        if (existedInRpf)
                        {
                            if (rpf.IsEntryIndividuallyEncrypted(innerNorm))
                            {
                                var opaqueBlob = rpf.ExportOpaqueEncryptedEntry(innerNorm)
                                    ?? throw new InvalidDataException($"Unable to export encrypted backup blob for '{innerNorm}'.");
                                byte[] serializedBlob = JsonSerializer.SerializeToUtf8Bytes(opaqueBlob);
                                shaBefore = HashVerifier.ComputeBytesSha256(serializedBlob);
                                prevStashPath = Path.Combine(modStashDir, $"{stepIndex:D3}_rpf_{Path.GetFileName(innerNorm)}.gtama_opaque.json");
                                await File.WriteAllBytesAsync(prevStashPath, serializedBlob, cancellationToken);
                            }
                            else
                            {
                                byte[] prevBytes = rpf.ExtractFile(innerNorm);
                                shaBefore = HashVerifier.ComputeBytesSha256(prevBytes);
                                prevStashPath = Path.Combine(modStashDir, $"{stepIndex:D3}_rpf_{Path.GetFileName(innerNorm)}");
                                await File.WriteAllBytesAsync(prevStashPath, prevBytes, cancellationToken);
                            }

                            if (!HashVerifier.VerifyFileSha256(prevStashPath, shaBefore, out _))
                            {
                                throw new InvalidDataException($"Per-entry backup SHA-256 verification failed before replacing '{safeRpfRel} -> {innerNorm}'.");
                            }
                        }

                        byte[] sourceBytes = await File.ReadAllBytesAsync(step.SourceFullPath, cancellationToken);
                        string expectedInnerSha = HashVerifier.ComputeBytesSha256(sourceBytes);

                        if (Path.GetExtension(innerNorm).ToLowerInvariant() is ".xml" or ".meta")
                        {
                            if (!DlcListXmlService.ValidateGtaXml(sourceBytes, out string? xmlErr))
                            {
                                throw new InvalidDataException($"Refused to inject invalid XML '{innerNorm}': {xmlErr}");
                            }
                        }

                        rpf.PutFile(innerNorm, sourceBytes, compressIfBinary: false);
                        rpf.SaveAndVerify(rpfFull);

                        // Post-write RPF7 read-back verification!
                        var verifyRpf = Rpf7Archive.Open(rpfFull);
                        byte[] readBack = verifyRpf.ExtractFile(innerNorm);
                        string actualInnerSha = HashVerifier.ComputeBytesSha256(readBack);
                        if (!string.Equals(actualInnerSha, expectedInnerSha, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException($"RPF7 post-write SHA-256 verification failed for '{safeRpfRel} -> {innerNorm}'.");
                        }

                        fileLedger.Add(new InstalledModFileRecord
                        {
                            ModId = modId,
                            Operation = InstallOperationKind.InjectIntoRpfArchive,
                            TargetRelativePath = safeRpfRel,
                            RpfContainerRelativePath = safeRpfRel,
                            RpfInternalPath = innerNorm,
                            ExistedBefore = existedInRpf,
                            Sha256Before = shaBefore,
                            Sha256After = actualInnerSha,
                            PreviousFileStashPath = prevStashPath
                        });

                        verificationProofs.Add($"Verified RPF7 entry '{safeRpfRel} -> {innerNorm}' SHA-256 = {actualInnerSha[..12]}...");
                    }
                    else
                    {
                        deferredEncryptedRpfSteps.Add(step);
                    }
                }
                else if (step.Operation == InstallOperationKind.RegisterDlcListEntry)
                {
                    string safeUpdateRel = "mods/update/update.rpf";
                    string updateFull = PathSafetyGuard.ResolveSafeChildPath(gameEnv.GtaRootPath, safeUpdateRel);
                    var hdr = Rpf7Archive.InspectHeader(updateFull);

                    if (hdr.CanDirectEditWithoutNgKeys)
                    {
                        var probeRpf = Rpf7Archive.Open(updateFull);
                        if (probeRpf.ContainsFile(DlcListXmlService.RpfInternalDlcListPath) &&
                            probeRpf.IsEntryIndividuallyEncrypted(DlcListXmlService.RpfInternalDlcListPath))
                        {
                            deferredEncryptedRpfSteps.Add(step);
                        }
                        else
                        {
                            var outcome = DlcListXmlService.UpdateOpenUpdateRpfDlcList(updateFull, step.DlcListEntry, remove: false);
                            string xmlSha = HashVerifier.ComputeBytesSha256(System.Text.Encoding.UTF8.GetBytes(outcome.ValidatedXmlContent));

                            fileLedger.Add(new InstalledModFileRecord
                            {
                                ModId = modId,
                                Operation = InstallOperationKind.RegisterDlcListEntry,
                                TargetRelativePath = safeUpdateRel,
                                RpfContainerRelativePath = safeUpdateRel,
                                RpfInternalPath = DlcListXmlService.RpfInternalDlcListPath,
                                DlcListEntry = outcome.CanonicalEntry,
                                ExistedBefore = outcome.AlreadyPresent,
                                Sha256After = xmlSha
                            });

                            verificationProofs.Add($"Verified dlclist.xml registration '{outcome.CanonicalEntry}' inside OPEN RPF7 (XML valid, no duplicates).");
                        }
                    }
                    else
                    {
                        deferredEncryptedRpfSteps.Add(step);
                    }
                }
            }

            // Handle any NG/AES-encrypted RPF7 targets via OpenIV .oiv package generation + optional OpenIV execution
            string? generatedOivPath = null;
            var verificationState = InstallVerificationState.VerifiedComplete;

            if (deferredEncryptedRpfSteps.Count > 0)
            {
                progress?.Report(new InstallProgressReport(85, "Building OpenIV Package", "Generating OpenIV v2.1 .oiv package for NG-encrypted RPF7 container(s)..."));
                generatedOivPath = Path.Combine(_generatedOivDirectory, $"{modId}_{plan.ModSlug}.oiv");
                OivPackageService.GenerateOivPackage(
                    generatedOivPath,
                    plan.ModName,
                    plan.ModVersion,
                    plan.Author,
                    deferredEncryptedRpfSteps);

                foreach (var defStep in deferredEncryptedRpfSteps)
                {
                    string safeRpfRel = PathSafetyGuard.EnsureSafeModsPath(
                        string.IsNullOrWhiteSpace(defStep.RpfContainerRelativePath) ? "mods/update/update.rpf" : defStep.RpfContainerRelativePath);
                    string srcSha = !string.IsNullOrEmpty(defStep.SourceFullPath) && File.Exists(defStep.SourceFullPath)
                        ? HashVerifier.ComputeFileSha256(defStep.SourceFullPath)
                        : HashVerifier.ComputeBytesSha256(System.Text.Encoding.UTF8.GetBytes(defStep.DlcListEntry));

                    fileLedger.Add(new InstalledModFileRecord
                    {
                        ModId = modId,
                        Operation = defStep.Operation,
                        TargetRelativePath = safeRpfRel,
                        RpfContainerRelativePath = safeRpfRel,
                        RpfInternalPath = defStep.RpfInternalPath,
                        DlcListEntry = defStep.DlcListEntry,
                        ExistedBefore = false,
                        Sha256After = srcSha
                    });
                }

                if (autoLaunchOpenIvForEncryptedRpfs && gameEnv.OpenIvInstalled)
                {
                    progress?.Report(new InstallProgressReport(
                        92,
                        "Installing Encrypted RPF Automatically",
                        "OpenIV is running in the background; no clicks are needed while the package is verified."));
                    var targetRpfFulls = deferredEncryptedRpfSteps
                        .Select(s => PathSafetyGuard.ResolveSafeChildPath(
                            gameEnv.GtaRootPath,
                            PathSafetyGuard.EnsureSafeModsPath(string.IsNullOrWhiteSpace(s.RpfContainerRelativePath) ? "mods/update/update.rpf" : s.RpfContainerRelativePath)))
                        .Distinct(StringComparer.OrdinalIgnoreCase);

                    var oivVerify = await OivPackageService.LaunchOpenIvAndVerifyAsync(
                        gameEnv.OpenIvExecutablePath,
                        generatedOivPath,
                        targetRpfFulls,
                        cancellationToken,
                        _openIvPackageInstallerAutomation,
                        plan.ModName);

                    if (oivVerify.VerifiedChanged)
                    {
                        verificationState = InstallVerificationState.VerifiedComplete;
                        verificationProofs.Add(oivVerify.Details);
                    }
                    else
                    {
                        verificationState = InstallVerificationState.PendingOpenIvCommit;
                        verificationProofs.Add($"Staged OpenIV package '{generatedOivPath}'. Status: {oivVerify.Details}");
                    }
                }
                else
                {
                    verificationState = InstallVerificationState.PendingOpenIvCommit;
                    verificationProofs.Add(
                        $"Target RPF7 uses Rockstar NG encryption; generated verified OpenIV package '{generatedOivPath}' instead of risking binary corruption. Run OpenIV Package Installer to commit encrypted RPF edits.");
                }
            }

            var now = DateTimeOffset.UtcNow;
            var modRecord = new InstalledModRecord
            {
                Id = modId,
                Slug = plan.ModSlug,
                Name = plan.ModName,
                Version = plan.ModVersion,
                Author = plan.Author,
                SourcePath = plan.SourcePath,
                Category = plan.Category,
                TargetGameBuild = plan.Compatibility.ModTargetBuild,
                VerificationState = verificationState,
                IsEnabled = true,
                BackupId = backupRecord.Id,
                GeneratedOivPath = generatedOivPath,
                InstalledAtUtc = now,
                VerifiedAtUtc = verificationState == InstallVerificationState.VerifiedComplete ? now : null,
                AiSummary = plan.AiReasoningSummary,
                PlanJson = JsonSerializer.Serialize(plan),
                FileCount = fileLedger.Count
            };

            _repository.SaveInstalledMod(modRecord, fileLedger);

            bool fullyVerified = verificationState == InstallVerificationState.VerifiedComplete;
            string finalSummary = fullyVerified
                ? $"Successfully installed and SHA-256 verified '{plan.ModName}' ({fileLedger.Count} operation(s))."
                : $"Deployed filesystem assets for '{plan.ModName}' and generated OpenIV package '{Path.GetFileName(generatedOivPath)}' — awaiting OpenIV commit on NG-encrypted RPF7 archive.";

            progress?.Report(new InstallProgressReport(
                100,
                fullyVerified ? "Installation Verified" : "Awaiting OpenIV Commit",
                finalSummary));

            _repository.AppendLog(
                fullyVerified ? AuditLogLevel.Verify : AuditLogLevel.Warning,
                "Installer",
                finalSummary,
                string.Join("\n", verificationProofs),
                cid);

            return new InstallExecutionResult
            {
                Succeeded = fullyVerified,
                VerificationState = verificationState,
                InstalledModId = modId,
                BackupId = backupRecord.Id,
                GeneratedOivPackagePath = generatedOivPath,
                VerifiedFileCount = fileLedger.Count,
                SummaryMessage = finalSummary,
                VerificationProofs = verificationProofs
            };
        }
        catch (Exception ex)
        {
            _repository.AppendLog(
                AuditLogLevel.Error,
                "Installer",
                $"Installation failed for '{plan.ModName}' — initiating automatic rollback from verified backup {backupRecord.Id}!",
                ex.ToString(),
                cid);

            try
            {
                _backupService.RestoreVerifiedBackup(backupRecord, gameEnv.GtaRootPath, progress, cid);
            }
            catch (Exception rbEx)
            {
                _repository.AppendLog(AuditLogLevel.Error, "Installer", "Automatic rollback encountered an error.", rbEx.Message, cid);
            }

            return new InstallExecutionResult
            {
                Succeeded = false,
                VerificationState = InstallVerificationState.FailedVerification,
                BackupId = backupRecord.Id,
                SummaryMessage = $"Installation failed and was rolled back to pre-install state: {ex.Message}",
                VerificationProofs = verificationProofs
            };
        }
    }

    public async Task<bool> UninstallAndRollbackModAsync(
        string modId,
        GameEnvironmentInfo gameEnv,
        IProgress<InstallProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string cid = "uninst_" + Guid.NewGuid().ToString("N")[..8];
        var mod = _repository.GetInstalledModById(modId)
                  ?? throw new KeyNotFoundException($"Installed mod '{modId}' not found.");
        var modFiles = _repository.GetModFiles(modId);

        progress?.Report(new InstallProgressReport(10, "Pre-Rollback Backup", $"Creating safety backup before removing '{mod.Name}'..."));

        const long MaxUninstallRpfSnapshotBytes = 64L * 1024 * 1024;
        var affectedDiskPaths = modFiles
            .Select(f => f.TargetRelativePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(rel =>
            {
                if (!rel.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                string full = PathSafetyGuard.ResolveSafeChildPath(gameEnv.GtaRootPath, rel);
                return !File.Exists(full) || new FileInfo(full).Length <= MaxUninstallRpfSnapshotBytes;
            })
            .ToList();

        _backupService.CreateVerifiedBackup(
            gameEnv.GtaRootPath,
            $"Pre-Uninstall: {mod.Name}",
            "PreUninstall",
            affectedDiskPaths,
            correlationId: cid);

        // Check if any OTHER installed mod shares a DLC list entry
        var otherModsFiles = _repository.GetModFiles()
            .Where(f => !string.Equals(f.ModId, modId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        int idx = 0;
        foreach (var fileRec in modFiles.Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            idx++;
            int pct = 20 + (int)(70.0 * idx / Math.Max(1, modFiles.Count));
            progress?.Report(new InstallProgressReport(pct, "Rolling Back Changes", $"Reverting {fileRec.TargetRelativePath}..."));

            // Clean disabled stash if mod was disabled when uninstalled
            if (!string.IsNullOrWhiteSpace(fileRec.DisabledStashPath) && File.Exists(fileRec.DisabledStashPath))
            {
                File.Delete(fileRec.DisabledStashPath);
            }

            if (fileRec.Operation == InstallOperationKind.CopyFileToModsOrRoot)
            {
                string destFull = PathSafetyGuard.ResolveSafeChildPath(gameEnv.GtaRootPath, fileRec.TargetRelativePath);
                if (fileRec.ExistedBefore && !string.IsNullOrWhiteSpace(fileRec.PreviousFileStashPath) && File.Exists(fileRec.PreviousFileStashPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
                    File.Copy(fileRec.PreviousFileStashPath, destFull, overwrite: true);
                    if (fileRec.Sha256Before is not null &&
                        !HashVerifier.VerifyFileSha256(destFull, fileRec.Sha256Before, out _))
                    {
                        throw new InvalidDataException($"Rollback SHA-256 verification failed for '{fileRec.TargetRelativePath}'.");
                    }
                }
                else
                {
                    if (File.Exists(destFull))
                    {
                        File.Delete(destFull);
                    }
                    BackupService.TryCleanEmptyModDirectories(gameEnv.GtaRootPath, Path.GetDirectoryName(destFull));
                }
            }
            else if (fileRec.Operation == InstallOperationKind.InjectIntoRpfArchive)
            {
                string rpfFull = PathSafetyGuard.ResolveSafeChildPath(gameEnv.GtaRootPath, fileRec.RpfContainerRelativePath);
                if (File.Exists(rpfFull) && Rpf7Archive.InspectHeader(rpfFull).CanDirectEditWithoutNgKeys)
                {
                    var rpf = Rpf7Archive.Open(rpfFull);
                    if (fileRec.ExistedBefore && !string.IsNullOrWhiteSpace(fileRec.PreviousFileStashPath) && File.Exists(fileRec.PreviousFileStashPath))
                    {
                        byte[] prevBytes = await File.ReadAllBytesAsync(fileRec.PreviousFileStashPath, cancellationToken);
                        if (fileRec.Sha256Before is not null)
                        {
                            string stashSha = HashVerifier.ComputeBytesSha256(prevBytes);
                            if (!string.Equals(stashSha, fileRec.Sha256Before, StringComparison.OrdinalIgnoreCase))
                            {
                                throw new InvalidDataException($"Backed-up original RPF entry '{fileRec.RpfInternalPath}' failed SHA-256 check prior to restore.");
                            }
                        }

                        if (fileRec.PreviousFileStashPath.EndsWith(".gtama_opaque.json", StringComparison.OrdinalIgnoreCase))
                        {
                            var opaqueBlob = JsonSerializer.Deserialize<OpaqueEncryptedEntryBlob>(prevBytes)
                                ?? throw new InvalidDataException($"Failed to deserialize encrypted backup blob for '{fileRec.RpfInternalPath}'.");
                            rpf.RestoreOpaqueEncryptedEntry(fileRec.RpfInternalPath, opaqueBlob);
                            rpf.SaveAndVerify(rpfFull);
                        }
                        else
                        {
                            rpf.PutFile(fileRec.RpfInternalPath, prevBytes);
                            rpf.SaveAndVerify(rpfFull);

                            if (fileRec.Sha256Before is not null)
                            {
                                var verifyRestoredRpf = Rpf7Archive.Open(rpfFull);
                                string restoredSha = HashVerifier.ComputeBytesSha256(verifyRestoredRpf.ExtractFile(fileRec.RpfInternalPath));
                                if (!string.Equals(restoredSha, fileRec.Sha256Before, StringComparison.OrdinalIgnoreCase))
                                {
                                    throw new InvalidDataException($"Post-uninstall RPF7 SHA-256 verification failed for restored '{fileRec.RpfInternalPath}'.");
                                }
                            }
                        }
                    }
                    else
                    {
                        rpf.RemoveFile(fileRec.RpfInternalPath);
                        rpf.SaveAndVerify(rpfFull);

                        var verifyRemovedRpf = Rpf7Archive.Open(rpfFull);
                        if (verifyRemovedRpf.ContainsFile(fileRec.RpfInternalPath))
                        {
                            throw new InvalidDataException($"Post-uninstall RPF7 verification failed: '{fileRec.RpfInternalPath}' still present.");
                        }
                    }
                }
            }
            else if (fileRec.Operation == InstallOperationKind.RegisterDlcListEntry)
            {
                bool usedByOtherMod = otherModsFiles.Any(o =>
                    o.Operation == InstallOperationKind.RegisterDlcListEntry &&
                    string.Equals(o.DlcListEntry, fileRec.DlcListEntry, StringComparison.OrdinalIgnoreCase));

                if (!usedByOtherMod)
                {
                    string updateFull = PathSafetyGuard.ResolveSafeChildPath(gameEnv.GtaRootPath, "mods/update/update.rpf");
                    if (File.Exists(updateFull) && Rpf7Archive.InspectHeader(updateFull).CanDirectEditWithoutNgKeys)
                    {
                        DlcListXmlService.UpdateOpenUpdateRpfDlcList(updateFull, fileRec.DlcListEntry, remove: true);
                    }
                }
            }
        }

        // Delete conflict stash folder for this mod
        string modStashDir = Path.Combine(_stashRootDirectory, modId);
        if (Directory.Exists(modStashDir))
        {
            try { Directory.Delete(modStashDir, recursive: true); } catch { /* ignore */ }
        }

        _repository.DeleteInstalledMod(modId);
        progress?.Report(new InstallProgressReport(100, "Uninstall Verified", $"Removed '{mod.Name}' and verified clean state."));
        _repository.AppendLog(
            AuditLogLevel.Verify,
            "Uninstall",
            $"Uninstalled and verified rollback of '{mod.Name}' ({modId})",
            $"Reverted {modFiles.Count} recorded change(s).",
            cid);

        return true;
    }

    private void EnsureModsRpfCopyOnWrite(string gtaRootPath, string safeModsRpfRelativePath, string correlationId)
    {
        if (!safeModsRpfRelativePath.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"RPF target '{safeModsRpfRelativePath}' must reside inside mods/.");
        }

        string modsFullPath = PathSafetyGuard.ResolveSafeChildPath(gtaRootPath, safeModsRpfRelativePath);
        if (File.Exists(modsFullPath))
        {
            return;
        }

        string vanillaRelative = safeModsRpfRelativePath["mods/".Length..];
        string vanillaFullPath = PathSafetyGuard.ResolveSafeChildPath(gtaRootPath, vanillaRelative);

        Directory.CreateDirectory(Path.GetDirectoryName(modsFullPath)!);

        if (File.Exists(vanillaFullPath))
        {
            string vanillaSha = HashVerifier.ComputeFileSha256(vanillaFullPath);
            string tmp = modsFullPath + ".gtama_cow_tmp";
            File.Copy(vanillaFullPath, tmp, overwrite: true);
            if (!HashVerifier.VerifyFileSha256(tmp, vanillaSha, out _))
            {
                File.Delete(tmp);
                throw new InvalidDataException($"Copy-on-write SHA-256 verification failed while mirroring '{vanillaRelative}' to '{safeModsRpfRelativePath}'.");
            }
            File.Move(tmp, modsFullPath, overwrite: true);

            _repository.AppendLog(
                AuditLogLevel.Verify,
                "ModsFolder",
                $"Mirrored vanilla '{vanillaRelative}' -> '{safeModsRpfRelativePath}' (SHA-256 verified)",
                $"Vanilla archive untouched; SHA-256={vanillaSha[..16]}...",
                correlationId);
        }
        else
        {
            // Initialize clean OPEN RPF7 container (with baseline dlclist.xml when update.rpf)
            var rpf = Rpf7Archive.CreateNewOpenArchive();
            if (safeModsRpfRelativePath.EndsWith("update/update.rpf", StringComparison.OrdinalIgnoreCase))
            {
                byte[] dlcBytes = System.Text.Encoding.UTF8.GetBytes(DlcListXmlService.CreateBaselineDlcListXml());
                rpf.PutFile(DlcListXmlService.RpfInternalDlcListPath, dlcBytes);
            }
            rpf.SaveAndVerify(modsFullPath);

            _repository.AppendLog(
                AuditLogLevel.Info,
                "ModsFolder",
                $"Initialized clean OPEN RPF7 container at '{safeModsRpfRelativePath}'",
                "",
                correlationId);
        }
    }
}
