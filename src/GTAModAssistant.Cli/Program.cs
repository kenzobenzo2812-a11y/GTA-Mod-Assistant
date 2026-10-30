using GTAModAssistant.Core.Archives;
using GTAModAssistant.Core.Models;
using GTAModAssistant.Core.Security;
using GTAModAssistant.Core.Services;

namespace GTAModAssistant.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("==========================================================");
        Console.WriteLine("  GTA Mod Assistant CLI Engine v1.1.0");
        Console.WriteLine("==========================================================");

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        string? customDataDir = GetOption(args, "--data-dir");
        string? customGtaDir = GetOption(args, "--gta-dir");
        var paths = new AppWorkspacePaths(customDataDir);
        var repo = new SqliteHistoryRepository(paths.DatabasePath);
        var secrets = new SecretStore(paths.RootDataDirectory);
        var detector = new GameDetectionService();
        var analyzer = new AiModAnalyzerService(new ArchiveExtractorService(), secrets);
        var backups = new BackupService(paths.BackupsDirectory, repo);
        var installer = new ModInstallerService(repo, backups, paths.RootDataDirectory);

        string cmd = args[0].ToLowerInvariant();
        var env = detector.ScanEnvironment(customGtaDir ?? repo.GetSetting("gta_root_override"));
        var activeFiles = repo.GetModFiles(onlyEnabledMods: true);
        var modNamesById = repo.GetInstalledMods()
            .ToDictionary(m => m.Id, m => m.Name, StringComparer.OrdinalIgnoreCase);

        switch (cmd)
        {
            case "scan":
                Console.WriteLine($"Detected      : {env.IsDetected}");
                Console.WriteLine($"GTA V Path    : {env.GtaRootPath}");
                Console.WriteLine($"Platform      : {env.Platform}");
                Console.WriteLine($"Game Version  : {env.GameVersion} (Build {env.BuildNumber} - {env.BuildTitleName})");
                Console.WriteLine($"OpenIV        : {(env.OpenIvInstalled ? env.OpenIvExecutablePath : "Not Found")}");
                Console.WriteLine($"Mods Folder   : {env.ModsFolderPath} (Exists={env.ModsFolderExists})");
                foreach (var w in env.HealthWarnings)
                {
                    Console.WriteLine($"  [WARN] {w}");
                }
                return env.IsDetected ? 0 : 1;

            case "analyze":
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("Usage: gtama-cli analyze <mod-archive-or-folder> [--gta-dir <path>]");
                    return 2;
                }
                var plan = await analyzer.AnalyzeModAsync(
                    args[1],
                    paths.StagingDirectory,
                    env,
                    new AiSettings(),
                    activeFiles,
                    modNamesById);
                PrintPlan(plan);
                return plan.CanInstall ? 0 : 1;

            case "install":
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("Usage: gtama-cli install <mod-archive-or-folder> [--gta-dir <path>] [--rpf-target <file=mods/x.rpf::inner/path>] [--keep-first-duplicate]");
                    return 2;
                }
                var installPlan = await analyzer.AnalyzeModAsync(
                    args[1],
                    paths.StagingDirectory,
                    env,
                    new AiSettings(),
                    activeFiles,
                    modNamesById);

                // Apply any explicit --rpf-target <fileName>=<outerRpf>::<innerPath> overrides
                foreach (var overrideSpec in GetAllOptions(args, "--rpf-target"))
                {
                    int eq = overrideSpec.IndexOf('=');
                    int sep = overrideSpec.IndexOf("::", StringComparison.Ordinal);
                    if (eq > 0 && sep > eq)
                    {
                        string targetFileName = overrideSpec[..eq].Trim();
                        string outerRpf = overrideSpec[(eq + 1)..sep].Trim();
                        string innerPath = overrideSpec[(sep + 2)..].Trim();

                        var matchingStep = installPlan.Steps.FirstOrDefault(s =>
                            string.Equals(s.FileName, targetFileName, StringComparison.OrdinalIgnoreCase));
                        if (matchingStep is not null)
                        {
                            installPlan = AiModAnalyzerService.ApplyUserTargetSelection(
                                installPlan,
                                matchingStep.StepId,
                                outerRpf,
                                innerPath,
                                applyToCompanionStemFiles: true,
                                activeFiles,
                                env,
                                modNamesById);
                        }
                    }
                }

                PrintPlan(installPlan);

                if (installPlan.HasUnresolvedTargets)
                {
                    Console.Error.WriteLine(
                        "Uncertain RPF destination detected. Pass --rpf-target <fileName>=<mods/archive.rpf>::<internal/path> instead of guessing.");
                    return 3;
                }

                if (!installPlan.CanInstall)
                {
                    Console.Error.WriteLine("Refusing to install: pre-flight safety or compatibility check blocked plan.");
                    return 1;
                }

                var strategy = args.Any(a => string.Equals(a, "--keep-first-duplicate", StringComparison.OrdinalIgnoreCase))
                    ? ConflictResolutionStrategy.KeepFirstDuplicateOnly
                    : ConflictResolutionStrategy.BackupAndOverwrite;

                var progress = new Progress<InstallProgressReport>(p =>
                    Console.WriteLine($"[{p.Percentage,3}%] {p.Stage}: {p.Detail}"));
                var res = await installer.InstallAsync(
                    installPlan,
                    env,
                    strategy,
                    autoLaunchOpenIvForEncryptedRpfs: false,
                    progress: progress);
                Console.WriteLine(res.SummaryMessage);
                return res.VerificationState is InstallVerificationState.VerifiedComplete or InstallVerificationState.PendingOpenIvCommit ? 0 : 1;

            case "uninstall":
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("Usage: gtama-cli uninstall <mod-id> [--gta-dir <path>]");
                    return 2;
                }
                bool ok = await installer.UninstallAndRollbackModAsync(
                    args[1],
                    env,
                    new Progress<InstallProgressReport>(p => Console.WriteLine($"[{p.Percentage,3}%] {p.Stage}: {p.Detail}")));
                return ok ? 0 : 1;

            case "list":
                foreach (var m in repo.GetInstalledMods())
                {
                    Console.WriteLine($"{m.Id} | {(m.IsEnabled ? "ENABLED " : "DISABLED")} | {m.VerificationState} | {m.Name} v{m.Version} ({m.Category})");
                }
                return 0;

            case "logs":
                foreach (var l in repo.GetRecentLogs(100))
                {
                    Console.WriteLine($"[{l.FormattedTimestamp}] [{l.Level}] [{l.Category}] {l.Message}");
                }
                return 0;

            default:
                PrintUsage();
                return 2;
        }
    }

    private static void PrintPlan(ModInstallationPlan plan)
    {
        Console.WriteLine($"Mod Name      : {plan.ModName} v{plan.ModVersion} by {plan.Author}");
        Console.WriteLine($"Category      : {plan.Category}");
        Console.WriteLine($"Engine        : {plan.AnalysisEngineUsed} (Confidence {plan.ConfidenceScore:P0})");
        Console.WriteLine($"Compatibility : [{plan.Compatibility.Severity}] {plan.Compatibility.Headline}");
        Console.WriteLine($"AI Summary    : {plan.AiReasoningSummary}");
        Console.WriteLine($"File Preview ({plan.Steps.Count} step(s)):");
        foreach (var s in plan.Steps)
        {
            Console.WriteLine(
                $"  - {s.FileName,-24} | {s.AssetFormatLabel,-38} | {s.FullPreviewDestination} | [{s.ResolutionSource}] | {s.ConflictStatusPreview}");
        }
        foreach (var sec in plan.SecurityFindings)
        {
            Console.WriteLine($"  [SECURITY:{sec.Severity}] {sec.FilePath}: {sec.Reason}");
        }
        foreach (var c in plan.Conflicts)
        {
            Console.WriteLine($"  [CONFLICT:{c.Kind}] {c.Description}");
        }
    }

    private static string? GetOption(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static IEnumerable<string> GetAllOptions(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            {
                yield return args[i + 1];
            }
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Commands:");
        Console.WriteLine("  gtama-cli scan [--gta-dir <path>]");
        Console.WriteLine("  gtama-cli analyze <zip|rar|oiv|folder> [--gta-dir <path>]");
        Console.WriteLine("  gtama-cli install <zip|rar|oiv|folder> [--gta-dir <path>] [--rpf-target <file=mods/x.rpf::inner>] [--keep-first-duplicate]");
        Console.WriteLine("  gtama-cli uninstall <mod-id> [--gta-dir <path>]");
        Console.WriteLine("  gtama-cli list [--data-dir <path>]");
        Console.WriteLine("  gtama-cli logs [--data-dir <path>]");
    }
}
