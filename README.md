# GTA Mod Assistant (v1.2.7)

An AI-powered **Windows Desktop Application (C# / .NET 8 / WPF)** that analyzes, plans, previews, backs up, installs, verifies, manages profiles for, and cleanly rolls back **GTA V Single-Player mods** — supporting **Epic Games, Steam, and Rockstar Games Launcher GTA V**, **OpenIV** (`%LOCALAPPDATA%\New Technology Studio\Apps\OpenIV\OpenIV.exe`), **RPF7 binary containers**, **RAGE `.yft` / `_hi.yft` / `.ytd` / `+hi.ytd` assets**, and safe `mods\` folder isolation.

**This project is open source under the [MIT License](LICENSE).** The complete application, CLI, installer, and tests are available in this repository.

## Download the latest version

Get the official Windows downloads from the **[latest GitHub release](https://github.com/kenzobenzo2812-a11y/GTA-Mod-Assistant/releases/latest)**. That link always points to the newest published version. Verify downloads using the `SHA256SUMS.txt` attached to each release.

Every version tag pushed to this repository runs the tests, builds the portable app and installer, and publishes them with checksums as a new GitHub release.

---

## Which File Should I Download?

1. **`GTAModAssistant-Portable-win-x64.exe`** (**Recommended — Zero Dependencies!**)
   - **100% standalone, self-contained single-file Windows 64-bit desktop executable (~71 MB).**
   - Bundles the entire .NET 8 + WPF Windows Desktop runtime, SQLite engine, and SharpCompress archive library inside one `.exe`.
   - **How to launch:** Download `GTAModAssistant-Portable-win-x64.exe` onto your Windows PC and double-click it. Nothing else needs to be installed. *(Tip: create an empty file named `portable.mode` next to the `.exe` if you want its SQLite database and backups stored beside the `.exe` instead of `%LOCALAPPDATA%\GTAModAssistant`.)*

2. **`GTAModAssistant-Setup-win-x64.exe`** (**Lightweight Windows Installer — ~3.8 MB**)
   - Standard **NSIS Windows Setup wizard** that installs `GTAModAssistant.exe` and `gtama-cli.exe` into `%LOCALAPPDATA%\Programs\GTAModAssistant`, creates **Desktop** and **Start Menu** shortcuts, and registers an uninstaller in **Windows Settings > Installed Apps**.
   - Uses your system's [.NET 8 Windows Desktop Runtime x64](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) (or run `.\scripts\build-windows.ps1` locally to bundle the offline runtime into the installer).

3. **Source code**
   - The complete Visual Studio / .NET 8 solution, tests, installer, and sample fixtures are available directly in this repository. No separate source archive is required.

## Privacy and AI

- Mod analysis works locally using deterministic rules and file inspection.
- Automatic update checks contact the GitHub Releases API for version information; they do not send game files, mod packages, or logs. Updates are reviewed and downloaded from GitHub by the user.
- Optional AI analysis runs only when enabled and a cloud API key or local model endpoint is configured. The request includes the mod's README text (if present), a file-name/size manifest, inspected file metadata, and the detected GTA V version.
- You choose the AI endpoint. A local Ollama endpoint can keep these requests on your machine; cloud endpoints receive the request data under their own privacy policies.
- API keys are stored locally using Windows DPAPI. The application does not include a bundled API key.

---

## What's New in v1.1.0 — Full `.yft` (`_hi.yft`) & `.ytd` (`+hi.ytd`) Model & Texture Support

- **Folder, `.zip`, and `.rar` Import & Binary Inspection (`RageAssetInspector`):** Detects `.yft` base fragments, `_hi.yft` high-LOD fragments, `.ytd` texture dictionaries, and `+hi.ytd` / `_hi.ytd` high-resolution texture dictionaries, extracts canonical model stems (`police` across `police.yft`, `police_hi.yft`, `police.ytd`, `police+hi.ytd`), and validates binary `RSC7` (`0x37435352`) resource headers.
- **Multi-Archive Destination Resolution (`AiModAnalyzerService` + `GameRpfSearchService`):** Never assumes every `.yft` or `.ytd` belongs in the same archive. Resolves each file's exact destination from:
  1. Explicit `.rpf` or `patchdayXXng` folder hierarchy inside the mod package,
  2. `.meta` / `.xml` `<modelName>` <-> `<txdName>` metadata relationships,
  3. Per-section / per-file contextual README instructions (supporting composite mods targeting `patchday27ng`, `x64e.rpf`, and `patchday8ng` simultaneously),
  4. Companion model-stem pairing (`_hi.yft` and `.ytd` paired to their verified `.yft` archive directory), and
  5. Live TOC scanning across GTA V RPF7 archives (`patchdayXXng` override priority aware).
- **Asks the User When Uncertain (Never Guesses):** If any `.yft`, `_hi.yft`, or `.ytd` file has an ambiguous or unspecified target archive, it is flagged `[Action Needed] Choose Target RPF Archive...` and opens the **Select Target RPF7 Archive** dialog (`RpfTargetSelectionDialog`) rather than guessing.
- **Pre-Install Preview, Duplicate Filename & Conflict Detection:** Previews exact file names, asset types, safe `mods\` RPF paths, resolution sources, and replacement/conflict status before changing anything — catching both intra-mod duplicate filenames (e.g., `2K/police.ytd` vs `4K/police.ytd` with per-row checkboxes & `Keep First Duplicate Only`) and cross-mod RPF replacement conflicts.
- **Per-File + Snapshot SHA-256 Backups, Restore & Uninstall:** Originals are never overwritten in vanilla GTA V folders; `mods\` copies are created via SHA-256-verified copy-on-write, every replaced `.yft`/`_hi.yft`/`.ytd` entry is backed up individually and in a pre-install snapshot before replacement, and one-click Uninstall or Snapshot Restore restores original bytes with post-write SHA-256 verification.
- **Hands-Free Encrypted RPF Commits:** For Rockstar NG-encrypted RPF7 files, the Windows app launches OpenIV hidden, selects its `mods` folder target, and activates the package installer automatically. Pending packages are retried on startup, and bounded UI automation prevents a stalled OpenIV window from freezing the app. It reports completion only after verifying the changed RPF; if OpenIV's controls cannot be safely identified or verification fails, the install remains pending.
- **Compatibility Checker:** Checks game-build requirements and common ScriptHookV, ASI Loader, OpenIV, and ScriptHookVDotNet prerequisites before installation. It blocks known build mismatches and presents warnings for missing requirements; it cannot guarantee compatibility for every third-party mod.
- **Automatic Update Checks:** Checks the latest stable GitHub release at startup and on request. Updates open the official release page for review; the app never silently replaces its own executable.
- **Crash Diagnosis:** Analyzes selected text-based GTA V/mod `.log` or `.txt` files locally for known crash signatures and suggests troubleshooting steps. It does not upload logs or claim a definitive root cause; binary `.dmp` files are not parsed.
- **Modpacks:** Export a profile's enabled/disabled states and original source packages into a `.gtmapack` file. Import safely extracts and verifies package hashes; review and install each mod through the normal analyzer workflow. Import does not directly modify GTA V.
- **Texture / Model Preview:** Renders supported image files (PNG, JPEG, BMP, GIF, TIFF) from the staged package. For proprietary `.yft`/`.ytd` assets, it reports RSC7 header metadata; rendering those game-native models/textures is not supported.

---

## Architecture & Implemented Features

| Module | Project | Responsibilities |
|---|---|---|
| **Core Engine** | `src/GTAModAssistant.Core` | Binary `Rpf7Archive` reader/writer (`0x52504637` magic, fast TOC scanner, `OPEN` TOC/Deflate/`RSC7` resource flags, nested `.rpf` containers, `NG`/`AES` encryption guard), `RageAssetInspector` (`.yft`/`_hi.yft`/`.ytd`/`+hi.ytd` & `RSC7` binary header inspector), `GameRpfSearchService`, `OivPackageService` (`assembly.xml` v2.1 parser & generator), `DlcListXmlService` (XXE-safe XML validation & duplicate prevention), `PeBinaryInspector`, `AiModAnalyzerService`, `VersionCompatibilityService`, `BackupService`, `ConflictDetectionService`, `ModInstallerService`, `ProfileManagerService`, and `SqliteHistoryRepository`. |
| **MVVM Layer** | `src/GTAModAssistant.ViewModels` | `MainViewModel`, async commands, per-step include toggles, uncertain RPF target prompts, progress reporting, log filtering, conflict prompts, and mandatory confirmation guards before destructive operations. |
| **WPF Desktop UI** | `src/GTAModAssistant.App` | Modern dark Windows WPF interface (`Scan Game`, `Add Mod`, `Install`, `Backup`, `Restore`, `Installed Mods`, `Logs`, Pre-Install File/Path/Conflict Preview Grid, `RpfTargetSelectionDialog`, `ConflictResolutionDialog`, Drag-and-Drop overlay, Profile Switcher, and DPAPI-encrypted AI Settings). |
| **CLI Companion** | `src/GTAModAssistant.Cli` | Headless `gtama-cli` (`scan`, `analyze`, `install`, `uninstall`, `list`, `logs`, `--rpf-target`, `--keep-first-duplicate`) sharing the same Core library. |
| **Automated Tests** | `tests/GTAModAssistant.Tests` | `xUnit` unit and end-to-end integration tests cover archive imports, multi-archive routing, compatibility, crash-log signatures, modpack hash verification, asset metadata, duplicate/conflict detection, RPF7 binary round-trips, path safety, backup/restore/uninstall, profiles, and encrypted-RPF behavior. |

---

## Building & Running Tests From Source on Windows

```powershell
# Prerequisite: .NET 8.0 SDK (https://dotnet.microsoft.com/download/dotnet/8.0)
# Run these commands from the repository root

# Run the automated tests and build the solution
dotnet test GTAModAssistant.sln -c Release

# Launch the WPF desktop app directly during development
dotnet run --project src\GTAModAssistant.App\GTAModAssistant.App.csproj

# Or build the standalone single-file portable Windows .exe + installer
.\scripts\build-windows.ps1
```

The GitHub Actions workflow runs the solution tests on pushes and pull requests. Push a version tag such as `v1.2.1` to build and publish a new Windows release automatically.
