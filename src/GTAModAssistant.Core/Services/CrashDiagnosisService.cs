using System.Text;
using GTAModAssistant.Core.Models;

namespace GTAModAssistant.Core.Services;

public sealed class CrashDiagnosisService
{
    private sealed record DiagnosticRule(
        string[] Terms,
        CrashFindingSeverity Severity,
        string Title,
        string SuggestedAction);

    private static readonly DiagnosticRule[] Rules =
    {
        new(
            ["scripthookv", "script hook v", "unsupported game version"],
            CrashFindingSeverity.LikelyCause,
            "ScriptHookV may not support this GTA V build",
            "Check the installed game build and install a ScriptHookV release that explicitly supports it. Temporarily remove other ASI plugins when testing."),
        new(
            ["gameconfig.xml", "out of game memory", "err_mem_embeddedalloc"],
            CrashFindingSeverity.LikelyCause,
            "Gameconfig or memory limits may be incompatible",
            "Use a gameconfig.xml made for the installed GTA V build. Test without HeapAdjuster/PackfileLimitAdjuster changes, then add compatible versions back one at a time."),
        new(
            ["out of memory", "not enough memory", "err_mem"],
            CrashFindingSeverity.LikelyCause,
            "The game reported a memory allocation failure",
            "Check add-on DLC and texture memory usage, confirm gameconfig and limit-adjuster versions, and test with recently added mods disabled."),
        new(
            ["failed to load", "could not load", "module not found", "error loading"],
            CrashFindingSeverity.Warning,
            "A plugin or dependency failed to load",
            "Use the nearby module name to check dependencies and version compatibility. Temporarily disable that plugin to confirm."),
        new(
            ["access violation", "0xc0000005", "exception code"],
            CrashFindingSeverity.Warning,
            "The log reports an access violation",
            "This is a symptom rather than a unique root cause. Test with recent script/ASI mods disabled and verify game files if the issue persists."),
        new(
            ["rpf", "archive is corrupted", "invalid archive"],
            CrashFindingSeverity.Warning,
            "An archive or RPF read error was reported",
            "Restore the affected archive from a verified backup and check whether OpenIV or another mod manager changed the same file."),
        new(
            ["dinput8.dll", "asi loader"],
            CrashFindingSeverity.Information,
            "ASI Loader is mentioned in the log",
            "Check that the ASI Loader and installed ASI plugins match your game version. Disable them temporarily to isolate the crash.")
    };

    public CrashDiagnosisReport Diagnose(string logText, string sourceFileName = "crash log")
    {
        ArgumentNullException.ThrowIfNull(logText);
        var findings = new List<CrashFinding>();
        string[] lines = logText.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);

        foreach (DiagnosticRule rule in Rules)
        {
            string? evidence = lines.FirstOrDefault(line =>
                rule.Terms.Any(term => line.Contains(term, StringComparison.OrdinalIgnoreCase)));
            if (!string.IsNullOrWhiteSpace(evidence))
            {
                findings.Add(new CrashFinding(
                    rule.Severity,
                    rule.Title,
                    Truncate(evidence.Trim(), 300),
                    rule.SuggestedAction));
            }
        }

        if (findings.Count == 0)
        {
            findings.Add(new CrashFinding(
                CrashFindingSeverity.Information,
                "No known crash signature was found",
                "The selected text log did not contain any of the built-in diagnostic markers.",
                "Review the final log lines, note the last mod or plugin mentioned, and retry with recently added mods disabled. This heuristic report cannot prove a root cause."));
        }

        string summary = findings.Any(f => f.Severity == CrashFindingSeverity.LikelyCause)
            ? "Potential causes found. Treat these as troubleshooting leads, not a definitive diagnosis."
            : "No definitive cause found. Review the evidence and isolate recent changes.";

        return new CrashDiagnosisReport(Path.GetFileName(sourceFileName), summary, findings);
    }

    public async Task<CrashDiagnosisReport> DiagnoseFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The selected crash log does not exist.", path);
        }

        string extension = Path.GetExtension(path);
        if (!extension.Equals(".log", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Choose a text-based .log or .txt file. Binary crash dumps are not parsed.");
        }

        var info = new FileInfo(path);
        if (info.Length > 10 * 1024 * 1024)
        {
            throw new InvalidDataException("Crash logs larger than 10 MB are not accepted.");
        }

        string text = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
        return Diagnose(text, path);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}
