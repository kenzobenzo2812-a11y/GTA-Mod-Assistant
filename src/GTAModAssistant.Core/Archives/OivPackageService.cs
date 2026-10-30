using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using GTAModAssistant.Core.Models;
using GTAModAssistant.Core.Security;

namespace GTAModAssistant.Core.Archives;

public sealed record ParsedOivMetadata(
    string PackageId,
    string FormatVersion,
    string TargetGame,
    string Name,
    string Version,
    string Author,
    string Description,
    IReadOnlyList<ModInstallStep> ExtractedSteps,
    IReadOnlyList<SecurityFinding> SecurityFindings,
    IReadOnlyList<string> Warnings);

public sealed record OpenIvCommitVerification(
    bool VerifiedChanged,
    string Details,
    IReadOnlyDictionary<string, string> PostHashes);

public sealed record OpenIvAutomationResult(bool Completed, string Details);

public interface IOpenIvPackageInstallerAutomation
{
    Task<OpenIvAutomationResult> InstallAsync(
        string openIvExePath,
        string oivPackagePath,
        string expectedPackageName,
        CancellationToken cancellationToken = default);
}

public static class OivPackageService
{
    public static ParsedOivMetadata ParseExtractedOivDirectory(string stagingDir)
    {
        string assemblyXmlPath = Directory.EnumerateFiles(stagingDir, "assembly.xml", SearchOption.AllDirectories)
            .OrderBy(p => p.Length)
            .FirstOrDefault()
            ?? throw new FileNotFoundException("Invalid .oiv package: missing assembly.xml.", "assembly.xml");

        string oivRoot = Path.GetDirectoryName(assemblyXmlPath)!;
        string contentDir = Path.Combine(oivRoot, "content");
        if (!Directory.Exists(contentDir))
        {
            contentDir = oivRoot;
        }

        byte[] xmlBytes = File.ReadAllBytes(assemblyXmlPath);
        if (!DlcListXmlService.ValidateGtaXml(xmlBytes, out string? xmlErr))
        {
            throw new InvalidDataException($"Invalid assembly.xml in .oiv package: {xmlErr}");
        }

        using var ms = new MemoryStream(xmlBytes);
        using var reader = XmlReader.Create(ms, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        var doc = XDocument.Load(reader);
        var root = doc.Root ?? throw new InvalidDataException("assembly.xml has no root element.");

        string pkgVersion = (string?)root.Attribute("version") ?? "2.1";
        string pkgId = (string?)root.Attribute("id") ?? Guid.NewGuid().ToString();
        string targetGame = (string?)root.Attribute("target") ?? "Five";

        var meta = root.Element("metadata");
        string name = meta?.Element("name")?.Value.Trim() ?? Path.GetFileNameWithoutExtension(stagingDir);
        string verMajor = meta?.Element("version")?.Element("major")?.Value.Trim() ?? "1";
        string verMinor = meta?.Element("version")?.Element("minor")?.Value.Trim() ?? "0";
        string version = $"{verMajor}.{verMinor}";
        string author = meta?.Element("author")?.Element("displayName")?.Value.Trim() ?? "Unknown";
        string description = meta?.Element("description")?.Value.Trim() ?? string.Empty;

        var steps = new List<ModInstallStep>();
        var findings = new List<SecurityFinding>();
        var warnings = new List<string>();

        if (!string.Equals(targetGame, "Five", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"OIV package targets '{targetGame}' rather than 'Five' (GTA V).");
        }

        var contentEl = root.Element("content");
        if (contentEl is not null)
        {
            TraverseOivContentElement(contentEl, contentDir, archiveChain: new List<string>(), steps, findings, warnings);
        }

        return new ParsedOivMetadata(
            pkgId,
            pkgVersion,
            targetGame,
            name,
            version,
            author,
            description,
            steps,
            findings,
            warnings);
    }

    private static void TraverseOivContentElement(
        XElement parent,
        string contentRootDir,
        List<string> archiveChain,
        List<ModInstallStep> steps,
        List<SecurityFinding> findings,
        List<string> warnings)
    {
        foreach (var el in parent.Elements())
        {
            string localName = el.Name.LocalName.ToLowerInvariant();
            if (localName == "archive")
            {
                string rawArchivePath = ((string?)el.Attribute("path") ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(rawArchivePath))
                {
                    var nextChain = new List<string>(archiveChain) { rawArchivePath };
                    TraverseOivContentElement(el, contentRootDir, nextChain, steps, findings, warnings);
                }
            }
            else if (localName == "add")
            {
                string sourceAttr = ((string?)el.Attribute("source") ?? string.Empty).Trim();
                string targetVal = el.Value.Trim();
                if (string.IsNullOrEmpty(sourceAttr) || string.IsNullOrEmpty(targetVal))
                {
                    continue;
                }

                string sourceFullPath;
                try
                {
                    sourceFullPath = PathSafetyGuard.ResolveSafeChildPath(contentRootDir, sourceAttr);
                }
                catch (Exception ex)
                {
                    findings.Add(new SecurityFinding(SecuritySeverity.BlockedUnsafePath, sourceAttr, ex.Message));
                    continue;
                }

                if (PathSafetyGuard.IsBlockedExecutableOrScript(sourceAttr) ||
                    PathSafetyGuard.IsBlockedExecutableOrScript(targetVal))
                {
                    findings.Add(new SecurityFinding(
                        SecuritySeverity.BlockedExecutable,
                        sourceAttr,
                        "OIV package attempted to deploy an executable or script file; automatically blocked."));
                    continue;
                }

                if (archiveChain.Count == 0)
                {
                    string safeTarget = PathSafetyGuard.EnsureSafeModsPath(targetVal);
                    steps.Add(new ModInstallStep
                    {
                        Operation = InstallOperationKind.CopyFileToModsOrRoot,
                        SourceRelativePath = PathSafetyGuard.NormalizeRelativePath(sourceAttr),
                        SourceFullPath = sourceFullPath,
                        TargetRelativePath = safeTarget,
                        UsesSafeModsFolder = safeTarget.StartsWith("mods/", StringComparison.OrdinalIgnoreCase),
                        Description = $"Deploy '{sourceAttr}' -> '{safeTarget}'"
                    });
                }
                else
                {
                    string outerRpf = PathSafetyGuard.EnsureSafeModsPath(archiveChain[0]);
                    string innerPrefix = archiveChain.Count > 1
                        ? string.Join("/", archiveChain.Skip(1).Select(PathSafetyGuard.NormalizeRelativePath)) + "/"
                        : string.Empty;
                    string rpfInner = PathSafetyGuard.NormalizeRelativePath(innerPrefix + targetVal);

                    steps.Add(new ModInstallStep
                    {
                        Operation = InstallOperationKind.InjectIntoRpfArchive,
                        SourceRelativePath = PathSafetyGuard.NormalizeRelativePath(sourceAttr),
                        SourceFullPath = sourceFullPath,
                        TargetRelativePath = outerRpf,
                        RpfContainerRelativePath = outerRpf,
                        RpfInternalPath = rpfInner,
                        UsesSafeModsFolder = true,
                        Description = $"Inject '{sourceAttr}' into '{outerRpf}' -> '{rpfInner}'"
                    });
                }
            }
            else if (localName == "xml")
            {
                string xmlPath = ((string?)el.Attribute("path") ?? string.Empty).Trim();
                if (xmlPath.EndsWith("dlclist.xml", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var addEl in el.Elements("add"))
                    {
                        var itemEl = addEl.Element("Item") ?? (addEl.Name.LocalName == "Item" ? addEl : null);
                        string val = itemEl?.Value.Trim() ?? addEl.Value.Trim();
                        if (!string.IsNullOrWhiteSpace(val) && val.Contains("dlcpacks", StringComparison.OrdinalIgnoreCase))
                        {
                            string canonical = DlcListXmlService.NormalizeDlcEntry(val);
                            steps.Add(new ModInstallStep
                            {
                                Operation = InstallOperationKind.RegisterDlcListEntry,
                                TargetRelativePath = "mods/update/update.rpf",
                                RpfContainerRelativePath = "mods/update/update.rpf",
                                RpfInternalPath = DlcListXmlService.RpfInternalDlcListPath,
                                DlcListEntry = canonical,
                                UsesSafeModsFolder = true,
                                Description = $"Register '{canonical}' in dlclist.xml"
                            });
                        }
                    }
                }
                else
                {
                    warnings.Add($"OIV includes custom <xml> patch on '{xmlPath}' which requires OpenIV Package Installer for encrypted RPFs.");
                }
            }
        }
    }

    /// <summary>
    /// Generates a standards-compliant OpenIV v2.1 .oiv archive for encrypted RPF7 targets and dlclist.xml updates.
    /// </summary>
    public static string GenerateOivPackage(
        string outputOivPath,
        string modName,
        string modVersion,
        string author,
        IEnumerable<ModInstallStep> rpfAndDlcSteps)
    {
        var stepList = rpfAndDlcSteps.ToList();
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputOivPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (File.Exists(outputOivPath))
        {
            File.Delete(outputOivPath);
        }

        var verParts = (modVersion ?? "1.0").Split('.', StringSplitOptions.RemoveEmptyEntries);
        string major = verParts.Length > 0 && int.TryParse(verParts[0], out _) ? verParts[0] : "1";
        string minor = verParts.Length > 1 && int.TryParse(verParts[1], out _) ? verParts[1] : "0";

        var contentElement = new XElement("content");
        var filesToBundle = new List<(string ZipContentPath, string DiskSourcePath)>();
        int idx = 0;

        // Group steps by outer RPF path (OpenIV relative to mods/ root when user selects "mods folder")
        var groupedByOuterRpf = stepList
            .Where(s => s.Operation is InstallOperationKind.InjectIntoRpfArchive or InstallOperationKind.RegisterDlcListEntry)
            .GroupBy(s => StripLeadingModsPrefix(
                string.IsNullOrWhiteSpace(s.RpfContainerRelativePath)
                    ? "update/update.rpf"
                    : s.RpfContainerRelativePath),
                StringComparer.OrdinalIgnoreCase);

        foreach (var group in groupedByOuterRpf)
        {
            var outerArchiveEl = new XElement("archive",
                new XAttribute("path", group.Key.Replace('/', '\\')),
                new XAttribute("createIfNotExist", "True"),
                new XAttribute("type", "RPF7"));

            foreach (var step in group)
            {
                if (step.Operation == InstallOperationKind.InjectIntoRpfArchive)
                {
                    idx++;
                    string bundledName = $"payload_{idx}_{Path.GetFileName(step.SourceFullPath)}";
                    filesToBundle.Add(($"content/{bundledName}", step.SourceFullPath));

                    var (nestedRpf, remainder) = Rpf7Archive.SplitNestedRpfPath(step.RpfInternalPath);
                    if (nestedRpf is not null && remainder is not null)
                    {
                        var nestedEl = new XElement("archive",
                            new XAttribute("path", nestedRpf.Replace('/', '\\')),
                            new XAttribute("createIfNotExist", "False"),
                            new XAttribute("type", "RPF7"),
                            new XElement("add",
                                new XAttribute("source", bundledName),
                                remainder.Replace('/', '\\')));
                        outerArchiveEl.Add(nestedEl);
                    }
                    else
                    {
                        outerArchiveEl.Add(new XElement("add",
                            new XAttribute("source", bundledName),
                            step.RpfInternalPath.Replace('/', '\\')));
                    }
                }
                else if (step.Operation == InstallOperationKind.RegisterDlcListEntry)
                {
                    string canonical = DlcListXmlService.NormalizeDlcEntry(step.DlcListEntry);
                    var xmlEl = new XElement("xml",
                        new XAttribute("path", @"common\data\dlclist.xml"),
                        new XElement("remove",
                            new XAttribute("xpath", $"/SMandatoryPacksData/Paths/Item[text()='{canonical}']")),
                        new XElement("add",
                            new XAttribute("xpath", "/SMandatoryPacksData/Paths"),
                            new XElement("Item", canonical)));
                    outerArchiveEl.Add(xmlEl);
                }
            }

            contentElement.Add(outerArchiveEl);
        }

        var assemblyDoc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("package",
                new XAttribute("version", "2.1"),
                new XAttribute("id", $"{{{Guid.NewGuid().ToString().ToUpperInvariant()}}}"),
                new XAttribute("target", "Five"),
                new XElement("metadata",
                    new XElement("name", modName),
                    new XElement("version",
                        new XElement("major", major),
                        new XElement("minor", minor)),
                    new XElement("author",
                        new XElement("displayName", string.IsNullOrWhiteSpace(author) ? "GTA Mod Assistant" : author)),
                    new XElement("description",
                        new XCData($"Generated by GTA Mod Assistant for verified installation into GTA V's mods folder: '{modName}'."))),
                contentElement));

        using (var zipFs = new FileStream(outputOivPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(zipFs, ZipArchiveMode.Create))
        {
            var asmEntry = zip.CreateEntry("assembly.xml", CompressionLevel.Optimal);
            using (var asmStream = asmEntry.Open())
            using (var writer = XmlWriter.Create(asmStream, new XmlWriterSettings
            {
                Indent = true,
                Encoding = new UTF8Encoding(false)
            }))
            {
                assemblyDoc.Save(writer);
            }

            foreach (var (zipPath, diskSource) in filesToBundle)
            {
                if (File.Exists(diskSource))
                {
                    zip.CreateEntryFromFile(diskSource, zipPath, CompressionLevel.Optimal);
                }
            }
        }

        return outputOivPath;
    }

    public static async Task<OpenIvCommitVerification> LaunchOpenIvAndVerifyAsync(
        string openIvExePath,
        string oivPackagePath,
        IEnumerable<string> targetModsRpfFullPaths,
        CancellationToken cancellationToken = default,
        IOpenIvPackageInstallerAutomation? installerAutomation = null,
        string expectedPackageName = "")
    {
        if (!OperatingSystem.IsWindows())
        {
            return new OpenIvCommitVerification(
                false,
                "OpenIV.exe requires Windows to execute interactively; generated .oiv package is staged and verified ready.",
                new Dictionary<string, string>());
        }

        if (string.IsNullOrWhiteSpace(openIvExePath) || !File.Exists(openIvExePath))
        {
            return new OpenIvCommitVerification(
                false,
                "OpenIV.exe was not found at the configured path.",
                new Dictionary<string, string>());
        }

        var targets = targetModsRpfFullPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var preHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
        {
            if (File.Exists(t))
            {
                preHashes[t] = HashVerifier.ComputeFileSha256(t);
            }
        }

        string? automationDetails = null;
        if (installerAutomation is not null)
        {
            var automationResult = await installerAutomation.InstallAsync(
                openIvExePath,
                oivPackagePath,
                expectedPackageName,
                cancellationToken);
            automationDetails = automationResult.Completed
                ? automationResult.Details
                : $"Automatic OpenIV installation did not complete: {automationResult.Details}";
        }
        else
        {
            var psi = new ProcessStartInfo
            {
                FileName = openIvExePath,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(openIvExePath) ?? string.Empty
            };
            psi.ArgumentList.Add(oivPackagePath);

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return new OpenIvCommitVerification(false, "Failed to start OpenIV.exe process.", preHashes);
            }

            await proc.WaitForExitAsync(cancellationToken);
        }

        var postHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool anyModifiedAndValid = false;
        foreach (var t in targets)
        {
            if (File.Exists(t))
            {
                string post = HashVerifier.ComputeFileSha256(t);
                postHashes[t] = post;
                var hdr = Rpf7Archive.InspectHeader(t);
                if (hdr.IsValidRpf7 && (!preHashes.TryGetValue(t, out string? pre) || !string.Equals(pre, post, StringComparison.OrdinalIgnoreCase)))
                {
                    anyModifiedAndValid = true;
                }
            }
        }

        if (!anyModifiedAndValid)
        {
            return new OpenIvCommitVerification(
                false,
                automationDetails is null
                    ? "OpenIV closed without modifying target mods/*.rpf archives (installation in OpenIV may have been cancelled)."
                    : $"{automationDetails} No target mods/*.rpf archive change was verified.",
                postHashes);
        }

        return new OpenIvCommitVerification(
            true,
            automationDetails is null
                ? "Verified post-OpenIV SHA-256 modification and valid RPF7 header on target mods archive(s)."
                : $"Verified post-OpenIV SHA-256 modification and valid RPF7 header on target mods archive(s). {automationDetails}",
            postHashes);
    }

    private static string StripLeadingModsPrefix(string path)
    {
        string norm = PathSafetyGuard.NormalizeRelativePath(path);
        return norm.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)
            ? norm["mods/".Length..]
            : norm;
    }
}
