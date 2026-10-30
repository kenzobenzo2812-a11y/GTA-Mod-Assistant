using System.Collections.ObjectModel;
using System.Text;
using GTAModAssistant.Core.Archives;
using GTAModAssistant.Core.Models;
using GTAModAssistant.Core.Security;
using GTAModAssistant.Core.Services;

namespace GTAModAssistant.ViewModels;

public enum WorkspaceTab
{
    AnalyzerAndPlan = 0,
    InstalledMods = 1,
    BackupsAndRestore = 2,
    Logs = 3,
    AiSettings = 4,
    MemoryCleanup = 5
}

public enum StatusBannerSeverity
{
    Info = 0,
    Verified = 1,
    Warning = 2,
    Error = 3
}

public sealed class MainViewModel : ObservableObject
{
    private readonly AppWorkspacePaths _paths;
    private readonly SqliteHistoryRepository _repository;
    private readonly SecretStore _secretStore;
    private readonly GameDetectionService _gameDetection;
    private readonly AiModAnalyzerService _aiAnalyzer;
    private readonly BackupService _backupService;
    private readonly ModInstallerService _installer;
    private readonly ProfileManagerService _profileManager;
    private readonly SafeCleanupService _cleanupService;
    private readonly IUserInteractionService _interaction;
    private readonly Action<Action> _uiDispatch;

    private WorkspaceTab _selectedTab = WorkspaceTab.AnalyzerAndPlan;
    private GameEnvironmentInfo _gameEnv = new();
    private ModInstallationPlan? _currentPlan;
    private InstalledModRecord? _selectedInstalledMod;
    private BackupRecord? _selectedBackup;
    private ModProfile? _selectedProfile;
    private bool _suppressProfileSwitchEvent;

    private bool _isBusy;
    private int _progressPercentage;
    private string _progressStage = "Ready";
    private string _progressDetail = "Drop a mod .zip, .rar, .oiv, .7z, or folder to analyze.";
    private string _statusBannerText = "GTA Mod Assistant ready. Click 'Scan Game' or drop a mod package.";
    private StatusBannerSeverity _statusBannerKind = StatusBannerSeverity.Info;
    private string _planStatusHeadline = "READY TO INSTALL — CLICK 'INSTALL MOD NOW' TO APPLY";

    private string _customGtaPathOverride = string.Empty;
    private string _customOpenIvOverride = string.Empty;

    private bool _enableAiLlm = true;
    private string _aiEndpointUrl = "https://api.openai.com/v1/chat/completions";
    private string _aiModelName = "gpt-4o-mini";
    private bool _hasConfiguredApiKey;
    private bool _autoLaunchOpenIvOnEncryptedRpf = true;

    private string _logSearchText = string.Empty;
    private string _selectedLogLevelFilter = "ALL";

    private readonly List<AuditLogEntry> _allLogsBuffer = new();

    public ObservableCollection<InstalledModRecord> InstalledMods { get; } = new();
    public ObservableCollection<BackupRecord> Backups { get; } = new();
    public ObservableCollection<ModProfile> Profiles { get; } = new();
    public ObservableCollection<AuditLogEntry> FilteredLogs { get; } = new();
    public ObservableCollection<CleanupCategoryItem> CleanupCategories { get; } = new();

    private SystemMemorySnapshot _memorySnapshot = new(0, 0, 0, string.Empty);
    private string _cleanupSummaryHeadline = "Ready to scan leftover staging caches, crash dumps, temp archives, and idle RAM.";

    public IReadOnlyList<string> LogLevelFilterOptions { get; } = new[]
    {
        "ALL", "INFO", "VERIFY", "WARNING", "ERROR", "SECURITY"
    };

    // Primary UI Navigation & Action Commands (Requirement 11)
    public AsyncRelayCommand ScanGameCommand { get; }
    public AsyncRelayCommand AddModFileCommand { get; }
    public AsyncRelayCommand AddModFolderCommand { get; }
    public AsyncRelayCommand InstallModCommand { get; }
    public AsyncRelayCommand CreateBackupCommand { get; }
    public AsyncRelayCommand RestoreBackupCommand { get; }

    public RelayCommand ShowAnalyzerTabCommand { get; }
    public RelayCommand ShowInstalledModsCommand { get; }
    public RelayCommand ShowBackupsTabCommand { get; }
    public RelayCommand ShowLogsCommand { get; }
    public RelayCommand ShowAiSettingsCommand { get; }
    public AsyncRelayCommand ShowCleanupTabCommand { get; }
    public AsyncRelayCommand ScanCleanupCommand { get; }
    public AsyncRelayCommand ExecuteCleanupCommand { get; }
    public AsyncRelayCommand QuickTrimRamOnlyCommand { get; }
    public RelayCommand ToggleCleanupCategoryCommand { get; }

    // Secondary Management Commands
    public AsyncRelayCommand BrowseGtaFolderCommand { get; }
    public RelayCommand ChooseStepRpfTargetCommand { get; }
    public RelayCommand ToggleStepIncludedCommand { get; }
    public AsyncRelayCommand ToggleModEnabledCommand { get; }
    public AsyncRelayCommand UninstallModCommand { get; }
    public AsyncRelayCommand CommitOpenIvForModCommand { get; }
    public AsyncRelayCommand VerifyBackupCommand { get; }
    public AsyncRelayCommand DeleteBackupCommand { get; }
    public AsyncRelayCommand CreateProfileCommand { get; }
    public AsyncRelayCommand DeleteProfileCommand { get; }
    public RelayCommand SaveAiSettingsCommand { get; }
    public RelayCommand UseLocalOllamaPresetCommand { get; }
    public RelayCommand UseOpenAiPresetCommand { get; }
    public RelayCommand UseGroqPresetCommand { get; }
    public RelayCommand ExportLogsCommand { get; }

