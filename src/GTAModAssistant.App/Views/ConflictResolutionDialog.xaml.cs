using System.Windows;
using GTAModAssistant.Core.Models;

namespace GTAModAssistant.App.Views;

public partial class ConflictResolutionDialog : Window
{
    public ConflictResolutionStrategy SelectedStrategy { get; private set; } = ConflictResolutionStrategy.CancelInstall;

    public ConflictResolutionDialog(IReadOnlyList<ConflictItem> conflicts, string modName)
    {
        InitializeComponent();
        HeaderBlock.Text = $"{conflicts.Count} Conflict / Duplicate Warning(s) While Installing '{modName}'";
        ConflictList.ItemsSource = conflicts;
        if (conflicts.Any(c => c.Kind == ConflictKind.DuplicateFileInPackage))
        {
            KeepFirstDupButton.Visibility = Visibility.Visible;
        }
    }

    private void OnKeepFirstDuplicateClick(object sender, RoutedEventArgs e)
    {
        SelectedStrategy = ConflictResolutionStrategy.KeepFirstDuplicateOnly;
        DialogResult = true;
        Close();
    }

    private void OnOverwriteClick(object sender, RoutedEventArgs e)
    {
        SelectedStrategy = ConflictResolutionStrategy.BackupAndOverwrite;
        DialogResult = true;
        Close();
    }

    private void OnSkipClick(object sender, RoutedEventArgs e)
    {
        SelectedStrategy = ConflictResolutionStrategy.SkipConflictingSteps;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        SelectedStrategy = ConflictResolutionStrategy.CancelInstall;
        DialogResult = false;
        Close();
    }
}
