using SharpCompress.Archives;
using GTAModAssistant.Core.Models;
using GTAModAssistant.Core.Security;

namespace GTAModAssistant.Core.Archives;

public sealed record StagedModInput(
    string SourcePath,
    string StagingDirectory,
    bool IsOriginallyOivPackage,
    IReadOnlyList<ExtractedFileEntry> Files,
    IReadOnlyList<SecurityFinding> ExtractionSecurityFindings);

public sealed class ArchiveExtractorService
{
    private const long MaxSingleFileSizeBytes = 8L * 1024 * 1024 * 1024; // 8 GB (large DLC packs)
    private const long MaxTotalExtractedBytes = 16L * 1024 * 1024 * 1024; // 16 GB
    private const int MaxFileCount = 15_000;

    private static readonly HashSet<string> SupportedArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".oiv", ".7z", ".tar", ".gz"
    };

    private static readonly HashSet<string> ExpandableNestedArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".oiv", ".zip", ".rar", ".7z"
    };

    public static bool IsSupportedInputPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (Directory.Exists(path))
        {
            return true;
        }

        if (File.Exists(path))
        {
            var ext = Path.GetExtension(path);
            return SupportedArchiveExtensions.Contains(ext);
        }

        return false;
    }

    public StagedModInput StageInput(string sourcePath, string stagingRootDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("Mod source path is required.", nameof(sourcePath));
        }

        Directory.CreateDirectory(stagingRootDirectory);
        string sessionDir = Path.Combine(
            stagingRootDirectory,
            $"stage_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(sessionDir);

        var findings = new List<SecurityFinding>();
        var files = new List<ExtractedFileEntry>();
        bool isOiv = false;

        if (Directory.Exists(sourcePath))
        {
            StageFromFolder(sourcePath, sessionDir, files, findings);
        }
        else if (File.Exists(sourcePath))
        {
            string ext = Path.GetExtension(sourcePath);
            if (!SupportedArchiveExtensions.Contains(ext))
            {
                throw new NotSupportedException(
                    $"Unsupported file extension '{ext}'. Drop a .zip, .rar, .oiv, .7z archive, or an extracted mod folder.");
            }

            isOiv = string.Equals(ext, ".oiv", StringComparison.OrdinalIgnoreCase);
            ExtractArchiveSafely(sourcePath, sessionDir, string.Empty, files, findings);
        }
        else
        {
            throw new FileNotFoundException($"Mod input path not found: '{sourcePath}'.", sourcePath);
        }

        // Expand nested .oiv / .zip / .rar / .7z packages inside downloaded archives or folders
        // (e.g., GTA5-Mods .rar downloads that bundle an inner Auto-Installer .oiv or Add-On .zip!)
        ExpandNestedArchives(sessionDir, files, findings, maxDepth: 2);

        if (!isOiv)
        {
            isOiv = files.Any(f => string.Equals(Path.GetFileName(f.RelativePath), "assembly.xml", StringComparison.OrdinalIgnoreCase)) &&
                    !files.Any(f =>
                        string.Equals(Path.GetFileName(f.RelativePath), "dlc.rpf", StringComparison.OrdinalIgnoreCase) &&
                        !f.RelativePath.Contains("_nested_", StringComparison.OrdinalIgnoreCase));
        }

        return new StagedModInput(
            Path.GetFullPath(sourcePath),
            sessionDir,
            isOiv,
            files,
            findings);
    }

    private static void ExpandNestedArchives(
        string sessionDir,
        List<ExtractedFileEntry> files,
        List<SecurityFinding> findings,
        int maxDepth)
    {
        var alreadyExpanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int depth = 0; depth < maxDepth; depth++)
        {
            var nestedCandidates = files
                .Where(f => ExpandableNestedArchiveExtensions.Contains(f.Extension) &&
                            !alreadyExpanded.Contains(f.FullStagingPath))
                .ToList();

            if (nestedCandidates.Count == 0)
            {
                break;
            }

            foreach (var nested in nestedCandidates)
            {
                alreadyExpanded.Add(nested.FullStagingPath);
                string parentRel = Path.GetDirectoryName(nested.RelativePath)?.Replace('\\', '/') ?? string.Empty;
                string unpackFolderName = $"_nested_{Path.GetFileNameWithoutExtension(nested.RelativePath)}";
                string unpackRelPrefix = string.IsNullOrEmpty(parentRel)
                    ? unpackFolderName
                    : $"{parentRel}/{unpackFolderName}";
                string unpackFullDir = PathSafetyGuard.ResolveSafeChildPath(sessionDir, unpackRelPrefix);
                Directory.CreateDirectory(unpackFullDir);

                try
                {
                    ExtractArchiveSafely(nested.FullStagingPath, unpackFullDir, unpackRelPrefix, files, findings);
                }
                catch
                {
                    // Ignore corrupted or password-locked optional sub-archive without aborting outer package
                }
            }
        }
    }

    private static void StageFromFolder(
        string sourceFolder,
        string sessionDir,
        List<ExtractedFileEntry> files,
        List<SecurityFinding> findings)
    {
        string fullRoot = Path.GetFullPath(sourceFolder);
        long totalBytes = 0;
        int count = 0;

        foreach (var filePath in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories))
        {
            count++;
            if (count > MaxFileCount)
            {
                throw new InvalidOperationException($"Mod folder exceeds maximum file count ({MaxFileCount}).");
            }

            string rel = Path.GetRelativePath(fullRoot, filePath);
            string normalizedRel = PathSafetyGuard.NormalizeRelativePath(rel);
            string destFull = PathSafetyGuard.ResolveSafeChildPath(sessionDir, normalizedRel);

            var info = new FileInfo(filePath);
            if (info.Length > MaxSingleFileSizeBytes)
            {
                throw new InvalidOperationException($"File '{normalizedRel}' exceeds size limit.");
            }

            totalBytes += info.Length;
            if (totalBytes > MaxTotalExtractedBytes)
            {
                throw new InvalidOperationException("Mod folder exceeds maximum total staging size.");
            }

            if (PathSafetyGuard.IsBlockedExecutableOrScript(normalizedRel))
            {
                findings.Add(new SecurityFinding(
                    SecuritySeverity.BlockedExecutable,
                    normalizedRel,
                    "Executable or script detected inside mod folder. GTA Mod Assistant will NEVER execute or deploy this file."));
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
            File.Copy(filePath, destFull, overwrite: true);

            string sha256 = HashVerifier.ComputeFileSha256(destFull);
            files.Add(new ExtractedFileEntry(
                normalizedRel,
                destFull,
                info.Length,
                Path.GetExtension(normalizedRel).ToLowerInvariant(),
                sha256));
        }
    }

    private static void ExtractArchiveSafely(
        string archivePath,
        string targetExtractDir,
        string relativePrefix,
        List<ExtractedFileEntry> files,
        List<SecurityFinding> findings)
    {
        using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = ArchiveFactory.OpenArchive(fs);

        long totalBytes = 0;
        int count = 0;

        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            string rawKey = entry.Key ?? string.Empty;
            count++;
            if (count > MaxFileCount)
            {
                throw new InvalidOperationException($"Archive exceeds maximum file count ({MaxFileCount}).");
            }

            string destFull;
            try
            {
                destFull = PathSafetyGuard.ResolveSafeChildPath(targetExtractDir, rawKey);
            }
            catch (Exception ex)
            {
                findings.Add(new SecurityFinding(
                    SecuritySeverity.BlockedUnsafePath,
                    rawKey,
                    ex.Message));
                continue;
            }

            string entryNorm = PathSafetyGuard.NormalizeRelativePath(rawKey);
            string normalizedRel = string.IsNullOrEmpty(relativePrefix)
                ? entryNorm
                : $"{relativePrefix}/{entryNorm}";

            if (PathSafetyGuard.IsBlockedExecutableOrScript(normalizedRel))
            {
                findings.Add(new SecurityFinding(
                    SecuritySeverity.BlockedExecutable,
                    normalizedRel,
                    "Executable or script detected inside downloaded archive. Blocked from extraction and execution."));
                continue;
            }

            if (entry.Size > MaxSingleFileSizeBytes)
            {
                throw new InvalidOperationException($"Archive entry '{normalizedRel}' exceeds single-file size limit.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
            long writtenBytes = 0;
            using (var entryStream = entry.OpenEntryStream())
            using (var outFs = new FileStream(destFull, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = entryStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    writtenBytes += read;
                    totalBytes += read;
                    if (writtenBytes > MaxSingleFileSizeBytes || totalBytes > MaxTotalExtractedBytes)
                    {
                        throw new InvalidOperationException("Archive extraction exceeded decompressed size safety limit.");
                    }
                    outFs.Write(buffer, 0, read);
                }
            }

            string sha256 = HashVerifier.ComputeFileSha256(destFull);
            files.Add(new ExtractedFileEntry(
                normalizedRel,
                destFull,
                writtenBytes,
                Path.GetExtension(normalizedRel).ToLowerInvariant(),
                sha256));
        }
    }
}
