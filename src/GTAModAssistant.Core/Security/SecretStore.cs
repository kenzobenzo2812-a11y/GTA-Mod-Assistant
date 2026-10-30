using System.Security.Cryptography;
using System.Text;

namespace GTAModAssistant.Core.Security;

public sealed class SecretStore
{
    private readonly string _secretFilePath;

    public SecretStore(string appDataDirectory)
    {
        Directory.CreateDirectory(appDataDirectory);
        _secretFilePath = Path.Combine(appDataDirectory, "ai_secret.dpapi");
    }

    public string? GetAiApiKey()
    {
        var envKey = Environment.GetEnvironmentVariable("GTAMA_AI_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            return envKey.Trim();
        }

        var openAiEnv = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(openAiEnv))
        {
            return openAiEnv.Trim();
        }

        if (!File.Exists(_secretFilePath))
        {
            return null;
        }

        try
        {
            var cipherBytes = File.ReadAllBytes(_secretFilePath);
            if (OperatingSystem.IsWindows())
            {
                var plainBytes = ProtectedData.Unprotect(cipherBytes, GetEntropy(), DataProtectionScope.CurrentUser);
                var key = Encoding.UTF8.GetString(plainBytes).Trim();
                return string.IsNullOrWhiteSpace(key) ? null : key;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public bool HasConfiguredApiKey() => !string.IsNullOrWhiteSpace(GetAiApiKey());

    public void SaveAiApiKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (File.Exists(_secretFilePath))
            {
                File.Delete(_secretFilePath);
            }
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Persisting encrypted user secrets uses Windows DPAPI; set GTAMA_AI_API_KEY environment variable on non-Windows hosts.");
        }

        var plainBytes = Encoding.UTF8.GetBytes(apiKey.Trim());
        var cipherBytes = ProtectedData.Protect(plainBytes, GetEntropy(), DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_secretFilePath, cipherBytes);
    }

    private static byte[] GetEntropy() =>
        Encoding.UTF8.GetBytes("GTAModAssistant.UserScopedSecret.v1");
}
