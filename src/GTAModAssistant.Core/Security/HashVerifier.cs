using System.Security.Cryptography;

namespace GTAModAssistant.Core.Security;

public static class HashVerifier
{
    public static string ComputeFileSha256(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Cannot compute SHA-256 of missing file.", filePath);
        }

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
        return ComputeStreamSha256(stream);
    }

    public static string ComputeStreamSha256(Stream stream)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ComputeBytesSha256(ReadOnlySpan<byte> bytes)
    {
        Span<byte> dest = stackalloc byte[32];
        SHA256.HashData(bytes, dest);
        return Convert.ToHexString(dest).ToLowerInvariant();
    }

    public static bool VerifyFileSha256(string filePath, string expectedSha256, out string actualSha256)
    {
        actualSha256 = string.Empty;
        if (!File.Exists(filePath))
        {
            return false;
        }

        actualSha256 = ComputeFileSha256(filePath);
        return string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }
}
