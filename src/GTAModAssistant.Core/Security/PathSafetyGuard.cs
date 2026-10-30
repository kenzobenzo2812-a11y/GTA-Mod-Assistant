namespace GTAModAssistant.Core.Security;

public static class PathSafetyGuard
{
    private static readonly HashSet<string> BlockedExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".scr", ".com", ".pif", ".hta", ".reg", ".cpl"
    };

    private static readonly HashSet<string> ProtectedVanillaRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "gta5.exe",
        "playgtav.exe",
        "gtavlauncher.exe",
        "gtavlanguageselect.exe",
        "bink2w64.dll",
        "d3dcompiler_46.dll",
        "d3dcsx_46.dll",
        "gfsdk_shadowlib.win64.dll",
        "gfsdk_txaa.win64.dll",
        "gfsdk_txaa_alpharesolve.win64.dll",
        "gpuperfapidx11-x64.dll",
        "nvpmapi.core.win64.dll",
        "steam_api64.dll",
        "eossdk-win64-shipping.dll",
        "toxmod.dll",
        "opus.dll",
        "zlib1.dll",
        "oo2core_5_win64.dll"
    };

    public static bool IsBlockedExecutableOrScript(string path)
    {
        var ext = Path.GetExtension(NormalizeRelativeSeparators(path));
        return BlockedExecutableExtensions.Contains(ext);
    }

    public static bool IsProtectedVanillaRootFile(string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        if (normalized.Contains('/'))
        {
            // Check if it targets vanilla update/ or x64*.rpf outside mods/
            if (normalized.StartsWith("update/", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("x64/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }

        if (ProtectedVanillaRootFiles.Contains(normalized))
        {
            return true;
        }

        // Root x64a.rpf .. x64w.rpf, common.rpf are vanilla game archives - must always go to mods/!
        if (normalized.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Rewrites a vanilla RPF/DLC relative path (e.g. "update/update.rpf" or "x64e.rpf") to its safe "mods/..." equivalent.
    /// </summary>
    public static string EnsureSafeModsPath(string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Relative path cannot be empty.", nameof(relativePath));
        }

        if (normalized.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        if (normalized.StartsWith("update/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("x64/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("common.rpf", StringComparison.OrdinalIgnoreCase) ||
            (normalized.StartsWith("x64", StringComparison.OrdinalIgnoreCase) &&
             normalized.Contains(".rpf", StringComparison.OrdinalIgnoreCase)))
        {
            return "mods/" + normalized;
        }

        return normalized;
    }

    /// <summary>
    /// Safely resolves a relative path inside a root directory, throwing InvalidOperationException on Zip-Slip / traversal attempts.
    /// </summary>
    public static string ResolveSafeChildPath(string rootDirectory, string untrustedRelativePath)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("Root directory is required.", nameof(rootDirectory));
        }

        var normalizedRel = NormalizeRelativeSeparators(untrustedRelativePath).TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalizedRel))
        {
            throw new InvalidOperationException("Untrusted relative path is empty.");
        }

        if (Path.IsPathRooted(normalizedRel) || normalizedRel.Contains(':'))
        {
            throw new InvalidOperationException($"Blocked rooted or drive-qualified path in archive: '{untrustedRelativePath}'.");
        }

        var segments = normalizedRel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
        {
            if (seg == "." || seg == "..")
            {
                throw new InvalidOperationException($"Blocked path traversal segment in '{untrustedRelativePath}'.");
            }
        }

        var osRelative = string.Join(Path.DirectorySeparatorChar, segments);
        var fullRoot = Path.GetFullPath(rootDirectory);
        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
        {
            fullRoot += Path.DirectorySeparatorChar;
        }

        var combined = Path.GetFullPath(Path.Combine(fullRoot, osRelative));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!combined.StartsWith(fullRoot, comparison))
        {
            throw new InvalidOperationException($"Blocked Zip-Slip path escape outside '{rootDirectory}': '{untrustedRelativePath}'.");
        }

        return combined;
    }

    public static string NormalizeRelativePath(string path)
    {
        var s = NormalizeRelativeSeparators(path).Trim('/');
        while (s.Contains("//", StringComparison.Ordinal))
        {
            s = s.Replace("//", "/", StringComparison.Ordinal);
        }
        return s;
    }

    private static string NormalizeRelativeSeparators(string path) =>
        (path ?? string.Empty).Replace('\\', '/').Trim();
}
