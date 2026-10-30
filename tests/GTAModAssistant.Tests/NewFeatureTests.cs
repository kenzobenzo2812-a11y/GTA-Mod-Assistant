using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using GTAModAssistant.Core.Models;
using GTAModAssistant.Core.Services;
using Xunit;

namespace GTAModAssistant.Tests;

public sealed class NewFeatureTests
{
    [Fact]
    public void CrashDiagnosis_ReportsActionableSignatures_WithoutClaimingCertainty()
    {
        var service = new CrashDiagnosisService();

        CrashDiagnosisReport report = service.Diagnose(
            "Loading ScriptHookV.dll\nERR_MEM_EMBEDDEDALLOC: out of game memory",
            "launcher.log");

        Assert.Equal("launcher.log", report.SourceFileName);
        Assert.Contains(report.Findings, finding => finding.Title.Contains("ScriptHookV"));
        Assert.Contains(report.Findings, finding => finding.Title.Contains("memory", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("not a definitive diagnosis", report.Summary);
    }

    [Fact]
    public async Task CrashDiagnosis_RejectsBinaryAndOversizedInputs()
    {
        var service = new CrashDiagnosisService();
        string directory = CreateTempDirectory();
        try
        {
            string dumpPath = Path.Combine(directory, "crash.dmp");
            await File.WriteAllTextAsync(dumpPath, "data");
            await Assert.ThrowsAsync<InvalidDataException>(() => service.DiagnoseFileAsync(dumpPath));

            string largeLog = Path.Combine(directory, "large.log");
            await File.WriteAllBytesAsync(largeLog, new byte[10 * 1024 * 1024 + 1]);
            await Assert.ThrowsAsync<InvalidDataException>(() => service.DiagnoseFileAsync(largeLog));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void AssetPreview_DescribesImagesAndRsc7WithoutExecutingOrModifyingThem()
    {
        string directory = CreateTempDirectory();
        try
        {
            string imagePath = Path.Combine(directory, "livery.png");
            File.WriteAllBytes(imagePath, [137, 80, 78, 71]);
            string modelPath = Path.Combine(directory, "police.yft");
            byte[] header = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(header, 0x37435352);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 165);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 0x50000010);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 0x20000004);
            File.WriteAllBytes(modelPath, header);

            var service = new AssetPreviewService();
            IReadOnlyList<AssetPreviewItem> assets = service.DescribeAssets(
            [
                new ExtractedFileEntry("livery.png", imagePath, 4, ".png", string.Empty),
                new ExtractedFileEntry("police.yft", modelPath, 16, ".yft", string.Empty)
            ]);

            Assert.Equal(2, assets.Count);
            Assert.True(assets.Single(asset => asset.Kind == "Image").CanRenderImage);
            Assert.Contains("RSC7 resource v165", assets.Single(asset => asset.Kind.Contains(".yft")).Details);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void CompatibilityChecker_WarnsWhenRequiredLoaderIsMissing()
    {
        var game = new GameEnvironmentInfo
        {
            IsDetected = true,
            BuildNumber = 3411,
            GameVersion = "1.0.3411.0",
            BuildTitleName = "Test build"
        };
        var files = new[]
        {
            new ExtractedFileEntry("Trainer.ASI", "Trainer.ASI", 10, ".ASI", string.Empty)
        };

        CompatibilityReport report = VersionCompatibilityService.EvaluateCompatibility(
            game,
            ModCategory.AsiPlugin,
            files,
            "Trainer");

        Assert.Equal(CompatibilitySeverity.Advisory, report.Severity);
        Assert.Contains(report.Details, detail => detail.Contains("ASI Loader"));
        Assert.Contains(report.Details, detail => detail.Contains("ScriptHookV.dll"));
    }

    [Fact]
    public void Modpack_ExportsProfileSourcesAndImportsWithHashVerification()
    {
        string directory = CreateTempDirectory();
        try
        {
            string sourcePath = Path.Combine(directory, "sample_mod.zip");
            SyntheticGtaFixture.CreateZipArchive(
                sourcePath,
                ("readme.txt", Encoding.UTF8.GetBytes("Test package")),
                ("mod.asi", [1, 2, 3, 4]));
            string modpackPath = Path.Combine(directory, "setup.gtmapack");
            var profile = new ModProfile { Id = "default", Name = "Story Mode", Description = "Safe setup" };
            var mod = new InstalledModRecord
            {
                Id = "mod-123",
                Name = "Test Mod",
                Version = "2.0",
                Author = "Author",
                SourcePath = sourcePath,
                Category = ModCategory.AsiPlugin,
                IsEnabled = false
            };
            var service = new ModpackService();

            service.Export(
                modpackPath,
                profile,
                [mod, mod with { Name = "Test Mod 2" }],
                new Dictionary<string, bool> { ["mod-123"] = true },
                3411);
            ImportedModpack imported = service.Import(modpackPath, Path.Combine(directory, "imports"));

            Assert.Equal("Story Mode", imported.Manifest.Name);
            Assert.Equal(3411, imported.Manifest.GameBuild);
            Assert.All(imported.Manifest.Mods, item => Assert.True(item.Enabled));
            Assert.Equal(2, imported.ModSourcePaths.Count);
            foreach (string importedSource in imported.ModSourcePaths)
            {
                Assert.True(File.Exists(importedSource));
                Assert.Equal(File.ReadAllBytes(sourcePath), File.ReadAllBytes(importedSource));
            }

            using (ZipArchive archive = ZipFile.Open(modpackPath, ZipArchiveMode.Update))
            {
                ZipArchiveEntry entry = archive.GetEntry("packages/mod-123_0/sample_mod.zip")!;
                byte[] changedPackage;
                using (Stream input = entry.Open())
                using (var buffer = new MemoryStream())
                {
                    input.CopyTo(buffer);
                    changedPackage = buffer.ToArray();
                }
                changedPackage[0] ^= 0xFF;
                entry.Delete();
                ZipArchiveEntry replacement = archive.CreateEntry("packages/mod-123_0/sample_mod.zip");
                using Stream output = replacement.Open();
                output.Write(changedPackage);
            }

            string tamperedImport = Path.Combine(directory, "tampered-import");
            Assert.Throws<InvalidDataException>(() => service.Import(modpackPath, tamperedImport));
            Assert.Empty(Directory.GetFiles(tamperedImport, "*", SearchOption.AllDirectories));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void Modpack_FailedExportPreservesExistingDestination()
    {
        string directory = CreateTempDirectory();
        try
        {
            string destination = Path.Combine(directory, "existing.gtmapack");
            File.WriteAllText(destination, "existing content");
            var profile = new ModProfile { Id = "default", Name = "Story Mode", Description = "Safe setup" };
            var missingMod = new InstalledModRecord
            {
                Id = "missing",
                Name = "Missing source",
                SourcePath = Path.Combine(directory, "missing.zip")
            };

            Assert.Throws<FileNotFoundException>(() =>
                new ModpackService().Export(destination, profile, [missingMod], new Dictionary<string, bool>(), 0));
            Assert.Equal("existing content", File.ReadAllText(destination));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void Modpack_RejectsUnsafePackagePaths()
    {
        string directory = CreateTempDirectory();
        try
        {
            string modpackPath = Path.Combine(directory, "unsafe.gtmapack");
            using (ZipArchive archive = ZipFile.Open(modpackPath, ZipArchiveMode.Create))
            {
                ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.json");
                using StreamWriter writer = new(manifestEntry.Open());
                writer.Write(
                    """{"FormatVersion":1,"Name":"unsafe","Mods":[{"Id":"x","Name":"x","PackageRoot":"packages/../escape","IsArchive":false,"Files":[{"Path":"file.txt","SizeBytes":1,"Sha256":"00"}]}]}""");
            }

            var service = new ModpackService();
            Assert.Throws<InvalidDataException>(() =>
                service.Import(modpackPath, Path.Combine(directory, "imports")));
            Assert.Empty(Directory.GetFiles(Path.Combine(directory, "imports"), "*", SearchOption.AllDirectories));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task UpdateCheck_ReturnsOnlyNewerStableRelease()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(
            """{"tag_name":"v1.2.1","html_url":"https://github.com/kenzobenzo2812-a11y/GTA-Mod-Assistant/releases/tag/v1.2.1","name":"v1.2.1"}"""));
        var service = new GitHubUpdateService(client);

        AvailableRelease? update = await service.CheckForUpdateAsync("v1.2.0");
        AvailableRelease? current = await service.CheckForUpdateAsync("v1.2.1");

        Assert.NotNull(update);
        Assert.Equal("v1.2.1", update.Version);
        Assert.Null(current);
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "GTAModAssistant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class StubHttpMessageHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("api.github.com", request.RequestUri?.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
    }
}
