namespace GTAModAssistant.Core.Models;

public sealed record AiSettings
{
    public bool EnableCloudOrLocalLlm { get; init; } = true;
    public string EndpointUrl { get; init; } = "https://api.openai.com/v1/chat/completions";
    public string ModelName { get; init; } = "gpt-4o-mini";
    public int TimeoutSeconds { get; init; } = 25;
}

public sealed record LlmProposedStep
{
    public string Operation { get; init; } = string.Empty;
    public string SourceRelativePath { get; init; } = string.Empty;
    public string TargetRelativePath { get; init; } = string.Empty;
    public string RpfContainerRelativePath { get; init; } = string.Empty;
    public string RpfInternalPath { get; init; } = string.Empty;
    public string DlcListEntry { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
}

public sealed record LlmReadmeAnalysisResponse
{
    public string ModName { get; init; } = string.Empty;
    public string Version { get; init; } = "1.0";
    public string Author { get; init; } = "Unknown";
    public string Category { get; init; } = "Unknown";
    public int? MinimumBuild { get; init; }
    public int? TargetBuild { get; init; }
    public double Confidence { get; init; } = 0.85;
    public string ReasoningSummary { get; init; } = string.Empty;
    public List<string> Prerequisites { get; init; } = new();
    public List<string> SpawnNames { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
    public List<LlmProposedStep> Steps { get; init; } = new();
}
