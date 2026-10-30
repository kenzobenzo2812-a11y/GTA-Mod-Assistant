namespace GTAModAssistant.Core.Models;

public enum ModCategory
{
    Unknown = 0,
    AddOnDlcPack = 1,
    RpfAssetReplacement = 2,
    AsiPlugin = 3,
    ScriptHookVDotNetMod = 4,
    OpenIvPackage = 5,
    DataOrConfigMod = 6,
    CompositeMod = 7
}

public enum InstallOperationKind
{
    CopyFileToModsOrRoot = 1,
    InjectIntoRpfArchive = 2,
    RegisterDlcListEntry = 3,
    ApplyOpenIvPackage = 4
}

public enum CompatibilitySeverity
{
    Compatible = 0,
    Advisory = 1,
    WarningOutdated = 2,
    IncompatibleBlocked = 3
}

public enum SecuritySeverity
{
    Info = 0,
    Warning = 1,
    BlockedExecutable = 2,
    BlockedUnsafePath = 3
}

public enum ConflictKind
{
    InstalledModConflict = 1,
    DuplicateFileInPackage = 2,
    ExistingUnmanagedFileOnDisk = 3
}

public enum ConflictResolutionStrategy
{
    AskUser = 0,
    BackupAndOverwrite = 1,
    SkipConflictingSteps = 2,
    CancelInstall = 3,
    KeepFirstDuplicateOnly = 4
}

public enum InstallVerificationState
{
    NotStarted = 0,
    VerifiedComplete = 1,
    PendingOpenIvCommit = 2,
    FailedVerification = 3,
    Cancelled = 4
}

public sealed record RpfTargetCandidate(
    string SafeModsOuterRpfPath,
    string InternalDirectoryOrFilePath,
    string Reason);

public sealed record ExtractedFileEntry(
    string RelativePath,
    string FullStagingPath,
    long SizeBytes,
    string Extension,
    string Sha256);

public sealed record SecurityFinding(
    SecuritySeverity Severity,
    string FilePath,
    string Reason);

public sealed record CompatibilityReport(
    CompatibilitySeverity Severity,
    int? ModMinimumBuild,
    int? ModTargetBuild,
    int CurrentGameBuild,
    string Headline,
    IReadOnlyList<string> Details,
    IReadOnlyList<string> RequiredPrerequisites);

