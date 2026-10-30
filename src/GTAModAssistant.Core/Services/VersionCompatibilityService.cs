using System.Text.RegularExpressions;
using GTAModAssistant.Core.Models;

namespace GTAModAssistant.Core.Services;

public static partial class VersionCompatibilityService
{
    private static readonly (int MinBuild, string Name)[] KnownBuilds =
    {
        (350,  "PC Launch / Ill-Gotten Gains (2015)"),
        (678,  "Lowriders / Executives (2016)"),
        (877,  "Bikers (2016)"),
        (944,  "Import/Export (Dec 2016)"),
        (1103, "Gunrunning (Jun 2017)"),
        (1180, "Smuggler's Run (Aug 2017)"),
        (1290, "The Doomsday Heist (Dec 2017)"),
        (1365, "Southern SA Super Sport Series (Mar 2018)"),
        (1493, "After Hours (Jul 2018)"),
        (1604, "Arena War (Dec 2018)"),
        (1737, "The Diamond Casino & Resort (Jul 2019)"),
        (1868, "The Diamond Casino Heist (Dec 2019)"),
        (2060, "Los Santos Summer Special (Aug 2020)"),
        (2189, "The Cayo Perico Heist (Dec 2020)"),
        (2372, "Los Santos Tuners (Jul 2021)"),
        (2545, "The Contract (Dec 2021)"),
        (2612, "Spring 2022 Update"),
        (2699, "The Criminal Enterprises (Jul 2022)"),
        (2802, "Los Santos Drug Wars (Dec 2022)"),
        (2824, "Los Santos Drug Wars Patch (Feb 2023)"),
        (2845, "Los Santos Drug Wars Security Patch (Mar 2023)"),
        (2944, "San Andreas Mercenaries (Jun 2023)"),
        (3095, "The Chop Shop (Dec 2023)"),
        (3179, "Bottom Dollar Bounties (Jun 2024)"),
        (3258, "BattlEye / Fall 2024 Update"),
        (3323, "Agents of Sabotage (Dec 2024)"),
        (3351, "Agents of Sabotage Patch (2025)"),
        (3411, "Money Fronts Update (2025)"),
        (3586, "Late 2025 Title Update"),
        (3717, "2026 Title Update"),
        (3889, "Latest 2026 Title Update")
    };

    public static string GetTitleUpdateName(int buildNumber)
    {
        if (buildNumber <= 0)
        {
            return "Unverified Build";
        }

        string matched = "Legacy PC Build";
        foreach (var (minBuild, name) in KnownBuilds)
        {
            if (buildNumber >= minBuild)
            {
                matched = name;
            }
        }
        return matched;
    }

