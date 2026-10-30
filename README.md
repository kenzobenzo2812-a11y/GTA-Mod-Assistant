# GTA Mod Assistant (v1.2.0-beta)

An AI-powered **Windows Desktop Application (C# / .NET 8 / WPF)** that analyzes, plans, previews, backs up, installs, verifies, manages profiles for, and cleanly rolls back **GTA V Single-Player mods** — supporting **Epic Games, Steam, and Rockstar Games Launcher GTA V**, **OpenIV** (`%LOCALAPPDATA%\New Technology Studio\Apps\OpenIV\OpenIV.exe`), **RPF7 binary containers**, **RAGE `.yft` / `_hi.yft` / `.ytd` / `+hi.ytd` assets**, and safe `mods\` folder isolation.

**This project is open source under the [MIT License](LICENSE).** The complete application, CLI, installer, and tests are available in this repository.

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

---

## Architecture & Implemented Features

| Module | Project | Responsibilities |
|---|---|---|
| **Core Engine** | `src/GTAModAssistant.Core` | Binary `Rpf7Archive` reader/writer (`0x52504637` magic, fast TOC scanner, `OPEN` TOC/Deflate/`RSC7` resource flags, nested `.rpf` containers, `NG`/`AES` encryption guard), `RageAssetInspector` (`.yft`/`_hi.yft`/`.ytd`/`+hi.ytd` & `RSC7` binary header inspector), `GameRpfSearchService`, `OivPackageService` (`assembly.xml` v2.1 parser & generator), `DlcListXmlService` (XXE-safe XML validation & duplicate prevention), `PeBinaryInspector`, `AiModAnalyzerService`, `VersionCompatibilityService`, `BackupService`, `ConflictDetectionService`, `ModInstallerService`, `ProfileManagerService`, and `SqliteHistoryRepository`. |
| **MVVM Layer** | `src/GTAModAssistant.ViewModels` | `MainViewModel`, async commands, per-step include toggles, uncertain RPF target prompts, progress reporting, log filtering, conflict prompts, and mandatory confirmation guards before destructive operations. |
| **WPF Desktop UI** | `src/GTAModAssistant.App` | Modern dark Windows WPF interface (`Scan Game`, `Add Mod`, `Install`, `Backup`, `Restore`, `Installed Mods`, `Logs`, Pre-Install File/Path/Conflict Preview Grid, `RpfTargetSelectionDialog`, `ConflictResolutionDialog`, Drag-and-Drop overlay, Profile Switcher, and DPAPI-encrypted AI Settings). |
| **CLI Companion** | `src/GTAModAssistant.Cli` | Headless `gtama-cli` (`scan`, `analyze`, `install`, `uninstall`, `list`, `logs`, `--rpf-target`, `--keep-first-duplicate`) sharing the same Core library. |
| **Automated Tests** | `tests/GTAModAssistant.Tests` | **21 `xUnit` unit & end-to-end integration tests** verifying folder/ZIP/binary-RAR `.yft`+`_hi.yft`+`.ytd` imports, multi-archive routing, uncertain-target prompts, duplicate/conflict detection, RPF7 binary round-trips, `dlclist.xml` deduplication, Zip-Slip & `.exe` blocking, per-file backup/restore/uninstall, profile switching, and NG-encrypted RPF honesty rules. |

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

The GitHub Actions workflow runs the solution tests on pushes and pull requests. Windows release binaries remain available from the [GitHub Releases](../../releases) page.