    public MainViewModel(
        AppWorkspacePaths paths,
        IUserInteractionService interaction,
        Action<Action>? uiDispatch = null,
        HttpClient? httpClient = null)
    {
        _paths = paths;
        _interaction = interaction;
        _uiDispatch = uiDispatch ?? (a => a());

        _repository = new SqliteHistoryRepository(_paths.DatabasePath);
        _secretStore = new SecretStore(_paths.RootDataDirectory);
        _gameDetection = new GameDetectionService();
        _aiAnalyzer = new AiModAnalyzerService(new ArchiveExtractorService(), _secretStore, httpClient);
        _backupService = new BackupService(_paths.BackupsDirectory, _repository);
        _installer = new ModInstallerService(_repository, _backupService, _paths.RootDataDirectory);
        _profileManager = new ProfileManagerService(_repository, _backupService, _paths.RootDataDirectory);
        _cleanupService = new SafeCleanupService(_paths, _repository);

        _repository.LogAppended += entry =>
        {
            _uiDispatch(() =>
            {
                _allLogsBuffer.Add(entry);
                if (MatchesLogFilter(entry))
                {
                    FilteredLogs.Add(entry);
                }
            });
        };

        LoadPersistedSettings();

        ScanGameCommand = new AsyncRelayCommand(_ => ScanGameAsync());
        AddModFileCommand = new AsyncRelayCommand(_ => BrowseAndAnalyzeModFileAsync());
        AddModFolderCommand = new AsyncRelayCommand(_ => BrowseAndAnalyzeModFolderAsync());
        InstallModCommand = new AsyncRelayCommand(
            _ => ExecutePlannedInstallAsync(),
            _ => CurrentPlan is not null && CurrentPlan.CanInstall && GameEnv.IsDetected);
        CreateBackupCommand = new AsyncRelayCommand(
            _ => CreateManualBackupAsync(),
            _ => GameEnv.IsDetected);
        RestoreBackupCommand = new AsyncRelayCommand(_ => HandleRestoreActionAsync());

        ShowAnalyzerTabCommand = new RelayCommand(_ => SelectedTab = WorkspaceTab.AnalyzerAndPlan);
        ShowInstalledModsCommand = new RelayCommand(_ =>
        {
            RefreshInstalledMods();
            SelectedTab = WorkspaceTab.InstalledMods;
        });
        ShowBackupsTabCommand = new RelayCommand(_ =>
        {
            RefreshBackups();
            SelectedTab = WorkspaceTab.BackupsAndRestore;
        });
        ShowLogsCommand = new RelayCommand(_ => SelectedTab = WorkspaceTab.Logs);
        ShowAiSettingsCommand = new RelayCommand(_ => SelectedTab = WorkspaceTab.AiSettings);
        ShowCleanupTabCommand = new AsyncRelayCommand(async _ =>
        {
            SelectedTab = WorkspaceTab.MemoryCleanup;
            await ScanCleanupAsync();
        });
        ScanCleanupCommand = new AsyncRelayCommand(_ => ScanCleanupAsync());
        ExecuteCleanupCommand = new AsyncRelayCommand(_ => ExecuteCleanupAsync());
        QuickTrimRamOnlyCommand = new AsyncRelayCommand(_ => QuickTrimRamOnlyAsync());
        ToggleCleanupCategoryCommand = new RelayCommand(param => ToggleCleanupCategory(param as CleanupCategoryItem));

        BrowseGtaFolderCommand = new AsyncRelayCommand(_ => BrowseGtaFolderAsync());
        ChooseStepRpfTargetCommand = new RelayCommand(param => PromptAndApplyStepTarget(param as ModInstallStep));
        ToggleStepIncludedCommand = new RelayCommand(param => ToggleStepIncluded(param as ModInstallStep));
        ToggleModEnabledCommand = new AsyncRelayCommand(param => ToggleModEnabledAsync(param as InstalledModRecord));
        UninstallModCommand = new AsyncRelayCommand(param => UninstallModAsync(param as InstalledModRecord));
        CommitOpenIvForModCommand = new AsyncRelayCommand(param => CommitOpenIvPackageAsync(param as InstalledModRecord));
        VerifyBackupCommand = new AsyncRelayCommand(param => VerifySelectedBackupAsync(param as BackupRecord ?? SelectedBackup));
        DeleteBackupCommand = new AsyncRelayCommand(param => DeleteBackupAsync(param as BackupRecord ?? SelectedBackup));
        CreateProfileCommand = new AsyncRelayCommand(_ => CreateNewProfileAsync());
        DeleteProfileCommand = new AsyncRelayCommand(_ => DeleteSelectedProfileAsync());
        SaveAiSettingsCommand = new RelayCommand(_ => SaveAiConfiguration());
        UseLocalOllamaPresetCommand = new RelayCommand(_ =>
        {
            AiEndpointUrl = "http://localhost:11434/v1/chat/completions";
            AiModelName = "llama3.1";
            EnableAiLlm = true;
        });
        UseOpenAiPresetCommand = new RelayCommand(_ =>
        {
            AiEndpointUrl = "https://api.openai.com/v1/chat/completions";
            AiModelName = "gpt-4o-mini";
            EnableAiLlm = true;
        });
        UseGroqPresetCommand = new RelayCommand(_ =>
        {
            AiEndpointUrl = "https://api.groq.com/openai/v1/chat/completions";
            AiModelName = "llama-3.3-70b-versatile";
            EnableAiLlm = true;
        });
        ExportLogsCommand = new RelayCommand(_ => ExportLogs());

        LoadInitialData();
    }

    public SqliteHistoryRepository Repository => _repository;
    public SecretStore SecretStore => _secretStore;
    public string AppDataDirectory => _paths.RootDataDirectory;

    public string AppVersionBadge
    {
        get
        {
            var infoVer = System.Reflection.Assembly
                .GetAssembly(typeof(MainViewModel))?
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion;

            if (string.IsNullOrWhiteSpace(infoVer))
            {
                return "v1.2.0";
            }

            string clean = infoVer.Split('+')[0].Trim();
            return clean.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? clean : $"v{clean}";
        }
    }

