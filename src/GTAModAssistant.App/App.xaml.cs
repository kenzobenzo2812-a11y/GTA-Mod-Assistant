using System.Windows;
using GTAModAssistant.Core.Models;
using GTAModAssistant.Core.Services;
using GTAModAssistant.ViewModels;

namespace GTAModAssistant.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = new AppWorkspacePaths();
        var interaction = new WpfUserInteractionService();
        var viewModel = new MainViewModel(
            paths,
            interaction,
            uiDispatch: action =>
            {
                if (Dispatcher.CheckAccess())
                {
                    action();
                }
                else
                {
                    Dispatcher.Invoke(action);
                }
            },
            openIvPackageInstallerAutomation: new OpenIvPackageInstallerAutomation());

        DispatcherUnhandledException += (_, args) =>
        {
            viewModel.Repository.AppendLog(
                AuditLogLevel.Error,
                "UI",
                "Unhandled UI exception caught safely",
                args.Exception.ToString());
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{args.Exception.Message}",
                "GTA Mod Assistant Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        var mainWindow = new MainWindow(viewModel);
        MainWindow = mainWindow;
        mainWindow.Show();

        // Allow passing a .oiv / .zip mod file via Windows command-line / "Open With"
        if (e.Args.Length > 0 && (System.IO.File.Exists(e.Args[0]) || System.IO.Directory.Exists(e.Args[0])))
        {
            await viewModel.AnalyzeModInputAsync(e.Args[0]);
        }
    }
}
