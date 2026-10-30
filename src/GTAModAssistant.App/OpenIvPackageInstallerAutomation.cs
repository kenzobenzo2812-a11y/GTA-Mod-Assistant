using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using GTAModAssistant.Core.Archives;

namespace GTAModAssistant.App;

public sealed class OpenIvPackageInstallerAutomation : IOpenIvPackageInstallerAutomation
{
    private static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan InstallerDiscoveryTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan AutomationQueryTimeout = TimeSpan.FromSeconds(5);
    private static readonly SemaphoreSlim AutomationQueryGate = new(1, 1);

    public async Task<OpenIvAutomationResult> InstallAsync(
        string openIvExePath,
        string oivPackagePath,
        string expectedPackageName,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new OpenIvAutomationResult(false, "Automatic OpenIV UI control is only supported on Windows.");
        }

        if (string.IsNullOrWhiteSpace(expectedPackageName))
        {
            return new OpenIvAutomationResult(false, "The expected package name is missing; OpenIV was not automated.");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = openIvExePath,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(openIvExePath) ?? string.Empty
            };
            startInfo.ArgumentList.Add("-core.game.select:false");
            startInfo.ArgumentList.Add("-core.game:Five");
            startInfo.ArgumentList.Add(oivPackagePath);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new OpenIvAutomationResult(false, "OpenIV could not be started.");
            }

            var discoveryDeadline = DateTime.UtcNow + InstallerDiscoveryTimeout;
            AutomationElement? installerWindow = null;
            AutomationElement[] controls = Array.Empty<AutomationElement>();
            string processName = Path.GetFileNameWithoutExtension(openIvExePath);

            while (DateTime.UtcNow < discoveryDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var openIvProcessIds = GetOpenIvProcessIds(process.Id, processName);
                HideOpenIvWindows(openIvProcessIds);

                var scan = await FindPackageInstallerWindowAsync(
                    openIvProcessIds,
                    expectedPackageName,
                    cancellationToken);
                installerWindow = scan.Window;
                controls = scan.Controls;
                if (installerWindow is not null)
                {
                    break;
                }

                await Task.Delay(400, cancellationToken);
            }

            if (installerWindow is null)
            {
                return new OpenIvAutomationResult(
                    false,
                    "OpenIV's package installer could not be identified safely; no buttons were clicked.");
            }

            var installDeadline = DateTime.UtcNow + InstallerTimeout;
            bool modsTargetSelected = false;
            bool installInvoked = false;
            for (int step = 0; step < 4; step++)
            {
                var modsTarget = controls.FirstOrDefault(IsModsFolderChoice);
                if (modsTarget is not null)
                {
                    if (!SelectModsFolder(modsTarget))
                    {
                        return new OpenIvAutomationResult(
                            false,
                            "OpenIV exposed a 'mods folder' target but it could not be selected; installation was not started.");
                    }

                    modsTargetSelected = true;
                }

                if (!modsTargetSelected)
                {
                    if (!TryInvoke(controls.FirstOrDefault(IsNextButton)))
                    {
                        return new OpenIvAutomationResult(
                            false,
                            "OpenIV did not expose a selectable 'mods folder' target; installation was not started.");
                    }
                }
                else if (TryInvoke(controls.FirstOrDefault(IsInstallButton)))
                {
                    installInvoked = true;
                    break;
                }
                else if (!TryInvoke(controls.FirstOrDefault(IsNextButton)))
                {
                    return new OpenIvAutomationResult(
                        false,
                        "OpenIV did not expose an accessible Install button after selecting 'mods folder'; installation was not started.");
                }

                await Task.Delay(500, cancellationToken);
                var scan = await FindPackageInstallerWindowAsync(
                    GetOpenIvProcessIds(process.Id, processName),
                    expectedPackageName,
                    cancellationToken);
                installerWindow = scan.Window;
                controls = scan.Controls;
                if (installerWindow is null)
                {
                    return new OpenIvAutomationResult(
                        false,
                        "OpenIV's package installer changed or closed before installation could be confirmed.");
                }
            }

            if (!installInvoked)
            {
                return new OpenIvAutomationResult(
                    false,
                    "OpenIV's package installer did not reach a verified Install step; installation was not started.");
            }

            while (DateTime.UtcNow < installDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scan = await FindPackageInstallerWindowAsync(
                    GetOpenIvProcessIds(process.Id, processName),
                    expectedPackageName,
                    cancellationToken);
                installerWindow = scan.Window;
                controls = scan.Controls;
                if (installerWindow is null)
                {
                    return new OpenIvAutomationResult(true, "OpenIV installed the package without further user input.");
                }

                if (HasCompletionMessage(controls))
                {
                    var finishButton = controls.FirstOrDefault(IsCompletionButton);
                    if (finishButton is not null &&
                        finishButton.TryGetCurrentPattern(InvokePattern.Pattern, out object finishInvoke))
                    {
                        ((InvokePattern)finishInvoke).Invoke();
                    }
                }

                await Task.Delay(400, cancellationToken);
            }

            return new OpenIvAutomationResult(
                false,
                "OpenIV's package installer did not close before the automation timeout; verify the install in GTA Mod Assistant.");
        }
        catch (ElementNotAvailableException ex)
        {
            return new OpenIvAutomationResult(false, $"OpenIV's installer UI changed during automation: {ex.Message}");
        }
        catch (COMException ex)
        {
            return new OpenIvAutomationResult(false, $"Windows could not access OpenIV's installer controls: {ex.Message}");
        }
        catch (TimeoutException ex)
        {
            return new OpenIvAutomationResult(false, $"OpenIV's installer UI did not respond to automation in time: {ex.Message}");
        }
    }

    private static async Task<(AutomationElement? Window, AutomationElement[] Controls)> FindPackageInstallerWindowAsync(
        HashSet<int> processIds,
        string expectedPackageName,
        CancellationToken cancellationToken)
    {
        if (!await AutomationQueryGate.WaitAsync(AutomationQueryTimeout, cancellationToken))
        {
            throw new TimeoutException("An earlier OpenIV accessibility request is still blocked.");
        }

        var completion = new TaskCompletionSource<(AutomationElement? Window, AutomationElement[] Controls)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(FindPackageInstallerWindow(processIds, expectedPackageName));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally
            {
                AutomationQueryGate.Release();
            }
        })
        {
            IsBackground = true,
            Name = "OpenIV UI Automation"
        };
        thread.SetApartmentState(ApartmentState.MTA);
        try
        {
            thread.Start();
        }
        catch
        {
            AutomationQueryGate.Release();
            throw;
        }

        return await completion.Task.WaitAsync(AutomationQueryTimeout, cancellationToken);
    }

    private static (AutomationElement? Window, AutomationElement[] Controls) FindPackageInstallerWindow(
        HashSet<int> processIds,
        string expectedPackageName)
    {
        foreach (var nativeWindow in EnumerateTopLevelWindows(processIds))
        {
            AutomationElement window = AutomationElement.FromHandle(nativeWindow.Handle);
            string title = string.IsNullOrWhiteSpace(nativeWindow.Title)
                ? window.Current.Name
                : nativeWindow.Title;
            bool packageInstallerWindow =
                title.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Installer", StringComparison.OrdinalIgnoreCase) ||
                title.Contains(expectedPackageName, StringComparison.OrdinalIgnoreCase);
            if (!packageInstallerWindow)
            {
                continue;
            }

            var descendants = window.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            var currentControls = descendants.Cast<AutomationElement>().ToArray();
            bool packageMatches = title.Contains(expectedPackageName, StringComparison.OrdinalIgnoreCase) ||
                currentControls.Any(control => control.Current.Name.Contains(expectedPackageName, StringComparison.OrdinalIgnoreCase));
            if (!packageMatches ||
                (!currentControls.Any(IsModsFolderChoice) &&
                 !currentControls.Any(IsInstallButton) &&
                 !currentControls.Any(IsNextButton)))
            {
                continue;
            }

            return (window, currentControls);
        }

        return (null, Array.Empty<AutomationElement>());
    }

    private static HashSet<int> GetOpenIvProcessIds(int startedProcessId, string processName)
    {
        var processIds = new HashSet<int> { startedProcessId };
        foreach (Process openIvProcess in Process.GetProcessesByName(processName))
        {
            using (openIvProcess)
            {
                processIds.Add(openIvProcess.Id);
            }
        }

        return processIds;
    }

    private static void HideOpenIvWindows(HashSet<int> processIds)
    {
        foreach (var window in EnumerateTopLevelWindows(processIds))
        {
            ShowWindow(window.Handle, ShowWindowHide);
        }
    }

    private static List<(IntPtr Handle, int ProcessId, string Title)> EnumerateTopLevelWindows(HashSet<int> processIds)
    {
        var windows = new List<(IntPtr Handle, int ProcessId, string Title)>();
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out uint processId);
            if (!processIds.Contains((int)processId))
            {
                return true;
            }

            var title = new StringBuilder(512);
            _ = GetWindowText(handle, title, title.Capacity);
            windows.Add((handle, (int)processId, title.ToString()));
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    private static bool IsModsFolderChoice(AutomationElement element)
    {
        var controlType = element.Current.ControlType;
        if (controlType != ControlType.RadioButton &&
            controlType != ControlType.CheckBox &&
            controlType != ControlType.ListItem &&
            controlType != ControlType.ComboBox)
        {
            return false;
        }

        string name = element.Current.Name;
        return name.Contains("mods folder", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name.Trim(), "mods", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SelectModsFolder(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object selectionObject))
        {
            var selection = (SelectionItemPattern)selectionObject;
            if (!selection.Current.IsSelected)
            {
                selection.Select();
            }
            return selection.Current.IsSelected;
        }

        if (element.TryGetCurrentPattern(TogglePattern.Pattern, out object toggleObject))
        {
            var toggle = (TogglePattern)toggleObject;
            if (toggle.Current.ToggleState != ToggleState.On)
            {
                toggle.Toggle();
            }
            return toggle.Current.ToggleState == ToggleState.On;
        }

        return false;
    }

    private static bool IsInstallButton(AutomationElement element) =>
        element.Current.ControlType == ControlType.Button &&
        string.Equals(element.Current.Name.Trim(), "Install", StringComparison.OrdinalIgnoreCase);

    private static bool IsNextButton(AutomationElement? element) =>
        element is not null &&
        element.Current.ControlType == ControlType.Button &&
        string.Equals(element.Current.Name.Trim(), "Next", StringComparison.OrdinalIgnoreCase);

    private static bool TryInvoke(AutomationElement? element)
    {
        if (element is null ||
            !element.TryGetCurrentPattern(InvokePattern.Pattern, out object invokeObject))
        {
            return false;
        }

        ((InvokePattern)invokeObject).Invoke();
        return true;
    }

    private static bool HasCompletionMessage(IEnumerable<AutomationElement> controls) =>
        controls.Any(control =>
        {
            string name = control.Current.Name;
            return name.Contains("installation complete", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("installed successfully", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("package installed", StringComparison.OrdinalIgnoreCase);
        });

    private static bool IsCompletionButton(AutomationElement element)
    {
        if (element.Current.ControlType != ControlType.Button)
        {
            return false;
        }

        string name = element.Current.Name.Trim();
        return string.Equals(name, "Finish", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Close", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Done", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "OK", StringComparison.OrdinalIgnoreCase);
    }

    private const int ShowWindowHide = 0;

    private delegate bool EnumWindowsCallback(IntPtr handle, IntPtr extra);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr extra);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);
}
