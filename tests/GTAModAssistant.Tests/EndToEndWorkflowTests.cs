using System.Text;
using Xunit;
using GTAModAssistant.Core.Archives;
using GTAModAssistant.Core.Models;
using GTAModAssistant.Core.Security;
using GTAModAssistant.Core.Services;
using GTAModAssistant.ViewModels;

namespace GTAModAssistant.Tests;

public sealed class EndToEndWorkflowTests
{
    [Fact]
    public async Task FullLifecycle_AddOnInstall_Verify_DisableEnable_ProfileSwitch_And_Rollback()
    {
        string tempDir = SyntheticGtaFixture.CreateTempDirectory();
        try
        {
            var (gtaRoot, openIvExe) = SyntheticGtaFixture.CreateSyntheticEpicGtaEnvironment(tempDir, buildNumber: 3411, openUpdateRpf: true);
            string vanillaUpdateShaBefore = HashVerifier.ComputeFileSha256(Path.Combine(gtaRoot, "update", "update.rpf"));

            var appPaths = new AppWorkspacePaths(Path.Combine(tempDir, "appdata"));
            var interaction = new FakeUserInteractionService { ConfirmResult = true };
            var vm = new MainViewModel(appPaths, interaction)
            {
                CustomGtaPathOverride = gtaRoot,
                CustomOpenIvOverride = openIvExe,
                EnableAiLlm = false
            };

            // 1. Scan Game
            await vm.ScanGameAsync();
            Assert.True(vm.GameEnv.IsDetected);
            Assert.Equal(3411, vm.GameEnv.BuildNumber);

            // 2. Build & Drop Add-On DLC ZIP
            string dlcTemp = Path.Combine(tempDir, "dlc.rpf");
            var dlcArchive = Rpf7Archive.CreateNewOpenArchive();
            dlcArchive.PutFile("setup2.xml", Encoding.UTF8.GetBytes("<SSetupData><deviceName>dlc_cometretro</deviceName></SSetupData>"));
            dlcArchive.SaveAndVerify(dlcTemp);
            string expectedDlcSha = HashVerifier.ComputeFileSha256(dlcTemp);

            string modZip = Path.Combine(tempDir, "Comet_Retro_Custom.zip");
            SyntheticGtaFixture.CreateZipArchive(
                modZip,
                ("cometretro/dlc.rpf", File.ReadAllBytes(dlcTemp)),
                ("README.txt", Encoding.UTF8.GetBytes("Add <Item>dlcpacks:/cometretro/</Item> to dlclist.xml. Built for 1.0.3411.0")));

            await vm.HandleDroppedPathsAsync(new[] { modZip });
            Assert.NotNull(vm.CurrentPlan);
            Assert.True(vm.CurrentPlan!.CanInstall);

            // 3. Install Mod
            await vm.ExecutePlannedInstallAsync();
            Assert.Single(vm.InstalledMods);
            var installed = vm.InstalledMods[0];
            Assert.Equal(InstallVerificationState.VerifiedComplete, installed.VerificationState);
            Assert.True(installed.IsEnabled);

            // Vanilla update/update.rpf MUST remain 100% untouched!
            string vanillaUpdateShaAfter = HashVerifier.ComputeFileSha256(Path.Combine(gtaRoot, "update", "update.rpf"));
            Assert.Equal(vanillaUpdateShaBefore, vanillaUpdateShaAfter);

            // Deployed dlc.rpf in mods/ matches exact SHA-256
            string deployedDlcPath = Path.Combine(gtaRoot, "mods", "update", "x64", "dlcpacks", "cometretro", "dlc.rpf");
            Assert.True(File.Exists(deployedDlcPath));
            Assert.Equal(expectedDlcSha, HashVerifier.ComputeFileSha256(deployedDlcPath));

            // dlclist.xml inside mods/update/update.rpf contains dlcpacks:/cometretro/
            string modsUpdatePath = Path.Combine(gtaRoot, "mods", "update", "update.rpf");
            string dlcListXml = Encoding.UTF8.GetString(Rpf7Archive.Open(modsUpdatePath).ExtractFile(DlcListXmlService.RpfInternalDlcListPath));
            Assert.Contains("dlcpacks:/cometretro/", dlcListXml);

            // Pre-install backup exists and passes SHA-256 verification
            Assert.NotEmpty(vm.Backups);
            Assert.All(vm.Backups, b => Assert.True(b.IsVerified));

            // 4. Disable Mod without losing files
            await vm.ToggleModEnabledAsync(installed);
            var disabledRecord = vm.InstalledMods[0];
            Assert.False(disabledRecord.IsEnabled);
            Assert.False(File.Exists(deployedDlcPath));
            string dlcListWhenDisabled = Encoding.UTF8.GetString(Rpf7Archive.Open(modsUpdatePath).ExtractFile(DlcListXmlService.RpfInternalDlcListPath));
            Assert.DoesNotContain("cometretro", dlcListWhenDisabled);

            // 5. Re-enable Mod -> files & dlclist.xml restored intact!
            await vm.ToggleModEnabledAsync(disabledRecord);
            var reenabledRecord = vm.InstalledMods[0];
            Assert.True(reenabledRecord.IsEnabled);
            Assert.True(File.Exists(deployedDlcPath));
            Assert.Equal(expectedDlcSha, HashVerifier.ComputeFileSha256(deployedDlcPath));
            string dlcListWhenReenabled = Encoding.UTF8.GetString(Rpf7Archive.Open(modsUpdatePath).ExtractFile(DlcListXmlService.RpfInternalDlcListPath));
            Assert.Contains("dlcpacks:/cometretro/", dlcListWhenReenabled);

            // 6. Test Destructive Confirmation Guard: decline uninstall -> mod MUST NOT be deleted!
            interaction.ConfirmResult = false;
            await vm.UninstallModAsync(reenabledRecord);
            Assert.Single(vm.InstalledMods);
            Assert.True(File.Exists(deployedDlcPath));

            // 7. Confirm Uninstall -> one-click rollback removes mod & cleans dlclist.xml!
            interaction.ConfirmResult = true;
            await vm.UninstallModAsync(reenabledRecord);
            Assert.Empty(vm.InstalledMods);
            Assert.False(File.Exists(deployedDlcPath));
            string dlcListAfterUninstall = Encoding.UTF8.GetString(Rpf7Archive.Open(modsUpdatePath).ExtractFile(DlcListXmlService.RpfInternalDlcListPath));
            Assert.DoesNotContain("cometretro", dlcListAfterUninstall);
        }
        finally
        {
            SyntheticGtaFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ConflictResolution_DetectsRpfAssetCollision_OverwritesWithStash_AndRestoresPreviousModOnRollback()
    {
        string tempDir = SyntheticGtaFixture.CreateTempDirectory();
        try
        {
            var (gtaRoot, openIvExe) = SyntheticGtaFixture.CreateSyntheticEpicGtaEnvironment(tempDir, buildNumber: 3411);
            var paths = new AppWorkspacePaths(Path.Combine(tempDir, "appdata"));
            var repo = new SqliteHistoryRepository(paths.DatabasePath);
            var backups = new BackupService(paths.BackupsDirectory, repo);
            var installer = new ModInstallerService(repo, backups, paths.RootDataDirectory);
            var analyzer = new AiModAnalyzerService(new ArchiveExtractorService(), new SecretStore(paths.RootDataDirectory));
            var gameEnv = new GameDetectionService().ScanEnvironment(gtaRoot, openIvExe);

            byte[] mod1Yft = SyntheticGtaFixture.CreateSyntheticRsc7Asset("MOD_ONE_POLICE_YFT_V1");
            byte[] mod2Yft = SyntheticGtaFixture.CreateSyntheticRsc7Asset("MOD_TWO_POLICE_YFT_V2");

            string mod1Zip = SyntheticGtaFixture.CreateZipArchive(
                Path.Combine(tempDir, "PolicePackA.zip"),
                ("police.yft", mod1Yft),
                ("readme.txt", Encoding.UTF8.GetBytes(@"Replace inside x64e.rpf\levels\gta5\vehicles.rpf\police.yft")));

            string mod2Zip = SyntheticGtaFixture.CreateZipArchive(
                Path.Combine(tempDir, "PolicePackB.zip"),
                ("police.yft", mod2Yft),
                ("readme.txt", Encoding.UTF8.GetBytes(@"Replace inside x64e.rpf\levels\gta5\vehicles.rpf\police.yft")));

            // Install Mod 1
            var plan1 = await analyzer.AnalyzeModAsync(mod1Zip, paths.StagingDirectory, gameEnv, new AiSettings { EnableCloudOrLocalLlm = false }, repo.GetModFiles(onlyEnabledMods: true));
            Assert.Empty(plan1.Conflicts);
            var res1 = await installer.InstallAsync(plan1, gameEnv);
            Assert.True(res1.Succeeded);

            // Analyze Mod 2 -> MUST detect conflict with Mod 1 inside mods/x64e.rpf!
            var plan2 = await analyzer.AnalyzeModAsync(mod2Zip, paths.StagingDirectory, gameEnv, new AiSettings { EnableCloudOrLocalLlm = false }, repo.GetModFiles(onlyEnabledMods: true));
            Assert.Single(plan2.Conflicts);
            Assert.Equal(res1.InstalledModId, plan2.Conflicts[0].ExistingModId);

            // Install Mod 2 with BackupAndOverwrite
            var res2 = await installer.InstallAsync(plan2, gameEnv, ConflictResolutionStrategy.BackupAndOverwrite);
            Assert.True(res2.Succeeded);

            string modsX64e = Path.Combine(gtaRoot, "mods", "x64e.rpf");
            Assert.Equal(mod2Yft, Rpf7Archive.Open(modsX64e).ExtractFile("levels/gta5/vehicles.rpf/police.yft"));

            // Rollback Mod 2 -> Mod 1's police.yft MUST be restored intact inside mods/x64e.rpf!
            await installer.UninstallAndRollbackModAsync(res2.InstalledModId!, gameEnv);
            Assert.Equal(mod1Yft, Rpf7Archive.Open(modsX64e).ExtractFile("levels/gta5/vehicles.rpf/police.yft"));
        }
        finally
        {
            SyntheticGtaFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task NgEncryptedUpdateRpf_GeneratesValidOivPackage_AndNeverFalselyReportsVerifiedComplete()
    {
        string tempDir = SyntheticGtaFixture.CreateTempDirectory();
        try
        {
            var (gtaRoot, openIvExe) = SyntheticGtaFixture.CreateSyntheticEpicGtaEnvironment(tempDir, buildNumber: 3411, openUpdateRpf: false);
            var paths = new AppWorkspacePaths(Path.Combine(tempDir, "appdata"));
            var repo = new SqliteHistoryRepository(paths.DatabasePath);
            var backups = new BackupService(paths.BackupsDirectory, repo);
            var installer = new ModInstallerService(repo, backups, paths.RootDataDirectory);
            var analyzer = new AiModAnalyzerService(new ArchiveExtractorService(), new SecretStore(paths.RootDataDirectory));
            var gameEnv = new GameDetectionService().ScanEnvironment(gtaRoot, openIvExe);

            string dlcTemp = Path.Combine(tempDir, "dlc.rpf");
            var dlcArchive = Rpf7Archive.CreateNewOpenArchive();
            dlcArchive.PutFile("setup2.xml", Encoding.UTF8.GetBytes("<SSetupData><deviceName>dlc_nsx2017</deviceName></SSetupData>"));
            dlcArchive.SaveAndVerify(dlcTemp);

            string modZip = SyntheticGtaFixture.CreateZipArchive(
                Path.Combine(tempDir, "NSX2017.zip"),
                ("nsx2017/dlc.rpf", File.ReadAllBytes(dlcTemp)),
                ("readme.txt", Encoding.UTF8.GetBytes("Add dlcpacks:/nsx2017/ to dlclist.xml")));

            var plan = await analyzer.AnalyzeModAsync(modZip, paths.StagingDirectory, gameEnv, new AiSettings { EnableCloudOrLocalLlm = false }, Array.Empty<InstalledModFileRecord>());
            var res = await installer.InstallAsync(plan, gameEnv, autoLaunchOpenIvForEncryptedRpfs: false);

            // Requirement 10: NEVER report VerifiedComplete when NG-encrypted RPF commit via OpenIV hasn't occurred!
            Assert.False(res.Succeeded);
            Assert.Equal(InstallVerificationState.PendingOpenIvCommit, res.VerificationState);
            Assert.NotNull(res.GeneratedOivPackagePath);
            Assert.True(File.Exists(res.GeneratedOivPackagePath!));

            // Also verify the generated .oiv package parses cleanly via OivPackageService!
            var extractor = new ArchiveExtractorService();
            var stagedOiv = extractor.StageInput(res.GeneratedOivPackagePath!, Path.Combine(tempDir, "oiv_check"));
            var parsedOiv = OivPackageService.ParseExtractedOivDirectory(stagedOiv.StagingDirectory);
            Assert.Contains(parsedOiv.ExtractedSteps, s => s.DlcListEntry == "dlcpacks:/nsx2017/");
        }
        finally
        {
            SyntheticGtaFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task MultiProfileSwitching_TogglesModsAtomically_WithoutDataLoss()
    {
        string tempDir = SyntheticGtaFixture.CreateTempDirectory();
        try
        {
            var (gtaRoot, openIvExe) = SyntheticGtaFixture.CreateSyntheticEpicGtaEnvironment(tempDir, buildNumber: 3411);
            var appPaths = new AppWorkspacePaths(Path.Combine(tempDir, "appdata"));
            var interaction = new FakeUserInteractionService { ConfirmResult = true };
            var vm = new MainViewModel(appPaths, interaction)
            {
                CustomGtaPathOverride = gtaRoot,
                CustomOpenIvOverride = openIvExe,
                EnableAiLlm = false
            };
            await vm.ScanGameAsync();

            // Install an ASI mod in Default profile
            string asiZip = SyntheticGtaFixture.CreateZipArchive(
                Path.Combine(tempDir, "EnhancedCamera.zip"),
                ("EnhancedCam.asi", Encoding.UTF8.GetBytes("ASI_CAMERA_MOD_V1")),
                ("EnhancedCam.ini", Encoding.UTF8.GetBytes("FOV=75")));

            await vm.AnalyzeModInputAsync(asiZip);
            await vm.ExecutePlannedInstallAsync();

            string deployedAsi = Path.Combine(gtaRoot, "EnhancedCam.asi");
            Assert.True(File.Exists(deployedAsi));

            // Create new profile (clones current enabled state), then disable mod in new profile
            await vm.CreateNewProfileAsync();
            Assert.Equal(2, vm.Profiles.Count);
            Assert.Equal("Realism Overhaul", vm.SelectedProfile!.Name);

            await vm.ToggleModEnabledAsync(vm.InstalledMods[0]);
            Assert.False(File.Exists(deployedAsi));

            // Switch back to Default Single-Player profile -> EnhancedCam.asi MUST reappear automatically!
            var defaultProfile = vm.Profiles.Single(p => p.Id == "default");
            vm.SelectedProfile = defaultProfile;
            DateTime switchTimeout = DateTime.UtcNow.AddSeconds(5);
            while (vm.IsBusy && DateTime.UtcNow < switchTimeout)
            {
                await Task.Delay(10);
            }
            Assert.False(vm.IsBusy, "Profile switching did not complete before the test timed out.");
            Assert.True(File.Exists(deployedAsi));
            Assert.Equal("ASI_CAMERA_MOD_V1", File.ReadAllText(deployedAsi));
        }
        finally
        {
            SyntheticGtaFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ManualBackupAndRestore_RevertsSubsequentModifications_AndVerifiesHashes()
    {
        string tempDir = SyntheticGtaFixture.CreateTempDirectory();
        try
        {
            var (gtaRoot, openIvExe) = SyntheticGtaFixture.CreateSyntheticEpicGtaEnvironment(tempDir, buildNumber: 3411);
            var appPaths = new AppWorkspacePaths(Path.Combine(tempDir, "appdata"));
            var interaction = new FakeUserInteractionService { ConfirmResult = true };
            var vm = new MainViewModel(appPaths, interaction)
            {
                CustomGtaPathOverride = gtaRoot,
                CustomOpenIvOverride = openIvExe,
                EnableAiLlm = false
            };
            await vm.ScanGameAsync();

            string asiZip = SyntheticGtaFixture.CreateZipArchive(
                Path.Combine(tempDir, "Trainer.zip"),
                ("SimpleTrainer.asi", Encoding.UTF8.GetBytes("CLEAN_V1_BYTES")));
            await vm.AnalyzeModInputAsync(asiZip);
            await vm.ExecutePlannedInstallAsync();

            // Create manual snapshot of clean V1 state
            await vm.CreateManualBackupAsync();
            var manualSnapshot = vm.Backups.First(b => b.BackupType == "ManualSnapshot");

            // Corrupt/tamper with SimpleTrainer.asi on disk
            string asiPath = Path.Combine(gtaRoot, "SimpleTrainer.asi");
            File.WriteAllText(asiPath, "CORRUPTED_BYTES");

            // Switch to Backups tab and restore manualSnapshot
            vm.SelectedTab = WorkspaceTab.BackupsAndRestore;
            vm.SelectedBackup = manualSnapshot;
            await vm.HandleRestoreActionAsync();

            Assert.Equal("CLEAN_V1_BYTES", File.ReadAllText(asiPath));
        }
        finally
        {
            SyntheticGtaFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task IncomingOivPackage_ParsesNestedRpfActions_BlocksBundledExe_AndInstallsSafely()
    {
        string tempDir = SyntheticGtaFixture.CreateTempDirectory();
        try
        {
            var (gtaRoot, openIvExe) = SyntheticGtaFixture.CreateSyntheticEpicGtaEnvironment(tempDir, buildNumber: 3411);
            var appPaths = new AppWorkspacePaths(Path.Combine(tempDir, "appdata"));
            var interaction = new FakeUserInteractionService { ConfirmResult = true };
            var vm = new MainViewModel(appPaths, interaction)
            {
                CustomGtaPathOverride = gtaRoot,
                CustomOpenIvOverride = openIvExe,
                EnableAiLlm = false
            };
            await vm.ScanGameAsync();

            byte[] customYft = SyntheticGtaFixture.CreateSyntheticRsc7Asset("OIV_CUSTOM_POLICE_YFT");
            string assemblyXml = """
                <?xml version="1.0" encoding="UTF-8"?>
                <package version="2.1" id="{11111111-2222-3333-4444-555555555555}" target="Five">
                  <metadata>
                    <name>LSPD Patrol OIV Pack</name>
                    <version><major>3</major><minor>1</minor></version>
                    <author><displayName>ModAuthorDemo</displayName></author>
                    <description>Replaces police.yft safely</description>
                  </metadata>
                  <content>
                    <archive path="x64e.rpf" createIfNotExist="True" type="RPF7">
                      <archive path="levels\gta5\vehicles.rpf" createIfNotExist="False" type="RPF7">
                        <add source="police.yft">police.yft</add>
                      </archive>
                    </archive>
                    <add source="sneaky.exe">sneaky.exe</add>
                  </content>
                </package>
                """;

            string oivPath = SyntheticGtaFixture.CreateZipArchive(
                Path.Combine(tempDir, "LSPD_Patrol.oiv"),
                ("assembly.xml", Encoding.UTF8.GetBytes(assemblyXml)),
                ("content/police.yft", customYft),
                ("content/sneaky.exe", Encoding.UTF8.GetBytes("MZ_BLOCKED")));

            await vm.AnalyzeModInputAsync(oivPath);
            Assert.NotNull(vm.CurrentPlan);
            Assert.Equal("LSPD Patrol OIV Pack", vm.CurrentPlan!.ModName);
            Assert.Equal("3.1", vm.CurrentPlan.ModVersion);
            Assert.Contains(vm.CurrentPlan.SecurityFindings, f => f.Severity == SecuritySeverity.BlockedExecutable);
            Assert.False(File.Exists(Path.Combine(gtaRoot, "sneaky.exe")));

            await vm.ExecutePlannedInstallAsync();
            string modsX64e = Path.Combine(gtaRoot, "mods", "x64e.rpf");
            Assert.Equal(customYft, Rpf7Archive.Open(modsX64e).ExtractFile("levels/gta5/vehicles.rpf/police.yft"));
        }
        finally
        {
            SyntheticGtaFixture.DeleteTempDirectory(tempDir);
        }
    }

    private sealed class FakeUserInteractionService : IUserInteractionService
    {
        public bool ConfirmResult { get; set; } = true;
        public ConflictResolutionStrategy ConflictResult { get; set; } = ConflictResolutionStrategy.BackupAndOverwrite;
        public RpfTargetSelectionDecision? TargetDecisionResult { get; set; }

        public bool ConfirmDestructiveAction(string title, string message, string confirmButtonText = "Confirm") => ConfirmResult;
        public ConflictResolutionStrategy PromptConflictResolution(IReadOnlyList<ConflictItem> conflicts, string modName) => ConflictResult;
        public RpfTargetSelectionDecision? PromptAssetTargetSelection(ModInstallStep uncertainStep, string modName) => TargetDecisionResult;
        public NoReadmeUserChoice PromptNoReadmeDisclaimer(string modName, int discoveredFileCount, bool isLlmActive, string activeEngineDescription) =>
            NoReadmeUserChoice.ProceedWithAiFileAnalysis;
        public string? BrowseModArchiveFile() => null;
        public string? BrowseFolder(string title, string? initialDirectory = null) => null;
        public string? BrowseSaveFile() => null;
        public string? PromptSaveFile(string title, string defaultFileName, string filter) => null;
        public (string Name, string Description)? PromptNewProfile() => ("Realism Overhaul", "Realistic handling & vehicles");
        public void ShowNotification(string title, string message, bool isError = false) { }
    }
}
