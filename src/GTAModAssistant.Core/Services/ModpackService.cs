using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using GTAModAssistant.Core.Models;

namespace GTAModAssistant.Core.Services;

public sealed class ModpackService
{
    private const long MaximumModpackBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumModpackFiles = 10000;
    private const string ManifestEntryName = "manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public void Export(
        string destinationPath,
        ModProfile profile,
        IReadOnlyList<InstalledModRecord> installedMods,
        IReadOnlyDictionary<string, bool> profileModStates,
        int gameBuild)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(installedMods);
        ArgumentNullException.ThrowIfNull(profileModStates);
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("A destination path is required.", nameof(destinationPath));
        }

        string fullDestinationPath = Path.GetFullPath(destinationPath);
        string temporaryPath = fullDestinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var entries = new List<ModpackEntry>();
            long totalBytes = 0;
            int totalFiles = 0;
            var packagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
            {
                foreach (InstalledModRecord mod in installedMods)
                {
                    bool enabled = profileModStates.TryGetValue(mod.Id, out bool state) ? state : mod.IsEnabled;
                    if (!Directory.Exists(mod.SourcePath) && !File.Exists(mod.SourcePath))
                    {
                        throw new FileNotFoundException(
                            $"The original package for '{mod.Name}' is no longer available, so this modpack would be incomplete.",
                            mod.SourcePath);
                    }

                    string packageId = $"{SanitizePackageId(mod.Id)}_{entries.Count}";
                    string packageRoot = $"packages/{packageId}";
                    var fileEntries = new List<ModpackFileEntry>();
                    bool isArchive = File.Exists(mod.SourcePath);

                    IEnumerable<string> sourceFiles = isArchive
                        ? new[] { Path.GetFullPath(mod.SourcePath) }
                        : EnumerateSafeFiles(mod.SourcePath);

                    foreach (string sourceFile in sourceFiles)
                    {
                        totalFiles++;
                        if (totalFiles > MaximumModpackFiles)
                        {
                            throw new InvalidDataException("This modpack exceeds the 10,000-file export limit.");
                        }

                        var fileInfo = new FileInfo(sourceFile);
                        totalBytes = checked(totalBytes + fileInfo.Length);
                        if (totalBytes > MaximumModpackBytes)
                        {
                            throw new InvalidDataException("This modpack exceeds the 2 GB export limit.");
                        }

                        string relative = isArchive ? Path.GetFileName(sourceFile) : Path.GetRelativePath(mod.SourcePath, sourceFile);
                        relative = NormalizeArchivePath(relative);
                        string zipPath = $"{packageRoot}/{relative}";
                        if (!packagePaths.Add(zipPath))
                        {
                            throw new InvalidDataException($"The source packages contain duplicate paths: '{zipPath}'.");
                        }
                        ZipArchiveEntry output = archive.CreateEntry(zipPath, CompressionLevel.Optimal);
                        long copiedBytes;
                        string sha256;
                        using (Stream destination = output.Open())
                        using (FileStream input = File.OpenRead(sourceFile))
                        {
                            (copiedBytes, sha256) = CopyAndComputeSha256(input, destination);
                        }

                        if (copiedBytes != fileInfo.Length)
                        {
                            throw new IOException($"The source package changed while exporting: '{sourceFile}'.");
                        }

                        fileEntries.Add(new ModpackFileEntry(relative, copiedBytes, sha256));
                    }

                    if (fileEntries.Count == 0)
                    {
                        throw new InvalidDataException($"The original package for '{mod.Name}' contains no files.");
                    }

                    entries.Add(new ModpackEntry
                    {
                        Id = packageId,
                        Name = mod.Name,
                        Version = mod.Version,
                        Author = mod.Author,
                        Category = mod.Category.ToString(),
                        Enabled = enabled,
                        PackageRoot = packageRoot,
                        IsArchive = isArchive,
                        Files = fileEntries
                    });
                }

                var manifest = new ModpackManifest
                {
                    Name = profile.Name,
                    GameBuild = gameBuild,
                    Mods = entries
                };
                ZipArchiveEntry manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                using Stream manifestStream = manifestEntry.Open();
                JsonSerializer.Serialize(manifestStream, manifest, JsonOptions);
            }

            File.Move(temporaryPath, fullDestinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public ImportedModpack Import(string modpackPath, string destinationRoot)
    {
        if (string.IsNullOrWhiteSpace(modpackPath))
        {
            throw new ArgumentException("A modpack path is required.", nameof(modpackPath));
        }
        if (string.IsNullOrWhiteSpace(destinationRoot))
        {
            throw new ArgumentException("An import destination is required.", nameof(destinationRoot));
        }
        if (!File.Exists(modpackPath))
        {
            throw new FileNotFoundException("The selected modpack does not exist.", modpackPath);
        }
        if (new FileInfo(modpackPath).Length > MaximumModpackBytes)
        {
            throw new InvalidDataException("The selected modpack exceeds the 2 GB import limit.");
        }

        Directory.CreateDirectory(destinationRoot);
        string extractionDirectory = Path.Combine(
            Path.GetFullPath(destinationRoot),
            "imported_" + DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(extractionDirectory);

        try
        {
            using ZipArchive archive = ZipFile.OpenRead(modpackPath);
            if (archive.Entries.Count > MaximumModpackFiles + 1)
            {
                throw new InvalidDataException("The modpack contains too many files.");
            }

            ZipArchiveEntry manifestEntry = archive.GetEntry(ManifestEntryName)
                ?? throw new InvalidDataException("The selected archive is not a GTA Mod Assistant modpack: manifest.json is missing.");
            if (manifestEntry.Length > 1024 * 1024)
            {
                throw new InvalidDataException("The modpack manifest exceeds the 1 MB limit.");
            }
            ModpackManifest manifest;
            using (Stream stream = manifestEntry.Open())
            {
                manifest = JsonSerializer.Deserialize<ModpackManifest>(stream)
                    ?? throw new InvalidDataException("The modpack manifest is empty or invalid.");
            }

            if (manifest.FormatVersion != 1)
            {
                throw new InvalidDataException($"Unsupported modpack format version {manifest.FormatVersion}.");
            }
            if (manifest.Mods is null || manifest.Mods.Count > MaximumModpackFiles)
            {
                throw new InvalidDataException("The modpack manifest contains an invalid number of mods.");
            }

            var archiveEntries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string entryPath = NormalizeArchivePath(entry.FullName);
                if (!archiveEntries.TryAdd(entryPath, entry))
                {
                    throw new InvalidDataException($"The modpack contains duplicate paths: '{entryPath}'.");
                }
            }
            long totalBytes = 0;
            var sourcePaths = new List<string>();
            var packageRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ModpackEntry mod in manifest.Mods)
            {
                if (mod is null)
                {
                    throw new InvalidDataException("The modpack manifest contains an empty mod entry.");
                }
                string packageRoot = NormalizeArchivePath(mod.PackageRoot).TrimEnd('/');
                if (!packageRoot.StartsWith("packages/", StringComparison.OrdinalIgnoreCase) ||
                    !packageRoots.Add(packageRoot) ||
                    mod.Files is null ||
                    mod.Files.Count == 0 ||
                    (mod.IsArchive && mod.Files.Count != 1))
                {
                    throw new InvalidDataException($"Invalid package entry in modpack manifest: '{mod.PackageRoot}'.");
                }

                var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ModpackFileEntry file in mod.Files)
                {
                    if (file is null)
                    {
                        throw new InvalidDataException($"Modpack package '{packageRoot}' contains an empty file entry.");
                    }
                    string relative = NormalizeArchivePath(file.Path);
                    if (!filePaths.Add(relative))
                    {
                        throw new InvalidDataException($"Modpack package contains duplicate paths: '{relative}'.");
                    }
                    if (file.SizeBytes < 0 ||
                        string.IsNullOrEmpty(file.Sha256) ||
                        file.Sha256.Length != 64 ||
                        !file.Sha256.All(Uri.IsHexDigit))
                    {
                        throw new InvalidDataException($"Modpack file metadata is invalid: '{file.Path}'.");
                    }
                    string zipPath = $"{packageRoot}/{relative}";
                    if (!archiveEntries.TryGetValue(zipPath, out ZipArchiveEntry? source))
                    {
                        throw new InvalidDataException($"Modpack package file is missing: '{zipPath}'.");
                    }

                    totalBytes = checked(totalBytes + source.Length);
                    if (source.Length != file.SizeBytes || totalBytes > MaximumModpackBytes)
                    {
                        throw new InvalidDataException($"Modpack file failed size or SHA-256 verification: '{zipPath}'.");
                    }

                    string outputPath = ResolveSafePath(extractionDirectory, zipPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    long copiedBytes;
                    string sha256;
                    using Stream input = source.Open();
                    using FileStream output = new(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    (copiedBytes, sha256) = CopyAndComputeSha256(input, output);
                    if (copiedBytes != file.SizeBytes ||
                        !string.Equals(sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"Modpack file failed size or SHA-256 verification: '{zipPath}'.");
                    }
                }

                string extractedPackageRoot = ResolveSafePath(extractionDirectory, packageRoot);
                if (mod.IsArchive)
                {
                    string sourceFile = ResolveSafePath(extractionDirectory, $"{packageRoot}/{mod.Files.Single().Path}");
                    sourcePaths.Add(sourceFile);
                }
                else
                {
                    sourcePaths.Add(extractedPackageRoot);
                }
            }

            return new ImportedModpack(manifest, extractionDirectory, sourcePaths);
        }
        catch
        {
            if (Directory.Exists(extractionDirectory))
            {
                Directory.Delete(extractionDirectory, recursive: true);
            }
            throw;
        }
    }

    private static IEnumerable<string> EnumerateSafeFiles(string root)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Cannot include symbolic link or reparse point in modpack: '{root}'.");
        }

        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        while (pending.TryPop(out string? directory))
        {
            foreach (string child in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"Cannot include symbolic link or reparse point in modpack: '{child}'.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(child);
                }
                else
                {
                    yield return child;
                }
            }
        }
    }

    private static string SanitizePackageId(string id) =>
        string.Concat(id.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'));

    private static string NormalizeArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            throw new InvalidDataException("Modpack contains an empty or rooted path.");
        }

        string normalized = path.Replace('\\', '/');
        char[] invalidFileNameChars = Path.GetInvalidFileNameChars();
        if (normalized.Split('/').Any(part =>
            part is "." or ".." ||
            part.Length == 0 ||
            part.EndsWith(' ') ||
            part.EndsWith('.') ||
            part.IndexOfAny(invalidFileNameChars) >= 0))
        {
            throw new InvalidDataException($"Modpack contains an unsafe path: '{path}'.");
        }
        return normalized;
    }

    private static string ResolveSafePath(string root, string relative)
    {
        string normalized = NormalizeArchivePath(relative);
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Modpack path escapes extraction directory: '{relative}'.");
        }
        return fullPath;
    }

    private static (long BytesCopied, string Sha256) CopyAndComputeSha256(Stream source, Stream destination)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long bytesCopied = 0;
        int bytesRead;
        while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            destination.Write(buffer, 0, bytesRead);
            hash.AppendData(buffer, 0, bytesRead);
            bytesCopied = checked(bytesCopied + bytesRead);
        }

        return (bytesCopied, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }
}
