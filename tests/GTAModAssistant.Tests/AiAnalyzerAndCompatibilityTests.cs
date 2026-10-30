using System.Net;
using System.Text;
using Xunit;
using GTAModAssistant.Core.Archives;
using GTAModAssistant.Core.Models;
using GTAModAssistant.Core.Security;
using GTAModAssistant.Core.Services;

namespace GTAModAssistant.Tests;

public sealed class AiAnalyzerAndCompatibilityTests
{
    [Fact]
    public async Task Analyzer_CorrectlyPlans_AddOnDlc_And_RewritesVanillaReadmeRpfPathsToMods()
    {
        string tempDir = SyntheticGtaFixture.CreateTempDirectory();
        try
        {
            var (gtaRoot, openIvExe) = SyntheticGtaFixture.CreateSyntheticEpicGtaEnvironment(tempDir, buildNumber: 3411);
            var gameEnv = new GameDetectionService().ScanEnvironment(gtaRoot, openIvExe);

            // Build valid OPEN dlc.rpf
            string dlcRpfTemp = Path.Combine(tempDir, "dlc.rpf");
            var dlcRpf = Rpf7Archive.CreateNewOpenArchive();
            dlcRpf.PutFile("setup2.xml", Encoding.UTF8.GetBytes("<SSetupData><deviceName>dlc_elegyrh7</deviceName></SSetupData>"));
            dlcRpf.SaveAndVerify(dlcRpfTemp);

            string addonZip = Path.Combine(tempDir, "Annis_Elegy_RH7.zip");
            string readme = """
                Annis Elegy RH7 Add-On v2.4
                Author: Vanillaworks Team
                Built for GTA V v1.0.3095+
                1. Put elegyrh7 folder into update\x64\dlcpacks\
                2. Add <Item>dlcpacks:/elegyrh7/</Item> to update\update.rpf\common\data\dlclist.xml
                """;
            SyntheticGtaFixture.CreateZipArchive(
                addonZip,
                ("elegyrh7/dlc.rpf", File.ReadAllBytes(dlcRpfTemp)),
                ("README.txt", Encoding.UTF8.GetBytes(readme)));

            var analyzer = new AiModAnalyzerService(new ArchiveExtractorService(), new SecretStore(tempDir));
            var plan = await analyzer.AnalyzeModAsync(
                addonZip,
                Path.Combine(tempDir, "staging"),
                gameEnv,
                new AiSettings { EnableCloudOrLocalLlm = false },
                Array.Empty<InstalledModFileRecord>());

            Assert.True(plan.CanInstall);
            Assert.Equal(ModCategory.AddOnDlcPack, plan.Category);
            Assert.Equal("2.4", plan.ModVersion);
            Assert.Equal("Vanillaworks Team", plan.Author);

            Assert.Contains(plan.Steps, s =>
                s.Operation == InstallOperationKind.CopyFileToModsOrRoot &&
                s.TargetRelativePath == "mods/update/x64/dlcpacks/elegyrh7/dlc.rpf");

            Assert.Contains(plan.Steps, s =>
                s.Operation == InstallOperationKind.RegisterDlcListEntry &&
                s.DlcListEntry == "dlcpacks:/elegyrh7/");
        }
        finally
        {
            SyntheticGtaFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task Analyzer_MapsReplaceYddYtdYft_FromReadmeIntoSafeModsRpf_AndDetectsDotNetScriptDll()
    {
        string tempDir = SyntheticGtaFixture.CreateTempDirectory();
        try
        {
            var (gtaRoot, openIvExe) = SyntheticGtaFixture.CreateSyntheticEpicGtaEnvironment(tempDir, buildNumber: 3411);
            var gameEnv = new GameDetectionService().ScanEnvironment(gtaRoot, openIvExe);

            // Use our own compiled Core.dll as a real .NET CLR assembly for PeBinaryInspector!
            string realManagedDllPath = typeof(Rpf7Archive).Assembly.Location;
            byte[] managedDllBytes = File.ReadAllBytes(realManagedDllPath);

            string replaceZip = Path.Combine(tempDir, "LSPD_Cruiser_And_Radar.zip");
            string readme = """
                Installation:
                Open OpenIV and navigate to:
                x64e.rpf\levels\gta5\vehicles.rpf\
                Replace police.yft and police.ytd.
                Put PoliceRadar.dll and PoliceRadar.ini in your scripts folder.
                """;
            SyntheticGtaFixture.CreateZipArchive(
                replaceZip,
                ("Vehicle/police.yft", SyntheticGtaFixture.CreateSyntheticRsc7Asset("MOD_POLICE_YFT")),
                ("Vehicle/police.ytd", SyntheticGtaFixture.CreateSyntheticRsc7Asset("MOD_POLICE_YTD")),
                ("Plugin/PoliceRadar.dll", managedDllBytes),
                ("Plugin/PoliceRadar.ini", Encoding.UTF8.GetBytes("[Radar]\nKey=F5")),
                ("Readme.txt", Encoding.UTF8.GetBytes(readme)));

            var analyzer = new AiModAnalyzerService(new ArchiveExtractorService(), new SecretStore(tempDir));
            var plan = await analyzer.AnalyzeModAsync(
                replaceZip,
                Path.Combine(tempDir, "staging"),
                gameEnv,
                new AiSettings { EnableCloudOrLocalLlm = false },
                Array.Empty<InstalledModFileRecord>());

            Assert.True(plan.CanInstall);
            Assert.Equal(ModCategory.CompositeMod, plan.Category);

            // Verify RPF replacement routed safely to mods/x64e.rpf -> levels/gta5/vehicles.rpf/police.yft
            Assert.Contains(plan.Steps, s =>
                s.Operation == InstallOperationKind.InjectIntoRpfArchive &&
                s.RpfContainerRelativePath == "mods/x64e.rpf" &&
                s.RpfInternalPath == "levels/gta5/vehicles.rpf/police.yft");

            // Verify managed .NET DLL routed to scripts/PoliceRadar.dll
            Assert.Contains(plan.Steps, s =>
                s.Operation == InstallOperationKind.CopyFileToModsOrRoot &&
                s.TargetRelativePath == "scripts/PoliceRadar.dll");
        }
        finally
        {
            SyntheticGtaFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task CompatibilityEngine_BlocksOutdatedGameconfigXml_AndWarnsOnOutdatedAsi()
    {
        string tempDir = SyntheticGtaFixture.CreateTempDirectory();
        try
        {
            var (gtaRoot, openIvExe) = SyntheticGtaFixture.CreateSyntheticEpicGtaEnvironment(tempDir, buildNumber: 3411);
            var gameEnv = new GameDetectionService().ScanEnvironment(gtaRoot, openIvExe);
            var analyzer = new AiModAnalyzerService(new ArchiveExtractorService(), new SecretStore(tempDir));

            // 1. Outdated gameconfig.xml built for 1.0.2802.0 -> MUST BE BLOCKED!
            string oldGameconfigZip = Path.Combine(tempDir, "Gameconfig_v2802.zip");
            SyntheticGtaFixture.CreateZipArchive(
                oldGameconfigZip,
                ("gameconfig.xml", Encoding.UTF8.GetBytes("<CGameConfig><PoolSize>2000</PoolSize></CGameConfig>")),
                ("readme.txt", Encoding.UTF8.GetBytes("Custom Gameconfig for GTA V 1.0.2802.0 (Los Santos Drug Wars)")));

            var gcPlan = await analyzer.AnalyzeModAsync(
                oldGameconfigZip,
                Path.Combine(tempDir, "staging1"),
                gameEnv,
                new AiSettings { EnableCloudOrLocalLlm = false },
                Array.Empty<InstalledModFileRecord>());

            Assert.Equal(CompatibilitySeverity.IncompatibleBlocked, gcPlan.Compatibility.Severity);
            Assert.False(gcPlan.CanInstall);

            // 2. Old .asi built for Build 2699 -> WarningOutdated, still installable with backup
            string oldAsiZip = Path.Combine(tempDir, "OldSpeedometer_b2699.zip");
            SyntheticGtaFixture.CreateZipArchive(
                oldAsiZip,
                ("OldSpeedo.asi", Encoding.UTF8.GetBytes("ASI_DATA")),
                ("readme.txt", Encoding.UTF8.GetBytes("Tested on GTA V 1.0.2699.0")));

            var asiPlan = await analyzer.AnalyzeModAsync(
                oldAsiZip,
                Path.Combine(tempDir, "staging2"),
                gameEnv,
                new AiSettings { EnableCloudOrLocalLlm = false },
                Array.Empty<InstalledModFileRecord>());

            Assert.Equal(CompatibilitySeverity.WarningOutdated, asiPlan.Compatibility.Severity);
            Assert.True(asiPlan.CanInstall);
        }
        finally
        {
            SyntheticGtaFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task HybridLlmAnalyzer_SanitizesVanillaPathsProposedByLlm_ToSafeModsPaths()
    {
        string tempDir = SyntheticGtaFixture.CreateTempDirectory();
        try
        {
            var (gtaRoot, openIvExe) = SyntheticGtaFixture.CreateSyntheticEpicGtaEnvironment(tempDir, buildNumber: 3411);
            var gameEnv = new GameDetectionService().ScanEnvironment(gtaRoot, openIvExe);

            string modZip = Path.Combine(tempDir, "VisualSettingsMod.zip");
            SyntheticGtaFixture.CreateZipArchive(
                modZip,
                ("visualsettings.dat", Encoding.UTF8.GetBytes("# Visual settings\ncar.headlight.intensity 5.0\n")),
                ("README.txt", Encoding.UTF8.GetBytes("Install visualsettings.dat into update.rpf")));

            // Mock localhost LLM returning vanilla "update/update.rpf" without "mods/" prefix
            string mockLlmJson = """
                {
                  "choices": [{
                    "message": {
                      "content": "{\"modName\":\"Bright Headlights Visuals\",\"version\":\"1.5\",\"author\":\"VisualModAuthor\",\"category\":\"DataOrConfigMod\",\"confidence\":0.96,\"reasoningSummary\":\"LLM parsed README\",\"steps\":[{\"operation\":\"InjectIntoRpfArchive\",\"sourceRelativePath\":\"visualsettings.dat\",\"rpfContainerRelativePath\":\"update/update.rpf\",\"rpfInternalPath\":\"common/data/visualsettings.dat\",\"description\":\"Inject visualsettings.dat\"}]}"
                    }
                  }]
                }
                """;
            using var httpClient = new HttpClient(new StubHttpHandler(mockLlmJson));
            var analyzer = new AiModAnalyzerService(new ArchiveExtractorService(), new SecretStore(tempDir), httpClient);

            var plan = await analyzer.AnalyzeModAsync(
                modZip,
                Path.Combine(tempDir, "staging"),
                gameEnv,
                new AiSettings { EnableCloudOrLocalLlm = true, EndpointUrl = "http://localhost:11434/v1/chat/completions", ModelName = "mock-llm" },
                Array.Empty<InstalledModFileRecord>());

            Assert.Equal("Bright Headlights Visuals", plan.ModName);
            var step = Assert.Single(plan.Steps);
            // Validator MUST rewrite update/update.rpf -> mods/update/update.rpf!
            Assert.Equal("mods/update/update.rpf", step.RpfContainerRelativePath);
            Assert.True(step.UsesSafeModsFolder);
        }
        finally
        {
            SyntheticGtaFixture.DeleteTempDirectory(tempDir);
        }
    }

    private sealed class StubHttpHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(resp);
        }
    }
}
