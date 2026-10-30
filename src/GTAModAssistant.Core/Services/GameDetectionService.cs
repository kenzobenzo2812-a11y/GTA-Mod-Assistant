using System.Text.Json;
using Microsoft.Win32;
using GTAModAssistant.Core.Archives;
using GTAModAssistant.Core.Models;
using GTAModAssistant.Core.Security;

namespace GTAModAssistant.Core.Services;

public sealed class GameDetectionService
{
    public const string DefaultEpicGamesGtaPath = @"C:\Program Files\Epic Games\GTAV";

    public GameEnvironmentInfo ScanEnvironment(
        string? customGtaRootOverride = null,
        string? customOpenIvExeOverride = null)
    {
        var (gtaRoot, platform) = LocateGtaInstallation(customGtaRootOverride);
        var (openIvFound, openIvPath, openIvVer) = LocateOpenIv(customOpenIvExeOverride);

        if (string.IsNullOrWhiteSpace(gtaRoot) || !IsValidGtaDirectory(gtaRoot))
        {
            return new GameEnvironmentInfo
            {
                IsDetected = false,
                GtaRootPath = customGtaRootOverride ?? DefaultEpicGamesGtaPath,
                Platform = GamePlatform.Unknown,
                OpenIvInstalled = openIvFound,
                OpenIvExecutablePath = openIvPath,
                OpenIvVersion = openIvVer,
                HealthWarnings = new[]
                {
                    "GTA5.exe was not found automatically. Click 'Browse GTA V Folder' or verify Epic Games installation at C:\\Program Files\\Epic Games\\GTAV."
                }
            };
        }

        string gtaExe = Path.Combine(gtaRoot, "GTA5.exe");
        var peInfo = PeBinaryInspector.Inspect(gtaExe);
        string gameVer = "1.0.0.0";
        int buildNumber = 0;

        if (peInfo.FileVersion is not null)
        {
            gameVer = $"{peInfo.FileVersion.Major}.{peInfo.FileVersion.Minor}.{peInfo.FileVersion.Build}.{Math.Max(0, peInfo.FileVersion.Revision)}";
            buildNumber = peInfo.FileVersion.Build;
        }
        else
        {
            string versionTxt = Path.Combine(gtaRoot, "version.txt");
            if (File.Exists(versionTxt))
            {
                string raw = File.ReadAllText(versionTxt).Trim().TrimStart('v', 'V');
                if (Version.TryParse(raw, out var parsedVer))
                {
                    gameVer = parsedVer.ToString();
                    buildNumber = parsedVer.Build > 0 ? parsedVer.Build : parsedVer.Minor;
                }
            }
        }

        string buildTitle = VersionCompatibilityService.GetTitleUpdateName(buildNumber);

        string modsFolder = Path.Combine(gtaRoot, "mods");
        bool modsExists = Directory.Exists(modsFolder);
        string modsUpdateRpf = Path.Combine(modsFolder, "update", "update.rpf");
        bool modsUpdateExists = File.Exists(modsUpdateRpf);
        var rpfEnc = RpfEncryptionKind.Unknown;
        if (modsUpdateExists)
        {
            rpfEnc = Rpf7Archive.InspectHeader(modsUpdateRpf).EncryptionKind;
        }

        bool dinput8 = File.Exists(Path.Combine(gtaRoot, "dinput8.dll"));
        bool openIvAsi = File.Exists(Path.Combine(gtaRoot, "OpenIV.asi"));
        string shvPath = Path.Combine(gtaRoot, "ScriptHookV.dll");
        bool shvInstalled = File.Exists(shvPath);
        string shvVersion = string.Empty;
        if (shvInstalled)
        {
            var shvPe = PeBinaryInspector.Inspect(shvPath);
            shvVersion = shvPe.FileVersion?.ToString() ?? "Installed";
        }

        bool shvdn = File.Exists(Path.Combine(gtaRoot, "ScriptHookVDotNet.asi")) ||
                     File.Exists(Path.Combine(gtaRoot, "ScriptHookVDotNet2.dll")) ||
                     File.Exists(Path.Combine(gtaRoot, "ScriptHookVDotNet3.dll"));
        bool scriptsExists = Directory.Exists(Path.Combine(gtaRoot, "scripts"));

        var warnings = new List<string>();
        if (!dinput8)
        {
            warnings.Add("Missing dinput8.dll (ASI Loader) in GTA V root. Install ASI Loader via OpenIV > Tools > ASI Manager so GTA V loads mods.");
        }
        if (!openIvAsi)
        {
            warnings.Add("Missing OpenIV.asi in GTA V root. Without OpenIV.asi, GTA V ignores the 'mods' folder at runtime!");
        }
        if (!openIvFound)
        {
            warnings.Add("OpenIV.exe was not detected. Encrypted vanilla RPF7 archives require OpenIV Package Installer (.oiv) unless already saved in OPEN format.");
        }
        if (!modsExists)
        {
            warnings.Add("The 'mods' folder has not been created yet. GTA Mod Assistant will safely create it automatically during your first mod install.");
        }

        return new GameEnvironmentInfo
        {
            IsDetected = true,
            GtaRootPath = Path.GetFullPath(gtaRoot),
            Platform = platform,
            GameVersion = gameVer,
            BuildNumber = buildNumber,
            BuildTitleName = buildTitle,
            OpenIvInstalled = openIvFound,
            OpenIvExecutablePath = openIvPath,
            OpenIvVersion = openIvVer,
            ModsFolderPath = modsFolder,
            ModsFolderExists = modsExists,
            ModsUpdateRpfExists = modsUpdateExists,
            ModsUpdateRpfEncryption = rpfEnc,
            AsiLoaderInstalled = dinput8,
            OpenIvAsiInstalled = openIvAsi,
            ScriptHookVInstalled = shvInstalled,
            ScriptHookVVersion = shvVersion,
            ScriptHookVDotNetInstalled = shvdn,
            ScriptsFolderExists = scriptsExists,
            HealthWarnings = warnings,
            ScannedAtUtc = DateTimeOffset.UtcNow
        };
    }