    public WorkspaceTab SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (SetProperty(ref _selectedTab, value))
            {
                OnPropertyChanged(nameof(IsAnalyzerTab));
                OnPropertyChanged(nameof(IsInstalledModsTab));
                OnPropertyChanged(nameof(IsBackupsTab));
                OnPropertyChanged(nameof(IsLogsTab));
                OnPropertyChanged(nameof(IsAiSettingsTab));
                OnPropertyChanged(nameof(IsCleanupTab));
            }
        }
    }

    public bool IsAnalyzerTab => SelectedTab == WorkspaceTab.AnalyzerAndPlan;
    public bool IsInstalledModsTab => SelectedTab == WorkspaceTab.InstalledMods;
    public bool IsBackupsTab => SelectedTab == WorkspaceTab.BackupsAndRestore;
    public bool IsLogsTab => SelectedTab == WorkspaceTab.Logs;
    public bool IsAiSettingsTab => SelectedTab == WorkspaceTab.AiSettings;
    public bool IsCleanupTab => SelectedTab == WorkspaceTab.MemoryCleanup;

    public SystemMemorySnapshot MemorySnapshot
    {
        get => _memorySnapshot;
        private set => SetProperty(ref _memorySnapshot, value);
    }

    public string CleanupSummaryHeadline
    {
        get => _cleanupSummaryHeadline;
        private set => SetProperty(ref _cleanupSummaryHeadline, value);
    }

    public GameEnvironmentInfo GameEnv
    {
        get => _gameEnv;
        private set
        {
            if (SetProperty(ref _gameEnv, value))
            {
                InstallModCommand.RaiseCanExecuteChanged();
                CreateBackupCommand.RaiseCanExecuteChanged();
                if (CurrentPlan is not null)
                {
                    UpdateInstallationPlanStatus(CurrentPlan);
                }
            }
        }
    }

    public ModInstallationPlan? CurrentPlan
    {
        get => _currentPlan;
        private set
        {
            if (SetProperty(ref _currentPlan, value))
            {
                OnPropertyChanged(nameof(HasCurrentPlan));
                InstallModCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasCurrentPlan => CurrentPlan is not null;

    public InstalledModRecord? SelectedInstalledMod
    {
        get => _selectedInstalledMod;
        set => SetProperty(ref _selectedInstalledMod, value);
    }

    public BackupRecord? SelectedBackup
    {
        get => _selectedBackup;
        set => SetProperty(ref _selectedBackup, value);
    }

    public ModProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value) && value is not null && !_suppressProfileSwitchEvent)
            {
                _ = SwitchActiveProfileInternalAsync(value);
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public int ProgressPercentage
    {
        get => _progressPercentage;
        private set => SetProperty(ref _progressPercentage, value);
    }

    public string ProgressStage
    {
        get => _progressStage;
        private set => SetProperty(ref _progressStage, value);
    }

    public string ProgressDetail
    {
        get => _progressDetail;
        private set => SetProperty(ref _progressDetail, value);
    }

    public string StatusBannerText
    {
        get => _statusBannerText;
        private set => SetProperty(ref _statusBannerText, value);
    }

    public StatusBannerSeverity StatusBannerKind
    {
        get => _statusBannerKind;
        private set => SetProperty(ref _statusBannerKind, value);
    }

    public string PlanStatusHeadline
    {
        get => _planStatusHeadline;
        private set => SetProperty(ref _planStatusHeadline, value);
    }

    public string CustomGtaPathOverride
    {
        get => _customGtaPathOverride;
        set => SetProperty(ref _customGtaPathOverride, value);
    }

    public string CustomOpenIvOverride
    {
        get => _customOpenIvOverride;
        set => SetProperty(ref _customOpenIvOverride, value);
    }

    public bool EnableAiLlm
    {
        get => _enableAiLlm;
        set => SetProperty(ref _enableAiLlm, value);
    }

    public string AiEndpointUrl
    {
        get => _aiEndpointUrl;
        set => SetProperty(ref _aiEndpointUrl, value);
    }

    public string AiModelName
    {
        get => _aiModelName;
        set => SetProperty(ref _aiModelName, value);
    }

    public bool HasConfiguredApiKey
    {
        get => _hasConfiguredApiKey;
        private set => SetProperty(ref _hasConfiguredApiKey, value);
    }

    public bool AutoLaunchOpenIvOnEncryptedRpf
    {
        get => _autoLaunchOpenIvOnEncryptedRpf;
        set => SetProperty(ref _autoLaunchOpenIvOnEncryptedRpf, value);
    }

    public string LogSearchText
    {
        get => _logSearchText;
        set
        {
            if (SetProperty(ref _logSearchText, value))
            {
                RebuildFilteredLogs();
            }
        }
    }

    public string SelectedLogLevelFilter
    {
        get => _selectedLogLevelFilter;
        set
        {
            if (SetProperty(ref _selectedLogLevelFilter, value))
            {
                RebuildFilteredLogs();
            }
        }
    }

    public async Task ScanGameAsync()
    {
        IsBusy = true;
        UpdateProgress(35, "Scanning Environment", "Detecting Epic Games GTA V, game build, OpenIV, and mods folder...");
        try
        {
            var info = await Task.Run(() => _gameDetection.ScanEnvironment(
                string.IsNullOrWhiteSpace(CustomGtaPathOverride) ? null : CustomGtaPathOverride,
                string.IsNullOrWhiteSpace(CustomOpenIvOverride) ? null : CustomOpenIvOverride));

            GameEnv = info;
            if (info.IsDetected)
            {
                _repository.SetSetting("gta_root_override", info.GtaRootPath);
                if (info.OpenIvInstalled)
                {
                    _repository.SetSetting("openiv_exe_override", info.OpenIvExecutablePath);
                }

                _repository.AppendLog(
                    AuditLogLevel.Verify,
                    "Scanner",
                    $"Detected {info.Platform} GTA V v{info.GameVersion} (Build {info.BuildNumber}) at '{info.GtaRootPath}'",
                    $"OpenIV={(info.OpenIvInstalled ? info.OpenIvExecutablePath : "Not Found")}, ModsFolder={(info.ModsFolderExists ? "Present" : "Will Create on First Mod")}");

                SetBanner(
                    info.HealthWarnings.Count == 0 ? StatusBannerSeverity.Verified : StatusBannerSeverity.Warning,
                    $"Detected {info.SummaryLine} at {info.GtaRootPath}");
                UpdateProgress(100, "Scan Verified", info.SummaryLine);
            }
            else
            {
                _repository.AppendLog(
                    AuditLogLevel.Warning,
                    "Scanner",
                    "GTA5.exe was not found in default paths.",
                    $"Checked '{GameDetectionService.DefaultEpicGamesGtaPath}' and launcher manifests.");
                SetBanner(
                    StatusBannerSeverity.Warning,
                    "GTA V not found automatically — click 'Set Folder' to point to your GTA V installation.");
                UpdateProgress(0, "Scan Incomplete", "GTA5.exe not found yet.");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task BrowseGtaFolderAsync()
    {
        string? chosen = _interaction.BrowseFolder("Select your GTA V installation directory (containing GTA5.exe)", CustomGtaPathOverride);
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            CustomGtaPathOverride = chosen;
            await ScanGameAsync();
        }
    }

    public async Task BrowseAndAnalyzeModFileAsync()
    {
        string? file = _interaction.BrowseModArchiveFile();
        if (!string.IsNullOrWhiteSpace(file))
        {
            await AnalyzeModInputAsync(file);
        }
    }

    public async Task BrowseAndAnalyzeModFolderAsync()
    {
        string? dir = _interaction.BrowseFolder("Select an extracted GTA V mod folder");
        if (!string.IsNullOrWhiteSpace(dir))
        {
            await AnalyzeModInputAsync(dir);
        }
    }

    public async Task HandleDroppedPathsAsync(IReadOnlyList<string> droppedPaths)
    {
        if (droppedPaths.Count == 0)
        {
            return;
        }

        string firstSupported = droppedPaths.FirstOrDefault(ArchiveExtractorService.IsSupportedInputPath) ?? droppedPaths[0];
        await AnalyzeModInputAsync(firstSupported);
    }

    public async Task AnalyzeModInputAsync(string inputPath)
    {
        IsBusy = true;
        SelectedTab = WorkspaceTab.AnalyzerAndPlan;
        UpdateProgress(20, "Staging & Extracting", $"Safely inspecting '{Path.GetFileName(inputPath)}'...");

        try
        {
            var aiSettings = new AiSettings
            {
                EnableCloudOrLocalLlm = EnableAiLlm,
                EndpointUrl = AiEndpointUrl,
                ModelName = AiModelName
            };

            var staged = await Task.Run(() => _aiAnalyzer.StageModInput(inputPath, _paths.StagingDirectory));
            bool hasReadmeOrOiv = AiModAnalyzerService.StagedPackageHasReadmeOrOiv(staged);

            if (!hasReadmeOrOiv)
            {
                bool llmReady = _aiAnalyzer.IsLlmEndpointActive(aiSettings);
                string engineDesc = llmReady
                    ? $"Connected AI Model ({AiModelName}) + Binary RPF7/RSC7 File Inspector"
                    : "Built-In AI Binary RPF7/RSC7 & XML Metadata File Inspector";
                string displayModName = Path.GetFileNameWithoutExtension(inputPath.TrimEnd('/', '\\'));
                if (string.IsNullOrWhiteSpace(displayModName))
                {
                    displayModName = Path.GetFileName(inputPath.TrimEnd('/', '\\'));
                }

                var noReadmeChoice = _interaction.PromptNoReadmeDisclaimer(
                    displayModName,
                    staged.Files.Count,
                    llmReady,
                    engineDesc);

                if (noReadmeChoice == NoReadmeUserChoice.Cancel)
                {
                    _repository.AppendLog(
                        AuditLogLevel.Warning,
                        "Analyzer",
                        $"User cancelled analysis of '{displayModName}' after No-README disclaimer.",
                        $"DiscoveredFiles={staged.Files.Count}");
                    SetBanner(StatusBannerSeverity.Info, $"Analysis cancelled — '{displayModName}' did not contain a README.");
                    UpdateProgress(0, "Cancelled", "No README found; analysis cancelled by user.");
                    return;
                }

                if (noReadmeChoice == NoReadmeUserChoice.OpenAiSettings)
                {
                    SelectedTab = WorkspaceTab.AiSettings;
                    SetBanner(
                        StatusBannerSeverity.Info,
                        "Configure an OpenAI/Groq API key or 1-click Local Ollama below, then drop your README-less mod again.");
                    UpdateProgress(0, "Configure AI", "Waiting for AI configuration.");
                    return;
                }
            }

            var activeFiles = _repository.GetModFiles(onlyEnabledMods: true);
            var modNamesById = _repository.GetInstalledMods()
                .ToDictionary(m => m.Id, m => m.Name, StringComparer.OrdinalIgnoreCase);
            UpdateProgress(
                55,
                hasReadmeOrOiv ? "AI README & Binary Analysis" : "AI Deep File Inspection (No README)",
                hasReadmeOrOiv
                    ? "Reading README instructions, RPF7/RSC7 headers (.yft/_hi.yft/.ytd), PE metadata, and version requirements..."
                    : "No README found — AI is analyzing RPF7/RSC7 headers, XML/.meta tags, PE assemblies, and game RPF matches...");

            var plan = await Task.Run(() => _aiAnalyzer.AnalyzeStagedModAsync(
                staged,
                GameEnv,
                aiSettings,
                activeFiles,
                modNamesById));

            CurrentPlan = plan;
            UpdateInstallationPlanStatus(plan);

            _repository.AppendLog(
                plan.CanInstall ? AuditLogLevel.Info : AuditLogLevel.Warning,
                "Analyzer",
                $"Prepared installation plan for '{plan.ModName}' ({plan.Category}, {plan.Steps.Count} step(s))",
                $"Engine: {plan.AnalysisEngineUsed} | Compatibility: {plan.Compatibility.Headline} | Conflicts: {plan.Conflicts.Count} | Uncertain Targets: {plan.UnresolvedTargetCount} | Security Findings: {plan.SecurityFindings.Count}");

            UpdateProgress(100, "Plan Ready", $"{plan.ModName} ({plan.Steps.Count} step(s))");
        }
        catch (Exception ex)
        {
            _repository.AppendLog(AuditLogLevel.Error, "Analyzer", $"Failed to analyze '{inputPath}'", ex.Message);
            SetBanner(StatusBannerSeverity.Error, $"Mod analysis failed: {ex.Message}");
            UpdateProgress(0, "Analysis Error", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public bool PromptAndApplyStepTarget(ModInstallStep? step)
    {
        if (CurrentPlan is null || step is null || !step.CanChooseCustomRpfTarget)
        {
            return false;
        }

        var decision = _interaction.PromptAssetTargetSelection(step, CurrentPlan.ModName);
        if (decision is null)
        {
            return false;
        }

        var activeFiles = _repository.GetModFiles(onlyEnabledMods: true);
        var modNamesById = _repository.GetInstalledMods()
            .ToDictionary(m => m.Id, m => m.Name, StringComparer.OrdinalIgnoreCase);

        CurrentPlan = AiModAnalyzerService.ApplyUserTargetSelection(
            CurrentPlan,
            step.StepId,
            decision.OuterRpfRelativePath,
            decision.InternalPath,
            decision.ApplyToCompanionStemFiles,
            activeFiles,
            GameEnv,
            modNamesById);
        UpdateInstallationPlanStatus(CurrentPlan);

        _repository.AppendLog(
            AuditLogLevel.Info,
            "Analyzer",
            $"User selected RPF target for '{step.FileName}': {decision.OuterRpfRelativePath} -> {decision.InternalPath}",
            $"ApplyToCompanionStemFiles={decision.ApplyToCompanionStemFiles}");

        return true;
    }

    public void ToggleStepIncluded(ModInstallStep? step)
    {
        if (CurrentPlan is null || step is null)
        {
            return;
        }

        var activeFiles = _repository.GetModFiles(onlyEnabledMods: true);
        var modNamesById = _repository.GetInstalledMods()
            .ToDictionary(m => m.Id, m => m.Name, StringComparer.OrdinalIgnoreCase);

        CurrentPlan = AiModAnalyzerService.SetStepSelection(
            CurrentPlan,
            step.StepId,
            !step.IsSelectedForInstall,
            activeFiles,
            GameEnv,
            modNamesById);
        UpdateInstallationPlanStatus(CurrentPlan);
    }

    public async Task ExecutePlannedInstallAsync(ConflictResolutionStrategy? explicitStrategy = null)
    {
        if (CurrentPlan is null)
        {
            return;
        }

        if (!GameEnv.IsDetected)
        {
            _interaction.ShowNotification("GTA V Not Detected", "Scan or select your GTA V installation folder before installing mods.", isError: true);
            return;
        }

        // Ask the user instead of guessing whenever any selected .yft / _hi.yft / .ytd step has an uncertain RPF target!
        while (CurrentPlan.HasUnresolvedTargets)
        {
            var nextUncertain = CurrentPlan.Steps.First(s => s.IsSelectedForInstall && s.RequiresUserTargetSelection);
            if (!PromptAndApplyStepTarget(nextUncertain))
            {
                SetBanner(
                    StatusBannerSeverity.Warning,
                    $"Installation paused — please choose the intended RPF archive for '{nextUncertain.FileName}' before installing.");
                return;
            }
        }

        var strategy = explicitStrategy ?? ConflictResolutionStrategy.BackupAndOverwrite;
        if (explicitStrategy is null && CurrentPlan.Conflicts.Count > 0)
        {
            strategy = _interaction.PromptConflictResolution(CurrentPlan.Conflicts, CurrentPlan.ModName);
            if (strategy == ConflictResolutionStrategy.CancelInstall)
            {
                SetBanner(StatusBannerSeverity.Info, "Installation cancelled by user at conflict prompt.");
                return;
            }
        }

        // If any step writes outside mods/ (e.g. GTA V root .asi or scripts/), confirm if overwriting existing file
        if (CurrentPlan.Conflicts.Count > 0 && strategy == ConflictResolutionStrategy.BackupAndOverwrite)
        {
            bool confirmed = _interaction.ConfirmDestructiveAction(
                "Confirm Overwrite With Verified Backup",
                $"Installing '{CurrentPlan.ModName}' will overwrite {CurrentPlan.Conflicts.Count} conflicting file(s) after creating a SHA-256 verified backup.\n\nProceed?",
                "Backup & Overwrite");
            if (!confirmed)
            {
                return;
            }
        }

        IsBusy = true;
        PlanStatusHeadline = "INSTALLING MOD INTO GTA V 'mods\\' FOLDER...";
        var progress = new Progress<InstallProgressReport>(p => UpdateProgress(p.Percentage, p.Stage, p.Detail));

        try
        {
            var result = await Task.Run(() => _installer.InstallAsync(
                CurrentPlan,
                GameEnv,
                strategy,
                AutoLaunchOpenIvOnEncryptedRpf,
                progress));

            RefreshInstalledMods();
            RefreshBackups();
            // Re-scan mods folder state silently
            GameEnv = _gameDetection.ScanEnvironment(GameEnv.GtaRootPath, GameEnv.OpenIvExecutablePath);

            if (result.VerificationState == InstallVerificationState.VerifiedComplete)
            {
                PlanStatusHeadline = "INSTALLED & SHA-256 VERIFIED — MOD IS NOW ACTIVE IN GTA V!";
                SetBanner(StatusBannerSeverity.Verified, result.SummaryMessage);
                UpdateProgress(100, "Installed & Verified", result.SummaryMessage);
            }
            else if (result.VerificationState == InstallVerificationState.PendingOpenIvCommit)
            {
                PlanStatusHeadline = "ALMOST DONE — CLICK 'INSTALL' IN OPENIV PACKAGE INSTALLER TO FINISH ENCRYPTED RPF";
                SetBanner(StatusBannerSeverity.Warning, result.SummaryMessage);
                UpdateProgress(95, "Waiting for OpenIV Commit", result.SummaryMessage);
            }
            else
            {
                PlanStatusHeadline = "INSTALL FAILED & ROLLED BACK — SEE ERROR MESSAGE AT TOP";
                SetBanner(StatusBannerSeverity.Error, result.SummaryMessage);
                UpdateProgress(0, "Rolled Back", result.SummaryMessage);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task CreateManualBackupAsync()
    {
        if (!GameEnv.IsDetected)
        {
            return;
        }

        IsBusy = true;
        UpdateProgress(30, "Creating Backup", "Hashing and archiving active managed mod files...");
        try
        {
            const long MaxManualRpfSnapshotBytes = 128L * 1024 * 1024;
            var trackedPaths = _repository.GetModFiles()
                .Select(f => f.TargetRelativePath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            string modsUpdate = "mods/update/update.rpf";
            if (File.Exists(Path.Combine(GameEnv.GtaRootPath, "mods", "update", "update.rpf")) &&
                !trackedPaths.Contains(modsUpdate, StringComparer.OrdinalIgnoreCase))
            {
                trackedPaths.Add(modsUpdate);
            }

            trackedPaths = trackedPaths
                .Where(rel =>
                {
                    if (!rel.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                    string full = PathSafetyGuard.ResolveSafeChildPath(GameEnv.GtaRootPath, rel);
                    return !File.Exists(full) || new FileInfo(full).Length <= MaxManualRpfSnapshotBytes;
                })
                .ToList();

            var rec = await Task.Run(() => _backupService.CreateVerifiedBackup(
                GameEnv.GtaRootPath,
                $"Manual Verified Snapshot ({DateTime.Now:yyyy-MM-dd HH:mm})",
                "ManualSnapshot",
                trackedPaths));

            RefreshBackups();
            SelectedBackup = Backups.FirstOrDefault(b => b.Id == rec.Id);
            SetBanner(StatusBannerSeverity.Verified, $"Created and SHA-256 verified backup '{rec.Label}' ({rec.FileCount} file(s)).");
            UpdateProgress(100, "Backup Verified", rec.Notes);
        }
        catch (Exception ex)
        {
            SetBanner(StatusBannerSeverity.Error, $"Backup failed: {ex.Message}");
            UpdateProgress(0, "Backup Failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task HandleRestoreActionAsync()
    {
        if (SelectedTab != WorkspaceTab.BackupsAndRestore)
        {
            RefreshBackups();
            SelectedTab = WorkspaceTab.BackupsAndRestore;
            if (SelectedBackup is null && Backups.Count > 0)
            {
                SelectedBackup = Backups[0];
            }
            return;
        }

        if (SelectedBackup is null)
        {
            _interaction.ShowNotification("Select a Backup", "Choose a verified backup snapshot from the list to restore.");
            return;
        }

        bool confirmed = _interaction.ConfirmDestructiveAction(
            "Confirm Snapshot Restore",
            $"Restoring backup '{SelectedBackup.Label}' ({SelectedBackup.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}) will overwrite current mod files with the snapshot state and remove files created after that snapshot.\n\nAre you sure you want to restore?",
            "Restore Snapshot");
        if (!confirmed)
        {
            return;
        }

        IsBusy = true;
        var progress = new Progress<InstallProgressReport>(p => UpdateProgress(p.Percentage, p.Stage, p.Detail));
        try
        {
            var target = SelectedBackup;
            await Task.Run(() => _backupService.RestoreVerifiedBackup(
                target,
                GameEnv.IsDetected ? GameEnv.GtaRootPath : null,
                progress));

            SetBanner(StatusBannerSeverity.Verified, $"Restored and SHA-256 verified snapshot '{target.Label}'.");
        }
        catch (Exception ex)
        {
            SetBanner(StatusBannerSeverity.Error, $"Restore failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task VerifySelectedBackupAsync(BackupRecord? record)
    {
        if (record is null)
        {
            return;
        }

        IsBusy = true;
        UpdateProgress(50, "Verifying Backup", $"Re-computing SHA-256 hashes inside {record.Id}...");
        try
        {
            var (ok, details) = await Task.Run(() =>
            {
                bool res = _backupService.VerifyExistingBackup(record, out string det);
                return (res, det);
            });

            _repository.AppendLog(
                ok ? AuditLogLevel.Verify : AuditLogLevel.Error,
                "Backup",
                ok ? $"Backup '{record.Label}' passed SHA-256 verification" : $"Backup '{record.Label}' FAILED verification",
                details);

            SetBanner(ok ? StatusBannerSeverity.Verified : StatusBannerSeverity.Error, details);
            UpdateProgress(ok ? 100 : 0, ok ? "Backup Intact" : "Verification Failed", details);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task DeleteBackupAsync(BackupRecord? record)
    {
        if (record is null)
        {
            return Task.CompletedTask;
        }

        bool confirmed = _interaction.ConfirmDestructiveAction(
            "Delete Backup Snapshot",
            $"Permanently delete backup '{record.Label}' ({record.FormattedSize}) from disk?",
            "Delete Backup");
        if (!confirmed)
        {
            return Task.CompletedTask;
        }

        if (File.Exists(record.ArchivePath))
        {
            string? dir = Path.GetDirectoryName(record.ArchivePath);
            File.Delete(record.ArchivePath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }

        _repository.DeleteBackupRecord(record.Id);
        _repository.AppendLog(AuditLogLevel.Info, "Backup", $"Deleted backup snapshot '{record.Label}' ({record.Id})");
        RefreshBackups();
        SetBanner(StatusBannerSeverity.Info, $"Deleted backup '{record.Label}'.");
        return Task.CompletedTask;
    }

    public async Task ToggleModEnabledAsync(InstalledModRecord? mod)
    {
        var target = mod ?? SelectedInstalledMod;
        if (target is null || !GameEnv.IsDetected)
        {
            return;
        }

        bool newState = !target.IsEnabled;
        IsBusy = true;
        var progress = new Progress<InstallProgressReport>(p => UpdateProgress(p.Percentage, p.Stage, p.Detail));
        try
        {
            await Task.Run(() => _profileManager.SetModEnabledAsync(target.Id, newState, GameEnv, progress));
            RefreshInstalledMods();
            SetBanner(
                StatusBannerSeverity.Verified,
                newState
                    ? $"Enabled '{target.Name}' and verified restored mod files."
                    : $"Disabled '{target.Name}' — files safely preserved in non-destructive stash.");
        }
        catch (Exception ex)
        {
            SetBanner(StatusBannerSeverity.Error, $"Failed to toggle '{target.Name}': {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task UninstallModAsync(InstalledModRecord? mod)
    {
        var target = mod ?? SelectedInstalledMod;
        if (target is null || !GameEnv.IsDetected)
        {
            return;
        }

        bool confirmed = _interaction.ConfirmDestructiveAction(
            "Confirm Mod Uninstall & Rollback",
            $"Remove '{target.Name}' v{target.Version}, clean its dlclist.xml registration, and restore any previously overwritten files?\n\nA safety backup will be created automatically before removal.",
            "Rollback & Remove");
        if (!confirmed)
        {
            return;
        }

        IsBusy = true;
        var progress = new Progress<InstallProgressReport>(p => UpdateProgress(p.Percentage, p.Stage, p.Detail));
        try
        {
            await Task.Run(() => _installer.UninstallAndRollbackModAsync(target.Id, GameEnv, progress));
            RefreshInstalledMods();
            RefreshBackups();
            SetBanner(StatusBannerSeverity.Verified, $"Uninstalled '{target.Name}' and verified clean rollback.");
        }
        catch (Exception ex)
        {
            SetBanner(StatusBannerSeverity.Error, $"Uninstall failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task CommitOpenIvPackageAsync(InstalledModRecord? mod)
    {
        var target = mod ?? SelectedInstalledMod;
        if (target is null || string.IsNullOrWhiteSpace(target.GeneratedOivPath) || !File.Exists(target.GeneratedOivPath))
        {
            _interaction.ShowNotification("No Pending .OIV Package", "This mod does not have a pending OpenIV package.", isError: true);
            return;
        }

        if (!GameEnv.OpenIvInstalled)
        {
            _interaction.ShowNotification("OpenIV Not Detected", "Locate OpenIV.exe first so GTA Mod Assistant can launch the .oiv Package Installer.", isError: true);
            return;
        }

        IsBusy = true;
        UpdateProgress(40, "Launching OpenIV", $"Opening '{Path.GetFileName(target.GeneratedOivPath)}' in OpenIV Package Installer...");
        try
        {
            var rpfTargets = _repository.GetModFiles(target.Id)
                .Where(f => !string.IsNullOrWhiteSpace(f.RpfContainerRelativePath))
                .Select(f => PathSafetyGuard.ResolveSafeChildPath(GameEnv.GtaRootPath, f.RpfContainerRelativePath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var verify = await OivPackageService.LaunchOpenIvAndVerifyAsync(
                GameEnv.OpenIvExecutablePath,
                target.GeneratedOivPath,
                rpfTargets);

            if (verify.VerifiedChanged)
            {
                _repository.UpdateModVerificationState(target.Id, InstallVerificationState.VerifiedComplete);
                _repository.AppendLog(AuditLogLevel.Verify, "OpenIV", $"Verified OpenIV RPF7 commit for '{target.Name}'", verify.Details);
                RefreshInstalledMods();
                SetBanner(StatusBannerSeverity.Verified, $"Verified OpenIV RPF7 installation for '{target.Name}'.");
                UpdateProgress(100, "OpenIV Commit Verified", verify.Details);
            }
            else
            {
                _repository.AppendLog(AuditLogLevel.Warning, "OpenIV", $"OpenIV closed without verified RPF change for '{target.Name}'", verify.Details);
                SetBanner(StatusBannerSeverity.Warning, verify.Details);
                UpdateProgress(0, "Pending OpenIV Commit", verify.Details);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task CreateNewProfileAsync()
    {
        var input = _interaction.PromptNewProfile();
        if (input is null || string.IsNullOrWhiteSpace(input.Value.Name))
        {
            return;
        }

        try
        {
            var created = _repository.CreateProfile(input.Value.Name, input.Value.Description, copyCurrentEnabledStates: true);
            _repository.AppendLog(AuditLogLevel.Info, "Profiles", $"Created mod profile '{created.Name}' ({created.Id})");
            RefreshProfiles();
            await SwitchActiveProfileInternalAsync(created);
        }
        catch (Exception ex)
        {
            _interaction.ShowNotification("Profile Error", ex.Message, isError: true);
        }
    }

    public Task DeleteSelectedProfileAsync()
    {
        if (SelectedProfile is null || string.Equals(SelectedProfile.Id, "default", StringComparison.OrdinalIgnoreCase))
        {
            _interaction.ShowNotification("Cannot Delete Default", "Switch to a custom non-active profile or delete a secondary profile.");
            return Task.CompletedTask;
        }

        // Switch back to default first if deleting active custom profile
        string toDeleteId = SelectedProfile.Id;
        string toDeleteName = SelectedProfile.Name;
        bool confirmed = _interaction.ConfirmDestructiveAction(
            "Delete Mod Profile",
            $"Delete profile '{toDeleteName}'? (Your installed mod files will NOT be deleted; active profile will switch to Default Single-Player.)",
            "Delete Profile");
        if (!confirmed)
        {
            return Task.CompletedTask;
        }

        return DeleteProfileConfirmedAsync(toDeleteId, toDeleteName);
    }

    private async Task DeleteProfileConfirmedAsync(string toDeleteId, string toDeleteName)
    {
        var defaultProf = Profiles.First(p => p.Id == "default");
        await SwitchActiveProfileInternalAsync(defaultProf);
        _repository.DeleteProfile(toDeleteId);
        _repository.AppendLog(AuditLogLevel.Info, "Profiles", $"Deleted profile '{toDeleteName}'");
        RefreshProfiles();
    }

    private async Task SwitchActiveProfileInternalAsync(ModProfile target)
    {
        if (target.IsActive)
        {
            return;
        }

        IsBusy = true;
        var progress = new Progress<InstallProgressReport>(p => UpdateProgress(p.Percentage, p.Stage, p.Detail));
        try
        {
            await Task.Run(() => _profileManager.SwitchProfileAsync(target.Id, GameEnv, progress));
            RefreshProfiles();
            RefreshInstalledMods();
            RefreshBackups();
            SetBanner(StatusBannerSeverity.Verified, $"Switched active profile to '{target.Name}'.");
        }
        catch (Exception ex)
        {
            SetBanner(StatusBannerSeverity.Error, $"Profile switch failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void SaveAiConfiguration(string? newApiKeyFromPasswordBox = null, bool clearSavedKey = false)
    {
        _repository.SetSetting("ai_enable_llm", EnableAiLlm ? "1" : "0");
        _repository.SetSetting("ai_endpoint_url", AiEndpointUrl.Trim());
        _repository.SetSetting("ai_model_name", AiModelName.Trim());

        if (clearSavedKey)
        {
            try { _secretStore.SaveAiApiKey(null); } catch { /* ignore */ }
        }
        else if (!string.IsNullOrWhiteSpace(newApiKeyFromPasswordBox))
        {
            _secretStore.SaveAiApiKey(newApiKeyFromPasswordBox);
        }

        HasConfiguredApiKey = _secretStore.HasConfiguredApiKey();
        _repository.AppendLog(
            AuditLogLevel.Info,
            "AI Settings",
            $"Saved AI configuration (Endpoint={AiEndpointUrl}, Model={AiModelName}, EncryptedKeyConfigured={HasConfiguredApiKey})");
        SetBanner(StatusBannerSeverity.Verified, "Saved AI analyzer configuration securely (no plaintext keys stored).");
    }

    public void ExportLogs()
    {
        string? dest = _interaction.PromptSaveFile(
            "Export GTA Mod Assistant Audit Logs",
            $"gtama_logs_{DateTime.Now:yyyyMMdd_HHmmss}.log",
            "Log Files (*.log)|*.log|Text Files (*.txt)|*.txt");
        if (string.IsNullOrWhiteSpace(dest))
        {
            return;
        }

        var sb = new StringBuilder();
        foreach (var l in _allLogsBuffer)
        {
            sb.AppendLine($"[{l.FormattedTimestamp}] [{l.Level.ToString().ToUpperInvariant()}] [{l.Category}] {l.Message}");
            if (!string.IsNullOrWhiteSpace(l.Details))
            {
                sb.AppendLine($"    Details: {l.Details}");
            }
        }

        File.WriteAllText(dest, sb.ToString());
        SetBanner(StatusBannerSeverity.Verified, $"Exported {_allLogsBuffer.Count} audit log entries to '{dest}'.");
    }

    public void RefreshInstalledMods()
    {
        var items = _repository.GetInstalledMods();
        _uiDispatch(() =>
        {
            InstalledMods.Clear();
            foreach (var m in items)
            {
                InstalledMods.Add(m);
            }
        });
    }

    public void RefreshBackups()
    {
        var items = _repository.GetBackups();
        _uiDispatch(() =>
        {
            Backups.Clear();
            foreach (var b in items)
            {
                Backups.Add(b);
            }
        });
    }

    public void RefreshProfiles()
    {
        var items = _repository.GetProfiles();
        _uiDispatch(() =>
        {
            _suppressProfileSwitchEvent = true;
            try
            {
                Profiles.Clear();
                foreach (var p in items)
                {
                    Profiles.Add(p);
                }
                _selectedProfile = Profiles.FirstOrDefault(p => p.IsActive) ?? Profiles.FirstOrDefault();
                OnPropertyChanged(nameof(SelectedProfile));
            }
            finally
            {
                _suppressProfileSwitchEvent = false;
            }
        });
    }

    private void LoadPersistedSettings()
    {
        _customGtaPathOverride = _repository.GetSetting("gta_root_override") ?? string.Empty;
        _customOpenIvOverride = _repository.GetSetting("openiv_exe_override") ?? string.Empty;
        _enableAiLlm = (_repository.GetSetting("ai_enable_llm") ?? "1") == "1";
        _aiEndpointUrl = _repository.GetSetting("ai_endpoint_url") ?? "https://api.openai.com/v1/chat/completions";
        _aiModelName = _repository.GetSetting("ai_model_name") ?? "gpt-4o-mini";
        _hasConfiguredApiKey = _secretStore.HasConfiguredApiKey();
    }

    private void LoadInitialData()
    {
        foreach (var l in _repository.GetRecentLogs(500))
        {
            _allLogsBuffer.Add(l);
        }
        RebuildFilteredLogs();
        RefreshProfiles();
        RefreshInstalledMods();
        RefreshBackups();
    }

    private void RebuildFilteredLogs()
    {
        _uiDispatch(() =>
        {
            FilteredLogs.Clear();
            foreach (var l in _allLogsBuffer.Where(MatchesLogFilter))
            {
                FilteredLogs.Add(l);
            }
        });
    }

    private bool MatchesLogFilter(AuditLogEntry entry)
    {
        if (!string.Equals(SelectedLogLevelFilter, "ALL", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(entry.Level.ToString(), SelectedLogLevelFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(LogSearchText))
        {
            string q = LogSearchText.Trim();
            return entry.Message.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   entry.Category.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   entry.Details.Contains(q, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private void UpdateProgress(int pct, string stage, string detail)
    {
        _uiDispatch(() =>
        {
            ProgressPercentage = Math.Clamp(pct, 0, 100);
            ProgressStage = stage;
            ProgressDetail = detail;
        });
    }

    private void SetBanner(StatusBannerSeverity severity, string text)
    {
        _uiDispatch(() =>
        {
            StatusBannerKind = severity;
            StatusBannerText = text;
        });
    }

    private void UpdateInstallationPlanStatus(ModInstallationPlan plan)
    {
        if (!GameEnv.IsDetected)
        {
            PlanStatusHeadline = "GTA V NOT DETECTED — CLICK 'SCAN GAME' BEFORE INSTALLING";
            SetBanner(
                StatusBannerSeverity.Warning,
                $"Plan ready for '{plan.ModName}', but GTA V is not detected. Click 'Scan Game' before installing.");
            return;
        }

        if (!plan.CanInstall && !plan.Steps.Any(step => step.IsSelectedForInstall))
        {
            PlanStatusHeadline = "NO INSTALL STEPS SELECTED — SELECT AT LEAST ONE STEP";
            SetBanner(
                StatusBannerSeverity.Warning,
                $"No install steps are selected for '{plan.ModName}'. Select at least one step to enable Install.");
            return;
        }

        if (!plan.CanInstall)
        {
            PlanStatusHeadline = "BLOCKED BY SAFETY / COMPATIBILITY CHECK — REVIEW DETAILS BELOW";
            SetBanner(
                StatusBannerSeverity.Error,
                $"Blocked from auto-installing '{plan.ModName}': {plan.Compatibility.Headline}");
            return;
        }

        PlanStatusHeadline = "PREVIEW READY — CLICK 'INSTALL MOD NOW' ON THE RIGHT TO INSTALL INTO GTA V";

        if (plan.HasUnresolvedTargets)
        {
            SetBanner(
                StatusBannerSeverity.Warning,
                $"Action needed for '{plan.ModName}': {plan.UnresolvedTargetCount} .yft/.ytd file(s) have an uncertain RPF archive — click 'Choose RPF...' or 'Install' to select destination.");
        }
        else if (plan.Compatibility.Severity == CompatibilitySeverity.WarningOutdated || plan.Conflicts.Count > 0)
        {
            SetBanner(
                StatusBannerSeverity.Warning,
                $"Plan ready for '{plan.ModName}' with {plan.Conflicts.Count} conflict/duplicate warning(s) — review preview table before clicking Install.");
        }
        else
        {
            SetBanner(
                StatusBannerSeverity.Verified,
                $"Installation plan ready for '{plan.ModName}' ({plan.Steps.Count} safe step(s)). Click 'Install' to proceed.");
        }
    }

    public async Task ScanCleanupAsync(
        IReadOnlyList<string>? extraCrashDirsForTest = null,
        string? customTempDirForTest = null,
        TimeSpan? staleTempThresholdOverride = null)
    {
        IsBusy = true;
        UpdateProgress(35, "Scanning Junk & RAM", "Inspecting leftover staging folders, crash dumps, %TEMP% archive remnants, and RAM...");
        try
        {
            string? gtaRoot = GameEnv.IsDetected ? GameEnv.GtaRootPath : null;
            string? activeStaging = CurrentPlan?.StagingDirectory;

            var (snapshot, candidates) = await Task.Run(() =>
            {
                var snap = _cleanupService.CaptureMemorySnapshot(gtaRoot);
                var items = _cleanupService.ScanCleanupCandidates(
                    gtaRoot,
                    activeStaging,
                    extraCrashDirsForTest,
                    customTempDirForTest,
                    staleTempThresholdOverride);
                return (snap, items);
            });

            _uiDispatch(() =>
            {
                MemorySnapshot = snapshot;
                CleanupCategories.Clear();
                foreach (var c in candidates)
                {
                    CleanupCategories.Add(c);
                }
            });

            long totalBytes = candidates
                .Where(c => c.IsSelected)
                .Sum(c => c.EstimatedReclaimableBytes);
            int totalDiskItems = candidates
                .Where(c => c.Kind is not CleanupCategoryKind.IdleRamWorkingSet and not CleanupCategoryKind.DatabaseWalBloat)
                .Sum(c => c.ItemCount);

            CleanupSummaryHeadline =
                $"Found {SystemMemorySnapshot.FormatSize(totalBytes)} of safe-to-remove junk ({totalDiskItems} leftover file/folder item(s)) + live idle RAM working-set trim.";
            UpdateProgress(100, "Cleanup Scan Ready", CleanupSummaryHeadline);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ToggleCleanupCategory(CleanupCategoryItem? item)
    {
        if (item is null)
        {
            return;
        }

        int idx = CleanupCategories.IndexOf(item);
        if (idx < 0)
        {
            return;
        }

        CleanupCategories[idx] = item with { IsSelected = !item.IsSelected };
        long totalBytes = CleanupCategories.Where(c => c.IsSelected).Sum(c => c.EstimatedReclaimableBytes);
        CleanupSummaryHeadline =
            $"Selected {SystemMemorySnapshot.FormatSize(totalBytes)} of disk junk for safe cleanup (GTA V mods\\ & backups remain 100% untouched).";
    }

    public async Task<CleanupExecutionResult?> ExecuteCleanupAsync()
    {
        var selected = CleanupCategories.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0)
        {
            SetBanner(StatusBannerSeverity.Info, "Select at least one cleanup category first.");
            return null;
        }

        int diskFileCount = selected
            .Where(c => c.Kind is not CleanupCategoryKind.IdleRamWorkingSet and not CleanupCategoryKind.DatabaseWalBloat)
            .Sum(c => c.ItemCount);
        long diskBytes = selected.Sum(c => c.EstimatedReclaimableBytes);

        if (diskFileCount > 0)
        {
            bool confirmed = _interaction.ConfirmDestructiveAction(
                "Confirm Safe Junk & Memory Cleanup",
                $"Remove {diskFileCount} leftover staging/temp/crash-dump item(s) ({SystemMemorySnapshot.FormatSize(diskBytes)}) and trim idle RAM?\n\n" +
                "Guaranteed Untouched:\n" +
                "• Your GTA V game folder & mods\\ folder\n" +
                "• All installed mods, profiles, and rollback backups\n" +
                "• Windows boot & system memory settings",
                "Clean Junk & Trim RAM");

            if (!confirmed)
            {
                SetBanner(StatusBannerSeverity.Info, "Cleanup cancelled — nothing was removed.");
                return null;
            }
        }

        IsBusy = true;
        UpdateProgress(50, "Cleaning Junk & Trimming RAM", "Removing leftover staging caches/temp junk and trimming idle app RAM...");
        try
        {
            string? gtaRoot = GameEnv.IsDetected ? GameEnv.GtaRootPath : null;
            string? activeStaging = CurrentPlan?.StagingDirectory;

            var snapshotList = CleanupCategories.ToList();
            var result = await Task.Run(() => _cleanupService.ExecuteCleanup(snapshotList, gtaRoot, activeStaging));

            _repository.AppendLog(
                AuditLogLevel.Verify,
                "Cleanup",
                result.SummaryHeadline,
                $"DeletedItems={result.DeletedDiskItems} | ReclaimedBytes={result.ReclaimedDiskBytes} | TrimmedProcs={result.TrimmedProcessesCount} | FreedRamBytes={result.FreedRamBytes}");

            await ScanCleanupAsync();

            CleanupSummaryHeadline = result.SummaryHeadline;
            SetBanner(StatusBannerSeverity.Verified, result.SummaryHeadline);
            UpdateProgress(100, "Cleanup Complete", result.SummaryHeadline);
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task QuickTrimRamOnlyAsync()
    {
        IsBusy = true;
        UpdateProgress(45, "Trimming Idle RAM", "Compacting managed heap and trimming idle user-space RAM working sets...");
        try
        {
            string? gtaRoot = GameEnv.IsDetected ? GameEnv.GtaRootPath : null;
            var (before, after, trimmedCount) = await Task.Run(() =>
            {
                var snapBefore = _cleanupService.CaptureMemorySnapshot(gtaRoot);
                int count = _cleanupService.TrimIdleRamWorkingSets();
                var snapAfter = _cleanupService.CaptureMemorySnapshot(gtaRoot);
                return (snapBefore, snapAfter, count);
            });

            MemorySnapshot = after;
            long freed = Math.Max(0L, after.AvailablePhysicalBytes - before.AvailablePhysicalBytes);
            string msg = freed > 0
                ? $"Quick RAM Trim complete: Freed {SystemMemorySnapshot.FormatSize(freed)} physical RAM across {trimmedCount} app working set(s) (0 files or system settings touched)."
                : $"Quick RAM Trim complete: Compacted {trimmedCount} app working set(s) — {after.RamSummaryDisplay}.";

            CleanupSummaryHeadline = msg;
            _repository.AppendLog(AuditLogLevel.Info, "Cleanup", msg, $"AvailBefore={before.AvailablePhysicalBytes} | AvailAfter={after.AvailablePhysicalBytes}");
            SetBanner(StatusBannerSeverity.Verified, msg);
            UpdateProgress(100, "RAM Trimmed", msg);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
