using System.Windows;
using GTAModAssistant.ViewModels;

namespace GTAModAssistant.App;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = ViewModel;

        Loaded += async (_, _) => await ViewModel.ScanGameAsync();
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
