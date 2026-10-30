using System.Windows;
using Microsoft.Win32;
using GTAModAssistant.App.Views;
using GTAModAssistant.Core.Models;
using GTAModAssistant.ViewModels;

namespace GTAModAssistant.App;

public sealed class WpfUserInteractionService : IUserInteractionService
{
    private Window? OwnerWindow => Application.Current?.MainWindow;

    public bool ConfirmDestructiveAction(string title, string message, string confirmButtonText = "Confirm")
    {
        var dlg = new ConfirmDialog(title, message, confirmButtonText);
        if (OwnerWindow is { IsLoaded: true })
        {
            dlg.Owner = OwnerWindow;
        }
        return dlg.ShowDialog() == true;
    }

    public ConflictResolutionStrategy PromptConflictResolution(IReadOnlyList<ConflictItem> conflicts, string modName)
    {
        var dlg = new ConflictResolutionDialog(conflicts, modName);
        if (OwnerWindow is { IsLoaded: true })
        {
            dlg.Owner = OwnerWindow;
        }
        dlg.ShowDialog();
        return dlg.SelectedStrategy;
    }

    public RpfTargetSelectionDecision? PromptAssetTargetSelection(ModInstallStep uncertainStep, string modName)
    {
        var dlg = new RpfTargetSelectionDialog(uncertainStep, modName);
        if (OwnerWindow is { IsLoaded: true })
        {
            dlg.Owner = OwnerWindow;
        }
        return dlg.ShowDialog() == true ? dlg.Decision : null;
    }

    public NoReadmeUserChoice PromptNoReadmeDisclaimer(
        string modName,
        int discoveredFileCount,
        bool isLlmActive,
        string activeEngineDescription)
    {
        var dlg = new NoReadmeDisclaimerDialog(modName, discoveredFileCount, isLlmActive, activeEngineDescription);
        if (OwnerWindow is { IsLoaded: true })
        {
            dlg.Owner = OwnerWindow;
        }
        dlg.ShowDialog();
        return dlg.Choice;
    }

    public string? BrowseModArchiveFile()
    {
        var ofd = new OpenFileDialog
        {
            Title = "Select GTA V Mod Package (.zip, .rar, .oiv, .7z)",
            Filter = "GTA V Mod Packages (*.zip;*.rar;*.oiv;*.7z)|*.zip;*.rar;*.oiv;*.7z|OpenIV Packages (*.oiv)|*.oiv|All Files (*.*)|*.*",
            CheckFileExists = true
        };
        return ofd.ShowDialog(OwnerWindow) == true ? ofd.FileName : null;
    }

    public string? BrowseFolder(string title, string? initialDirectory = null)
    {
        var dlg = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && System.IO.Directory.Exists(initialDirectory))
        {
            dlg.InitialDirectory = initialDirectory;
        }
        return dlg.ShowDialog(OwnerWindow) == true ? dlg.FolderName : null;
    }

    public string? PromptSaveFile(string title, string defaultFileName, string filter)
    {
        var sfd = new SaveFileDialog
        {
            Title = title,
            FileName = defaultFileName,
            Filter = filter
        };
        return sfd.ShowDialog(OwnerWindow) == true ? sfd.FileName : null;
    }

    public (string Name, string Description)? PromptNewProfile()
    {
        var dlg = new NewProfileDialog();
        if (OwnerWindow is { IsLoaded: true })
        {
            dlg.Owner = OwnerWindow;
        }
        return dlg.ShowDialog() == true ? (dlg.ProfileName, dlg.ProfileDescription) : null;
    }

    public void ShowNotification(string title, string message, bool isError = false)
    {
        MessageBox.Show(
            OwnerWindow ?? new Window(),
            message,
            title,
            MessageBoxButton.OK,
            isError ? MessageBoxImage.Error : MessageBoxImage.Information);
    }
}
