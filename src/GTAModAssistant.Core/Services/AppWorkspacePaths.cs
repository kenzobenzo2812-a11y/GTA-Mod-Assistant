namespace GTAModAssistant.Core.Services;

public sealed class AppWorkspacePaths
{
    public string RootDataDirectory { get; }
    public string DatabasePath => Path.Combine(RootDataDirectory, "gtama.db");
    public string BackupsDirectory => Path.Combine(RootDataDirectory, "backups");
    public string StagingDirectory => Path.Combine(RootDataDirectory, "staging");

    public AppWorkspacePaths(string? customDataDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(customDataDirectory))
        {
            RootDataDirectory = Path.GetFullPath(customDataDirectory);
        }
        else
        {
            string exeDir = AppContext.BaseDirectory;
            string portableMarker = Path.Combine(exeDir, "portable.mode");
            if (File.Exists(portableMarker))
            {
                RootDataDirectory = Path.Combine(exeDir, "GTAModAssistant_Data");
            }
            else
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(localAppData))
                {
                    localAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gtamodassistant");
                }
                RootDataDirectory = Path.Combine(localAppData, "GTAModAssistant");
            }
        }

        Directory.CreateDirectory(RootDataDirectory);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(StagingDirectory);
    }
}
