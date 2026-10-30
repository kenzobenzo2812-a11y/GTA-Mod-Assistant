using System.Text;
using Xunit;
using GTAModAssistant.Core.Archives;
using GTAModAssistant.Core.Models;
using GTAModAssistant.Core.Security;
using GTAModAssistant.Core.Services;
using GTAModAssistant.ViewModels;

namespace GTAModAssistant.Tests;

public sealed class YftAndYtdSupportTests
{
    [Fact]
    public async Task ImportAndDetect_Yft_HiYft_Ytd_FromFolder_Zip_And_Rar_Archives()
    {
        string tempDir = SyntheticGtaFixture.CreateTempDirectory();
        try
        {
            var (gtaRoot, openIvExe) = SyntheticGtaFixture.CreateSyntheticEpicGtaEnvironment(tempDir, buildNumber: 3411);
            var gameEnv = new GameDetectionService().ScanEnvironment(gtaRoot, openIvExe);
            var analyzer = new AiModAnalyzerService(new ArchiveExtractorService(), new SecretStore(tempDir));

            byte[] yftBytes = SyntheticGtaFixture.CreateSyntheticRsc7Asset("POLICE_BASE_YFT");
            byte[] hiYftBytes = SyntheticGtaFixture.CreateSyntheticRsc7Asset("POLICE_HI_LOD_YFT");
            byte[] ytdBytes = SyntheticGtaFixture.CreateSyntheticRsc7Asset("POLICE_TXD_YTD");
            byte[] hiYtdBytes = SyntheticGtaFixture.CreateSyntheticRsc7Asset("POLICE_HI_TXD_YTD");

            // 1. Extracted Folder source (structured with explicit RPF path)
            string folderMod = Path.Combine(tempDir, "Folder_Police_Mod");
            string folderVehicleDir = Path.Combine(folderMod, "x64e.rpf", "levels", "gta5", "vehicles.rpf");
            Directory.CreateDirectory(folderVehicleDir);
            await File.WriteAllBytesAsync(Path.Combine(folderVehicleDir, "police.yft"), yftBytes);
            await File.WriteAllBytesAsync(Path.Combine(folderVehicleDir, "police_hi.yft"), hiYftBytes);
            await File.WriteAllBytesAsync(Path.Combine(folderVehicleDir, "police.ytd"), ytdBytes);
            await File.WriteAllBytesAsync(Path.Combine(folderVehicleDir, "police+hi.ytd"), hiYtdBytes);

            var folderPlan = await analyzer.AnalyzeModAsync(
                folderMod,
                Path.Combine(tempDir, "stage_folder"),
                gameEnv,
                new AiSettings { EnableCloudOrLocalLlm = false },
                Array.Empty<InstalledModFileRecord>());

            Assert.Equal(4, folderPlan.Steps.Count);
            Assert.False(folderPlan.HasUnresolvedTargets);
            Assert.Contains(folderPlan.Steps, s => s.FileName == "police.yft" && s.AssetFormatLabel.Contains(".yft"));
            Assert.Contains(folderPlan.Steps, s => s.FileName == "police_hi.yft" && s.AssetFormatLabel.Contains("_hi.yft"));
            Assert.Contains(folderPlan.Steps, s => s.FileName == "police.ytd" && s.AssetFormatLabel.Contains(".ytd"));
            Assert.Contains(folderPlan.Steps, s => s.FileName == "police+hi.ytd" && s.AssetFormatLabel.Contains("+hi.ytd"));

            // 2. .ZIP archive source
            string zipMod = SyntheticGtaFixture.CreateZipArchive(
                Path.Combine(tempDir, "Zip_Police_Mod.zip"),
                ("police.yft", yftBytes),
                ("police_hi.yft", hiYftBytes),
                ("police.ytd", ytdBytes),
                ("README.txt", Encoding.UTF8.GetBytes(@"Install into mods\x64e.rpf\levels\gta5\vehicles.rpf\")));

            var zipPlan = await analyzer.AnalyzeModAsync(
                zipMod,
                Path.Combine(tempDir, "stage_zip"),
                gameEnv,
                new AiSettings { EnableCloudOrLocalLlm = false },
                Array.Empty<InstalledModFileRecord>());

            Assert.Equal(3, zipPlan.Steps.Count);
            Assert.All(zipPlan.Steps, s =>
            {
                Assert.Equal("mods/x64e.rpf", s.RpfContainerRelativePath);
                Assert.StartsWith("levels/gta5/vehicles.rpf/", s.RpfInternalPath);
                Assert.True(s.ReplacesExistingEntry);
            });

            // 3. Binary .RAR archive source (extracted via SharpCompress RarArchive)
            string rarMod = SyntheticGtaFixture.CreateRar4Archive(
                Path.Combine(tempDir, "Rar_Police_Mod.rar"),
                ("Model/police.yft", yftBytes),
                ("Model/police_hi.yft", hiYftBytes),
                ("Textures/police.ytd", ytdBytes),
                ("Readme.txt", Encoding.UTF8.GetBytes(@"Put files in x64e.rpf\levels\gta5\vehicles.rpf\")));

            var rarPlan = await analyzer.AnalyzeModAsync(
                rarMod,
                Path.Combine(tempDir, "stage_rar"),
                gameEnv,
                new AiSettings { EnableCloudOrLocalLlm = false },
                Array.Empty<InstalledModFileRecord>());

            Assert.Equal(3, rarPlan.Steps.Count);
            Assert.False(rarPlan.HasUnresolvedTargets);
            Assert.Contains(rarPlan.Steps, s => s.FileName == "police_hi.yft" && s.RpfInternalPath == "levels/gta5/vehicles.rpf/police_hi.yft");
            Assert.Contains(rarPlan.Steps, s => s.FileName == "police.ytd" && s.RpfInternalPath == "levels/gta5/vehicles.rpf/police.ytd");
        }
        finally
        {
            SyntheticGtaFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task MultiArchiveRouting_NeverAssumesSingleArchive_RoutesAcrossThreeDistinctRpfs_ViaReadmeMetadataAndGameLookup()
    {
        string tempDir = SyntheticGtaFixture.CreateTempDirectory();
        try
        {
            var (gtaRoot, openIvExe) = SyntheticGtaFixture.CreateSyntheticEpicGtaEnvironment(tempDir, buildNumber: 3411);

            // Add vanilla patchday27ng (police2 override) and patchday8ng (combatpistol override) RPF7 archives
            string pd27Vanilla = Path.Combine(gtaRoot, "update", "x64", "dlcpacks", "patchday27ng", "dlc.rpf");
            Directory.CreateDirectory(Path.GetDirectoryName(pd27Vanilla)!);
            var pd27Rpf = Rpf7Archive.CreateNewOpenArchive();
            pd27Rpf.PutFile("x64/levels/gta5/vehicles.rpf/police2.yft", SyntheticGtaFixture.CreateSyntheticRsc7Asset("VANILLA_PD27_POLICE2_YFT"));
            pd27Rpf.PutFile("x64/levels/gta5/vehicles.rpf/police2_hi.yft", SyntheticGtaFixture.CreateSyntheticRsc7Asset("VANILLA_PD27_POLICE2_HI_YFT"));
            pd27Rpf.PutFile("x64/levels/gta5/vehicles.rpf/police2.ytd", SyntheticGtaFixture.CreateSyntheticRsc7Asset("VANILLA_PD27_POLICE2_YTD"));
            pd27Rpf.SaveAndVerify(pd27Vanilla);

            string pd8Vanilla = Path.Combine(gtaRoot, "update", "x64", "dlcpacks", "patchday8ng", "dlc.rpf");
            Directory.CreateDirectory(Path.GetDirectoryName(pd8Vanilla)!);
            var pd8Rpf = Rpf7Archive.CreateNewOpenArchive();
            pd8Rpf.PutFile("x64/models/cdimages/weapons.rpf/w_pi_combatpistol.ytd", SyntheticGtaFixture.CreateSyntheticRsc7Asset("VANILLA_PD8_PISTOL_YTD"));
            pd8Rpf.SaveAndVerify(pd8Vanilla);

            string vanillaX64eSha = HashVerifier.ComputeFileSha256(Path.Combine(gtaRoot, "x64e.rpf"));
            string vanillaPd27Sha = HashVerifier.ComputeFileSha256(pd27Vanilla);
            string vanillaPd8Sha = HashVerifier.ComputeFileSha256(pd8Vanilla);

            var paths = new AppWorkspacePaths(Path.Combine(tempDir, "appdata"));
            var repo = new SqliteHistoryRepository(paths.DatabasePath);
            var backups = new BackupService(paths.BackupsDirectory, repo);
            var installer = new ModInstallerService(repo, backups, paths.RootDataDirectory);
            var analyzer = new AiModAnalyzerService(new ArchiveExtractorService(), new SecretStore(paths.RootDataDirectory));
            var gameEnv = new GameDetectionService().ScanEnvironment(gtaRoot, openIvExe);

            // Multi-archive mod pack with:
            // - police2.yft + police2_hi.yft + police2.ytd + vehicles.meta-linked police2_livery_shared.ytd -> patchday27ng
            // - vehshare.ytd -> x64e.rpf
            // - w_pi_combatpistol.ytd + w_pi_combatpistol+hi.ytd -> patchday8ng
            string vehiclesMeta = """
                <?xml version="1.0" encoding="UTF-8"?>
                <CVehicleModelInfo__InitDataList>
                  <InitDatas>
                    <Item>
                      <modelName>police2</modelName>
                      <txdName>police2_livery_shared</txdName>
                    </Item>
                  </InitDatas>
                </CVehicleModelInfo__InitDataList>
                """;

            string multiReadme = """
                LSPD Patrol + Duty Sidearm Overhaul v1.5

                Step 1 - Cruiser Model & Liveries:
                Place police2.yft, police2_hi.yft, and police2.ytd into:
                update\x64\dlcpacks\patchday27ng\dlc.rpf\x64\levels\gta5\vehicles.rpf\

                Step 2 - Shared Vehicle Glass/Tire Textures:
                Place vehshare.ytd into:
                x64e.rpf\levels\gta5\vehicles.rpf\

                Step 3 - Duty Combat Pistol Textures:
                Place w_pi_combatpistol.ytd and w_pi_combatpistol+hi.ytd into:
                update\x64\dlcpacks\patchday8ng\dlc.rpf\x64\models\cdimages\weapons.rpf\
                """;

            byte[] modPol2Yft = SyntheticGtaFixture.CreateSyntheticRsc7Asset("MOD_POL2_YFT");
            byte[] modPol2HiYft = SyntheticGtaFixture.CreateSyntheticRsc7Asset("MOD_POL2_HI_YFT");
            byte[] modPol2Ytd = SyntheticGtaFixture.CreateSyntheticRsc7Asset("MOD_POL2_YTD");
            byte[] modPol2SharedYtd = SyntheticGtaFixture.CreateSyntheticRsc7Asset("MOD_POL2_SHARED_TXD");
            byte[] modVehShareYtd = SyntheticGtaFixture.CreateSyntheticRsc7Asset("MOD_VEHSHARE_YTD");
            byte[] modPistolYtd = SyntheticGtaFixture.CreateSyntheticRsc7Asset("MOD_PISTOL_YTD");
            byte[] modPistolHiYtd = SyntheticGtaFixture.CreateSyntheticRsc7Asset("MOD_PISTOL_HI_YTD");

            string packZip = SyntheticGtaFixture.CreateZipArchive(
                Path.Combine(tempDir, "LSPD_MultiArchive_Pack.zip"),
                ("Cruiser/police2.yft", modPol2Yft),
                ("Cruiser/police2_hi.yft", modPol2HiYft),
                ("Cruiser/police2.ytd", modPol2Ytd),
                ("Cruiser/police2_livery_shared.ytd", modPol2SharedYtd),
                ("Shared/vehshare.ytd", modVehShareYtd),
                ("Weapon/w_pi_combatpistol.ytd", modPistolYtd),
                ("Weapon/w_pi_combatpistol+hi.ytd", modPistolHiYtd),
                ("data/vehicles.meta", Encoding.UTF8.GetBytes(vehiclesMeta)),
                ("README.txt", Encoding.UTF8.GetBytes(multiReadme)));

            var plan = await analyzer.AnalyzeModAsync(
                packZip,
                paths.StagingDirectory,
                gameEnv,
                new AiSettings { EnableCloudOrLocalLlm = false },
                Array.Empty<InstalledModFileRecord>());

            Assert.False(plan.HasUnresolvedTargets);

            // Verify each file was mapped to its OWN specific RPF7 archive!
            Assert.Contains(plan.Steps, s =>
                s.FileName == "police2.yft" &&
                s.RpfContainerRelativePath == "mods/update/x64/dlcpacks/patchday27ng/dlc.rpf" &&
                s.RpfInternalPath == "x64/levels/gta5/vehicles.rpf/police2.yft");

            Assert.Contains(plan.Steps, s =>
                s.FileName == "police2_hi.yft" &&
                s.RpfContainerRelativePath == "mods/update/x64/dlcpacks/patchday27ng/dlc.rpf" &&
                s.RpfInternalPath == "x64/levels/gta5/vehicles.rpf/police2_hi.yft");

            // Linked via vehicles.meta <txdName>police2_livery_shared</txdName> -> <modelName>police2</modelName>!
            Assert.Contains(plan.Steps, s =>
                s.FileName == "police2_livery_shared.ytd" &&
                s.RpfContainerRelativePath == "mods/update/x64/dlcpacks/patchday27ng/dlc.rpf" &&
                s.RpfInternalPath == "x64/levels/gta5/vehicles.rpf/police2_livery_shared.ytd");

            Assert.Contains(plan.Steps, s =>
                s.FileName == "vehshare.ytd" &&
                s.RpfContainerRelativePath == "mods/x64e.rpf" &&
                s.RpfInternalPath == "levels/gta5/vehicles.rpf/vehshare.ytd");

            Assert.Contains(plan.Steps, s =>
                s.FileName == "w_pi_combatpistol+hi.ytd" &&
                s.RpfContainerRelativePath == "mods/update/x64/dlcpacks/patchday8ng/dlc.rpf" &&
                s.RpfInternalPath == "x64/models/cdimages/weapons.rpf/w_pi_combatpistol+hi.ytd");

            // Execute installation across all 4 target RPF archives (patchday27ng, x64e, patchday8ng, update.rpf)
            var result = await installer.InstallAsync(plan, gameEnv);
            Assert.True(result.Succeeded);
            Assert.Equal(InstallVerificationState.VerifiedComplete, result.VerificationState);

            // Verify ALL original vanilla archives were NEVER modified!
            Assert.Equal(vanillaX64eSha, HashVerifier.ComputeFileSha256(Path.Combine(gtaRoot, "x64e.rpf")));
            Assert.Equal(vanillaPd27Sha, HashVerifier.ComputeFileSha256(pd27Vanilla));
            Assert.Equal(vanillaPd8Sha, HashVerifier.ComputeFileSha256(pd8Vanilla));

            // Verify each safe mods/ RPF7 archive holds its exact injected .yft / _hi.yft / .ytd entries!
            var modsPd27 = Rpf7Archive.Open(Path.Combine(gtaRoot, "mods", "update", "x64", "dlcpacks", "patchday27ng", "dlc.rpf"));
            Assert.Equal(modPol2Yft, modsPd27.ExtractFile("x64/levels/gta5/vehicles.rpf/police2.yft"));
            Assert.Equal(modPol2HiYft, modsPd27.ExtractFile("x64/levels/gta5/vehicles.rpf/police2_hi.yft"));
            Assert.Equal(modPol2Ytd, modsPd27.ExtractFile("x64/levels/gta5/vehicles.rpf/police2.ytd"));
            Assert.Equal(modPol2SharedYtd, modsPd27.ExtractFile("x64/levels/gta5/vehicles.rpf/police2_livery_shared.ytd"));

            var modsX64e = Rpf7Archive.Open(Path.Combine(gtaRoot, "mods", "x64e.rpf"));
            Assert.Equal(modVehShareYtd, modsX64e.ExtractFile("levels/gta5/vehicles.rpf/vehshare.ytd"));

            var modsPd8 = Rpf7Archive.Open(Path.Combine(gtaRoot, "mods", "update", "x64", "dlcpacks", "patchday8ng", "dlc.rpf"));
            Assert.Equal(modPistolYtd, modsPd8.ExtractFile("x64/models/cdimages/weapons.rpf/w_pi_combatpistol.ytd"));
            Assert.Equal(modPistolHiYtd, modsPd8.ExtractFile("x64/models/cdimages/weapons.rpf/w_pi_combatpistol+hi.ytd"));
        }
        finally
        {
            SyntheticGtaFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task UncertainTarget_NeverGuesses_AsksUserViaPrompt_AndBlocksUnresolvedExecution()
    {
        string tempDir = SyntheticGtaFixture.CreateTempDirectory();
        try
        {
            var (gtaRoot, openIvExe) = SyntheticGtaFixture.CreateSyntheticEpicGtaEnvironment(tempDir, buildNumber: 3411);
            var appPaths = new AppWorkspacePaths(Path.Combine(tempDir, "appdata"));
            var ui = new ConfigurableInteractionStub { ConfirmResult = true, TargetDecision = null };
            var vm = new MainViewModel(appPaths, ui)
            {
                CustomGtaPathOverride = gtaRoot,
                CustomOpenIvOverride = openIvExe,
                EnableAiLlm = false
            };

            await vm.ScanGameAsync();

            // Create a mod with mysterycar.yft, mysterycar_hi.yft, and mysterycar.ytd — NO folder RPF path, NO README RPF path, NOT in vanilla RPFs!
            byte[] mysYft = SyntheticGtaFixture.CreateSyntheticRsc7Asset("MYSTERY_YFT");
            byte[] mysHiYft = SyntheticGtaFixture.CreateSyntheticRsc7Asset("MYSTERY_HI_YFT");
            byte[] mysYtd = SyntheticGtaFixture.CreateSyntheticRsc7Asset("MYSTERY_YTD");

            string mysteryZip = SyntheticGtaFixture.CreateZipArchive(
                Path.Combine(tempDir, "Mystery_Supercar_NoReadmePath.zip"),
                ("mysterycar.yft", mysYft),
                ("mysterycar_hi.yft", mysHiYft),
                ("mysterycar.ytd", mysYtd),
                ("readme.txt", Encoding.UTF8.GetBytes("Enjoy this car model! Use OpenIV to replace whichever vehicle archive you want.")));

            await vm.AnalyzeModInputAsync(mysteryZip);
            Assert.NotNull(vm.CurrentPlan);
            Assert.True(vm.CurrentPlan!.HasUnresolvedTargets);
            Assert.Equal(3, vm.CurrentPlan.UnresolvedTargetCount);
            Assert.All(vm.CurrentPlan.Steps, s =>
            {
                Assert.True(s.RequiresUserTargetSelection);
                Assert.Empty(s.RpfContainerRelativePath); // Did NOT guess mods/x64e.rpf!
                Assert.NotEmpty(s.CandidateRpfTargets);
            });

            // 1. Direct engine call MUST refuse to guess when targets remain unresolved
            var directInstaller = new ModInstallerService(
                vm.Repository,
                new BackupService(appPaths.BackupsDirectory, vm.Repository),
                appPaths.RootDataDirectory);
            var blockedRes = await directInstaller.InstallAsync(vm.CurrentPlan, vm.GameEnv);
            Assert.False(blockedRes.Succeeded);
            Assert.Equal(InstallVerificationState.FailedVerification, blockedRes.VerificationState);
            Assert.Empty(vm.Repository.GetInstalledMods());

            // 2. ViewModel prompts user; if user cancels prompt (TargetDecision == null), nothing is installed!
            await vm.ExecutePlannedInstallAsync();
            Assert.Equal(1, ui.TargetPromptCallCount);
            Assert.Empty(vm.InstalledMods);

            // 3. User selects mods/update/x64/dlcpacks/patchday28ng/dlc.rpf -> x64/levels/gta5/vehicles.rpf with companion stem pairing enabled!
            ui.TargetDecision = new RpfTargetSelectionDecision(
                "mods/update/x64/dlcpacks/patchday28ng/dlc.rpf",
                "x64/levels/gta5/vehicles.rpf",
                ApplyToCompanionStemFiles: true);

            await vm.ExecutePlannedInstallAsync();
            Assert.Equal(2, ui.TargetPromptCallCount); // Companion stem pairing resolved all 3 files in a single prompt!
            Assert.False(vm.CurrentPlan.HasUnresolvedTargets);
            Assert.Single(vm.InstalledMods);

            string targetPd28 = Path.Combine(gtaRoot, "mods", "update", "x64", "dlcpacks", "patchday28ng", "dlc.rpf");
            var pd28Rpf = Rpf7Archive.Open(targetPd28);
            Assert.Equal(mysYft, pd28Rpf.ExtractFile("x64/levels/gta5/vehicles.rpf/mysterycar.yft"));
            Assert.Equal(mysHiYft, pd28Rpf.ExtractFile("x64/levels/gta5/vehicles.rpf/mysterycar_hi.yft"));
            Assert.Equal(mysYtd, pd28Rpf.ExtractFile("x64/levels/gta5/vehicles.rpf/mysterycar.ytd"));
        }
        finally
        {
            SyntheticGtaFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task DuplicateFilenames_And_ConflictingReplacements_ArePreviewed_AndResolvedCleanly()
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

            // Install Mod A first (replaces police.yft and police_hi.yft)
            string modAZip = SyntheticGtaFixture.CreateZipArchive(
                Path.Combine(tempDir, "LSPD_Cruiser_v1.zip"),
                ("police.yft", SyntheticGtaFixture.CreateSyntheticRsc7Asset("MOD_A_YFT")),
                ("police_hi.yft", SyntheticGtaFixture.CreateSyntheticRsc7Asset("MOD_A_HI_YFT")),
                ("readme.txt", Encoding.UTF8.GetBytes(@"Install to x64e.rpf\levels\gta5\vehicles.rpf\")));

            var planA = await analyzer.AnalyzeModAsync(modAZip, paths.StagingDirectory, gameEnv, new AiSettings { EnableCloudOrLocalLlm = false }, Array.Empty<InstalledModFileRecord>());
            // Preview detects replacement of original vanilla RPF entries before Mod A is even installed!
            Assert.All(planA.Steps, s => Assert.True(s.ReplacesExistingEntry));
            var resA = await installer.InstallAsync(planA, gameEnv);
            Assert.True(resA.Succeeded);

            // Now import Mod B containing:
            // - police_hi.yft (CONFLICTS with installed Mod A!)
            // - Liveries_2K/police.ytd AND Liveries_4K/police.ytd (DUPLICATE filename & colliding RPF target inside Mod B!)
            byte[] modBHiYft = SyntheticGtaFixture.CreateSyntheticRsc7Asset("MOD_B_HI_YFT");
            byte[] modBYtd2K = SyntheticGtaFixture.CreateSyntheticRsc7Asset("MOD_B_YTD_2K");
            byte[] modBYtd4K = SyntheticGtaFixture.CreateSyntheticRsc7Asset("MOD_B_YTD_4K");

            string modBZip = SyntheticGtaFixture.CreateZipArchive(
                Path.Combine(tempDir, "LSPD_LiveryPack_v2.zip"),
                ("Model/police_hi.yft", modBHiYft),
                ("Liveries_2K/police.ytd", modBYtd2K),
                ("Liveries_4K/police.ytd", modBYtd4K),
                ("readme.txt", Encoding.UTF8.GetBytes(@"Install into x64e.rpf\levels\gta5\vehicles.rpf\")));

            var activeFiles = repo.GetModFiles(onlyEnabledMods: true);
            var modNames = repo.GetInstalledMods().ToDictionary(m => m.Id, m => m.Name, StringComparer.OrdinalIgnoreCase);

            var planB = await analyzer.AnalyzeModAsync(
                modBZip,
                paths.StagingDirectory,
                gameEnv,
                new AiSettings { EnableCloudOrLocalLlm = false },
                activeFiles,
                modNames);

            // Verify BOTH conflict kinds are detected and previewed!
            Assert.Contains(planB.Conflicts, c => c.Kind == ConflictKind.InstalledModConflict && c.ExistingModName == planA.ModName);
            Assert.Contains(planB.Conflicts, c => c.Kind == ConflictKind.DuplicateFileInPackage);
            Assert.Contains(planB.Steps, s => s.FileName == "police_hi.yft" && s.ConflictStatusPreview.Contains("CONFLICT"));
            Assert.Contains(planB.Steps, s => s.FileName == "police.ytd" && s.ConflictStatusPreview.Contains("DUPLICATE"));

            // Install with KeepFirstDuplicateOnly -> keeps Liveries_2K/police.ytd, skips Liveries_4K/police.ytd, backs up & overwrites police_hi.yft!
            var resB = await installer.InstallAsync(planB, gameEnv, ConflictResolutionStrategy.KeepFirstDuplicateOnly);
            Assert.True(resB.Succeeded);

            var modsX64e = Rpf7Archive.Open(Path.Combine(gtaRoot, "mods", "x64e.rpf"));
            Assert.Equal(modBHiYft, modsX64e.ExtractFile("levels/gta5/vehicles.rpf/police_hi.yft"));
            Assert.Equal(modBYtd2K, modsX64e.ExtractFile("levels/gta5/vehicles.rpf/police.ytd"));
        }
        finally
        {
            SyntheticGtaFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task BackupRestoreAndUninstall_BacksUpEveryReplacedYftHiYftYtd_PreservesVanilla_AndRestoresOriginalsByteForByte()
    {
        string tempDir = SyntheticGtaFixture.CreateTempDirectory();
        try
        {
            var (gtaRoot, openIvExe) = SyntheticGtaFixture.CreateSyntheticEpicGtaEnvironment(tempDir, buildNumber: 3411);
            string vanillaX64ePath = Path.Combine(gtaRoot, "x64e.rpf");
            string vanillaX64eSha = HashVerifier.ComputeFileSha256(vanillaX64ePath);

            // Record exact original bytes inside vanilla x64e.rpf
            var vanillaOpen = Rpf7Archive.Open(vanillaX64ePath);
            byte[] origYft = vanillaOpen.ExtractFile("levels/gta5/vehicles.rpf/police.yft");
            byte[] origHiYft = vanillaOpen.ExtractFile("levels/gta5/vehicles.rpf/police_hi.yft");
            byte[] origYtd = vanillaOpen.ExtractFile("levels/gta5/vehicles.rpf/police.ytd");

            var paths = new AppWorkspacePaths(Path.Combine(tempDir, "appdata"));
            var repo = new SqliteHistoryRepository(paths.DatabasePath);
            var backups = new BackupService(paths.BackupsDirectory, repo);
            var installer = new ModInstallerService(repo, backups, paths.RootDataDirectory);
            var analyzer = new AiModAnalyzerService(new ArchiveExtractorService(), new SecretStore(paths.RootDataDirectory));
            var gameEnv = new GameDetectionService().ScanEnvironment(gtaRoot, openIvExe);

            byte[] repYft = SyntheticGtaFixture.CreateSyntheticRsc7Asset("CUSTOM_POLICE_YFT_V3");
            byte[] repHiYft = SyntheticGtaFixture.CreateSyntheticRsc7Asset("CUSTOM_POLICE_HI_YFT_V3");
            byte[] repYtd = SyntheticGtaFixture.CreateSyntheticRsc7Asset("CUSTOM_POLICE_YTD_V3");
            byte[] newExtraHiYtd = SyntheticGtaFixture.CreateSyntheticRsc7Asset("CUSTOM_NEW_POLICE_PLUS_HI_YTD_V3");

            // Package as a binary .RAR archive
            string rarPath = SyntheticGtaFixture.CreateRar4Archive(
                Path.Combine(tempDir, "CrownVic_FullSet.rar"),
                ("x64e.rpf/levels/gta5/vehicles.rpf/police.yft", repYft),
                ("x64e.rpf/levels/gta5/vehicles.rpf/police_hi.yft", repHiYft),
                ("x64e.rpf/levels/gta5/vehicles.rpf/police.ytd", repYtd),
                ("x64e.rpf/levels/gta5/vehicles.rpf/police+hi.ytd", newExtraHiYtd));

            var plan = await analyzer.AnalyzeModAsync(rarPath, paths.StagingDirectory, gameEnv, new AiSettings { EnableCloudOrLocalLlm = false }, Array.Empty<InstalledModFileRecord>());
            var installRes = await installer.InstallAsync(plan, gameEnv);
            Assert.True(installRes.Succeeded);

            // 1. Vanilla x64e.rpf MUST remain 100% untouched!
            Assert.Equal(vanillaX64eSha, HashVerifier.ComputeFileSha256(vanillaX64ePath));

            // 2. Every replaced file (police.yft, police_hi.yft, police.ytd) MUST have a verified per-file backup in ledger + verified pre-install zip!
            var ledger = repo.GetModFiles(installRes.InstalledModId!);
            Assert.Equal(4, ledger.Count);
            foreach (var replacedName in new[] { "police.yft", "police_hi.yft", "police.ytd" })
            {
                var rec = Assert.Single(ledger, f => f.RpfInternalPath.EndsWith("/" + replacedName, StringComparison.OrdinalIgnoreCase));
                Assert.True(rec.ExistedBefore);
                Assert.NotNull(rec.PreviousFileStashPath);
                Assert.True(File.Exists(rec.PreviousFileStashPath!));
                Assert.NotNull(rec.Sha256Before);
                Assert.True(HashVerifier.VerifyFileSha256(rec.PreviousFileStashPath!, rec.Sha256Before!, out _));
            }

            string modsX64ePath = Path.Combine(gtaRoot, "mods", "x64e.rpf");
            var moddedRpf = Rpf7Archive.Open(modsX64ePath);
            Assert.Equal(repYft, moddedRpf.ExtractFile("levels/gta5/vehicles.rpf/police.yft"));
            Assert.Equal(repHiYft, moddedRpf.ExtractFile("levels/gta5/vehicles.rpf/police_hi.yft"));
            Assert.Equal(repYtd, moddedRpf.ExtractFile("levels/gta5/vehicles.rpf/police.ytd"));
            Assert.Equal(newExtraHiYtd, moddedRpf.ExtractFile("levels/gta5/vehicles.rpf/police+hi.ytd"));

            // 3. Uninstall Mod -> restores exact original police.yft, police_hi.yft, police.ytd AND removes newly added police+hi.ytd!
            bool uninstalled = await installer.UninstallAndRollbackModAsync(installRes.InstalledModId!, gameEnv);
            Assert.True(uninstalled);

            var restoredAfterUninstall = Rpf7Archive.Open(modsX64ePath);
            Assert.Equal(origYft, restoredAfterUninstall.ExtractFile("levels/gta5/vehicles.rpf/police.yft"));
            Assert.Equal(origHiYft, restoredAfterUninstall.ExtractFile("levels/gta5/vehicles.rpf/police_hi.yft"));
            Assert.Equal(origYtd, restoredAfterUninstall.ExtractFile("levels/gta5/vehicles.rpf/police.ytd"));
            Assert.False(restoredAfterUninstall.ContainsFile("levels/gta5/vehicles.rpf/police+hi.ytd"));

            // 4. Re-install and test Backup Snapshot Restore (RestoreVerifiedBackup) as well!
            var reinstallRes = await installer.InstallAsync(plan, gameEnv);
            Assert.True(reinstallRes.Succeeded);
            var preInstallBackup = repo.GetBackups().First(b => b.Id == reinstallRes.BackupId);
            backups.RestoreVerifiedBackup(preInstallBackup, gtaRoot);

            var restoredViaSnapshot = Rpf7Archive.Open(modsX64ePath);
            Assert.Equal(origYft, restoredViaSnapshot.ExtractFile("levels/gta5/vehicles.rpf/police.yft"));
            Assert.Equal(origHiYft, restoredViaSnapshot.ExtractFile("levels/gta5/vehicles.rpf/police_hi.yft"));
            Assert.Equal(origYtd, restoredViaSnapshot.ExtractFile("levels/gta5/vehicles.rpf/police.ytd"));
            Assert.Equal(vanillaX64eSha, HashVerifier.ComputeFileSha256(vanillaX64ePath));
        }
        finally
        {
            SyntheticGtaFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task RealWorldRegression_OpenUpdateRpfWithEncryptedBranchesMeta_AndNestedOivInsideRar_InstallsCleanly()
    {
        string tempDir = SyntheticGtaFixture.CreateTempDirectory();
        try
        {
            var (gtaRoot, openIvExe) = SyntheticGtaFixture.CreateSyntheticEpicGtaEnvironment(tempDir, buildNumber: 3889, openUpdateRpf: true);

            // Inject an individually NG-encrypted binary entry 'common/data/branches.meta' (EncryptionFlag = 1) into update.rpf
            // reproducing exact real-world GTA V v1.0.3889.0 update.rpf behavior!
            string vanillaUpdatePath = Path.Combine(gtaRoot, "update", "update.rpf");
            var updateRpf = Rpf7Archive.Open(vanillaUpdatePath);
            byte[] encryptedBranchesSectors = new byte[512];
            Random.Shared.NextBytes(encryptedBranchesSectors);
            updateRpf.RestoreOpaqueEncryptedEntry(
                "common/data/branches.meta",
                new OpaqueEncryptedEntryBlob(encryptedBranchesSectors, OriginalCompressedSize: 312, OriginalUncompressedSize: 640, EncryptionFlag: 1));
            updateRpf.SaveAndVerify(vanillaUpdatePath);

            // Build nested rmodgt63.oiv inside outer 294778-Mercedes-AMG GT63S Coupe 1.2.rar (plus Optional/vehicles.meta)
            string innerDlcTemp = Path.Combine(tempDir, "rmod_dlc.rpf");
            var innerDlc = Rpf7Archive.CreateNewOpenArchive();
            innerDlc.PutFile("setup2.xml", Encoding.UTF8.GetBytes("<SSetupData><deviceName>dlc_rmodgt63</deviceName></SSetupData>"));
            innerDlc.SaveAndVerify(innerDlcTemp);

            string oivAssembly = """
                <?xml version="1.0" encoding="UTF-8"?>
                <package version="2.1" id="{99999999-2222-3333-4444-555555555555}" target="Five">
                  <metadata>
                    <name>Mercedes-AMG GT63 S Coupe</name>
                    <version><major>1</major><minor>2</minor></version>
                    <author><displayName>Rmod Customs</displayName></author>
                    <description>Automatic Add-On OIV installer</description>
                  </metadata>
                  <content>
                    <add source="dlc.rpf">update\x64\dlcpacks\rmodgt63\dlc.rpf</add>
                    <xml path="update\update.rpf\common\data\dlclist.xml">
                      <add xpath="/SMandatoryPacksData/Paths">
                        <Item>dlcpacks:/rmodgt63/</Item>
                      </add>
                    </xml>
                  </content>
                </package>
                """;

            string innerOivPath = SyntheticGtaFixture.CreateZipArchive(
                Path.Combine(tempDir, "rmodgt63.oiv"),
                ("assembly.xml", Encoding.UTF8.GetBytes(oivAssembly)),
                ("content/dlc.rpf", await File.ReadAllBytesAsync(innerDlcTemp)));

            string outerRarPath = SyntheticGtaFixture.CreateRar4Archive(
                Path.Combine(tempDir, "294778-Mercedes-AMG GT63S Coupe 1.2.rar"),
                ("Mercedes-AMG GT63S Coupe 1.2/OpenIV Automatic Installation/rmodgt63.oiv", await File.ReadAllBytesAsync(innerOivPath)),
                ("Mercedes-AMG GT63S Coupe 1.2/Optional (No Tint)/vehicles.meta", Encoding.UTF8.GetBytes("<CVehicleModelInfo__InitDataList><InitDatas><Item><modelName>rmodgt63</modelName></Item></InitDatas></CVehicleModelInfo__InitDataList>")));

            var appPaths = new AppWorkspacePaths(Path.Combine(tempDir, "appdata"));
            var ui = new ConfigurableInteractionStub { ConfirmResult = true };
            var vm = new MainViewModel(appPaths, ui)
            {
                CustomGtaPathOverride = gtaRoot,
                CustomOpenIvOverride = openIvExe,
                EnableAiLlm = false
            };

            await vm.ScanGameAsync();
            Assert.Equal(3889, vm.GameEnv.BuildNumber);

            await vm.AnalyzeModInputAsync(outerRarPath);
            Assert.NotNull(vm.CurrentPlan);
            Assert.Equal("Mercedes-AMG GT63 S Coupe", vm.CurrentPlan!.ModName);
            Assert.Equal("1.2", vm.CurrentPlan.ModVersion);
            Assert.Equal("Rmod Customs", vm.CurrentPlan.Author);
            Assert.StartsWith("PREVIEW READY", vm.PlanStatusHeadline);

            // Install! Must NOT fail on encrypted branches.meta (flag=1)!
            await vm.ExecutePlannedInstallAsync();
            Assert.Single(vm.InstalledMods);
            Assert.Equal(InstallVerificationState.VerifiedComplete, vm.InstalledMods[0].VerificationState);
            Assert.StartsWith("INSTALLED & SHA-256 VERIFIED", vm.PlanStatusHeadline);

            // Verify dlc.rpf deployed and dlclist.xml updated AND branches.meta preserved encrypted inside mods/update/update.rpf!
            Assert.True(File.Exists(Path.Combine(gtaRoot, "mods", "update", "x64", "dlcpacks", "rmodgt63", "dlc.rpf")));
            var modsUpdate = Rpf7Archive.Open(Path.Combine(gtaRoot, "mods", "update", "update.rpf"));
            Assert.True(modsUpdate.IsEntryIndividuallyEncrypted("common/data/branches.meta"));
            var preservedBlob = modsUpdate.ExportOpaqueEncryptedEntry("common/data/branches.meta")!;
            Assert.Equal(1u, preservedBlob.EncryptionFlag);
            Assert.Equal(encryptedBranchesSectors, preservedBlob.RawSectorBytes);

            string dlcListXml = Encoding.UTF8.GetString(modsUpdate.ExtractFile(DlcListXmlService.RpfInternalDlcListPath));
            Assert.Contains("dlcpacks:/rmodgt63/", dlcListXml);
        }
        finally
        {
            SyntheticGtaFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task NoReadmeMod_TriggersDisclaimerPopup_AndRunsAiDeepFileAnalysis_ExtractingSpawnNamesAndSteps()
    {
        string tempDir = SyntheticGtaFixture.CreateTempDirectory();
        try
        {
            var (gtaRoot, openIvExe) = SyntheticGtaFixture.CreateSyntheticEpicGtaEnvironment(tempDir, buildNumber: 3889);
            var appPaths = new AppWorkspacePaths(Path.Combine(tempDir, "appdata"));
            var ui = new ConfigurableInteractionStub { ConfirmResult = true };

            // Mock local Ollama LLM endpoint verifying it receives the Deep Binary & XML File Inspection Dossier when no README exists!
            string? capturedPrompt = null;
            var mockHttp = new HttpClient(new StubHttpHandler(async req =>
            {
                capturedPrompt = await req.Content!.ReadAsStringAsync();
                string llmJson = """
                    {
                      "modName": "Brabus 800 Add-On (AI File Forensics)",
                      "version": "1.0",
                      "author": "Unknown (No README)",
                      "category": "AddOnDlcPack",
                      "confidence": 0.91,
                      "reasoningSummary": "No README was included; inspected dlc.rpf setup2.xml deviceName 'dlc_brabus800' and internal .yft model 'brabus800' from the Deep File Dossier.",
                      "spawnNames": ["brabus800"],
                      "warnings": ["No README included — verify custom handling prerequisites if any."],
                      "steps": [
                        {
                          "operation": "CopyFileToModsOrRoot",
                          "sourceRelativePath": "brabus800/dlc.rpf",
                          "targetRelativePath": "mods/update/x64/dlcpacks/brabus800/dlc.rpf",
                          "description": "Deploy Add-On dlc.rpf to mods/update/x64/dlcpacks/brabus800/dlc.rpf"
                        },
                        {
                          "operation": "RegisterDlcListEntry",
                          "dlcListEntry": "dlcpacks:/brabus800/",
                          "description": "Register dlcpacks:/brabus800/ in dlclist.xml"
                        }
                      ]
                    }
                    """;
                string openAiEnvelope = System.Text.Json.JsonSerializer.Serialize(new
                {
                    choices = new[] { new { message = new { content = llmJson } } }
                });
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(openAiEnvelope, Encoding.UTF8, "application/json")
                };
            }));

            var vm = new MainViewModel(appPaths, ui, httpClient: mockHttp)
            {
                CustomGtaPathOverride = gtaRoot,
                CustomOpenIvOverride = openIvExe
            };
            vm.UseLocalOllamaPresetCommand.Execute(null); // http://localhost:11434/v1/chat/completions
            await vm.ScanGameAsync();

            // Build a mod archive with ZERO README files — only brabus800/dlc.rpf containing setup2.xml and brabus800.yft!
            string dlcTemp = Path.Combine(tempDir, "brabus_dlc.rpf");
            var dlcRpf = Rpf7Archive.CreateNewOpenArchive();
            dlcRpf.PutFile("setup2.xml", Encoding.UTF8.GetBytes("<SSetupData><deviceName>dlc_brabus800</deviceName></SSetupData>"));
            dlcRpf.PutFile("x64/vehicles.rpf/brabus800.yft", SyntheticGtaFixture.CreateSyntheticRsc7Asset("BRABUS_YFT"));
            dlcRpf.PutFile("x64/vehicles.rpf/brabus800_hi.yft", SyntheticGtaFixture.CreateSyntheticRsc7Asset("BRABUS_HI_YFT"));
            dlcRpf.PutFile("x64/vehicles.rpf/brabus800.ytd", SyntheticGtaFixture.CreateSyntheticRsc7Asset("BRABUS_YTD"));
            dlcRpf.SaveAndVerify(dlcTemp);

            string noReadmeZip = SyntheticGtaFixture.CreateZipArchive(
                Path.Combine(tempDir, "Brabus800_NoReadme.zip"),
                ("brabus800/dlc.rpf", await File.ReadAllBytesAsync(dlcTemp)));

            // 1. User clicks Cancel on No-README popup -> aborts cleanly
            ui.NoReadmeChoice = NoReadmeUserChoice.Cancel;
            await vm.AnalyzeModInputAsync(noReadmeZip);
            Assert.Equal(1, ui.NoReadmePromptCallCount);
            Assert.Null(vm.CurrentPlan);

            // 2. User clicks Configure AI Settings on No-README popup -> jumps straight to AiSettings tab
            ui.NoReadmeChoice = NoReadmeUserChoice.OpenAiSettings;
            await vm.AnalyzeModInputAsync(noReadmeZip);
            Assert.Equal(2, ui.NoReadmePromptCallCount);
            Assert.Equal(WorkspaceTab.AiSettings, vm.SelectedTab);

            // 3. User clicks "Analyze Files With AI Anyway" -> runs Deep File Inspection + AI LLM analysis!
            ui.NoReadmeChoice = NoReadmeUserChoice.ProceedWithAiFileAnalysis;
            await vm.AnalyzeModInputAsync(noReadmeZip);
            Assert.Equal(3, ui.NoReadmePromptCallCount);
            Assert.NotNull(vm.CurrentPlan);
            Assert.False(vm.CurrentPlan!.HasReadme);
            Assert.Contains("brabus800", vm.CurrentPlan.DetectedSpawnNames);
            Assert.Contains(vm.CurrentPlan.Warnings, w => w.Contains("[No-README Disclaimer]"));
            Assert.NotNull(capturedPrompt);
            using var promptJson = System.Text.Json.JsonDocument.Parse(capturedPrompt!);
            string sentUserPrompt = promptJson.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            Assert.Contains("AI DEEP FILE & BINARY INSPECTION DOSSIER", sentUserPrompt);
            Assert.Contains("dlc_brabus800", sentUserPrompt);

            // Install and verify it works end-to-end!
            await vm.ExecutePlannedInstallAsync();
            Assert.Single(vm.InstalledMods);
            Assert.True(File.Exists(Path.Combine(gtaRoot, "mods", "update", "x64", "dlcpacks", "brabus800", "dlc.rpf")));
        }
        finally
        {
            SyntheticGtaFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task SafeMemoryAndJunkCleaner_RemovesLeftoverStagingCrashDumpsAndTempJunk_WhileStrictlyProtectingModsBackupsAndActivePlan()
    {
        string tempDir = SyntheticGtaFixture.CreateTempDirectory();
        try
        {
            var (gtaRoot, openIvExe) = SyntheticGtaFixture.CreateSyntheticEpicGtaEnvironment(tempDir, buildNumber: 3889);
            var appPaths = new AppWorkspacePaths(Path.Combine(tempDir, "appdata"));
            var ui = new ConfigurableInteractionStub { ConfirmResult = true };
            var vm = new MainViewModel(appPaths, ui)
            {
                CustomGtaPathOverride = gtaRoot,
                CustomOpenIvOverride = openIvExe
            };
            await vm.ScanGameAsync();

            // 1. Install a real Add-On mod so mods\, backups\, and active conflict_stash\<modId> exist on disk
            string dlcTemp = Path.Combine(tempDir, "installed_dlc.rpf");
            var dlcRpf = Rpf7Archive.CreateNewOpenArchive();
            dlcRpf.PutFile("setup2.xml", Encoding.UTF8.GetBytes("<SSetupData><deviceName>dlc_supra</deviceName></SSetupData>"));
            dlcRpf.SaveAndVerify(dlcTemp);

            string installedZip = SyntheticGtaFixture.CreateZipArchive(
                Path.Combine(tempDir, "Supra_AddOn.zip"),
                ("README.txt", Encoding.UTF8.GetBytes("Copy supra folder to mods/update/x64/dlcpacks/supra/dlc.rpf and add dlcpacks:/supra/ to dlclist.xml")),
                ("supra/dlc.rpf", await File.ReadAllBytesAsync(dlcTemp)));

            await vm.AnalyzeModInputAsync(installedZip);
            await vm.ExecutePlannedInstallAsync();
            Assert.Single(vm.InstalledMods);
            string installedModId = vm.InstalledMods[0].Id;
            string installedModDlc = Path.Combine(gtaRoot, "mods", "update", "x64", "dlcpacks", "supra", "dlc.rpf");
            string activeModStashDir = Path.Combine(appPaths.RootDataDirectory, "conflict_stash", installedModId);
            Assert.True(File.Exists(installedModDlc));
            Assert.True(Directory.Exists(activeModStashDir));

            // 2. Also open a SECOND mod in preview so CurrentPlan.StagingDirectory is live and MUST be protected!
            string previewZip = SyntheticGtaFixture.CreateZipArchive(
                Path.Combine(tempDir, "PreviewOnly.zip"),
                ("README.txt", Encoding.UTF8.GetBytes("Install handling.meta")),
                (" supra2/dlc.rpf".Trim(), await File.ReadAllBytesAsync(dlcTemp)));
            await vm.AnalyzeModInputAsync(previewZip);
            string activePreviewStagingDir = vm.CurrentPlan!.StagingDirectory;
            Assert.True(Directory.Exists(activePreviewStagingDir));

            // 3. Seed junk items that SHOULD be cleaned AND protected items that MUST NOT be cleaned
            string staleStagingDir = Path.Combine(appPaths.StagingDirectory, "old_leftover_staging_999");
            Directory.CreateDirectory(staleStagingDir);
            await File.WriteAllBytesAsync(Path.Combine(staleStagingDir, "leftover.bin"), new byte[128 * 1024]);

            string orphanStashDir = Path.Combine(appPaths.RootDataDirectory, "conflict_stash", "uninstalled_old_mod_xyz");
            Directory.CreateDirectory(orphanStashDir);
            await File.WriteAllBytesAsync(Path.Combine(orphanStashDir, "orphan.bin"), new byte[64 * 1024]);

            string crashDumpDir = Path.Combine(tempDir, "Rockstar Games", "GTA V", "CrashDumps");
            Directory.CreateDirectory(crashDumpDir);
            string gtaCrashDump = Path.Combine(crashDumpDir, "GTA5.exe.4412.dmp");
            await File.WriteAllBytesAsync(gtaCrashDump, new byte[256 * 1024]);

            string fakeSysTemp = Path.Combine(tempDir, "fake_win_temp");
            Directory.CreateDirectory(fakeSysTemp);
            string staleOpenIvTemp = Path.Combine(fakeSysTemp, "OpenIV_old_extract");
            Directory.CreateDirectory(staleOpenIvTemp);
            await File.WriteAllBytesAsync(Path.Combine(staleOpenIvTemp, "temp.rpf"), new byte[32 * 1024]);
            Directory.SetLastWriteTimeUtc(staleOpenIvTemp, DateTime.UtcNow.AddHours(-3));

            string freshOpenIvTemp = Path.Combine(fakeSysTemp, "OpenIV_active_now");
            Directory.CreateDirectory(freshOpenIvTemp);
            await File.WriteAllBytesAsync(Path.Combine(freshOpenIvTemp, "active.rpf"), new byte[16 * 1024]);

            // 4. Scan cleanup candidates
            await vm.ScanCleanupAsync(
                extraCrashDirsForTest: new[] { crashDumpDir },
                customTempDirForTest: fakeSysTemp);

            Assert.Equal(6, vm.CleanupCategories.Count);
            Assert.True(vm.MemorySnapshot.TotalPhysicalBytes > 0);

            // 5. Declining confirmation dialog must leave ALL junk untouched
            ui.ConfirmResult = false;
            var cancelled = await vm.ExecuteCleanupAsync();
            Assert.Null(cancelled);
            Assert.True(Directory.Exists(staleStagingDir));

            // 6. Confirming cleanup deletes all 4 junk targets while strictly preserving mods\, backups\, active stash, active preview, and fresh temp!
            ui.ConfirmResult = true;
            var result = await vm.ExecuteCleanupAsync();
            Assert.NotNull(result);
            Assert.True(result!.DeletedDiskItems >= 4);
            Assert.True(result.ReclaimedDiskBytes >= (128 + 64 + 256 + 32) * 1024);

            // Junk is gone:
            Assert.False(Directory.Exists(staleStagingDir));
            Assert.False(Directory.Exists(orphanStashDir));
            Assert.False(File.Exists(gtaCrashDump));
            Assert.False(Directory.Exists(staleOpenIvTemp));

            // Protected mod files, backups, active preview staging, and active temp are 100% UNTOUCHED:
            Assert.True(File.Exists(installedModDlc));
            Assert.True(Directory.Exists(activeModStashDir));
            Assert.True(Directory.Exists(activePreviewStagingDir));
            Assert.True(Directory.Exists(freshOpenIvTemp));
            Assert.Single(vm.Repository.GetInstalledMods());
        }
        finally
        {
            SyntheticGtaFixture.DeleteTempDirectory(tempDir);
        }
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request);
    }

    private sealed class ConfigurableInteractionStub : IUserInteractionService
    {
        public bool ConfirmResult { get; set; } = true;
        public ConflictResolutionStrategy ConflictResult { get; set; } = ConflictResolutionStrategy.BackupAndOverwrite;
        public RpfTargetSelectionDecision? TargetDecision { get; set; }
        public int TargetPromptCallCount { get; private set; }
        public NoReadmeUserChoice NoReadmeChoice { get; set; } = NoReadmeUserChoice.ProceedWithAiFileAnalysis;
        public int NoReadmePromptCallCount { get; private set; }

        public bool ConfirmDestructiveAction(string title, string message, string confirmButtonText = "Confirm") => ConfirmResult;
        public ConflictResolutionStrategy PromptConflictResolution(IReadOnlyList<ConflictItem> conflicts, string modName) => ConflictResult;

        public RpfTargetSelectionDecision? PromptAssetTargetSelection(ModInstallStep uncertainStep, string modName)
        {
            TargetPromptCallCount++;
            return TargetDecision;
        }

        public NoReadmeUserChoice PromptNoReadmeDisclaimer(
            string modName,
            int discoveredFileCount,
            bool isLlmActive,
            string activeEngineDescription)
        {
            NoReadmePromptCallCount++;
            return NoReadmeChoice;
        }

        public string? BrowseModArchiveFile() => null;
        public string? BrowseFolder(string title, string? initialDirectory = null) => null;
        public string? PromptSaveFile(string title, string defaultFileName, string filter) => null;
        public (string Name, string Description)? PromptNewProfile() => ("Default", "Test");
        public void ShowNotification(string title, string message, bool isError = false) { }
    }
}
