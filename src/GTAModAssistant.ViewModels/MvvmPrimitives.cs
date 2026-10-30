using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using GTAModAssistant.Core.Models;

namespace GTAModAssistant.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _executeAsync;
    private readonly Predicate<object?>? _canExecute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<object?, Task> executeAsync, Predicate<object?>? canExecute = null)
    {
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync(parameter);

    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await _executeAsync(parameter);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed record RpfTargetSelectionDecision(
    string OuterRpfRelativePath,
    string InternalPath,
    bool ApplyToCompanionStemFiles);

public enum NoReadmeUserChoice
{
    ProceedWithAiFileAnalysis = 0,
    OpenAiSettings = 1,
    Cancel = 2
}

public interface IUserInteractionService
{
    bool ConfirmDestructiveAction(string title, string message, string confirmButtonText = "Confirm");
    ConflictResolutionStrategy PromptConflictResolution(IReadOnlyList<ConflictItem> conflicts, string modName);
    RpfTargetSelectionDecision? PromptAssetTargetSelection(ModInstallStep uncertainStep, string modName);
    NoReadmeUserChoice PromptNoReadmeDisclaimer(
        string modName,
        int discoveredFileCount,
        bool isLlmActive,
        string activeEngineDescription);
    string? BrowseModArchiveFile();
    string? BrowseFolder(string title, string? initialDirectory = null);
    string? PromptSaveFile(string title, string defaultFileName, string filter);
    (string Name, string Description)? PromptNewProfile();
    void ShowNotification(string title, string message, bool isError = false);
}