    public static bool IsValidGtaDirectory(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            return false;
        }
        return File.Exists(Path.Combine(dir, "GTA5.exe"));
    }

    private static (string Path, GamePlatform Platform) LocateGtaInstallation(string? customOverride)
    {
        if (!string.IsNullOrWhiteSpace(customOverride) && IsValidGtaDirectory(customOverride))
        {
            return (customOverride, InferPlatformFromDirectory(customOverride));
        }

        var envOverride = Environment.GetEnvironmentVariable("GTAMA_GTA_PATH");
        if (!string.IsNullOrWhiteSpace(envOverride) && IsValidGtaDirectory(envOverride))
        {
            return (envOverride, InferPlatformFromDirectory(envOverride));
        }

        // 1. User's primary Epic Games path
        if (IsValidGtaDirectory(DefaultEpicGamesGtaPath))
        {
            return (DefaultEpicGamesGtaPath, GamePlatform.EpicGames);
        }

        // 2. Epic Games Launcher .item JSON manifests
        string epicManifestDir = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
        if (Directory.Exists(epicManifestDir))
        {
            foreach (var itemFile in Directory.EnumerateFiles(epicManifestDir, "*.item"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(itemFile));
                    string displayName = doc.RootElement.TryGetProperty("DisplayName", out var dn) ? dn.GetString() ?? "" : "";
                    string installLoc = doc.RootElement.TryGetProperty("InstallLocation", out var il) ? il.GetString() ?? "" : "";
                    if ((displayName.Contains("Grand Theft Auto V", StringComparison.OrdinalIgnoreCase) ||
                         installLoc.EndsWith("GTAV", StringComparison.OrdinalIgnoreCase)) &&
                        IsValidGtaDirectory(installLoc))
                    {
                        return (installLoc, GamePlatform.EpicGames);
                    }
                }
                catch
                {
                    // Ignore unreadable manifest
                }
            }
        }

        // 3. Windows Registry check (guarded for Windows)
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var keyEpic = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Rockstar Games\GTAV");
                string? epicFolder = keyEpic?.GetValue("InstallFolderEpic") as string;
                if (IsValidGtaDirectory(epicFolder))
                {
                    return (epicFolder!, GamePlatform.EpicGames);
                }

                using var keyRgl = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Rockstar Games\Grand Theft Auto V");
                string? rglFolder = keyRgl?.GetValue("InstallFolder") as string;
                if (IsValidGtaDirectory(rglFolder))
                {
                    return (rglFolder!, InferPlatformFromDirectory(rglFolder!));
                }

                // Steam installation & libraryfolders.vdf lookup across all custom Steam drives
                using var keySteamCu = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                using var keySteamLm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                string? steamRoot = (keySteamCu?.GetValue("SteamPath") as string) ??
                                    (keySteamLm?.GetValue("InstallPath") as string);
                if (!string.IsNullOrWhiteSpace(steamRoot))
                {
                    steamRoot = steamRoot.Replace('/', '\\');
                    string defaultSteamGta = Path.Combine(steamRoot, "steamapps", "common", "Grand Theft Auto V");
                    if (IsValidGtaDirectory(defaultSteamGta))
                    {
                        return (defaultSteamGta, GamePlatform.Steam);
                    }

                    string vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(vdfPath))
                    {
                        foreach (var line in File.ReadAllLines(vdfPath))
                        {
                            if (!line.Contains("\"path\"", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            var parts = line.Split('"', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 3)
                            {
                                string libRoot = parts[^1].Replace(@"\\", @"\");
                                string candidateSteam = Path.Combine(libRoot, "steamapps", "common", "Grand Theft Auto V");
                                if (IsValidGtaDirectory(candidateSteam))
                                {
                                    return (candidateSteam, GamePlatform.Steam);
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore registry permission errors
            }
        }

        // 4. Standard Epic / Steam / Rockstar install locations across all fixed drives on the user's PC
        string[] candidateSuffixes =
        {
            @"Program Files\Epic Games\GTAV",
            @"Epic Games\GTAV",
            @"Games\Epic Games\GTAV",
            @"Games\GTAV",
            @"Program Files\Rockstar Games\Grand Theft Auto V",
            @"Rockstar Games\Grand Theft Auto V",
            @"Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V",
            @"SteamLibrary\steamapps\common\Grand Theft Auto V",
            @"Steam\steamapps\common\Grand Theft Auto V"
        };

        var driveRoots = new List<string> { @"C:\", @"D:\", @"E:\", @"F:\", @"G:\" };
        if (OperatingSystem.IsWindows())
        {
            try
            {
                foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
                {
                    if (!driveRoots.Contains(d.RootDirectory.FullName, StringComparer.OrdinalIgnoreCase))
                    {
                        driveRoots.Add(d.RootDirectory.FullName);
                    }
                }
            }
            catch
            {
                // Ignore drive enumeration issues
            }
        }

        foreach (string driveRoot in driveRoots)
        {
            foreach (var suffix in candidateSuffixes)
            {
                string candidate = Path.Combine(driveRoot, suffix);
                if (IsValidGtaDirectory(candidate))
                {
                    return (candidate, InferPlatformFromDirectory(candidate));
                }
            }
        }

        return (string.Empty, GamePlatform.Unknown);
    }

    private static GamePlatform InferPlatformFromDirectory(string dir)
    {
        if (File.Exists(Path.Combine(dir, "EOSSDK-Win64-Shipping.dll")) ||
            Directory.Exists(Path.Combine(dir, ".egstore")) ||
            dir.Contains("Epic Games", StringComparison.OrdinalIgnoreCase))
        {
            return GamePlatform.EpicGames;
        }

        if (File.Exists(Path.Combine(dir, "steam_api64.dll")) ||
            dir.Contains("steamapps", StringComparison.OrdinalIgnoreCase))
        {
            return GamePlatform.Steam;
        }

        if (dir.Contains("Rockstar Games", StringComparison.OrdinalIgnoreCase))
        {
            return GamePlatform.RockstarLauncher;
        }

        return GamePlatform.ManualCustom;
    }

    private static (bool Installed, string ExePath, string Version) LocateOpenIv(string? customOverride)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(customOverride))
        {
            candidates.Add(customOverride);
        }

        var envOpenIv = Environment.GetEnvironmentVariable("GTAMA_OPENIV_PATH");
        if (!string.IsNullOrWhiteSpace(envOpenIv))
        {
            candidates.Add(envOpenIv);
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            candidates.Add(Path.Combine(localAppData, "New Technology Studio", "Apps", "OpenIV", "OpenIV.exe"));
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            candidates.Add(Path.Combine(userProfile, "AppData", "Local", "New Technology Studio", "Apps", "OpenIV", "OpenIV.exe"));
        }

        if (Directory.Exists(@"C:\Users"))
        {
            try
            {
                foreach (var userDir in Directory.EnumerateDirectories(@"C:\Users"))
                {
                    candidates.Add(Path.Combine(userDir, "AppData", "Local", "New Technology Studio", "Apps", "OpenIV", "OpenIV.exe"));
                }
            }
            catch
            {
                // Ignore inaccessible profile dirs
            }
        }

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(path))
            {
                var pe = PeBinaryInspector.Inspect(path);
                string ver = pe.FileVersion?.ToString() ?? "4.1+";
                return (true, Path.GetFullPath(path), ver);
            }
        }

        return (false, string.Empty, string.Empty);
    }
}