    public static (int? MinBuild, int? MaxMentionedBuild) ExtractBuildNumbersFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }

        var found = new HashSet<int>();

        // Pattern 1: 1.0.<build>.<rev> e.g. 1.0.3095.0 or v1.0.2802
        foreach (Match m in FullVersionRegex().Matches(text))
        {
            if (int.TryParse(m.Groups[1].Value, out int b) && IsPlausibleGtaBuild(b))
            {
                found.Add(b);
            }
        }

        // Pattern 2: "build 3095", "gamebuild 2802", "b3351", "v3095+"
        foreach (Match m in ExplicitBuildRegex().Matches(text))
        {
            if (int.TryParse(m.Groups[1].Value, out int b) && IsPlausibleGtaBuild(b))
            {
                found.Add(b);
            }
        }

        if (found.Count == 0)
        {
            return (null, null);
        }

        return (found.Min(), found.Max());
    }

    public static CompatibilityReport EvaluateCompatibility(
        GameEnvironmentInfo gameEnv,
        ModCategory category,
        IReadOnlyList<ExtractedFileEntry> files,
        string combinedReadmeAndSourceNames,
        int? llmMinBuild = null,
        int? llmTargetBuild = null)
    {
        int currentBuild = gameEnv.BuildNumber;
        var (textMin, textMax) = ExtractBuildNumbersFromText(combinedReadmeAndSourceNames);

        int? minBuild = llmMinBuild ?? textMin;
        int? targetBuild = llmTargetBuild ?? textMax;

        var details = new List<string>();
        var prereqs = new List<string>();

        bool touchesGameconfig = files.Any(f =>
            string.Equals(Path.GetFileName(f.RelativePath), "gameconfig.xml", StringComparison.OrdinalIgnoreCase));
        bool bundlesScriptHookV = files.Any(f =>
            string.Equals(Path.GetFileName(f.RelativePath), "ScriptHookV.dll", StringComparison.OrdinalIgnoreCase));
        bool hasAsi = files.Any(f => f.Extension == ".asi");
        bool hasDlcRpf = files.Any(f =>
            string.Equals(Path.GetFileName(f.RelativePath), "dlc.rpf", StringComparison.OrdinalIgnoreCase));
        bool hasShvdnDll = category == ModCategory.ScriptHookVDotNetMod;

        if (hasAsi || bundlesScriptHookV)
        {
            prereqs.Add("ScriptHookV + dinput8.dll (ASI Loader)");
        }
        if (hasShvdnDll)
        {
            prereqs.Add("ScriptHookV + ScriptHookVDotNet (v2/v3)");
        }
        if (hasDlcRpf || category is ModCategory.AddOnDlcPack or ModCategory.RpfAssetReplacement or ModCategory.DataOrConfigMod)
        {
            prereqs.Add("OpenIV.asi + dinput8.dll (required for 'mods' folder loading)");
        }
        if (hasDlcRpf)
        {
            prereqs.Add("Updated gameconfig.xml + HeapAdjuster + PackfileLimitAdjuster (recommended for Add-On DLCs)");
        }

        if (currentBuild <= 0)
        {
            details.Add("GTA5.exe build version could not be verified yet; run 'Scan Game' on a valid GTA V folder before installing build-sensitive mods.");
            return new CompatibilityReport(
                CompatibilitySeverity.Advisory,
                minBuild,
                targetBuild,
                currentBuild,
                "Game Version Unscanned - Verify GTA V Path First",
                details,
                prereqs);
        }

        // Rule 1: Mod requires a NEWER GTA V build than installed!
        if (minBuild.HasValue && minBuild.Value > currentBuild && (!targetBuild.HasValue || targetBuild.Value >= minBuild.Value))
        {
            // Only if the lowest mentioned build is newer than current game
            if (textMin.HasValue && textMin.Value > currentBuild || (!textMin.HasValue && minBuild.Value > currentBuild))
            {
                details.Add($"Mod requires GTA V Build {minBuild.Value}+ ({GetTitleUpdateName(minBuild.Value)}), while your installed GTA V is Build {currentBuild} ({gameEnv.BuildTitleName}).");
                return new CompatibilityReport(
                    CompatibilitySeverity.IncompatibleBlocked,
                    minBuild,
                    targetBuild,
                    currentBuild,
                    $"Incompatible: Requires Newer GTA V Build ({minBuild.Value}+)",
                    details,
                    prereqs);
            }
        }

        // Rule 2: gameconfig.xml is STRICTLY locked to exact GTA V Title Update builds!
        if (touchesGameconfig && targetBuild.HasValue && targetBuild.Value < currentBuild)
        {
            details.Add(
                $"CRITICAL: This mod contains gameconfig.xml built for GTA V Build {targetBuild.Value} ({GetTitleUpdateName(targetBuild.Value)}), " +
                $"which is older than your installed GTA V Build {currentBuild} ({gameEnv.BuildTitleName}). " +
                "Installing an outdated gameconfig.xml causes GTA V to crash on launch!");
            return new CompatibilityReport(
                CompatibilitySeverity.IncompatibleBlocked,
                minBuild,
                targetBuild,
                currentBuild,
                $"BLOCKED: Outdated gameconfig.xml (Built for {targetBuild.Value}, Game is {currentBuild})",
                details,
                prereqs);
        }

        // Rule 3: Bundled ScriptHookV.dll from an older build
        if (bundlesScriptHookV && targetBuild.HasValue && targetBuild.Value < currentBuild)
        {
            details.Add(
                $"Mod bundles an outdated ScriptHookV.dll (Build {targetBuild.Value}) which will refuse to boot GTA V Build {currentBuild}.");
            return new CompatibilityReport(
                CompatibilitySeverity.IncompatibleBlocked,
                minBuild,
                targetBuild,
                currentBuild,
                $"BLOCKED: Outdated Bundled ScriptHookV.dll (Build {targetBuild.Value} < {currentBuild})",
                details,
                prereqs);
        }

        // Rule 4: Outdated native .asi / script mod warning
        if ((hasAsi || hasShvdnDll) && targetBuild.HasValue && currentBuild - targetBuild.Value >= 200)
        {
            details.Add(
                $"This script/ASI mod was last updated for GTA V Build {targetBuild.Value} ({GetTitleUpdateName(targetBuild.Value)}), " +
                $"which is {currentBuild - targetBuild.Value} build revisions older than your GTA V Build {currentBuild}. " +
                "Native memory patterns or hashes may be outdated. A verified backup will allow one-click rollback if needed.");
            return new CompatibilityReport(
                CompatibilitySeverity.WarningOutdated,
                minBuild,
                targetBuild,
                currentBuild,
                $"Warning: Older Script/ASI Mod (Target Build {targetBuild.Value} vs Game {currentBuild})",
                details,
                prereqs);
        }

        // Rule 5: Older data/meta file warning
        if (category == ModCategory.DataOrConfigMod && targetBuild.HasValue && currentBuild - targetBuild.Value >= 250)
        {
            details.Add(
                $"Data/meta configuration targets GTA V Build {targetBuild.Value} while your game is Build {currentBuild}. " +
                "Newer DLC entries or settings added by Rockstar since Build " + targetBuild.Value + " could be omitted.");
            return new CompatibilityReport(
                CompatibilitySeverity.WarningOutdated,
                minBuild,
                targetBuild,
                currentBuild,
                $"Warning: Older Data/Config File (Build {targetBuild.Value} vs {currentBuild})",
                details,
                prereqs);
        }

        if (targetBuild.HasValue)
        {
            details.Add($"Verified compatible with GTA V Build {currentBuild} (mod references Build {targetBuild.Value} - {GetTitleUpdateName(targetBuild.Value)}).");
        }
        else
        {
            details.Add($"Asset format verified compatible with GTA V v{gameEnv.GameVersion} (Build {currentBuild}).");
        }

        if (!gameEnv.OpenIvAsiInstalled && (hasDlcRpf || category is ModCategory.AddOnDlcPack or ModCategory.RpfAssetReplacement or ModCategory.DataOrConfigMod))
        {
            details.Add("Note: OpenIV.asi was not detected in your GTA V root folder yet; enable it in OpenIV > Tools > ASI Manager so GTA V loads the 'mods' folder.");
        }

        return new CompatibilityReport(
            CompatibilitySeverity.Compatible,
            minBuild,
            targetBuild,
            currentBuild,
            $"Compatible with GTA V v{gameEnv.GameVersion}",
            details,
            prereqs);
    }

    private static bool IsPlausibleGtaBuild(int build) => build >= 323 && build <= 9999;

    [GeneratedRegex(@"\b1\.0\.(\d{3,4})(?:\.\d+)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex FullVersionRegex();

    [GeneratedRegex(@"\b(?:game\s*build|build|patch|v)\s*[:#-]?\s*(\d{3,4})\+?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ExplicitBuildRegex();
}
