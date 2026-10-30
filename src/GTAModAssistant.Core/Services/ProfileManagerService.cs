using GTAModAssistant.Core.Archives;
using GTAModAssistant.Core.Models;
using GTAModAssistant.Core.Security;

namespace GTAModAssistant.Core.Services;

public sealed class ProfileManagerService
{
    private readonly SqliteHistoryRepository _repository;
    private readonly BackupService _backupService;
    private readonly string _disabledStashRoot;

    public ProfileManagerService(
        SqliteHistoryRepository repository,
        BackupService backupService,
        string appDataDirectory)
    {
        _repository = repository;
        _backupService = backupService;
        _disabledStashRoot = Path.Combine(appDataDirectory, "disabled_mods");
        Directory.CreateDirectory(_disabledStashRoot);
    }

    public async Task SetModEnabledAsync(
        string modId,
        bool enable,
        GameEnvironmentInfo gameEnv,
        IProgress<InstallProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var mod = _repository.GetInstalledModById(modId)
                  ?? throw new KeyNotFoundException($"Mod '{modId}' not found.");
        if (mod.IsEnabled == enable)
        {
            return;
        }

        string cid = (enable ? "enable_" : "disable_") + Guid.NewGuid().ToString("N")[..8];
        var files = _repository.GetModFiles(modId);
        string modDisabledDir = Path.Combine(_disabledStashRoot, modId);
        Directory.CreateDirectory(modDisabledDir);

        var updatedStashPaths = new Dictionary<long, string?>();

        if (!enable)
        {
            progress?.Report(new InstallProgressReport(25, "Disabling Mod", $"Safely stashing '{mod.Name}' without deleting files..."));

            foreach (var f in files.Reverse())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (f.Operation == InstallOperationKind.CopyFileToModsOrRoot)
                {
                    string activeFull = PathSafetyGuard.ResolveSafeChildPath(gameEnv.GtaRootPath, f.TargetRelativePath);
                    if (File.Exists(activeFull))
                    {
                        string stashFile = Path.Combine(modDisabledDir, $"fs_{f.Id}_{Path.GetFileName(activeFull)}");
                        string shaBeforeStash = HashVerifier.ComputeFileSha256(activeFull);
                        File.Copy(activeFull, stashFile, overwrite: true);
                        if (!HashVerifier.VerifyFileSha256(stashFile, shaBeforeStash, out _))
                        {
                            throw new InvalidDataException($"Failed to verify stashed copy for '{f.TargetRelativePath}'.");
                        }

                        if (f.ExistedBefore && !string.IsNullOrWhiteSpace(f.PreviousFileStashPath) && File.Exists(f.PreviousFileStashPath))
                        {
                            File.Copy(f.PreviousFileStashPath, activeFull, overwrite: true);
                        }
                        else
                        {
                            File.Delete(activeFull);
                            BackupService.TryCleanEmptyModDirectories(gameEnv.GtaRootPath, Path.GetDirectoryName(activeFull));
                        }

                        updatedStashPaths[f.Id] = stashFile;
                    }
                }
                else if (f.Operation == InstallOperationKind.InjectIntoRpfArchive)
                {
                    string rpfFull = PathSafetyGuard.ResolveSafeChildPath(gameEnv.GtaRootPath, f.RpfContainerRelativePath);
                    if (File.Exists(rpfFull) && Rpf7Archive.InspectHeader(rpfFull).CanDirectEditWithoutNgKeys)
                    {
                        var rpf = Rpf7Archive.Open(rpfFull);
                        if (rpf.ContainsFile(f.RpfInternalPath))
                        {
                            byte[] currentBytes = rpf.ExtractFile(f.RpfInternalPath);
                            string stashFile = Path.Combine(modDisabledDir, $"rpf_{f.Id}_{Path.GetFileName(f.RpfInternalPath)}");
                            await File.WriteAllBytesAsync(stashFile, currentBytes, cancellationToken);

                            if (f.ExistedBefore && !string.IsNullOrWhiteSpace(f.PreviousFileStashPath) && File.Exists(f.PreviousFileStashPath))
                            {
                                byte[] prevBytes = await File.ReadAllBytesAsync(f.PreviousFileStashPath, cancellationToken);
                                rpf.PutFile(f.RpfInternalPath, prevBytes);
                            }
                            else
                            {
                                rpf.RemoveFile(f.RpfInternalPath);
                            }

                            rpf.SaveAndVerify(rpfFull);
                            updatedStashPaths[f.Id] = stashFile;
                        }
                    }
                }
                else if (f.Operation == InstallOperationKind.RegisterDlcListEntry)
                {
                    string updateFull = PathSafetyGuard.ResolveSafeChildPath(gameEnv.GtaRootPath, "mods/update/update.rpf");
                    if (File.Exists(updateFull) && Rpf7Archive.InspectHeader(updateFull).CanDirectEditWithoutNgKeys)
                    {
                        DlcListXmlService.UpdateOpenUpdateRpfDlcList(updateFull, f.DlcListEntry, remove: true);
                    }
                    updatedStashPaths[f.Id] = "dlc_disabled";
                }
            }

            _repository.UpdateModEnabledState(modId, isEnabled: false, updatedStashPaths);
            progress?.Report(new InstallProgressReport(100, "Mod Disabled", $"'{mod.Name}' disabled; all files preserved in safe stash."));
            _repository.AppendLog(AuditLogLevel.Verify, "Profiles", $"Disabled '{mod.Name}' (all files preserved & SHA-256 verified in stash)", modDisabledDir, cid);
        }
        else
        {
            progress?.Report(new InstallProgressReport(25, "Enabling Mod", $"Restoring stashed files for '{mod.Name}'..."));

            foreach (var f in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (f.Operation == InstallOperationKind.CopyFileToModsOrRoot)
                {
                    if (!string.IsNullOrWhiteSpace(f.DisabledStashPath) && File.Exists(f.DisabledStashPath))
                    {
                        string activeFull = PathSafetyGuard.ResolveSafeChildPath(gameEnv.GtaRootPath, f.TargetRelativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(activeFull)!);
                        string expectedSha = HashVerifier.ComputeFileSha256(f.DisabledStashPath);
                        File.Copy(f.DisabledStashPath, activeFull, overwrite: true);
                        if (!HashVerifier.VerifyFileSha256(activeFull, expectedSha, out _))
                        {
                            throw new InvalidDataException($"SHA-256 verification failed while re-enabling '{f.TargetRelativePath}'.");
                        }
                        File.Delete(f.DisabledStashPath);
                    }
                    updatedStashPaths[f.Id] = null;
                }
                else if (f.Operation == InstallOperationKind.InjectIntoRpfArchive)
                {
                    if (!string.IsNullOrWhiteSpace(f.DisabledStashPath) && File.Exists(f.DisabledStashPath))
                    {
                        string rpfFull = PathSafetyGuard.ResolveSafeChildPath(gameEnv.GtaRootPath, f.RpfContainerRelativePath);
                        if (File.Exists(rpfFull) && Rpf7Archive.InspectHeader(rpfFull).CanDirectEditWithoutNgKeys)
                        {
                            byte[] bytes = await File.ReadAllBytesAsync(f.DisabledStashPath, cancellationToken);
                            string expectedSha = HashVerifier.ComputeBytesSha256(bytes);

                            var rpf = Rpf7Archive.Open(rpfFull);
                            rpf.PutFile(f.RpfInternalPath, bytes);
                            rpf.SaveAndVerify(rpfFull);

                            var checkRpf = Rpf7Archive.Open(rpfFull);
                            string actualSha = HashVerifier.ComputeBytesSha256(checkRpf.ExtractFile(f.RpfInternalPath));
                            if (!string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase))
                            {
                                throw new InvalidDataException($"RPF7 entry SHA-256 mismatch while re-enabling '{f.RpfInternalPath}'.");
                            }
                            File.Delete(f.DisabledStashPath);
                        }
                    }
                    updatedStashPaths[f.Id] = null;
                }
                else if (f.Operation == InstallOperationKind.RegisterDlcListEntry)
                {
                    string updateFull = PathSafetyGuard.ResolveSafeChildPath(gameEnv.GtaRootPath, "mods/update/update.rpf");
                    if (File.Exists(updateFull) && Rpf7Archive.InspectHeader(updateFull).CanDirectEditWithoutNgKeys)
                    {
                        DlcListXmlService.UpdateOpenUpdateRpfDlcList(updateFull, f.DlcListEntry, remove: false);
                    }
                    updatedStashPaths[f.Id] = null;
                }
            }

