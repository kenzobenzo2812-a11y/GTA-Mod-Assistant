using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using GTAModAssistant.App.Views;
using GTAModAssistant.Core.Models;
using GTAModAssistant.Core.Services;
using GTAModAssistant.ViewModels;
using Microsoft.Win32;

namespace GTAModAssistant.App;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = ViewModel;

        Loaded += async (_, _) =>
        {
            await ViewModel.ScanGameAsync();
            await CheckForUpdatesAsync(showNoUpdate: false);
        };
    }

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync(showNoUpdate: true);

    private async Task CheckForUpdatesAsync(bool showNoUpdate)
    {
        try
        {
            var service = new GitHubUpdateService();
            var update = await service.CheckForUpdateAsync(ViewModel.AppVersionBadge);
            if (update is null)
            {
                if (showNoUpdate)
                {
                    MessageBox.Show(this, "You are using the latest stable release.", "No updates available",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return;
            }

            MessageBoxResult answer = MessageBox.Show(
                this,
                $"{update.Name} is available. Open the official GitHub release page to review and download it?",
                "GTA Mod Assistant update available",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo(update.Url) { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, $"Could not check for updates: {ex.Message}", "Update check failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnDiagnoseCrashClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a GTA V or mod crash log",
            Filter = "Text logs (*.log;*.txt)|*.log;*.txt|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var report = await new CrashDiagnosisService().DiagnoseFileAsync(dialog.FileName);
            string findings = string.Join(
                Environment.NewLine + Environment.NewLine,
                report.Findings.Select(f => $"[{f.Severity}] {f.Title}{Environment.NewLine}Evidence: {f.Evidence}{Environment.NewLine}Suggested action: {f.SuggestedAction}"));
            MessageBox.Show(this, $"{report.Summary}{Environment.NewLine}{Environment.NewLine}{findings}",
                $"Crash diagnosis — {report.SourceFileName}", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(this, ex.Message, "Could not diagnose crash log",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnExportModpackClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedProfile is null)
        {
            MessageBox.Show(this, "Select a mod profile before exporting.", "No profile selected",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export complete mod setup",
            Filter = "GTA Mod Assistant modpack (*.gtmapack)|*.gtmapack",
            FileName = $"{ViewModel.SelectedProfile.Name}.gtmapack",
            AddExtension = true,
            DefaultExt = ".gtmapack"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var service = new ModpackService();
            service.Export(
                dialog.FileName,
                ViewModel.SelectedProfile,
                ViewModel.Repository.GetInstalledMods(),
                ViewModel.Repository.GetProfileModStates(ViewModel.SelectedProfile.Id),
                ViewModel.GameEnv.BuildNumber);
            MessageBox.Show(this, $"Modpack exported with original mod packages and profile states:{Environment.NewLine}{dialog.FileName}",
                "Modpack exported", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or OverflowException)
        {
            MessageBox.Show(this, ex.Message, "Could not export modpack",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnImportModpackClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a GTA Mod Assistant modpack",
            Filter = "GTA Mod Assistant modpack (*.gtmapack)|*.gtmapack",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            string destination = System.IO.Path.Combine(ViewModel.AppDataDirectory, "imported_modpacks");
            ImportedModpack modpack = new ModpackService().Import(dialog.FileName, destination);
            if (modpack.ModSourcePaths.Count == 0)
            {
                MessageBox.Show(this, "The modpack contains no source packages.", "Empty modpack",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string packageList = string.Join(
                Environment.NewLine,
                modpack.Manifest.Mods.Take(10).Select(mod =>
                    $"• {mod.Name} ({(mod.Enabled ? "enabled" : "disabled")})"));
            if (modpack.Manifest.Mods.Count > 10)
            {
                packageList += $"{Environment.NewLine}...and {modpack.Manifest.Mods.Count - 10} more.";
            }
            MessageBoxResult answer = MessageBox.Show(
                this,
                $"Imported '{modpack.Manifest.Name}' with {modpack.ModSourcePaths.Count} mod package(s).{Environment.NewLine}" +
                $"Profile states:{Environment.NewLine}{packageList}{Environment.NewLine}{Environment.NewLine}" +
                $"The packages were extracted safely to:{Environment.NewLine}{modpack.ExtractionDirectory}{Environment.NewLine}{Environment.NewLine}" +
                "Analyze the first package now? Use Add Mod/Add Folder to review the remaining packages. Install each separately; importing does not modify GTA V.",
                "Modpack imported",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes)
            {
                await ViewModel.AnalyzeModInputAsync(modpack.ModSourcePaths[0]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException or OverflowException)
        {
            MessageBox.Show(this, ex.Message, "Could not import modpack",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnPreviewAssetsClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentPlan is null)
        {
            MessageBox.Show(this, "Analyze a mod package first to preview its assets.", "No mod package",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        IReadOnlyList<GTAModAssistant.Core.Models.AssetPreviewItem> assets =
            new AssetPreviewService().DescribeAssets(ViewModel.CurrentPlan.DiscoveredFiles);
        if (assets.Count == 0)
        {
            MessageBox.Show(this, "This package has no supported images, .yft models, or .ytd texture dictionaries to preview.",
                "No previewable assets", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        new AssetPreviewWindow(assets) { Owner = this }.ShowDialog();
    }

    private void OnWindowDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            DragDropOverlay.Visibility = Visibility.Visible;
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWindowDragLeave(object sender, DragEventArgs e)
    {
        DragDropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        DragDropOverlay.Visibility = Visibility.Collapsed;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            await ViewModel.HandleDroppedPathsAsync(files);
        }
    }

    private void OnSaveAiSettingsClick(object sender, RoutedEventArgs e)
    {
        string? newKey = string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password)
            ? null
            : ApiKeyPasswordBox.Password;
        ViewModel.SaveAiConfiguration(newKey, clearSavedKey: false);
        ApiKeyPasswordBox.Clear();
    }

    private void OnClearApiKeyClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SaveAiConfiguration(null, clearSavedKey: true);
        ApiKeyPasswordBox.Clear();
    }
}