public sealed record ModInstallStep
{
    public string StepId { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public InstallOperationKind Operation { get; init; }
    public string SourceRelativePath { get; init; } = string.Empty;
    public string SourceFullPath { get; init; } = string.Empty;

    /// <summary>
    /// File name for preview (e.g. "police_hi.yft", "police.ytd", "dlclist.xml").
    /// </summary>
    public string FileName =>
        !string.IsNullOrWhiteSpace(SourceRelativePath)
            ? Path.GetFileName(SourceRelativePath.Replace('\\', '/'))
            : Operation == InstallOperationKind.RegisterDlcListEntry
                ? "dlclist.xml"
                : Path.GetFileName(TargetRelativePath.Replace('\\', '/'));

    public string AssetFormatLabel { get; init; } = string.Empty;
    public string CanonicalModelStem { get; init; } = string.Empty;
    public string ResolutionSource { get; init; } = "Rule Engine";

    /// <summary>
    /// True when the destination archive could not be determined with certainty from folder structure, metadata, README, or game RPF scan.
    /// </summary>
    public bool RequiresUserTargetSelection { get; init; }
    public string UncertaintyReason { get; init; } = string.Empty;
    public IReadOnlyList<RpfTargetCandidate> CandidateRpfTargets { get; init; } = Array.Empty<RpfTargetCandidate>();

    /// <summary>
    /// Path relative to the GTA V root directory (always under mods\ when RPF/data files are targeted).
    /// </summary>
    public string TargetRelativePath { get; init; } = string.Empty;

    /// <summary>
    /// If Operation == InjectIntoRpfArchive, relative path to the target .rpf container under GTA V root (e.g. "mods/x64e.rpf" or "mods/update/update.rpf").
    /// </summary>
    public string RpfContainerRelativePath { get; init; } = string.Empty;

    /// <summary>
    /// Internal path inside the .rpf archive (e.g. "levels/gta5/vehicles.rpf/police.yft" or "common/data/gameconfig.xml").
    /// </summary>
    public string RpfInternalPath { get; init; } = string.Empty;

    /// <summary>
    /// For RegisterDlcListEntry, the normalized DLC entry (e.g. "dlcpacks:/elegyrh7/").
    /// </summary>
    public string DlcListEntry { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;
    public bool UsesSafeModsFolder { get; init; } = true;
    public bool ReplacesExistingEntry { get; init; }
    public string ConflictStatusPreview { get; init; } = "Clean";
    public bool IsSelectedForInstall { get; init; } = true;

    public string FullPreviewDestination =>
        RequiresUserTargetSelection
            ? "[Action Needed] Choose Target RPF Archive..."
            : Operation == InstallOperationKind.InjectIntoRpfArchive
                ? $"{RpfContainerRelativePath} -> {RpfInternalPath}"
                : Operation == InstallOperationKind.RegisterDlcListEntry
                    ? $"{RpfContainerRelativePath} -> common/data/dlclist.xml ({DlcListEntry})"
                    : TargetRelativePath;

    /// <summary>
    /// True only when manually choosing/overriding an inner .rpf archive actually makes sense (RPF entries or unresolved .yft/.ytd).
    /// Prevents showing a misleading "Choose RPF..." button on Add-On dlc.rpf folder copies or dlclist.xml steps.
    /// </summary>
    public bool CanChooseCustomRpfTarget =>
        RequiresUserTargetSelection || Operation == InstallOperationKind.InjectIntoRpfArchive;

    /// <summary>
    /// Clear button label distinguishing required user selection ("Choose RPF...") from an optional override on an already auto-mapped RPF ("Override RPF...").
    /// </summary>
    public string TargetActionButtonLabel =>
        RequiresUserTargetSelection ? "Choose RPF..." : "Override RPF...";
}

public sealed record ConflictItem(
    string StepId,
    string TargetKey,
    string ExistingModId,
    string ExistingModName,
    bool ExistsOnDiskUnmanaged,
    string Description,
    ConflictKind Kind = ConflictKind.InstalledModConflict);

public sealed record ModInstallationPlan
{
    public string PlanId { get; init; } = Guid.NewGuid().ToString("N");
    public string ModName { get; init; } = string.Empty;
    public string ModSlug { get; init; } = string.Empty;
    public string ModVersion { get; init; } = "1.0";
    public string Author { get; init; } = "Unknown";
    public string SourcePath { get; init; } = string.Empty;
    public string StagingDirectory { get; init; } = string.Empty;
    public ModCategory Category { get; init; } = ModCategory.Unknown;

    public string AnalysisEngineUsed { get; init; } = "Deterministic GTA V Expert Engine";
    public bool HasReadme { get; init; } = true;
    public double ConfidenceScore { get; init; } = 1.0;
    public string ReadmeExcerpt { get; init; } = string.Empty;
    public string DeepFileInspectionReport { get; init; } = string.Empty;
    public IReadOnlyList<string> DetectedSpawnNames { get; init; } = Array.Empty<string>();
    public string AiReasoningSummary { get; init; } = string.Empty;

    public string SpawnNamesDisplay =>
        DetectedSpawnNames.Count > 0 ? string.Join(", ", DetectedSpawnNames) : string.Empty;

    public IReadOnlyList<ExtractedFileEntry> DiscoveredFiles { get; init; } = Array.Empty<ExtractedFileEntry>();
    public IReadOnlyList<ModInstallStep> Steps { get; init; } = Array.Empty<ModInstallStep>();
    public CompatibilityReport Compatibility { get; init; } = new(
        CompatibilitySeverity.Compatible, null, null, 0, "Compatible", Array.Empty<string>(), Array.Empty<string>());
    public IReadOnlyList<SecurityFinding> SecurityFindings { get; init; } = Array.Empty<SecurityFinding>();
    public IReadOnlyList<ConflictItem> Conflicts { get; init; } = Array.Empty<ConflictItem>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public bool HasUnresolvedTargets => Steps.Any(s => s.IsSelectedForInstall && s.RequiresUserTargetSelection);
    public int UnresolvedTargetCount => Steps.Count(s => s.IsSelectedForInstall && s.RequiresUserTargetSelection);

    public bool CanInstall =>
        Steps.Any(s => s.IsSelectedForInstall) &&
        Compatibility.Severity != CompatibilitySeverity.IncompatibleBlocked &&
        SecurityFindings.All(f => f.Severity != SecuritySeverity.BlockedUnsafePath);
}

public sealed record InstallProgressReport(
    int Percentage,
    string Stage,
    string Detail);

public sealed record InstallExecutionResult
{
    public bool Succeeded { get; init; }
    public InstallVerificationState VerificationState { get; init; }
    public string? InstalledModId { get; init; }
    public string? BackupId { get; init; }
    public string? GeneratedOivPackagePath { get; init; }
    public int VerifiedFileCount { get; init; }
    public string SummaryMessage { get; init; } = string.Empty;
    public IReadOnlyList<string> VerificationProofs { get; init; } = Array.Empty<string>();
}
