namespace GTAModAssistant.Core.Models;

public sealed record ModpackManifest
{
    public int FormatVersion { get; init; } = 1;
    public string Name { get; init; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public int GameBuild { get; init; }
    public IReadOnlyList<ModpackEntry> Mods { get; init; } = Array.Empty<ModpackEntry>();
}

public sealed record ModpackEntry
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public string PackageRoot { get; init; } = string.Empty;
    public bool IsArchive { get; init; }
    public IReadOnlyList<ModpackFileEntry> Files { get; init; } = Array.Empty<ModpackFileEntry>();
}

public sealed record ModpackFileEntry(
    string Path,
    long SizeBytes,
    string Sha256);

public sealed record ImportedModpack(
    ModpackManifest Manifest,
    string ExtractionDirectory,
    IReadOnlyList<string> ModSourcePaths);
