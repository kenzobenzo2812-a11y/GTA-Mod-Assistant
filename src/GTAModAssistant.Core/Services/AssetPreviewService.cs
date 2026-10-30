using System.Buffers.Binary;
using GTAModAssistant.Core.Models;

namespace GTAModAssistant.Core.Services;

public sealed class AssetPreviewService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"
    };

    public IReadOnlyList<AssetPreviewItem> DescribeAssets(IEnumerable<ExtractedFileEntry> files)
    {
        var assets = new List<AssetPreviewItem>();
        foreach (ExtractedFileEntry file in files)
        {
            string extension = file.Extension;
            bool isImageFile = ImageExtensions.Contains(extension);
            bool canRenderImage = isImageFile && file.SizeBytes <= 50L * 1024 * 1024;
            bool isModel = extension.Equals(".yft", StringComparison.OrdinalIgnoreCase);
            bool isTextureDictionary = extension.Equals(".ytd", StringComparison.OrdinalIgnoreCase);
            if (!isImageFile && !isModel && !isTextureDictionary)
            {
                continue;
            }

            string details = isImageFile
                ? canRenderImage
                    ? $"Image file ({extension.TrimStart('.').ToUpperInvariant()}); preview is rendered locally."
                    : "Image exceeds the 50 MB preview limit."
                : DescribeRsc7Header(file.FullStagingPath);

            assets.Add(new AssetPreviewItem(
                file.RelativePath,
                file.FullStagingPath,
                isModel ? "GTA V model (.yft)" : isTextureDictionary ? "GTA V texture dictionary (.ytd)" : "Image",
                file.SizeBytes,
                details,
                canRenderImage));
        }

        return assets;
    }

    private static string DescribeRsc7Header(string path)
    {
        Span<byte> header = stackalloc byte[16];
        using FileStream stream = File.OpenRead(path);
        try
        {
            stream.ReadExactly(header);
        }
        catch (EndOfStreamException)
        {
            return "Binary asset header is incomplete; no rendered preview is available.";
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
        if (magic != 0x37435352)
        {
            return "Not a recognized RSC7 resource header; binary preview is unavailable.";
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4));
        uint systemFlags = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8, 4));
        uint graphicsFlags = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(12, 4));
        return $"RSC7 resource v{version}; system flags 0x{systemFlags:X8}; graphics flags 0x{graphicsFlags:X8}. " +
               "The asset is inspected locally; native YFT/YTD rendering is not supported.";
    }
}
