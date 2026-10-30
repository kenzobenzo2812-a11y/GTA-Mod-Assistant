using System.Windows;
using GTAModAssistant.ViewModels;

namespace GTAModAssistant.App.Views;

public partial class NoReadmeDisclaimerDialog : Window
{
    public NoReadmeUserChoice Choice { get; private set; } = NoReadmeUserChoice.Cancel;

    public NoReadmeDisclaimerDialog(
        string modName,
        int discoveredFileCount,
        bool isLlmActive,
        string activeEngineDescription)
    {
        InitializeComponent();
        HeaderBlock.Text = $"No README Found in '{modName}'";
        SubheaderBlock.Text = $"Discovered {discoveredFileCount} file(s) inside package — zero README / installation text files detected.";
        ActiveEngineBlock.Text = isLlmActive
            ? $"Active AI Engine: {activeEngineDescription}"
            : $"Active AI Engine: {activeEngineDescription} (or click 'Configure AI Settings...' to connect OpenAI, Groq, or free local Ollama).";
    }

    private void OnProceedClick(object sender, RoutedEventArgs e)
    {
        Choice = NoReadmeUserChoice.ProceedWithAiFileAnalysis;
        DialogResult = true;
        Close();
    }

    private void OnOpenAiSettingsClick(object sender, RoutedEventArgs e)
    {
        Choice = NoReadmeUserChoice.OpenAiSettings;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Choice = NoReadmeUserChoice.Cancel;
        DialogResult = false;
        Close();
    }
}
