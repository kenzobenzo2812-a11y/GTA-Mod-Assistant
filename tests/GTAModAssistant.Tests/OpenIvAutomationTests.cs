using System.Text;
using GTAModAssistant.Core.Archives;
using Xunit;

namespace GTAModAssistant.Tests;

public sealed class OpenIvAutomationTests
{
    [Fact]
    public async Task LaunchAndVerify_UsesInjectedInstallerAndRequiresVerifiedRpfChange()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string tempDir = SyntheticGtaFixture.CreateTempDirectory();
        try
        {
            string openIvExe = Path.Combine(tempDir, "OpenIV.exe");
            string targetRpf = Path.Combine(tempDir, "mods.rpf");
            File.WriteAllBytes(openIvExe, Array.Empty<byte>());

            var originalArchive = Rpf7Archive.CreateNewOpenArchive();
            originalArchive.PutFile("common/data/original.xml", Encoding.UTF8.GetBytes("<Original />"));
            originalArchive.SaveAndVerify(targetRpf);

            var automation = new FakeOpenIvPackageInstallerAutomation(targetRpf);
            var verification = await OivPackageService.LaunchOpenIvAndVerifyAsync(
                openIvExe,
                Path.Combine(tempDir, "package.oiv"),
                new[] { targetRpf },
                installerAutomation: automation,
                expectedPackageName: "Test Mod");

            Assert.True(verification.VerifiedChanged);
            Assert.Equal(1, automation.Calls);
            Assert.Equal("Test Mod", automation.ExpectedPackageName);
            Assert.Contains("Verified post-OpenIV", verification.Details);
        }
        finally
        {
            SyntheticGtaFixture.DeleteTempDirectory(tempDir);
        }
    }

    private sealed class FakeOpenIvPackageInstallerAutomation(string targetRpf) : IOpenIvPackageInstallerAutomation
    {
        public int Calls { get; private set; }
        public string ExpectedPackageName { get; private set; } = string.Empty;

        public Task<OpenIvAutomationResult> InstallAsync(
            string openIvExePath,
            string oivPackagePath,
            string expectedPackageName,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            ExpectedPackageName = expectedPackageName;

            var modifiedArchive = Rpf7Archive.CreateNewOpenArchive();
            modifiedArchive.PutFile("common/data/installed.xml", Encoding.UTF8.GetBytes("<Installed />"));
            modifiedArchive.SaveAndVerify(targetRpf);

            return Task.FromResult(new OpenIvAutomationResult(true, "Test automation completed."));
        }
    }
}
