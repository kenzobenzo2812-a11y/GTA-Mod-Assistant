using System.Text.RegularExpressions;
using GTAModAssistant.Core.Archives;
using GTAModAssistant.Core.Models;
using GTAModAssistant.Core.Security;

namespace GTAModAssistant.Core.Services;

public sealed record RpfAssetLocationMatch(
    string SafeModsOuterRpfPath,
    string InternalPath,
    int PatchdayPriority,
    bool FromModsFolderCopy);

public sealed partial class GameRpfSearchService
{
    private readonly Dictionary<string, List<RpfAssetLocationMatch>> _byFileName = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _fullCompositeKeys = new(StringComparer.OrdinalIgnoreCase);

    public bool HasAnyIndexedEntries => _byFileName.Count > 0;

    public static GameRpfSearchService BuildIndex(string? gtaRootPath)
    {
        var svc = new GameRpfSearchService();
        if (string.IsNullOrWhiteSpace(gtaRootPath) || !Directory.Exists(gtaRootPath))
        {
            return svc;
        }

        var candidateRpfs = new List<(string DiskFullPath, string SafeModsRelativePath, bool IsModsCopy)>();

        // 1. Existing mods/ RPF archives
        string modsDir = Path.Combine(gtaRootPath, "mods");
        if (Directory.Exists(modsDir))
        {
            foreach (var rpfFile in SafeEnumerateRpfFiles(modsDir))
            {
                string relFromRoot = PathSafetyGuard.NormalizeRelativePath(Path.GetRelativePath(gtaRootPath, rpfFile));
                if (!relFromRoot.Contains("_gtama_", StringComparison.OrdinalIgnoreCase))
                {
                    candidateRpfs.Add((rpfFile, relFromRoot, true));
                }
            }
        }

        // 2. Vanilla root x64*.rpf / common.rpf & update/ RPF archives (never modified directly; mapped to mods/...)
        foreach (var rootRpf in Directory.EnumerateFiles(gtaRootPath, "*.rpf", SearchOption.TopDirectoryOnly))
        {
            string rel = PathSafetyGuard.NormalizeRelativePath(Path.GetRelativePath(gtaRootPath, rootRpf));
            candidateRpfs.Add((rootRpf, PathSafetyGuard.EnsureSafeModsPath(rel), false));
        }

        string vanillaUpdateDir = Path.Combine(gtaRootPath, "update");
        if (Directory.Exists(vanillaUpdateDir))
        {
            foreach (var updRpf in SafeEnumerateRpfFiles(vanillaUpdateDir))
            {
                string rel = PathSafetyGuard.NormalizeRelativePath(Path.GetRelativePath(gtaRootPath, updRpf));
                candidateRpfs.Add((updRpf, PathSafetyGuard.EnsureSafeModsPath(rel), false));
            }
        }

        // Index TOCs (preferring mods/ copy if both mods/ and vanilla exist for the same outer RPF)
        var indexedOuterRpfs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (diskPath, safeModsRel, isModsCopy) in candidateRpfs.OrderByDescending(c => c.IsModsCopy))
        {
            if (!indexedOuterRpfs.Add(safeModsRel))
            {
                continue;
            }

            int priority = ComputeArchivePriority(safeModsRel);
            var internalPaths = Rpf7Archive.ListEntryPathsFast(diskPath, maxNestedDepth: 2);
            foreach (var inner in internalPaths)
            {
                string fileName = Path.GetFileName(inner);
                if (string.IsNullOrEmpty(fileName))
                {
                    continue;
                }

                svc._fullCompositeKeys.Add($"{safeModsRel.ToLowerInvariant()}::{inner.ToLowerInvariant()}");

                if (!svc._byFileName.TryGetValue(fileName, out var list))
                {
                    list = new List<RpfAssetLocationMatch>();
                    svc._byFileName[fileName] = list;
                }

                list.Add(new RpfAssetLocationMatch(safeModsRel, inner, priority, isModsCopy));
            }
        }

        return svc;
    }

    public bool ContainsExactEntry(string safeModsOuterRpfPath, string internalPath)
    {
        string outer = PathSafetyGuard.EnsureSafeModsPath(safeModsOuterRpfPath).ToLowerInvariant();
        string inner = Rpf7Archive.NormalizeInternalPath(internalPath).ToLowerInvariant();
        return _fullCompositeKeys.Contains($"{outer}::{inner}");
    }

    public (RpfAssetLocationMatch? UnambiguousBestMatch, IReadOnlyList<RpfAssetLocationMatch> AllMatches, bool IsAmbiguous) FindLocationsForFileName(string fileName)
    {
        if (!_byFileName.TryGetValue(fileName, out var rawMatches) || rawMatches.Count == 0)
        {
            return (null, Array.Empty<RpfAssetLocationMatch>(), false);
        }

        var ordered = rawMatches
            .OrderByDescending(m => m.PatchdayPriority)
            .ThenByDescending(m => m.FromModsFolderCopy)
            .ToList();

        if (ordered.Count == 1)
        {
            return (ordered[0], ordered, false);
        }

        // If highest priority is a patchdayXXng override of a base x64*.rpf / earlier patchday vehicle/weapon archive,
        // GTA V's VFS deterministically uses the highest patchday archive!
        var top = ordered[0];
        bool allPartOfPatchdayChain = ordered.All(m =>
            m.PatchdayPriority > 0 ||
            m.SafeModsOuterRpfPath.StartsWith("mods/x64", StringComparison.OrdinalIgnoreCase));

        if (allPartOfPatchdayChain && top.PatchdayPriority > ordered[1].PatchdayPriority)
        {
            return (top, ordered, false);
        }

        // Multiple unrelated DLC archives have the same filename -> ambiguous, ask user!
        return (null, ordered, true);
    }

    private static IEnumerable<string> SafeEnumerateRpfFiles(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*.rpf", SearchOption.AllDirectories);
        }
        catch
        {
            return Enumerable.Empty<string>();
        }
    }

    private static int ComputeArchivePriority(string safeModsRel)
    {
        var m = PatchdayNumberRegex().Match(safeModsRel);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int pdNum))
        {
            return 1000 + pdNum;
        }

        if (safeModsRel.Contains("dlcpacks/", StringComparison.OrdinalIgnoreCase))
        {
            return 500;
        }

        if (safeModsRel.EndsWith("update/update.rpf", StringComparison.OrdinalIgnoreCase))
        {
            return 300;
        }

        return 100;
    }

    [GeneratedRegex(@"patchday(\d+)(?:ng)?", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PatchdayNumberRegex();
}