            _repository.UpdateModEnabledState(modId, isEnabled: true, updatedStashPaths);
            progress?.Report(new InstallProgressReport(100, "Mod Enabled", $"'{mod.Name}' re-enabled and SHA-256 verified."));
            _repository.AppendLog(AuditLogLevel.Verify, "Profiles", $"Re-enabled '{mod.Name}' and verified restored files", "", cid);
        }
    }

    public async Task SwitchProfileAsync(
        string targetProfileId,
        GameEnvironmentInfo gameEnv,
        IProgress<InstallProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var profiles = _repository.GetProfiles();
        var targetProfile = profiles.FirstOrDefault(p => string.Equals(p.Id, targetProfileId, StringComparison.OrdinalIgnoreCase))
                            ?? throw new KeyNotFoundException($"Profile '{targetProfileId}' not found.");

        if (targetProfile.IsActive)
        {
            return;
        }

        string cid = "prof_" + Guid.NewGuid().ToString("N")[..8];
        var allMods = _repository.GetInstalledMods();
        var targetStates = _repository.GetProfileModStates(targetProfileId);

        if (gameEnv.IsDetected && allMods.Count > 0)
        {
            var allManagedRelPaths = _repository.GetModFiles()
                .Select(f => f.TargetRelativePath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            progress?.Report(new InstallProgressReport(15, "Profile Switch Backup", $"Backing up state before switching to '{targetProfile.Name}'..."));
            _backupService.CreateVerifiedBackup(
                gameEnv.GtaRootPath,
                $"Pre-Profile Switch -> {targetProfile.Name}",
                "PreProfileSwitch",
                allManagedRelPaths,
                correlationId: cid);
        }

        // Persist current profile's states and activate new profile record
        _repository.SetActiveProfileRecord(targetProfileId);

        int i = 0;
        foreach (var mod in allMods)
        {
            i++;
            bool desiredEnabled = targetStates.TryGetValue(mod.Id, out bool savedState) ? savedState : false;
            if (mod.IsEnabled != desiredEnabled && gameEnv.IsDetected)
            {
                int pct = 25 + (int)(70.0 * i / Math.Max(1, allMods.Count));
                progress?.Report(new InstallProgressReport(pct, "Applying Profile", $"{(desiredEnabled ? "Enabling" : "Disabling")} {mod.Name}..."));
                await SetModEnabledAsync(mod.Id, desiredEnabled, gameEnv, null, cancellationToken);
            }
        }

        progress?.Report(new InstallProgressReport(100, "Profile Active", $"Switched to profile '{targetProfile.Name}'."));
        _repository.AppendLog(AuditLogLevel.Verify, "Profiles", $"Switched active mod profile to '{targetProfile.Name}'", "", cid);
    }
}
