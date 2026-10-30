using System.Windows;
using System.Windows.Controls;
using GTAModAssistant.Core.Models;
using GTAModAssistant.Core.Security;
using GTAModAssistant.ViewModels;

namespace GTAModAssistant.App.Views;

public partial class RpfTargetSelectionDialog : Window
{
    private readonly ModInstallStep _step;

    public RpfTargetSelectionDecision? Decision { get; private set; }

    public RpfTargetSelectionDialog(ModInstallStep step, string modName)
    {
        InitializeComponent();
        _step = step;

        TitleBlock.Text = $"Choose RPF7 Destination for '{step.FileName}' ({step.AssetFormatLabel})";
        ReasonBlock.Text = !string.IsNullOrWhiteSpace(step.UncertaintyReason)
            ? $"Why GTA Mod Assistant is asking instead of guessing: {step.UncertaintyReason}"
            : $"Select or update the safe 'mods/' RPF7 archive destination for '{step.FileName}' in '{modName}'.";

        CandidatesList.ItemsSource = step.CandidateRpfTargets;

        if (!string.IsNullOrWhiteSpace(step.RpfContainerRelativePath))
        {
            OuterRpfBox.Text = step.RpfContainerRelativePath;
            InnerPathBox.Text = step.RpfInternalPath;
        }
        else if (step.CandidateRpfTargets.Count > 0)
        {
            CandidatesList.SelectedIndex = 0;
        }
        else
        {
            OuterRpfBox.Text = "mods/x64e.rpf";
            InnerPathBox.Text = $"levels/gta5/vehicles.rpf/{step.FileName}";
        }

        if (!string.IsNullOrWhiteSpace(step.CanonicalModelStem))
        {
            ApplyToStemCheck.Content =
                $"Also apply this RPF archive directory to companion files sharing stem '{step.CanonicalModelStem}' (_hi.yft / .ytd)";
        }
    }

    private void OnCandidateSelected(object sender, SelectionChangedEventArgs e)
    {
        if (CandidatesList.SelectedItem is RpfTargetCandidate cand)
        {
            OuterRpfBox.Text = cand.SafeModsOuterRpfPath;
            InnerPathBox.Text = cand.InternalDirectoryOrFilePath;
        }
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        string rawOuter = OuterRpfBox.Text.Trim();
        string rawInner = InnerPathBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(rawOuter) || !rawOuter.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
        {
            ShowError("Outer archive path must end with '.rpf' (for example: mods/x64e.rpf or mods/update/x64/dlcpacks/patchday28ng/dlc.rpf).");
            return;
        }

        if (string.IsNullOrWhiteSpace(rawInner))
        {
            ShowError($"Internal RPF path cannot be empty (for example: levels/gta5/vehicles.rpf/{_step.FileName}).");
            return;
        }

        try
        {
            string safeOuter = PathSafetyGuard.EnsureSafeModsPath(rawOuter);
            if (!safeOuter.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
            {
                safeOuter = "mods/" + safeOuter;
            }

            Decision = new RpfTargetSelectionDecision(
                safeOuter,
                rawInner,
                ApplyToStemCheck.IsChecked == true);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ShowError(string msg)
    {
        ValidationErrorBlock.Text = msg;
        ValidationErrorBlock.Visibility = Visibility.Visible;
    }
}
