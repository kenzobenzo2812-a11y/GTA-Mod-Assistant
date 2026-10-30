namespace GTAModAssistant.Core.Models;

public sealed record AssetPreviewItem(
    string RelativePath,
    string FullPath,
    string Kind,
    long SizeBytes,
    string Details,
    bool CanRenderImage);
