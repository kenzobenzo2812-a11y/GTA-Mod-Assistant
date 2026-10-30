namespace GTAModAssistant.Core.Models;

public enum CrashFindingSeverity
{
    Information = 0,
    Warning = 1,
    LikelyCause = 2
}

public sealed record CrashFinding(
    CrashFindingSeverity Severity,
    string Title,
    string Evidence,
    string SuggestedAction);

public sealed record CrashDiagnosisReport(
    string SourceFileName,
    string Summary,
    IReadOnlyList<CrashFinding> Findings);
