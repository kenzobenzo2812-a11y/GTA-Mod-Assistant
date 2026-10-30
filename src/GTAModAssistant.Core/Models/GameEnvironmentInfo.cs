namespace GTAModAssistant.Core.Models;

public enum GamePlatform
{
    Unknown = 0,
    EpicGames = 1,
    Steam = 2,
    RockstarLauncher = 3,
    ManualCustom = 4
}

public sealed record GameEnvironmentInfo
{
    public bool IsDetected { get; init; }
    public string GtaRootPath { get; init; } = string.Empty;
    public GamePlatform Platform { get; init; } = GamePlatform.Unknown;
    public string GameVersion { get; init; } = "Unknown";
    public int BuildNumber { get; init; }
    public string BuildTitleName { get; init; } = "Unknown Build";

    public bool OpenIvInstalled { get; init; }
    public string OpenIvExecutablePath { get; init; } = string.Empty;
    public string OpenIvVersion { get; init; } = string.Empty;

    public string ModsFolderPath { get; init; } = string.Empty;
    public bool ModsFolderExists { get; init; }
    public bool ModsUpdateRpfExists { get; init; }
    public RpfEncryptionKind ModsUpdateRpfEncryption { get; init; } = RpfEncryptionKind.Unknown;

    public bool AsiLoaderInstalled { get; init; }
    public bool OpenIvAsiInstalled { get; init; }
    public bool ScriptHookVInstalled { get; init; }
    public string ScriptHookVVersion { get; init; } = string.Empty;
    public bool ScriptHookVDotNetInstalled { get; init; }
    public bool ScriptsFolderExists { get; init; }

    public IReadOnlyList<string> HealthWarnings { get; init; } = Array.Empty<string>();
    public DateTimeOffset ScannedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string SummaryLine =>
        IsDetected
            ? $"{Platform} | GTA V v{GameVersion} (Build {BuildNumber} - {BuildTitleName})"
            : "GTA V installation not detected yet";
}

public enum RpfEncryptionKind
{
    Unknown = 0,
    Unencrypted = 1,
    OpenFlag = 2,
    Aes = 3,
    Ng = 4
}
