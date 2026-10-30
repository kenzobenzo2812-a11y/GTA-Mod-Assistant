using System.Text;
using Xunit;
using GTAModAssistant.Core.Archives;
using GTAModAssistant.Core.Models;
using GTAModAssistant.Core.Security;
using GTAModAssistant.Core.Services;

namespace GTAModAssistant.Tests;

public sealed class SecurityAndDetectionTests
{
    [Fact]
    public void GameDetectionService_Detects_EpicGames_BuildVersion_OpenIv_And_Prerequisites()
    {
        string tempDir = SyntheticGtaFixture.CreateTempDirectory();
        try
        {
            var (gtaRoot, openIvExe) = SyntheticGtaFixture.CreateSyntheticEpicGtaEnvironment(tempDir, buildNumber: 3411);
            var detector = new GameDetectionService();
            var info = detector.ScanEnvironment(gtaRoot, openIvExe);

            Assert.True(info.IsDetected);
            Assert.Equal(GamePlatform.EpicGames, info.Platform);
            Assert.Equal("1.0.3411.0", info.GameVersion);
            Assert.Equal(3411, info.BuildNumber);
            Assert.True(info.OpenIvInstalled);
            Assert.Equal(Path.GetFullPath(openIvExe), info.OpenIvExecutablePath);
            Assert.True(info.AsiLoaderInstalled);
            Assert.True(info.OpenIvAsiInstalled);
            Assert.True(info.ScriptHookVInstalled);
        }
        finally
        {
            SyntheticGtaFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void ArchiveExtractorService_Blocks_ZipSlip_And_Bundled_Executables_Or_Scripts()
    {
        string tempDir = SyntheticGtaFixture.CreateTempDirectory();
        try
        {
            string zipPath = Path.Combine(tempDir, "untrusted_mod.zip");
            SyntheticGtaFixture.CreateZipArchive(
                zipPath,
                ("../../evil_escape.dll", Encoding.UTF8.GetBytes("ESCAPE")),
                ("setup_trojan.exe", Encoding.UTF8.GetBytes("MZ_FAKE_EXE")),
                ("run_me.bat", Encoding.UTF8.GetBytes("@echo off\nformat c:")),
                ("Loader.ps1", Encoding.UTF8.GetBytes("Invoke-WebRequest ...")),
                ("SafeTrainer.asi", Encoding.UTF8.GetBytes("SAFE_ASI_PAYLOAD")),
                ("README.txt", Encoding.UTF8.GetBytes("Drop SafeTrainer.asi into GTA V folder.")));

            var extractor = new ArchiveExtractorService();
            var staged = extractor.StageInput(zipPath, Path.Combine(tempDir, "staging"));

            // Only SafeTrainer.asi and README.txt should be staged
            Assert.Equal(2, staged.Files.Count);
            Assert.Contains(staged.Files, f => f.RelativePath == "SafeTrainer.asi");
            Assert.DoesNotContain(staged.Files, f => f.Extension is ".exe" or ".bat" or ".ps1");

            // Zip-slip + 3 executables/scripts flagged in SecurityFindings
            Assert.Contains(staged.ExtractionSecurityFindings, f => f.Severity == SecuritySeverity.BlockedUnsafePath);
            Assert.Equal(3, staged.ExtractionSecurityFindings.Count(f => f.Severity == SecuritySeverity.BlockedExecutable));
            Assert.False(File.Exists(Path.Combine(tempDir, "evil_escape.dll")));
        }
        finally
        {
            SyntheticGtaFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void PathSafetyGuard_RewritesVanillaRpfPathsToMods_AndProtectsVanillaRootFiles()
    {
        Assert.Equal("mods/update/update.rpf", PathSafetyGuard.EnsureSafeModsPath(@"update\update.rpf"));
        Assert.Equal("mods/x64e.rpf", PathSafetyGuard.EnsureSafeModsPath("x64e.rpf"));
        Assert.Equal("mods/update/x64/dlcpacks/mycar/dlc.rpf", PathSafetyGuard.EnsureSafeModsPath(@"update\x64\dlcpacks\mycar\dlc.rpf"));

        Assert.True(PathSafetyGuard.IsProtectedVanillaRootFile("GTA5.exe"));
        Assert.True(PathSafetyGuard.IsProtectedVanillaRootFile("EOSSDK-Win64-Shipping.dll"));
        Assert.True(PathSafetyGuard.IsProtectedVanillaRootFile("x64a.rpf"));
        Assert.False(PathSafetyGuard.IsProtectedVanillaRootFile("SimpleTrainer.asi"));
        Assert.False(PathSafetyGuard.IsProtectedVanillaRootFile("mods/x64a.rpf"));
    }
}
