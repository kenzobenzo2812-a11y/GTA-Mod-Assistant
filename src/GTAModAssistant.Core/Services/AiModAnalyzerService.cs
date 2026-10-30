using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GTAModAssistant.Core.Archives;
using GTAModAssistant.Core.Models;
using GTAModAssistant.Core.Security;

namespace GTAModAssistant.Core.Services;

public sealed partial class AiModAnalyzerService
{
    private readonly ArchiveExtractorService _extractor;
    private readonly SecretStore _secretStore;
    private readonly HttpClient _httpClient;

    private static readonly HashSet<string> DocumentationExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".nfo", ".rtf"
    };

    private static readonly HashSet<string> IgnoredMediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".url", ".lnk", ".pdf"
    };

    public AiModAnalyzerService(
        ArchiveExtractorService extractor,
        SecretStore secretStore,
        HttpClient? httpClient = null)
    {
        _extractor = extractor;
        _secretStore = secretStore;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
    }

    public StagedModInput StageModInput(string sourcePath, string stagingRootDirectory) =>
        _extractor.StageInput(sourcePath, stagingRootDirectory);

    public static bool StagedPackageHasReadmeOrOiv(StagedModInput staged)
    {
        if (staged.IsOriginallyOivPackage ||
            staged.Files.Any(f => string.Equals(Path.GetFileName(f.RelativePath), "assembly.xml", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return staged.Files.Any(f =>
            DocumentationExtensions.Contains(f.Extension) ||
            Path.GetFileNameWithoutExtension(f.RelativePath).Contains("readme", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileNameWithoutExtension(f.RelativePath).Contains("install", StringComparison.OrdinalIgnoreCase));
    }

    public bool IsLlmEndpointActive(AiSettings aiSettings) =>
        aiSettings.EnableCloudOrLocalLlm && CanCallLlmEndpoint(aiSettings);

    public async Task<ModInstallationPlan> AnalyzeModAsync(
        string sourcePath,
        string stagingRootDirectory,
        GameEnvironmentInfo gameEnv,
        AiSettings aiSettings,
        IReadOnlyList<InstalledModFileRecord> currentlyInstalledFiles,
        IReadOnlyDictionary<string, string>? installedModNamesById = null,
        CancellationToken cancellationToken = default)
    {
        var staged = _extractor.StageInput(sourcePath, stagingRootDirectory);
        return await AnalyzeStagedModAsync(
            staged,
            gameEnv,
            aiSettings,
            currentlyInstalledFiles,
            installedModNamesById,
            cancellationToken);
    }

    public async Task<ModInstallationPlan> AnalyzeStagedModAsync(
        StagedModInput staged,
        GameEnvironmentInfo gameEnv,
        AiSettings aiSettings,
        IReadOnlyList<InstalledModFileRecord> currentlyInstalledFiles,
        IReadOnlyDictionary<string, string>? installedModNamesById = null,
        CancellationToken cancellationToken = default)
    {
        string sourcePath = staged.SourcePath;
        var securityFindings = new List<SecurityFinding>(staged.ExtractionSecurityFindings);
        var warnings = new List<string>();

        string fallbackModName = CleanModDisplayName(Path.GetFileNameWithoutExtension(sourcePath.TrimEnd('/', '\\')));
        if (string.IsNullOrWhiteSpace(fallbackModName))
        {
            fallbackModName = Path.GetFileName(sourcePath.TrimEnd('/', '\\'));
        }

        var rpfIndex = gameEnv.IsDetected
            ? GameRpfSearchService.BuildIndex(gameEnv.GtaRootPath)
            : new GameRpfSearchService();

        string fileDossier = BuildDeepFileInspectionDossier(staged.Files, rpfIndex, out var dossierSpawnNames);

        // Case 1: Native OpenIV .oiv package
        if (staged.IsOriginallyOivPackage ||
            staged.Files.Any(f => string.Equals(Path.GetFileName(f.RelativePath), "assembly.xml", StringComparison.OrdinalIgnoreCase)))
        {
            var oivMeta = OivPackageService.ParseExtractedOivDirectory(staged.StagingDirectory);
            securityFindings.AddRange(oivMeta.SecurityFindings);
            warnings.AddRange(oivMeta.Warnings);

            var annotatedOivSteps = oivMeta.ExtractedSteps
                .Select(AnnotateStepAssetFormat)
                .ToList();

            string combinedText = $"{oivMeta.Name}\n{oivMeta.Description}\n{Path.GetFileName(sourcePath)}";
            var compat = VersionCompatibilityService.EvaluateCompatibility(
                gameEnv,
                ModCategory.OpenIvPackage,
                staged.Files,
                combinedText);

            var conflicts = ConflictDetectionService.FindConflicts(
                annotatedOivSteps,
                currentlyInstalledFiles,
                gameEnv.GtaRootPath,
                installedModNamesById);

            var enrichedOivSteps = ConflictDetectionService.EnrichStepsWithPreviewMetadata(
                annotatedOivSteps,
                conflicts,
                rpfIndex,
                gameEnv.GtaRootPath);

            return new ModInstallationPlan
            {
                ModName = oivMeta.Name,
                ModSlug = Slugify(oivMeta.Name),
                ModVersion = oivMeta.Version,
                Author = oivMeta.Author,
                SourcePath = staged.SourcePath,
                StagingDirectory = staged.StagingDirectory,
                Category = ModCategory.OpenIvPackage,
                AnalysisEngineUsed = "OpenIV assembly.xml Schema Analyzer",
                HasReadme = true,
                ConfidenceScore = 0.99,
                ReadmeExcerpt = string.IsNullOrWhiteSpace(oivMeta.Description) ? fileDossier : Truncate(oivMeta.Description, 1200),
                DeepFileInspectionReport = fileDossier,
                DetectedSpawnNames = dossierSpawnNames,
                AiReasoningSummary =
                    $"Parsed OpenIV package v{oivMeta.FormatVersion} ('{oivMeta.Name}' by {oivMeta.Author}). " +
                    $"Mapped {enrichedOivSteps.Count} archive/file action(s) exclusively into safe 'mods/' paths.",
                DiscoveredFiles = staged.Files,
                Steps = enrichedOivSteps,
                Compatibility = compat,
                SecurityFindings = securityFindings,
                Conflicts = conflicts,
                Warnings = warnings
            };
        }

        // Gather all README / documentation files
        var docFiles = staged.Files
            .Where(f => DocumentationExtensions.Contains(f.Extension) ||
                        Path.GetFileNameWithoutExtension(f.RelativePath).Contains("readme", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileNameWithoutExtension(f.RelativePath).Contains("install", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.RelativePath.Contains("readme", StringComparison.OrdinalIgnoreCase) ||
                                    f.RelativePath.Contains("install", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var readmeBuilder = new StringBuilder();
        foreach (var doc in docFiles.Take(5))
        {
            try
            {
                string text = File.ReadAllText(doc.FullStagingPath);
                readmeBuilder.AppendLine($"--- {doc.RelativePath} ---");
                readmeBuilder.AppendLine(Truncate(text, 8000));
            }
            catch
            {
                // Ignore binary or unreadable text file
            }
        }

        string combinedReadme = readmeBuilder.ToString().Trim();
        bool hasReadme = !string.IsNullOrWhiteSpace(combinedReadme);

        if (!hasReadme)
        {
            warnings.Add(
                "[No-README Disclaimer] This mod package does not include a README or installation guide. " +
                "GTA Mod Assistant analyzed the binary RPF7/RSC7 headers, XML/.meta tags, and folder layout directly — " +
                "please review the preview table carefully, as missing author prerequisites or special steps may affect in-game behavior.");
        }

        // Validate all XML / META files up-front!
        foreach (var xmlCandidate in staged.Files.Where(f => f.Extension is ".xml" or ".meta"))
        {
            byte[] rawBytes = await File.ReadAllBytesAsync(xmlCandidate.FullStagingPath, cancellationToken);
            if (!DlcListXmlService.ValidateGtaXml(rawBytes, out string? xmlErr))
            {
                securityFindings.Add(new SecurityFinding(
                    SecuritySeverity.BlockedUnsafePath,
                    xmlCandidate.RelativePath,
                    $"Malformed XML/META file '{xmlCandidate.RelativePath}': {xmlErr}. Blocked to prevent GTA V startup crash."));
            }
        }

        // Optional LLM analysis (when API key or local Ollama endpoint is available) — runs on README + Deep File Inspection Dossier!
        LlmReadmeAnalysisResponse? llmResponse = null;
        string engineLabel = hasReadme
            ? "Deterministic GTA V README & Binary Expert Engine"
            : "Built-In AI Deep File & Binary Forensics Engine (No README)";
        if (aiSettings.EnableCloudOrLocalLlm && CanCallLlmEndpoint(aiSettings))
        {
            llmResponse = await TryQueryLlmAsync(
                fallbackModName,
                combinedReadme,
                fileDossier,
                hasReadme,
                staged.Files,
                gameEnv,
                aiSettings,
                cancellationToken);
            if (llmResponse is not null)
            {
                engineLabel = hasReadme
                    ? $"Hybrid AI ({aiSettings.ModelName} + Deterministic GTA V Safety Verifier)"
                    : $"AI Deep File Analyzer ({aiSettings.ModelName} + Binary RPF7/RSC7 Inspector)";
            }
        }

        // Run deterministic ground-truth planner & merge/validate LLM steps
        var deterministic = BuildDeterministicPlan(
            fallbackModName,
            combinedReadme,
            staged.Files,
            rpfIndex,
            warnings);

        IReadOnlyList<ModInstallStep> rawSteps = deterministic.Steps;
        if (llmResponse is not null && llmResponse.Steps.Count > 0)
        {
            var validatedLlmSteps = ValidateLlmProposedSteps(llmResponse.Steps, staged.Files, warnings);
            if (validatedLlmSteps.Count > 0)
            {
                rawSteps = MergeStepsPreferringVerifiedPaths(validatedLlmSteps, deterministic.Steps);
            }
        }

        string modName = !string.IsNullOrWhiteSpace(llmResponse?.ModName) ? llmResponse!.ModName : deterministic.ModName;
        string modVer = !string.IsNullOrWhiteSpace(llmResponse?.Version) ? llmResponse!.Version : deterministic.Version;
        string author = !string.IsNullOrWhiteSpace(llmResponse?.Author) && llmResponse.Author != "Unknown"
            ? llmResponse.Author
            : deterministic.Author;
        var category = InferOverallCategory(rawSteps, staged.Files);

        var compatReport = VersionCompatibilityService.EvaluateCompatibility(
            gameEnv,
            category,
            staged.Files,
            $"{Path.GetFileName(sourcePath)}\n{combinedReadme}",
            llmResponse?.MinimumBuild,
            llmResponse?.TargetBuild);

        if (llmResponse?.Warnings is { Count: > 0 })
        {
            foreach (var w in llmResponse.Warnings.Where(w => !string.IsNullOrWhiteSpace(w)))
            {
                warnings.Add(w);
            }
        }

        var conflictsList = ConflictDetectionService.FindConflicts(
            rawSteps,
            currentlyInstalledFiles,
            gameEnv.GtaRootPath,
            installedModNamesById);

        if (conflictsList.Any(c => c.Kind == ConflictKind.DuplicateFileInPackage))
        {
            warnings.Add(
                "Duplicate model/texture filenames detected inside this mod package (e.g., multiple texture resolution or livery variants). " +
                "Uncheck unwanted variants in the preview table or choose a conflict resolution mode when installing.");
        }

        var enrichedSteps = ConflictDetectionService.EnrichStepsWithPreviewMetadata(
            rawSteps,
            conflictsList,
            rpfIndex,
            gameEnv.GtaRootPath);

        if (enrichedSteps.Any(s => s.RequiresUserTargetSelection))
        {
            int uncertainCount = enrichedSteps.Count(s => s.RequiresUserTargetSelection);
            warnings.Add(
                $"{uncertainCount} model/texture file(s) do not specify an unambiguous RPF archive in the folder structure, metadata, README, or indexed game archives. " +
                "GTA Mod Assistant will ask you to choose the target RPF archive rather than guessing.");
        }

        string reasoning = !string.IsNullOrWhiteSpace(llmResponse?.ReasoningSummary)
            ? $"{llmResponse!.ReasoningSummary} (Verified {enrichedSteps.Count} safe installation step(s) via GTA V path guard.)"
            : !hasReadme
                ? $"No README was included — ran Deep File Inspection across {staged.Files.Count} file(s). {deterministic.Reasoning}"
                : deterministic.Reasoning;

        double confidence = llmResponse is not null
            ? Math.Clamp(llmResponse.Confidence, 0.5, 0.99)
            : hasReadme
                ? deterministic.Confidence
                : Math.Min(deterministic.Confidence, 0.84);

        var combinedSpawnNames = new List<string>(dossierSpawnNames);
        if (llmResponse?.SpawnNames is { Count: > 0 })
        {
            foreach (var sp in llmResponse.SpawnNames.Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                if (!combinedSpawnNames.Contains(sp.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    combinedSpawnNames.Add(sp.Trim());
                }
            }
        }

        return new ModInstallationPlan
        {
            ModName = modName,
            ModSlug = Slugify(modName),
            ModVersion = modVer,
            Author = author,
            SourcePath = staged.SourcePath,
            StagingDirectory = staged.StagingDirectory,
            Category = category,
            AnalysisEngineUsed = engineLabel,
            HasReadme = hasReadme,
            ConfidenceScore = confidence,
            ReadmeExcerpt = hasReadme
                ? Truncate(combinedReadme, 1600)
                : fileDossier,
            DeepFileInspectionReport = fileDossier,
            DetectedSpawnNames = combinedSpawnNames,
            AiReasoningSummary = reasoning,
            DiscoveredFiles = staged.Files,
            Steps = enrichedSteps,
            Compatibility = compatReport,
            SecurityFindings = securityFindings,
            Conflicts = conflictsList,
            Warnings = warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    /// <summary>
    /// Updates a step (and optionally companion files sharing its model stem, e.g. police.yft + police_hi.yft + police.ytd)
    /// with a user-confirmed RPF destination and refreshes conflict/preview metadata.
    /// </summary>
    public static ModInstallationPlan ApplyUserTargetSelection(
        ModInstallationPlan plan,
        string stepId,
        string chosenOuterRpfPath,
        string chosenInternalDirectoryOrFilePath,
        bool applyToCompanionStemFiles,
        IReadOnlyList<InstalledModFileRecord> currentlyInstalledFiles,
        GameEnvironmentInfo gameEnv,
        IReadOnlyDictionary<string, string>? installedModNamesById = null)
    {
        var targetStep = plan.Steps.FirstOrDefault(s => s.StepId == stepId)
                         ?? throw new KeyNotFoundException($"Step '{stepId}' not found in installation plan.");

        string safeOuter = PathSafetyGuard.EnsureSafeModsPath(chosenOuterRpfPath);
        if (!safeOuter.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
        {
            safeOuter = "mods/" + safeOuter;
        }
        if (!safeOuter.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Outer container path '{chosenOuterRpfPath}' must end with .rpf.", nameof(chosenOuterRpfPath));
        }

        string normInnerInput = Rpf7Archive.NormalizeInternalPath(chosenInternalDirectoryOrFilePath);
        string innerDirectory = HasAssetFileExtension(normInnerInput)
            ? Rpf7Archive.NormalizeInternalPath(Path.GetDirectoryName(normInnerInput) ?? string.Empty)
            : normInnerInput;

        var updatedSteps = new List<ModInstallStep>(plan.Steps.Count);
        foreach (var step in plan.Steps)
        {
            bool isDirectMatch = step.StepId == stepId;
            bool isStemCompanion = applyToCompanionStemFiles &&
                                   step.RequiresUserTargetSelection &&
                                   !string.IsNullOrWhiteSpace(targetStep.CanonicalModelStem) &&
                                   string.Equals(step.CanonicalModelStem, targetStep.CanonicalModelStem, StringComparison.OrdinalIgnoreCase);

            if (isDirectMatch || isStemCompanion)
            {
                string fileName = Path.GetFileName(step.SourceRelativePath.Replace('\\', '/'));
                string finalInner = isDirectMatch && HasAssetFileExtension(normInnerInput)
                    ? normInnerInput
                    : string.IsNullOrEmpty(innerDirectory)
                        ? fileName
                        : $"{innerDirectory}/{fileName}";

                updatedSteps.Add(step with
                {
                    Operation = InstallOperationKind.InjectIntoRpfArchive,
                    TargetRelativePath = safeOuter,
                    RpfContainerRelativePath = safeOuter,
                    RpfInternalPath = finalInner,
                    UsesSafeModsFolder = true,
                    RequiresUserTargetSelection = false,
                    UncertaintyReason = string.Empty,
                    ResolutionSource = isDirectMatch ? "User Selected" : $"User Selected (Paired '{targetStep.CanonicalModelStem}')",
                    Description = $"Inject '{fileName}' into '{safeOuter}' -> '{finalInner}'"
                });
            }
            else
            {
                updatedSteps.Add(step);
            }
        }

        var rpfIndex = gameEnv.IsDetected
            ? GameRpfSearchService.BuildIndex(gameEnv.GtaRootPath)
            : null;

        var conflicts = ConflictDetectionService.FindConflicts(
            updatedSteps,
            currentlyInstalledFiles,
            gameEnv.GtaRootPath,
            installedModNamesById);

        var enriched = ConflictDetectionService.EnrichStepsWithPreviewMetadata(
            updatedSteps,
            conflicts,
            rpfIndex,
            gameEnv.GtaRootPath);

        return plan with
        {
            Steps = enriched,
            Conflicts = conflicts
        };
    }

    public static ModInstallationPlan SetStepSelection(
        ModInstallationPlan plan,
        string stepId,
        bool isSelected,
        IReadOnlyList<InstalledModFileRecord> currentlyInstalledFiles,
        GameEnvironmentInfo gameEnv,
        IReadOnlyDictionary<string, string>? installedModNamesById = null)
    {
        var updatedSteps = plan.Steps
            .Select(s => s.StepId == stepId ? s with { IsSelectedForInstall = isSelected } : s)
            .ToList();

        var rpfIndex = gameEnv.IsDetected
            ? GameRpfSearchService.BuildIndex(gameEnv.GtaRootPath)
            : null;

        var conflicts = ConflictDetectionService.FindConflicts(
            updatedSteps,
            currentlyInstalledFiles,
            gameEnv.GtaRootPath,
            installedModNamesById);

        var enriched = ConflictDetectionService.EnrichStepsWithPreviewMetadata(
            updatedSteps,
            conflicts,
            rpfIndex,
            gameEnv.GtaRootPath);

        return plan with
        {
            Steps = enriched,
            Conflicts = conflicts
        };
    }

    private bool CanCallLlmEndpoint(AiSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.EndpointUrl))
        {
            return false;
        }

        if (_secretStore.HasConfiguredApiKey())
        {
            return true;
        }

        if (Uri.TryCreate(settings.EndpointUrl, UriKind.Absolute, out var uri) &&
            (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.Equals("127.0.0.1", StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }

    private async Task<LlmReadmeAnalysisResponse?> TryQueryLlmAsync(
        string fallbackModName,
        string readmeText,
        string deepFileDossier,
        bool hasReadme,
        IReadOnlyList<ExtractedFileEntry> files,
        GameEnvironmentInfo gameEnv,
        AiSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            string fileManifest = string.Join("\n", files.Take(80).Select(f => $"- {f.RelativePath} ({f.SizeBytes} bytes)"));
            string systemPrompt = """
                You are an expert GTA V Single-Player Modding Architect & Binary File Forensics AI.
                Analyze the provided mod file manifest, Deep Binary/XML File Inspection Dossier, and README instructions (if any).
                Even when NO README is included, determine how the mod should be installed by inspecting the binary RPF7/RSC7 headers,
                dlc.rpf setup2.xml/content.xml metadata, .yft/_hi.yft/.ytd model stems, .asi/.dll PE CLR headers, and game RPF matches.
                Output ONLY valid JSON matching this schema:
                {
                  "modName": "string",
                  "version": "string",
                  "author": "string",
                  "category": "AddOnDlcPack | RpfAssetReplacement | AsiPlugin | ScriptHookVDotNetMod | DataOrConfigMod | CompositeMod",
                  "minimumBuild": null,
                  "targetBuild": null,
                  "confidence": 0.90,
                  "reasoningSummary": "string explaining how files were analyzed (especially when no README was provided)",
                  "spawnNames": ["in-game model/spawn name if detected, e.g. rmodgt63 or police2"],
                  "prerequisites": ["string"],
                  "warnings": ["string"],
                  "steps": [
                    {
                      "operation": "CopyFileToModsOrRoot | InjectIntoRpfArchive | RegisterDlcListEntry",
                      "sourceRelativePath": "exact relative path from manifest (empty for RegisterDlcListEntry)",
                      "targetRelativePath": "GTA V relative path using mods/ for any RPF or dlcpacks!",
                      "rpfContainerRelativePath": "outer .rpf path starting with mods/ when operation is InjectIntoRpfArchive",
                      "rpfInternalPath": "path inside the outer .rpf when operation is InjectIntoRpfArchive",
                      "dlcListEntry": "dlcpacks:/name/ when operation is RegisterDlcListEntry",
                      "description": "human-readable step description"
                    }
                  ]
                }
                Rules:
                1. NEVER overwrite vanilla RPF or data files; ALWAYS prefix update/..., x64*.rpf, common.rpf with "mods/".
                2. NEVER assume every .yft, _hi.yft, or .ytd goes to the same archive; map each file to the specific archive indicated by its folder path, game RPF match in the dossier, or README section. If uncertain about a file's archive, omit that file from steps so the deterministic engine asks the user.
                3. NEVER include .exe, .bat, .ps1, or .cmd files.
                4. Add-On dlc.rpf files go to "mods/update/x64/dlcpacks/<dlcname>/dlc.rpf" with a matching RegisterDlcListEntry step "dlcpacks:/<dlcname>/".
                """;

            string readmeSection = hasReadme
                ? $"README Text:\n{Truncate(readmeText, 5000)}"
                : "README Text:\n[NO README INCLUDED IN PACKAGE — PERFORM DEEP AI FILE & BINARY FORENSICS USING THE DOSSIER BELOW]";

            string userPrompt =
                $"Installed GTA V Build: {gameEnv.BuildNumber} ({gameEnv.GameVersion})\n" +
                $"Default Mod Name: {fallbackModName}\n\n" +
                $"Staged File Manifest:\n{fileManifest}\n\n" +
                $"Deep Binary & XML File Inspection Dossier:\n{Truncate(deepFileDossier, 4500)}\n\n" +
                readmeSection;

            var payload = new
            {
                model = settings.ModelName,
                temperature = 0.1,
                response_format = new { type = "json_object" },
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, settings.EndpointUrl);
            string? apiKey = _secretStore.GetAiApiKey();
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var resp = await _httpClient.SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            string respJson = await resp.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(respJson);
            string? content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            return JsonSerializer.Deserialize<LlmReadmeAnalysisResponse>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    private static List<ModInstallStep> ValidateLlmProposedSteps(
        IReadOnlyList<LlmProposedStep> rawSteps,
        IReadOnlyList<ExtractedFileEntry> stagedFiles,
        List<string> warnings)
    {
        var fileMap = stagedFiles.ToDictionary(f => PathSafetyGuard.NormalizeRelativePath(f.RelativePath), StringComparer.OrdinalIgnoreCase);
        var valid = new List<ModInstallStep>();

        foreach (var raw in rawSteps)
        {
            if (string.Equals(raw.Operation, "RegisterDlcListEntry", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(raw.DlcListEntry))
                {
                    try
                    {
                        string canonical = DlcListXmlService.NormalizeDlcEntry(raw.DlcListEntry);
                        valid.Add(new ModInstallStep
                        {
                            Operation = InstallOperationKind.RegisterDlcListEntry,
                            AssetFormatLabel = "DLC Registration (dlclist.xml)",
                            ResolutionSource = "AI README Analysis",
                            TargetRelativePath = "mods/update/update.rpf",
                            RpfContainerRelativePath = "mods/update/update.rpf",
                            RpfInternalPath = DlcListXmlService.RpfInternalDlcListPath,
                            DlcListEntry = canonical,
                            UsesSafeModsFolder = true,
                            Description = $"Register '{canonical}' in dlclist.xml"
                        });
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Ignored invalid AI DLC entry '{raw.DlcListEntry}': {ex.Message}");
                    }
                }
                continue;
            }

            string srcKey = PathSafetyGuard.NormalizeRelativePath(raw.SourceRelativePath);
            if (!fileMap.TryGetValue(srcKey, out var srcEntry))
            {
                continue;
            }

            if (PathSafetyGuard.IsBlockedExecutableOrScript(srcEntry.RelativePath))
            {
                continue;
            }

            var rageInfo = RageAssetInspector.Inspect(srcEntry.FullStagingPath, srcEntry.RelativePath);

            if (string.Equals(raw.Operation, "InjectIntoRpfArchive", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(raw.RpfContainerRelativePath) ||
                    string.IsNullOrWhiteSpace(raw.RpfInternalPath))
                {
                    continue;
                }

                string safeOuter = PathSafetyGuard.EnsureSafeModsPath(raw.RpfContainerRelativePath);
                if (!safeOuter.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
                {
                    safeOuter = "mods/" + safeOuter;
                }

                valid.Add(new ModInstallStep
                {
                    Operation = InstallOperationKind.InjectIntoRpfArchive,
                    SourceRelativePath = srcEntry.RelativePath,
                    SourceFullPath = srcEntry.FullStagingPath,
                    AssetFormatLabel = rageInfo.FormatDisplayLabel,
                    CanonicalModelStem = rageInfo.CanonicalModelStem,
                    ResolutionSource = "AI README Analysis",
                    TargetRelativePath = safeOuter,
                    RpfContainerRelativePath = safeOuter,
                    RpfInternalPath = Rpf7Archive.NormalizeInternalPath(raw.RpfInternalPath),
                    UsesSafeModsFolder = true,
                    Description = string.IsNullOrWhiteSpace(raw.Description)
                        ? $"Inject '{srcEntry.RelativePath}' into '{safeOuter}' -> '{raw.RpfInternalPath}'"
                        : raw.Description
                });
            }
            else if (string.Equals(raw.Operation, "CopyFileToModsOrRoot", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(raw.TargetRelativePath))
                {
                    continue;
                }

                string safeTarget = PathSafetyGuard.EnsureSafeModsPath(raw.TargetRelativePath);
                if (PathSafetyGuard.IsProtectedVanillaRootFile(safeTarget))
                {
                    warnings.Add($"Blocked AI step attempting to overwrite protected vanilla file '{safeTarget}'.");
                    continue;
                }

                valid.Add(new ModInstallStep
                {
                    Operation = InstallOperationKind.CopyFileToModsOrRoot,
                    SourceRelativePath = srcEntry.RelativePath,
                    SourceFullPath = srcEntry.FullStagingPath,
                    AssetFormatLabel = rageInfo.FormatDisplayLabel,
                    CanonicalModelStem = rageInfo.CanonicalModelStem,
                    ResolutionSource = "AI README Analysis",
                    TargetRelativePath = safeTarget,
                    UsesSafeModsFolder = safeTarget.StartsWith("mods/", StringComparison.OrdinalIgnoreCase),
                    Description = string.IsNullOrWhiteSpace(raw.Description)
                        ? $"Deploy '{srcEntry.RelativePath}' -> '{safeTarget}'"
                        : raw.Description
                });
            }
        }

        return valid;
    }

    private static List<ModInstallStep> MergeStepsPreferringVerifiedPaths(
        List<ModInstallStep> llmSteps,
        List<ModInstallStep> deterministicSteps)
    {
        var detBySource = deterministicSteps
            .Where(s => !string.IsNullOrEmpty(s.SourceRelativePath))
            .ToDictionary(s => s.SourceRelativePath, StringComparer.OrdinalIgnoreCase);

        var merged = new List<ModInstallStep>();
        var coveredSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var llmStep in llmSteps)
        {
            if (!string.IsNullOrEmpty(llmStep.SourceRelativePath) &&
                detBySource.TryGetValue(llmStep.SourceRelativePath, out var detStep) &&
                detStep.ResolutionSource == "Mod Folder Structure")
            {
                // Explicit .rpf folder structure on disk always beats LLM text inference
                merged.Add(detStep);
                coveredSources.Add(detStep.SourceRelativePath);
            }
            else
            {
                merged.Add(llmStep);
                if (!string.IsNullOrEmpty(llmStep.SourceRelativePath))
                {
                    coveredSources.Add(llmStep.SourceRelativePath);
                }
            }
        }

        bool hasDlcReg = merged.Any(s => s.Operation == InstallOperationKind.RegisterDlcListEntry);
        foreach (var det in deterministicSteps)
        {
            if (det.Operation == InstallOperationKind.RegisterDlcListEntry && !hasDlcReg)
            {
                merged.Add(det);
            }
            else if (!string.IsNullOrEmpty(det.SourceRelativePath) && !coveredSources.Contains(det.SourceRelativePath))
            {
                merged.Add(det);
            }
        }

        return merged;
    }

    private sealed record DeterministicPlanDraft(
        string ModName,
        string Version,
        string Author,
        double Confidence,
        string Reasoning,
        List<ModInstallStep> Steps);

    private sealed record ReadmeRpfContextEntry(
        string RawNormalizedRpfPath,
        string ContextWindowText);

    private static DeterministicPlanDraft BuildDeterministicPlan(
        string fallbackModName,
        string readmeText,
        IReadOnlyList<ExtractedFileEntry> files,
        GameRpfSearchService rpfIndex,
        List<string> warnings)
    {
        var steps = new List<ModInstallStep>();
        var reasoningParts = new List<string>();
        double confidence = 0.95;

        string author = ExtractAuthorFromReadme(readmeText) ?? "Unknown";
        string version = ExtractModVersionFromReadme(readmeText, fallbackModName) ?? "1.0";
        var readmeContextPaths = ExtractContextualRpfPathsFromReadme(readmeText);
        var readmeFlatPaths = readmeContextPaths.Select(c => c.RawNormalizedRpfPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var readmeDlcEntries = ExtractDlcEntriesFromReadme(readmeText);
        bool readmeMentionsScriptsFolder = ScriptsFolderHintRegex().IsMatch(readmeText);

        // Parse .meta / .xml files for <modelName> <-> <txdName> relationships!
        var txdToModelStemMap = ExtractTxdToModelStemLinksFromMeta(files);

        var handledSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var registeredDlcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Add-On DLC packs (dlc.rpf) vs patchday replacement dlc.rpf
        foreach (var dlcFile in files.Where(f => string.Equals(Path.GetFileName(f.RelativePath), "dlc.rpf", StringComparison.OrdinalIgnoreCase)))
        {
            var hdr = Rpf7Archive.InspectHeader(dlcFile.FullStagingPath);
            if (!hdr.IsValidRpf7)
            {
                warnings.Add($"Skipped '{dlcFile.RelativePath}': file is named dlc.rpf but does not have a valid RPF7 (0x52504637) header.");
                handledSources.Add(dlcFile.RelativePath);
                continue;
            }

            // If packaged directly as update/x64/dlcpacks/patchdayXXng/dlc.rpf, copy to mods/... without adding duplicate patchday DLC registration
            if (dlcFile.RelativePath.Contains("patchday", StringComparison.OrdinalIgnoreCase))
            {
                string safePatchdayRel = PathSafetyGuard.EnsureSafeModsPath(dlcFile.RelativePath);
                if (!safePatchdayRel.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
                {
                    safePatchdayRel = "mods/" + safePatchdayRel;
                }
                steps.Add(new ModInstallStep
                {
                    Operation = InstallOperationKind.CopyFileToModsOrRoot,
                    SourceRelativePath = dlcFile.RelativePath,
                    SourceFullPath = dlcFile.FullStagingPath,
                    AssetFormatLabel = "RPF7 Patchday Container (dlc.rpf)",
                    ResolutionSource = "Mod Folder Structure",
                    TargetRelativePath = safePatchdayRel,
                    UsesSafeModsFolder = true,
                    Description = $"Deploy replacement RPF7 archive to '{safePatchdayRel}'"
                });
                handledSources.Add(dlcFile.RelativePath);
                continue;
            }

            string? dlcPackName = InferAddOnDlcFolderName(dlcFile, readmeDlcEntries, fallbackModName);
            if (string.IsNullOrWhiteSpace(dlcPackName))
            {
                continue;
            }

            string targetRel = $"mods/update/x64/dlcpacks/{dlcPackName}/dlc.rpf";
            steps.Add(new ModInstallStep
            {
                Operation = InstallOperationKind.CopyFileToModsOrRoot,
                SourceRelativePath = dlcFile.RelativePath,
                SourceFullPath = dlcFile.FullStagingPath,
                AssetFormatLabel = "Add-On RPF7 Container (dlc.rpf)",
                ResolutionSource = "DLC Pack Metadata / README",
                TargetRelativePath = targetRel,
                UsesSafeModsFolder = true,
                Description = $"Install Add-On RPF7 archive ({hdr.EncryptionKind}) to '{targetRel}'"
            });
            handledSources.Add(dlcFile.RelativePath);

            string canonicalEntry = DlcListXmlService.NormalizeDlcEntry(dlcPackName);
            if (registeredDlcs.Add(canonicalEntry))
            {
                steps.Add(new ModInstallStep
                {
                    Operation = InstallOperationKind.RegisterDlcListEntry,
                    AssetFormatLabel = "DLC Registration (dlclist.xml)",
                    ResolutionSource = "Add-On DLC Requirement",
                    TargetRelativePath = "mods/update/update.rpf",
                    RpfContainerRelativePath = "mods/update/update.rpf",
                    RpfInternalPath = DlcListXmlService.RpfInternalDlcListPath,
                    DlcListEntry = canonicalEntry,
                    UsesSafeModsFolder = true,
                    Description = $"Safely register '{canonicalEntry}' in mods/update/update.rpf -> common/data/dlclist.xml"
                });
            }

            reasoningParts.Add($"Detected valid RPF7 Add-On DLC pack '{dlcPackName}' and scheduled deduplicated dlclist.xml registration.");
        }

        // 2. .asi plugins & companion config files
        var asiFiles = files.Where(f => f.Extension == ".asi" && !handledSources.Contains(f.RelativePath)).ToList();
        var asiDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var asiBaseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asi in asiFiles)
        {
            string fileName = Path.GetFileName(asi.RelativePath);
            steps.Add(new ModInstallStep
            {
                Operation = InstallOperationKind.CopyFileToModsOrRoot,
                SourceRelativePath = asi.RelativePath,
                SourceFullPath = asi.FullStagingPath,
                AssetFormatLabel = "ASI Native Plugin (.asi)",
                ResolutionSource = "ASI Plugin Convention",
                TargetRelativePath = fileName,
                UsesSafeModsFolder = false,
                Description = $"Install ASI plugin '{fileName}' to GTA V root folder"
            });
            handledSources.Add(asi.RelativePath);
            asiDirs.Add(Path.GetDirectoryName(asi.RelativePath)?.Replace('\\', '/') ?? string.Empty);
            asiBaseNames.Add(Path.GetFileNameWithoutExtension(fileName));
        }

        if (asiFiles.Count > 0)
        {
            reasoningParts.Add($"Detected {asiFiles.Count} ScriptHookV ASI plugin(s) targeting GTA V root.");
            foreach (var cfg in files.Where(f => !handledSources.Contains(f.RelativePath) && f.Extension is ".ini" or ".toml" or ".cfg"))
            {
                string cfgDir = Path.GetDirectoryName(cfg.RelativePath)?.Replace('\\', '/') ?? string.Empty;
                string cfgBase = Path.GetFileNameWithoutExtension(cfg.RelativePath);
                if (asiDirs.Contains(cfgDir) || asiBaseNames.Contains(cfgBase))
                {
                    string fileName = Path.GetFileName(cfg.RelativePath);
                    steps.Add(new ModInstallStep
                    {
                        Operation = InstallOperationKind.CopyFileToModsOrRoot,
                        SourceRelativePath = cfg.RelativePath,
                        SourceFullPath = cfg.FullStagingPath,
                        AssetFormatLabel = $"Plugin Config ({cfg.Extension})",
                        ResolutionSource = "Companion ASI Config",
                        TargetRelativePath = fileName,
                        UsesSafeModsFolder = false,
                        Description = $"Install companion ASI configuration '{fileName}' to GTA V root"
                    });
                    handledSources.Add(cfg.RelativePath);
                }
            }
        }

        // 3. .dll, .cs, .vb mods (ScriptHookVDotNet scripts vs native helper DLLs)
        var shvdnDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dllFile in files.Where(f => f.Extension == ".dll" && !handledSources.Contains(f.RelativePath)))
        {
            string fileName = Path.GetFileName(dllFile.RelativePath);
            if (PathSafetyGuard.IsProtectedVanillaRootFile(fileName))
            {
                warnings.Add($"Blocked '{dllFile.RelativePath}' because '{fileName}' is a protected vanilla Rockstar DLL.");
                handledSources.Add(dllFile.RelativePath);
                continue;
            }

            var pe = PeBinaryInspector.Inspect(dllFile.FullStagingPath);
            bool inScriptsSubfolder = dllFile.RelativePath.Contains("scripts/", StringComparison.OrdinalIgnoreCase);
            bool routeToScripts = pe.IsManagedDotNetAssembly || pe.ReferencesScriptHookVDotNet || inScriptsSubfolder || readmeMentionsScriptsFolder;

            if (string.Equals(fileName, "ScriptHookV.dll", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, "dinput8.dll", StringComparison.OrdinalIgnoreCase))
            {
                routeToScripts = false;
            }

            string targetRel = routeToScripts
                ? ExtractSubpathFromMarker(dllFile.RelativePath, "scripts/") ?? $"scripts/{fileName}"
                : fileName;

            steps.Add(new ModInstallStep
            {
                Operation = InstallOperationKind.CopyFileToModsOrRoot,
                SourceRelativePath = dllFile.RelativePath,
                SourceFullPath = dllFile.FullStagingPath,
                AssetFormatLabel = routeToScripts ? ".NET Script (.dll)" : "Native Library (.dll)",
                ResolutionSource = pe.IsManagedDotNetAssembly ? "PE CLR Header Inspection" : "README / Folder Layout",
                TargetRelativePath = targetRel,
                UsesSafeModsFolder = false,
                Description = routeToScripts
                    ? $"Install .NET ScriptHookVDotNet assembly '{fileName}' to '{targetRel}'"
                    : $"Install native library '{fileName}' to GTA V root"
            });
            handledSources.Add(dllFile.RelativePath);

            if (routeToScripts)
            {
                shvdnDirs.Add(Path.GetDirectoryName(dllFile.RelativePath)?.Replace('\\', '/') ?? string.Empty);
                reasoningParts.Add($"Inspected PE/CLR metadata of '{fileName}' -> verified managed ScriptHookVDotNet script targeting 'scripts/'.");
            }
        }

        foreach (var scriptCompanion in files.Where(f =>
                     !handledSources.Contains(f.RelativePath) &&
                     (f.Extension is ".cs" or ".vb" ||
                      f.RelativePath.Contains("scripts/", StringComparison.OrdinalIgnoreCase) ||
                      (f.Extension is ".ini" && shvdnDirs.Contains(Path.GetDirectoryName(f.RelativePath)?.Replace('\\', '/') ?? string.Empty)))))
        {
            string fileName = Path.GetFileName(scriptCompanion.RelativePath);
            string targetRel = ExtractSubpathFromMarker(scriptCompanion.RelativePath, "scripts/") ?? $"scripts/{fileName}";
            steps.Add(new ModInstallStep
            {
                Operation = InstallOperationKind.CopyFileToModsOrRoot,
                SourceRelativePath = scriptCompanion.RelativePath,
                SourceFullPath = scriptCompanion.FullStagingPath,
                AssetFormatLabel = $"Script Resource ({scriptCompanion.Extension})",
                ResolutionSource = "Scripts Folder Layout",
                TargetRelativePath = targetRel,
                UsesSafeModsFolder = false,
                Description = $"Install script resource '{fileName}' to '{targetRel}'"
            });
            handledSources.Add(scriptCompanion.RelativePath);
        }

        // 4. RAGE Model & Texture Dictionary Replacements (.yft, _hi.yft, .ytd, +hi.ytd, .ydd, .ymt, .ymap, .ytyp, .awc)
        var rageCandidates = files
            .Where(f => !handledSources.Contains(f.RelativePath) && RageAssetInspector.IsModelOrTextureAssetExtension(f.Extension))
            .Select(f => (Entry: f, Analysis: RageAssetInspector.Inspect(f.FullStagingPath, f.RelativePath)))
            .ToList();

        // Validate RSC7 binary header on .yft / .ytd / .ydd files so corrupt non-RAGE files are flagged clearly
        foreach (var (_, analysis) in rageCandidates)
        {
            if (analysis.Extension is ".yft" or ".ytd" or ".ydd" && !analysis.HasValidRsc7Header)
            {
                warnings.Add(
                    $"Warning: '{analysis.FileName}' does not start with a standard RSC7 (0x37435352) resource header. Verify the file is not corrupted before installing.");
            }
        }

        // Pass 1: Resolve each asset individually from (1) Folder Structure, (2) Contextual README, or (3) Game RPF7 Archive Scan
        var resolvedByEntryPath = new Dictionary<string, (string OuterRpf, string InnerPath, string SourceReason)>(StringComparer.OrdinalIgnoreCase);
        var ambiguousCandidatesByEntryPath = new Dictionary<string, (string Reason, List<RpfTargetCandidate> Candidates)>(StringComparer.OrdinalIgnoreCase);

        // Identify all non-data RPF directory paths in README
        var nonDataReadmeDirs = readmeContextPaths
            .Select(c => StripKnownAssetFileName(c.RawNormalizedRpfPath))
            .Where(p => !string.IsNullOrWhiteSpace(p) &&
                        !p.EndsWith("update.rpf", StringComparison.OrdinalIgnoreCase) &&
                        !p.Contains("/data", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var (entry, analysis) in rageCandidates)
        {
            string linkedStem = txdToModelStemMap.TryGetValue(analysis.CanonicalModelStem, out var mappedModel)
                ? mappedModel
                : analysis.CanonicalModelStem;

            // Tier 1: Explicit .rpf or patchday folder structure inside the imported mod
            var folderResolved = TryResolveFromModFolderStructure(entry.RelativePath);
            if (folderResolved is not null)
            {
                resolvedByEntryPath[entry.RelativePath] = (folderResolved.Value.OuterRpfModsPath, folderResolved.Value.InternalPath, "Mod Folder Structure");
                continue;
            }

            // Tier 2: Explicit or contextual README RPF match for this specific file or model stem
            var readmeMatch = TryResolveFromContextualReadme(entry, analysis, linkedStem, readmeContextPaths, nonDataReadmeDirs, rageCandidates.Count);
            if (readmeMatch.Resolved is not null)
            {
                resolvedByEntryPath[entry.RelativePath] = readmeMatch.Resolved.Value;
                continue;
            }
            if (readmeMatch.AmbiguousCandidates is { Count: > 0 })
            {
                ambiguousCandidatesByEntryPath[entry.RelativePath] = (readmeMatch.AmbiguityReason!, readmeMatch.AmbiguousCandidates);
                continue;
            }

            // Tier 3: Live Game RPF7 Archive Lookup (checks exact filename, e.g. police2.yft / police2_hi.yft / police2.ytd)
            if (rpfIndex.HasAnyIndexedEntries)
            {
                var (bestMatch, allMatches, isAmbiguous) = rpfIndex.FindLocationsForFileName(analysis.FileName);
                if (bestMatch is not null && !isAmbiguous)
                {
                    resolvedByEntryPath[entry.RelativePath] = (bestMatch.SafeModsOuterRpfPath, bestMatch.InternalPath, "Game RPF7 Archive Lookup");
                    continue;
                }
                if (isAmbiguous && allMatches.Count > 1)
                {
                    var cands = allMatches
                        .Select(m => new RpfTargetCandidate(m.SafeModsOuterRpfPath, m.InternalPath, "Matched filename in game RPF7 archive"))
                        .ToList();
                    ambiguousCandidatesByEntryPath[entry.RelativePath] = (
                        $"'{analysis.FileName}' exists in {allMatches.Count} unrelated RPF7 archives; select which one to replace.",
                        cands);
                }
            }
        }

        // Pass 2: Companion Model Stem Pairing!
        // Pair <stem>_hi.yft, <stem>.ytd, <stem>+hi.ytd, or metadata-linked <txdName>.ytd with <stem>.yft's verified archive directory!
        var resolvedDirByStemAndFolder = new Dictionary<string, (string OuterRpf, string InnerDir, string StemName)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (entry, analysis) in rageCandidates)
        {
            if (!resolvedByEntryPath.TryGetValue(entry.RelativePath, out var res))
            {
                continue;
            }

            string innerDir = Rpf7Archive.NormalizeInternalPath(Path.GetDirectoryName(res.InnerPath) ?? string.Empty);
            string srcParent = PathSafetyGuard.NormalizeRelativePath(Path.GetDirectoryName(entry.RelativePath) ?? string.Empty);

            // Key by (source subfolder + canonical stem) first, and also fallback by canonical stem if unambiguous across package
            resolvedDirByStemAndFolder[$"{srcParent}::{analysis.CanonicalModelStem}"] = (res.OuterRpf, innerDir, analysis.CanonicalModelStem);
            if (!resolvedDirByStemAndFolder.ContainsKey($"*::*{analysis.CanonicalModelStem}"))
            {
                resolvedDirByStemAndFolder[$"*::*{analysis.CanonicalModelStem}"] = (res.OuterRpf, innerDir, analysis.CanonicalModelStem);
            }
        }

        foreach (var (entry, analysis) in rageCandidates)
        {
            if (resolvedByEntryPath.ContainsKey(entry.RelativePath))
            {
                continue;
            }

            string linkedStem = txdToModelStemMap.TryGetValue(analysis.CanonicalModelStem, out var mappedModel)
                ? mappedModel
                : analysis.CanonicalModelStem;
            string srcParent = PathSafetyGuard.NormalizeRelativePath(Path.GetDirectoryName(entry.RelativePath) ?? string.Empty);

            if (resolvedDirByStemAndFolder.TryGetValue($"{srcParent}::{linkedStem}", out var companion) ||
                resolvedDirByStemAndFolder.TryGetValue($"*::*{linkedStem}", out companion))
            {
                string innerPath = string.IsNullOrEmpty(companion.InnerDir)
                    ? analysis.FileName
                    : $"{companion.InnerDir}/{analysis.FileName}";
                string viaMeta = !string.Equals(linkedStem, analysis.CanonicalModelStem, StringComparison.OrdinalIgnoreCase)
                    ? $"Metadata (<txdName> linked to '{linkedStem}')"
                    : $"Paired with '{companion.StemName}' Model Set";

                resolvedByEntryPath[entry.RelativePath] = (companion.OuterRpf, innerPath, viaMeta);
                ambiguousCandidatesByEntryPath.Remove(entry.RelativePath);
                continue;
            }

            // Also check if <stem>.yft exists in GameRpfSearchService even when <stem>+hi.ytd or <stem>_hi.yft wasn't indexed yet
            if (rpfIndex.HasAnyIndexedEntries && !ambiguousCandidatesByEntryPath.ContainsKey(entry.RelativePath))
            {
                var (stemMatch, _, stemAmbiguous) = rpfIndex.FindLocationsForFileName($"{linkedStem}.yft");
                if (stemMatch is not null && !stemAmbiguous)
                {
                    string stemDir = Rpf7Archive.NormalizeInternalPath(Path.GetDirectoryName(stemMatch.InternalPath) ?? string.Empty);
                    string innerPath = string.IsNullOrEmpty(stemDir) ? analysis.FileName : $"{stemDir}/{analysis.FileName}";
                    resolvedByEntryPath[entry.RelativePath] = (stemMatch.SafeModsOuterRpfPath, innerPath, $"Game RPF7 Lookup ('{linkedStem}.yft' directory)");
                }
            }
        }

        // Emit ModInstallSteps for all RAGE model & texture assets (resolved OR explicitly marked RequiresUserTargetSelection — NEVER guessing!)
        int resolvedAssetCount = 0;
        int uncertainAssetCount = 0;

        foreach (var (entry, analysis) in rageCandidates)
        {
            handledSources.Add(entry.RelativePath);
            string linkedStem = txdToModelStemMap.TryGetValue(analysis.CanonicalModelStem, out var mappedModel)
                ? mappedModel
                : analysis.CanonicalModelStem;

            if (resolvedByEntryPath.TryGetValue(entry.RelativePath, out var resolved))
            {
                resolvedAssetCount++;
                steps.Add(new ModInstallStep
                {
                    Operation = InstallOperationKind.InjectIntoRpfArchive,
                    SourceRelativePath = entry.RelativePath,
                    SourceFullPath = entry.FullStagingPath,
                    AssetFormatLabel = analysis.FormatDisplayLabel,
                    CanonicalModelStem = linkedStem,
                    ResolutionSource = resolved.SourceReason,
                    RequiresUserTargetSelection = false,
                    TargetRelativePath = resolved.OuterRpf,
                    RpfContainerRelativePath = resolved.OuterRpf,
                    RpfInternalPath = resolved.InnerPath,
                    UsesSafeModsFolder = true,
                    Description = $"Inject {analysis.FormatDisplayLabel} '{analysis.FileName}' into '{resolved.OuterRpf}' -> '{resolved.InnerPath}'"
                });
            }
            else
            {
                uncertainAssetCount++;
                confidence = Math.Min(confidence, 0.55);

                var (reason, candidates) = ambiguousCandidatesByEntryPath.TryGetValue(entry.RelativePath, out var amb)
                    ? amb
                    : BuildDefaultUncertaintyInfo(analysis, nonDataReadmeDirs);

                steps.Add(new ModInstallStep
                {
                    Operation = InstallOperationKind.InjectIntoRpfArchive,
                    SourceRelativePath = entry.RelativePath,
                    SourceFullPath = entry.FullStagingPath,
                    AssetFormatLabel = analysis.FormatDisplayLabel,
                    CanonicalModelStem = linkedStem,
                    ResolutionSource = "Uncertain — Ask User",
                    RequiresUserTargetSelection = true,
                    UncertaintyReason = reason,
                    CandidateRpfTargets = candidates,
                    TargetRelativePath = string.Empty,
                    RpfContainerRelativePath = string.Empty,
                    RpfInternalPath = string.Empty,
                    UsesSafeModsFolder = true,
                    Description = $"[Target Needed] Choose RPF7 archive destination for '{analysis.FileName}' ({reason})"
                });
            }
        }

        if (resolvedAssetCount > 0)
        {
            var distinctArchives = steps
                .Where(s => s.Operation == InstallOperationKind.InjectIntoRpfArchive && !s.RequiresUserTargetSelection)
                .Select(s => s.RpfContainerRelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            reasoningParts.Add(
                $"Mapped {resolvedAssetCount} RAGE model/texture asset(s) (.yft/_hi.yft/.ytd) across {distinctArchives.Count} safe 'mods/' RPF7 archive(s).");
        }

        if (uncertainAssetCount > 0)
        {
            reasoningParts.Add(
                $"Flagged {uncertainAssetCount} RAGE asset(s) with uncertain RPF destination for explicit user confirmation rather than guessing.");
        }

        // 5. Data / Meta / Config files (.xml, .meta, .dat) & standalone .rpf archives
        foreach (var asset in files.Where(f => !handledSources.Contains(f.RelativePath)))
        {
            if (DocumentationExtensions.Contains(asset.Extension) || IgnoredMediaExtensions.Contains(asset.Extension))
            {
                continue;
            }

            // Skip FiveM server-only subfolders in dual SP/FiveM downloads
            if (IsInSubfolderNamed(asset.RelativePath, "fivem"))
            {
                continue;
            }

            if (asset.Extension is ".xml" or ".meta" or ".dat")
            {
                string fileName = Path.GetFileName(asset.RelativePath);
                if (string.Equals(fileName, "dlclist.xml", StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add(
                        "Ignored full raw dlclist.xml replacement inside mod archive to avoid wiping existing DLC packs; GTA Mod Assistant merges DLC entries safely instead.");
                    continue;
                }

                // If an Add-On DLC pack (dlc.rpf) is already scheduled and this loose .meta sits in an Optional/Extras folder,
                // do NOT overwrite vanilla update.rpf's master vehicles.meta/handling.meta!
                bool hasExplicitRpfPath = SplitOuterAndInnerRpf(asset.RelativePath) is not null;
                if (registeredDlcs.Count > 0 && !hasExplicitRpfPath && IsInSubfolderNamed(asset.RelativePath, "optional", "extra", "extras"))
                {
                    warnings.Add(
                        $"Left optional variant '{asset.RelativePath}' untouched so your Add-On DLC installs cleanly without overwriting vanilla update.rpf.");
                    handledSources.Add(asset.RelativePath);
                    continue;
                }

                // Protect GTA V's 700-car master vehicles.meta / handling.meta from being wiped by a 1-car snippet!
                bool isSingleCarMetaSnippet = !hasExplicitRpfPath &&
                                              !readmeFlatPaths.Any(p => p.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase)) &&
                                              IsTinyVehicleMetaSnippet(asset.FullStagingPath, fileName);
                if (isSingleCarMetaSnippet && steps.Count > 0)
                {
                    warnings.Add(
                        $"Unchecked single-vehicle snippet '{asset.RelativePath}' by default so it does not overwrite GTA V's full '{fileName}' master list.");
                }

                var dataTarget = ResolveRpfTargetForDataFile(asset, readmeFlatPaths);
                steps.Add(new ModInstallStep
                {
                    Operation = InstallOperationKind.InjectIntoRpfArchive,
                    SourceRelativePath = asset.RelativePath,
                    SourceFullPath = asset.FullStagingPath,
                    AssetFormatLabel = $"GTA Data/Config ({asset.Extension})",
                    ResolutionSource = "Data Schema / README",
                    TargetRelativePath = dataTarget.OuterRpfModsPath,
                    RpfContainerRelativePath = dataTarget.OuterRpfModsPath,
                    RpfInternalPath = dataTarget.InternalPath,
                    UsesSafeModsFolder = true,
                    IsSelectedForInstall = !(isSingleCarMetaSnippet && steps.Count > 0),
                    Description = $"Inject validated data file '{fileName}' into '{dataTarget.OuterRpfModsPath}' -> '{dataTarget.InternalPath}'"
                });
                handledSources.Add(asset.RelativePath);
                reasoningParts.Add($"Validated '{fileName}' structure and mapped to '{dataTarget.OuterRpfModsPath}' -> '{dataTarget.InternalPath}'.");
            }
            else if (asset.Extension == ".rpf")
            {
                var hdr = Rpf7Archive.InspectHeader(asset.FullStagingPath);
                if (hdr.IsValidRpf7)
                {
                    string safeTarget = PathSafetyGuard.EnsureSafeModsPath(asset.RelativePath);
                    if (!safeTarget.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
                    {
                        safeTarget = "mods/" + safeTarget;
                    }
                    steps.Add(new ModInstallStep
                    {
                        Operation = InstallOperationKind.CopyFileToModsOrRoot,
                        SourceRelativePath = asset.RelativePath,
                        SourceFullPath = asset.FullStagingPath,
                        AssetFormatLabel = "RPF7 Archive (.rpf)",
                        ResolutionSource = "Mod Folder Structure",
                        TargetRelativePath = safeTarget,
                        UsesSafeModsFolder = true,
                        Description = $"Deploy RPF7 archive '{asset.RelativePath}' to '{safeTarget}'"
                    });
                    handledSources.Add(asset.RelativePath);
                }
            }
        }

        string summary = reasoningParts.Count > 0
            ? string.Join(" ", reasoningParts.Distinct())
            : "Analyzed mod directory structure and README instructions; no installable GTA V files were detected.";

        return new DeterministicPlanDraft(fallbackModName, version, author, confidence, summary, steps);
    }

    private static (string OuterRpfModsPath, string InternalPath)? TryResolveFromModFolderStructure(string relativePath)
    {
        var directSplit = SplitOuterAndInnerRpf(relativePath);
        if (directSplit is not null)
        {
            return directSplit;
        }

        // Also recognize shorthand DLC folder hierarchy e.g. "patchday27ng/vehicles.rpf/police2.yft" or "patchday27ng/police2.yft"
        string norm = PathSafetyGuard.NormalizeRelativePath(relativePath);
        var segments = norm.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (PatchdaySegmentRegex().IsMatch(segments[i]))
            {
                string pdName = segments[i];
                string fileName = segments[^1];
                string remainderBetween = string.Join("/", segments.Skip(i + 1).Take(segments.Length - i - 2));

                if (remainderBetween.Contains(".rpf", StringComparison.OrdinalIgnoreCase))
                {
                    string inner = remainderBetween.StartsWith("x64/", StringComparison.OrdinalIgnoreCase)
                        ? $"{remainderBetween}/{fileName}"
                        : $"x64/levels/gta5/{remainderBetween}/{fileName}";
                    return ($"mods/update/x64/dlcpacks/{pdName}/dlc.rpf", Rpf7Archive.NormalizeInternalPath(inner));
                }
            }
        }

        return null;
    }

    private static (
        (string OuterRpf, string InnerPath, string SourceReason)? Resolved,
        string? AmbiguityReason,
        List<RpfTargetCandidate>? AmbiguousCandidates)
        TryResolveFromContextualReadme(
            ExtractedFileEntry entry,
            RageAssetAnalysis analysis,
            string linkedStem,
            IReadOnlyList<ReadmeRpfContextEntry> readmeContextPaths,
            IReadOnlyList<string> nonDataReadmeDirs,
            int totalRageAssetsInMod)
    {
        if (readmeContextPaths.Count == 0)
        {
            return (null, null, null);
        }

        string fileName = analysis.FileName;
        string parentFolderName = Path.GetFileName(Path.GetDirectoryName(entry.RelativePath.Replace('\\', '/')) ?? string.Empty);

        // 1. Exact full RPF path in README ending with this exact file name
        foreach (var ctx in readmeContextPaths)
        {
            if (ctx.RawNormalizedRpfPath.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase))
            {
                var split = SplitOuterAndInnerRpf(ctx.RawNormalizedRpfPath);
                if (split is not null)
                {
                    return ((split.Value.OuterRpfModsPath, split.Value.InternalPath, "README Exact File Path"), null, null);
                }
            }
        }

        // 2. Score README RPF directories by local text context mentioning this exact filename, canonical stem, or parent subfolder!
        var scoredDirs = new List<(string OuterRpf, string InnerDir, int Score)>();
        foreach (var ctx in readmeContextPaths)
        {
            string dirOnly = StripKnownAssetFileName(ctx.RawNormalizedRpfPath);
            if (dirOnly.EndsWith("update.rpf", StringComparison.OrdinalIgnoreCase) ||
                dirOnly.Contains("/data", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var splitDir = SplitOuterAndInnerRpfForDirectory(dirOnly);
            if (splitDir is null)
            {
                continue;
            }

            int score = 0;
            string win = ctx.ContextWindowText;
            if (ContainsWholeToken(win, fileName))
            {
                score += 100;
            }
            if (linkedStem.Length >= 3 && ContainsWholeToken(win, linkedStem))
            {
                score += 65;
            }
            if (!string.IsNullOrWhiteSpace(parentFolderName) &&
                parentFolderName.Length >= 3 &&
                !IsGenericWrapperFolderName(parentFolderName) &&
                ContainsWholeToken(win, parentFolderName))
            {
                score += 40;
            }

            // Domain compatibility bonus ONLY when there is already a textual or domain-specific RPF match
            if (analysis.DomainHint == RageAssetDomainHint.SharedVehicleTexture &&
                splitDir.Value.OuterRpfModsPath.Equals("mods/x64e.rpf", StringComparison.OrdinalIgnoreCase) &&
                splitDir.Value.InternalDir.Contains("vehicles.rpf", StringComparison.OrdinalIgnoreCase))
            {
                score += 55;
            }
            else if (analysis.DomainHint == RageAssetDomainHint.Weapon &&
                     splitDir.Value.InternalDir.Contains("weapons.rpf", StringComparison.OrdinalIgnoreCase))
            {
                score += 50;
            }
            else if (analysis.DomainHint == RageAssetDomainHint.PedOrComponent &&
                     splitDir.Value.InternalDir.Contains("ped", StringComparison.OrdinalIgnoreCase))
            {
                score += 50;
            }

            if (score > 0)
            {
                scoredDirs.Add((splitDir.Value.OuterRpfModsPath, splitDir.Value.InternalDir, score));
            }
        }

        if (scoredDirs.Count > 0)
        {
            var grouped = scoredDirs
                .GroupBy(s => $"{s.OuterRpf}::{s.InnerDir}", StringComparer.OrdinalIgnoreCase)
                .Select(g => (First: g.First(), MaxScore: g.Max(x => x.Score)))
                .OrderByDescending(x => x.MaxScore)
                .ToList();

            if (grouped.Count == 1 || grouped[0].MaxScore > grouped[1].MaxScore)
            {
                var winner = grouped[0].First;
                string inner = string.IsNullOrEmpty(winner.InnerDir) ? fileName : $"{winner.InnerDir}/{fileName}";
                return ((winner.OuterRpf, inner, "README Instruction"), null, null);
            }

            // Tied score across multiple distinct RPF paths -> ambiguous, ask user!
            var tiedCands = grouped
                .Select(g => new RpfTargetCandidate(g.First.OuterRpf, $"{g.First.InnerDir}/{fileName}".TrimStart('/'), "Mentioned in README"))
                .ToList();
            return (null, $"README mentions multiple possible RPF archives for '{fileName}'.", tiedCands);
        }

        // 3. If the entire README specifies ONLY ONE asset RPF directory, check if domain is compatible
        if (nonDataReadmeDirs.Count == 1)
        {
            var singleSplit = SplitOuterAndInnerRpfForDirectory(nonDataReadmeDirs[0]);
            if (singleSplit is not null && IsArchiveDomainCompatible(analysis.DomainHint, singleSplit.Value.InternalDir))
            {
                string inner = string.IsNullOrEmpty(singleSplit.Value.InternalDir)
                    ? fileName
                    : $"{singleSplit.Value.InternalDir}/{fileName}";
                return ((singleSplit.Value.OuterRpfModsPath, inner, "README Target Archive"), null, null);
            }
        }

        // 4. Multiple distinct asset RPF directories in README and this file wasn't matched to any specific one -> ask user!
        if (nonDataReadmeDirs.Count > 1)
        {
            var candidates = new List<RpfTargetCandidate>();
            foreach (var rawDir in nonDataReadmeDirs)
            {
                var sp = SplitOuterAndInnerRpfForDirectory(rawDir);
                if (sp is not null)
                {
                    string inner = string.IsNullOrEmpty(sp.Value.InternalDir) ? fileName : $"{sp.Value.InternalDir}/{fileName}";
                    candidates.Add(new RpfTargetCandidate(sp.Value.OuterRpfModsPath, inner, "Candidate archive from README"));
                }
            }

            if (candidates.Count > 0)
            {
                return (
                    null,
                    $"README lists {candidates.Count} different RPF archives without specifying where '{fileName}' belongs.",
                    candidates);
            }
        }

        return (null, null, null);
    }

    private static bool IsArchiveDomainCompatible(RageAssetDomainHint hint, string internalDir)
    {
        if (hint == RageAssetDomainHint.Weapon && internalDir.Contains("vehicles.rpf", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (hint == RageAssetDomainHint.PedOrComponent && internalDir.Contains("vehicles.rpf", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return true;
    }

    private static (string Reason, List<RpfTargetCandidate> Candidates) BuildDefaultUncertaintyInfo(
        RageAssetAnalysis analysis,
        IReadOnlyList<string> readmeDirs)
    {
        var list = new List<RpfTargetCandidate>();
        string fn = analysis.FileName;

        foreach (var rd in readmeDirs)
        {
            var sp = SplitOuterAndInnerRpfForDirectory(rd);
            if (sp is not null)
            {
                list.Add(new RpfTargetCandidate(
                    sp.Value.OuterRpfModsPath,
                    string.IsNullOrEmpty(sp.Value.InternalDir) ? fn : $"{sp.Value.InternalDir}/{fn}",
                    "Mentioned in mod README"));
            }
        }

        if (analysis.DomainHint == RageAssetDomainHint.Weapon)
        {
            list.Add(new RpfTargetCandidate("mods/update/x64/dlcpacks/patchday8ng/dlc.rpf", $"x64/models/cdimages/weapons.rpf/{fn}", "Common GTA V weapon override archive"));
            list.Add(new RpfTargetCandidate("mods/x64w.rpf", $"models/cdimages/weapons.rpf/{fn}", "Base weapon archive"));
        }
        else if (analysis.DomainHint == RageAssetDomainHint.PedOrComponent)
        {
            list.Add(new RpfTargetCandidate("mods/x64v.rpf", $"models/cdimages/streamedpeds_players.rpf/{fn}", "Player streamed peds archive"));
            list.Add(new RpfTargetCandidate("mods/x64e.rpf", $"models/cdimages/componentpeds_s_m_y.rpf/{fn}", "Component peds archive"));
        }
        else
        {
            list.Add(new RpfTargetCandidate("mods/update/x64/dlcpacks/patchday28ng/dlc.rpf", $"x64/levels/gta5/vehicles.rpf/{fn}", "Latest patchday vehicle archive"));
            list.Add(new RpfTargetCandidate("mods/x64e.rpf", $"levels/gta5/vehicles.rpf/{fn}", "Base GTA V vehicle archive"));
            list.Add(new RpfTargetCandidate("mods/update/update.rpf", $"x64/patch/data/cdimages/scaleform_generic.rpf/{fn}", "UI / HUD / Scaleform texture archive"));
        }

        string reason = $"No folder structure, metadata, README instruction, or game RPF match identified the exact archive for '{fn}'.";
        return (reason, list);
    }

    private static string BuildDeepFileInspectionDossier(
        IReadOnlyList<ExtractedFileEntry> files,
        GameRpfSearchService rpfIndex,
        out List<string> detectedSpawnNames)
    {
        var spawnSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        sb.AppendLine("=== AI DEEP FILE & BINARY INSPECTION DOSSIER ===");
        sb.AppendLine($"Total Staged Files: {files.Count}");

        foreach (var f in files.Take(60))
        {
            string fileName = Path.GetFileName(f.RelativePath);
            if (f.Extension == ".rpf")
            {
                var hdr = Rpf7Archive.InspectHeader(f.FullStagingPath);
                sb.AppendLine($"[RPF7 Archive] {f.RelativePath} ({f.SizeBytes:N0} B) -> Valid={hdr.IsValidRpf7}, Encryption={hdr.EncryptionKind}, Entries={hdr.EntryCount}");
                if (hdr.CanDirectEditWithoutNgKeys)
                {
                    try
                    {
                        var rpf = Rpf7Archive.Open(f.FullStagingPath);
                        var innerPaths = Rpf7Archive.ListEntryPathsFast(f.FullStagingPath, maxNestedDepth: 3);
                        foreach (var innerYft in innerPaths.Where(p => p.EndsWith(".yft", StringComparison.OrdinalIgnoreCase) && !p.EndsWith("_hi.yft", StringComparison.OrdinalIgnoreCase)))
                        {
                            string stem = RageAssetInspector.ExtractCanonicalStem(Path.GetFileName(innerYft));
                            if (!string.IsNullOrWhiteSpace(stem) && !stem.StartsWith("w_", StringComparison.OrdinalIgnoreCase))
                            {
                                spawnSet.Add(stem);
                            }
                        }

                        var setupEntry = rpf.ListEntries().FirstOrDefault(e =>
                            e.Name.Equals("setup2.xml", StringComparison.OrdinalIgnoreCase) ||
                            e.Name.Equals("content.xml", StringComparison.OrdinalIgnoreCase));
                        if (setupEntry is not null && !rpf.IsEntryIndividuallyEncrypted(setupEntry.FullInternalPath))
                        {
                            string xml = Encoding.UTF8.GetString(rpf.ExtractFile(setupEntry.FullInternalPath));
                            var doc = XDocument.Parse(xml);
                            string? devName = doc.Descendants("deviceName").FirstOrDefault()?.Value.Trim();
                            if (!string.IsNullOrWhiteSpace(devName))
                            {
                                sb.AppendLine($"  -> Internal {setupEntry.Name} deviceName: {devName}");
                                string cleanDev = devName.StartsWith("dlc_", StringComparison.OrdinalIgnoreCase) ? devName[4..] : devName;
                                if (spawnSet.Count == 0 && !string.IsNullOrWhiteSpace(cleanDev))
                                {
                                    spawnSet.Add(cleanDev);
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Ignore unreadable internal RPF entry during dossier scan
                    }
                }
            }
            else if (RageAssetInspector.IsModelOrTextureAssetExtension(f.Extension))
            {
                var info = RageAssetInspector.Inspect(f.FullStagingPath, f.RelativePath);
                string rpfLookupNote = "No indexed game RPF match";
                if (rpfIndex.HasAnyIndexedEntries)
                {
                    var (best, all, ambig) = rpfIndex.FindLocationsForFileName(info.FileName);
                    if (best is not null && !ambig)
                    {
                        rpfLookupNote = $"Matched in {best.SafeModsOuterRpfPath} -> {best.InternalPath}";
                    }
                    else if (ambig)
                    {
                        rpfLookupNote = $"Ambiguous across {all.Count} game RPFs";
                    }
                }

                if (info.Kind is RageAssetKind.FragmentModelYft or RageAssetKind.FragmentHighLodYft &&
                    !string.IsNullOrWhiteSpace(info.CanonicalModelStem))
                {
                    spawnSet.Add(info.CanonicalModelStem);
                }

                sb.AppendLine(
                    $"[RAGE Asset] {f.RelativePath} -> {info.FormatDisplayLabel}, RSC7={info.HasValidRsc7Header} (v{info.RscVersion}), Stem='{info.CanonicalModelStem}', GameLookup=({rpfLookupNote})");
            }
            else if (f.Extension is ".asi" or ".dll")
            {
                var pe = PeBinaryInspector.Inspect(f.FullStagingPath);
                sb.AppendLine(
                    $"[PE Binary] {f.RelativePath} -> ValidPE={pe.IsValidPe}, 64Bit={pe.Is64Bit}, ManagedDotNet={pe.IsManagedDotNetAssembly}, SHVDN={pe.ReferencesScriptHookVDotNet}");
            }
            else if (f.Extension is ".meta" or ".xml" && f.SizeBytes <= 5 * 1024 * 1024)
            {
                try
                {
                    var doc = XDocument.Load(f.FullStagingPath);
                    string rootName = doc.Root?.Name.LocalName ?? "Unknown";
                    var modelNames = doc.Descendants("modelName")
                        .Select(e => e.Value.Trim())
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(8)
                        .ToList();
                    foreach (var mn in modelNames)
                    {
                        spawnSet.Add(mn);
                    }
                    string modelsNote = modelNames.Count > 0 ? $", modelNames=[{string.Join(", ", modelNames)}]" : string.Empty;
                    sb.AppendLine($"[XML/Meta] {f.RelativePath} -> Root=<{rootName}>{modelsNote}");
                }
                catch
                {
                    sb.AppendLine($"[XML/Meta] {f.RelativePath} ({f.SizeBytes:N0} B)");
                }
            }
            else
            {
                sb.AppendLine($"[File] {f.RelativePath} ({f.SizeBytes:N0} B)");
            }
        }

        detectedSpawnNames = spawnSet.Take(8).ToList();
        if (detectedSpawnNames.Count > 0)
        {
            sb.AppendLine($"Detected Model / In-Game Spawn Identifier(s): {string.Join(", ", detectedSpawnNames)}");
        }

        return sb.ToString().Trim();
    }

    private static Dictionary<string, string> ExtractTxdToModelStemLinksFromMeta(IReadOnlyList<ExtractedFileEntry> files)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var metaFile in files.Where(f => f.Extension is ".meta" or ".xml" && f.SizeBytes <= 10 * 1024 * 1024))
        {
            try
            {
                var doc = XDocument.Load(metaFile.FullStagingPath);
                foreach (var item in doc.Descendants("Item"))
                {
                    string? modelName = item.Element("modelName")?.Value.Trim();
                    string? txdName = item.Element("txdName")?.Value.Trim();
                    if (!string.IsNullOrWhiteSpace(modelName) &&
                        !string.IsNullOrWhiteSpace(txdName) &&
                        !string.Equals(modelName, txdName, StringComparison.OrdinalIgnoreCase))
                    {
                        map[txdName.ToLowerInvariant()] = modelName.ToLowerInvariant();
                    }
                }
            }
            catch
            {
                // Ignore non-vehicle or malformed XML files (already flagged earlier)
            }
        }
        return map;
    }

    private static ModInstallStep AnnotateStepAssetFormat(ModInstallStep step)
    {
        if (!string.IsNullOrWhiteSpace(step.AssetFormatLabel))
        {
            return step;
        }

        if (step.Operation == InstallOperationKind.RegisterDlcListEntry)
        {
            return step with
            {
                AssetFormatLabel = "DLC Registration (dlclist.xml)",
                ResolutionSource = "OpenIV assembly.xml"
            };
        }

        if (!string.IsNullOrWhiteSpace(step.SourceFullPath) && File.Exists(step.SourceFullPath))
        {
            var info = RageAssetInspector.Inspect(step.SourceFullPath, step.SourceRelativePath);
            return step with
            {
                AssetFormatLabel = info.FormatDisplayLabel,
                CanonicalModelStem = info.CanonicalModelStem,
                ResolutionSource = "OpenIV assembly.xml"
            };
        }

        return step;
    }

    private static string? InferAddOnDlcFolderName(
        ExtractedFileEntry dlcFile,
        IReadOnlyList<string> readmeDlcEntries,
        string fallbackModName)
    {
        if (readmeDlcEntries.Count == 1)
        {
            return DlcListXmlService.ExtractDlcPackFolderName(readmeDlcEntries[0]);
        }

        var segments = dlcFile.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2)
        {
            string parentFolder = segments[^2];
            if (!IsGenericWrapperFolderName(parentFolder) &&
                !parentFolder.Equals("dlcpacks", StringComparison.OrdinalIgnoreCase))
            {
                if (readmeDlcEntries.Count > 1)
                {
                    var matching = readmeDlcEntries.FirstOrDefault(e =>
                        string.Equals(DlcListXmlService.ExtractDlcPackFolderName(e), parentFolder, StringComparison.OrdinalIgnoreCase));
                    if (matching is not null)
                    {
                        return DlcListXmlService.ExtractDlcPackFolderName(matching);
                    }
                }
                return SanitizeDlcFolderName(parentFolder);
            }
        }

        try
        {
            var hdr = Rpf7Archive.InspectHeader(dlcFile.FullStagingPath);
            if (hdr.CanDirectEditWithoutNgKeys)
            {
                var rpf = Rpf7Archive.Open(dlcFile.FullStagingPath);
                var setupEntry = rpf.ListEntries().FirstOrDefault(e =>
                    e.Name.Equals("setup2.xml", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.Equals("content.xml", StringComparison.OrdinalIgnoreCase));
                if (setupEntry is not null)
                {
                    string xml = Encoding.UTF8.GetString(rpf.ExtractFile(setupEntry.FullInternalPath));
                    var doc = XDocument.Parse(xml);
                    string? devName = doc.Descendants("deviceName").FirstOrDefault()?.Value.Trim();
                    if (!string.IsNullOrWhiteSpace(devName))
                    {
                        if (devName.StartsWith("dlc_", StringComparison.OrdinalIgnoreCase))
                        {
                            devName = devName[4..];
                        }
                        return SanitizeDlcFolderName(devName);
                    }
                }
            }
        }
        catch
        {
            // Ignore internal RPF inspection errors
        }

        return SanitizeDlcFolderName(fallbackModName);
    }

    private static (string OuterRpfModsPath, string InternalPath) ResolveRpfTargetForDataFile(
        ExtractedFileEntry asset,
        IReadOnlyList<string> readmeRpfPaths)
    {
        string fileName = Path.GetFileName(asset.RelativePath);

        var fromOwnPath = SplitOuterAndInnerRpf(asset.RelativePath);
        if (fromOwnPath is not null)
        {
            return fromOwnPath.Value;
        }

        foreach (var rp in readmeRpfPaths)
        {
            if (rp.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            {
                var split = SplitOuterAndInnerRpf(rp);
                if (split is not null)
                {
                    return split.Value;
                }
            }
        }

        if (fileName.Equals("gameconfig.xml", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("visualsettings.dat", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("handling.meta", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("carcols.ymt", StringComparison.OrdinalIgnoreCase))
        {
            return ("mods/update/update.rpf", $"common/data/{fileName}");
        }

        if (fileName.Equals("vehicles.meta", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("peds.meta", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("popgroups.ymt", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("water.xml", StringComparison.OrdinalIgnoreCase))
        {
            return ("mods/update/update.rpf", $"common/data/levels/gta5/{fileName}");
        }

        var dataReadmeDir = readmeRpfPaths.FirstOrDefault(p => p.Contains("data", StringComparison.OrdinalIgnoreCase));
        if (dataReadmeDir is not null)
        {
            var split = SplitOuterAndInnerRpf(dataReadmeDir.TrimEnd('/') + "/" + fileName);
            if (split is not null)
            {
                return split.Value;
            }
        }

        return ("mods/update/update.rpf", $"common/data/{fileName}");
    }

    public static (string OuterRpfModsPath, string InternalPath)? SplitOuterAndInnerRpf(string rawPath)
    {
        string norm = NormalizeAndAnchorToGtaRoot(rawPath);
        int firstRpfIdx = norm.IndexOf(".rpf/", StringComparison.OrdinalIgnoreCase);
        if (firstRpfIdx < 0)
        {
            return null;
        }

        string outer = PathSafetyGuard.EnsureSafeModsPath(norm[..(firstRpfIdx + 4)]);
        if (!outer.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
        {
            outer = "mods/" + outer;
        }

        string inner = Rpf7Archive.NormalizeInternalPath(norm[(firstRpfIdx + 5)..]);
        if (string.IsNullOrWhiteSpace(inner))
        {
            return null;
        }

        return (outer, inner);
    }

    private static (string OuterRpfModsPath, string InternalDir)? SplitOuterAndInnerRpfForDirectory(string rawDirPath)
    {
        string norm = NormalizeAndAnchorToGtaRoot(rawDirPath).TrimEnd('/');
        if (norm.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
        {
            int firstRpfIdx = norm.IndexOf(".rpf", StringComparison.OrdinalIgnoreCase);
            if (firstRpfIdx == norm.Length - 4)
            {
                // Single top-level .rpf with root internal directory (e.g. "x64a.rpf")
                string outerSingle = PathSafetyGuard.EnsureSafeModsPath(norm);
                if (!outerSingle.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
                {
                    outerSingle = "mods/" + outerSingle;
                }
                return (outerSingle, string.Empty);
            }
        }

        var split = SplitOuterAndInnerRpf(norm + "/__placeholder__");
        if (split is null)
        {
            return null;
        }

        string innerDir = split.Value.InternalPath.EndsWith("/__placeholder__", StringComparison.Ordinal)
            ? split.Value.InternalPath[..^"/__placeholder__".Length]
            : split.Value.InternalPath == "__placeholder__"
                ? string.Empty
                : split.Value.InternalPath;

        return (split.Value.OuterRpfModsPath, Rpf7Archive.NormalizeInternalPath(innerDir));
    }

    private static string NormalizeAndAnchorToGtaRoot(string rawPath)
    {
        string norm = PathSafetyGuard.NormalizeRelativePath(rawPath);
        string[] anchors = { "mods/", "update/", "x64", "common.rpf" };
        int bestAnchor = -1;
        foreach (var anchor in anchors)
        {
            int pos = norm.IndexOf(anchor, StringComparison.OrdinalIgnoreCase);
            if (pos >= 0 && (bestAnchor < 0 || pos < bestAnchor))
            {
                bestAnchor = pos;
            }
        }

        return bestAnchor > 0 ? norm[bestAnchor..] : norm;
    }

    private static List<ReadmeRpfContextEntry> ExtractContextualRpfPathsFromReadme(string readmeText)
    {
        var results = new List<ReadmeRpfContextEntry>();
        if (string.IsNullOrWhiteSpace(readmeText))
        {
            return results;
        }

        var lines = readmeText.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            foreach (Match m in ReadmeRpfPathRegex().Matches(lines[i]))
            {
                string raw = m.Value.Trim().TrimEnd('.', ',', ';', ':', ')', ']', '"', '\'');
                string norm = PathSafetyGuard.NormalizeRelativePath(raw);
                if (!norm.Contains(".rpf", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Build a local paragraph window (stops at blank lines / section dividers so multi-step instructions don't bleed into each other!)
                var ctxLines = new List<string> { lines[i] };
                for (int prev = i - 1; prev >= Math.Max(0, i - 2); prev--)
                {
                    if (string.IsNullOrWhiteSpace(lines[prev]) || ReadmeRpfPathRegex().IsMatch(lines[prev]))
                    {
                        break;
                    }
                    ctxLines.Insert(0, lines[prev]);
                }
                for (int next = i + 1; next <= Math.Min(lines.Length - 1, i + 3); next++)
                {
                    if (string.IsNullOrWhiteSpace(lines[next]) || ReadmeRpfPathRegex().IsMatch(lines[next]))
                    {
                        break;
                    }
                    ctxLines.Add(lines[next]);
                }

                results.Add(new ReadmeRpfContextEntry(norm, string.Join(" ", ctxLines)));
            }
        }

        return results;
    }

    private static string StripKnownAssetFileName(string normRpfPath)
    {
        string ext = Path.GetExtension(normRpfPath);
        if (!string.IsNullOrEmpty(ext) &&
            !ext.Equals(".rpf", StringComparison.OrdinalIgnoreCase) &&
            (RageAssetInspector.IsModelOrTextureAssetExtension(ext) || ext is ".xml" or ".meta" or ".dat"))
        {
            int slash = normRpfPath.LastIndexOf('/');
            return slash > 0 ? normRpfPath[..slash] : normRpfPath;
        }
        return normRpfPath.TrimEnd('/');
    }

    private static bool HasAssetFileExtension(string path)
    {
        string ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) &&
               !ext.Equals(".rpf", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsWholeToken(string haystack, string token)
    {
        if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        string pattern = $@"(?<![A-Za-z0-9_]){Regex.Escape(token)}(?![A-Za-z0-9_])";
        return Regex.IsMatch(haystack, pattern, RegexOptions.IgnoreCase);
    }

    private static bool IsGenericWrapperFolderName(string folder) =>
        folder.Equals("files", StringComparison.OrdinalIgnoreCase) ||
        folder.Equals("replace", StringComparison.OrdinalIgnoreCase) ||
        folder.Equals("model", StringComparison.OrdinalIgnoreCase) ||
        folder.Equals("models", StringComparison.OrdinalIgnoreCase) ||
        folder.Equals("textures", StringComparison.OrdinalIgnoreCase) ||
        folder.Equals("vehicle", StringComparison.OrdinalIgnoreCase) ||
        folder.Equals("vehicles", StringComparison.OrdinalIgnoreCase) ||
        folder.Equals("addon", StringComparison.OrdinalIgnoreCase) ||
        folder.Equals("add-on", StringComparison.OrdinalIgnoreCase);

    private static bool IsInSubfolderNamed(string relativePath, params string[] prefixes)
    {
        var segments = PathSafetyGuard.NormalizeRelativePath(relativePath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length - 1; i++)
        {
            foreach (var p in prefixes)
            {
                if (segments[i].StartsWith(p, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsTinyVehicleMetaSnippet(string fullStagingPath, string fileName)
    {
        if (!fileName.Equals("vehicles.meta", StringComparison.OrdinalIgnoreCase) &&
            !fileName.Equals("handling.meta", StringComparison.OrdinalIgnoreCase) &&
            !fileName.Equals("carvariations.meta", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var doc = XDocument.Load(fullStagingPath);
            int itemCount = doc.Descendants("Item").Count();
            return itemCount > 0 && itemCount < 15;
        }
        catch
        {
            return false;
        }
    }

    private static List<string> ExtractDlcEntriesFromReadme(string readmeText)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(readmeText))
        {
            return list;
        }

        foreach (Match m in DlcPacksRegex().Matches(readmeText))
        {
            try
            {
                list.Add(DlcListXmlService.NormalizeDlcEntry(m.Groups[1].Value));
            }
            catch
            {
                // Ignore malformed match
            }
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? ExtractAuthorFromReadme(string readme)
    {
        if (string.IsNullOrWhiteSpace(readme))
        {
            return null;
        }
        var m = AuthorRegex().Match(readme);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string? ExtractModVersionFromReadme(string readme, string fallbackName)
    {
        var m = ModVersionRegex().Match(readme ?? string.Empty);
        if (m.Success && !IsGtaGameVersionString(m.Groups[1].Value))
        {
            return m.Groups[1].Value.Trim();
        }

        foreach (Match tm in TitleVersionTagRegex().Matches($"{fallbackName}\n{readme}"))
        {
            string candidate = tm.Groups[1].Value.Trim();
            if (!IsGtaGameVersionString(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsGtaGameVersionString(string ver) =>
        ver.StartsWith("1.0.", StringComparison.Ordinal) && ver.Length >= 7;

    private static ModCategory InferOverallCategory(IReadOnlyList<ModInstallStep> steps, IReadOnlyList<ExtractedFileEntry> files)
    {
        bool hasDlc = steps.Any(s => s.Operation == InstallOperationKind.RegisterDlcListEntry);
        bool hasRpfInject = steps.Any(s => s.Operation == InstallOperationKind.InjectIntoRpfArchive);
        bool hasAsi = files.Any(f => f.Extension == ".asi");
        bool hasScripts = steps.Any(s => s.TargetRelativePath.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase));
        bool onlyDataInRpf = hasRpfInject && steps
            .Where(s => s.Operation == InstallOperationKind.InjectIntoRpfArchive)
            .All(s => !RageAssetInspector.IsModelOrTextureAssetExtension(Path.GetExtension(s.SourceRelativePath)));

        int distinctTypes = (hasDlc ? 1 : 0) + (hasRpfInject ? 1 : 0) + (hasAsi ? 1 : 0) + (hasScripts ? 1 : 0);
        if (distinctTypes > 1)
        {
            return ModCategory.CompositeMod;
        }
        if (hasDlc)
        {
            return ModCategory.AddOnDlcPack;
        }
        if (onlyDataInRpf)
        {
            return ModCategory.DataOrConfigMod;
        }
        if (hasRpfInject)
        {
            return ModCategory.RpfAssetReplacement;
        }
        if (hasAsi)
        {
            return ModCategory.AsiPlugin;
        }
        if (hasScripts)
        {
            return ModCategory.ScriptHookVDotNetMod;
        }
        return ModCategory.Unknown;
    }

    private static string? ExtractSubpathFromMarker(string relPath, string marker)
    {
        string norm = PathSafetyGuard.NormalizeRelativePath(relPath);
        int idx = norm.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? norm[idx..] : null;
    }

    private static string CleanModDisplayName(string raw) =>
        raw.Replace('_', ' ').Replace('-', ' ').Trim();

    private static string SanitizeDlcFolderName(string raw)
    {
        var chars = raw.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray();
        return chars.Length > 0 ? new string(chars) : "customdlc";
    }

    private static string Slugify(string raw)
    {
        var chars = raw.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        string s = new string(chars).Trim('-');
        while (s.Contains("--", StringComparison.Ordinal))
        {
            s = s.Replace("--", "-", StringComparison.Ordinal);
        }
        return string.IsNullOrEmpty(s) ? "mod" : s;
    }

    private static string Truncate(string s, int maxChars) =>
        string.IsNullOrEmpty(s) || s.Length <= maxChars ? s : s[..maxChars] + "\n...[truncated]";

    [GeneratedRegex(@"(?:mods[\\/])?(?:update|x64[a-z]?\.rpf|common\.rpf)[A-Za-z0-9_./\\+-]*\.rpf(?:[\\/][A-Za-z0-9_./\\+-]+)?", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ReadmeRpfPathRegex();

    [GeneratedRegex(@"dlcpacks\s*:\s*[/\\]+([A-Za-z0-9_-]+)[/\\]*", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DlcPacksRegex();

    [GeneratedRegex(@"^patchday\d+(?:ng)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PatchdaySegmentRegex();

    [GeneratedRegex(@"(?:author|created\s+by|credits?|by)\s*[:=-]\s*([A-Za-z0-9_ .@-]{2,40})", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex AuthorRegex();

    [GeneratedRegex(@"(?:version|ver)\s*[:=-]?\s*v?(\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ModVersionRegex();

    [GeneratedRegex(@"\bv(\d+\.\d+(?:\.\d+)?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TitleVersionTagRegex();

    [GeneratedRegex(@"\bscripts\s+folder\b|[\\/]scripts[\\/]", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ScriptsFolderHintRegex();
}
