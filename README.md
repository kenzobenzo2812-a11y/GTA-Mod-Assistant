# GTA Mod Assistant (Windows x64)

**AI-Powered Safe Mod Installer, RPF7 Manager & Profile Switcher for GTA V Single-Player**

> **Closed-Source Release Repository:** This public repository hosts official Windows executable releases, changelogs, and SHA-256 verification checksums for **GTA Mod Assistant**. Source code is maintained in a private repository (`All Rights Reserved`).

---

## Download Latest Release

Go to the [**Releases**](../../releases/latest) page on the right and download:

1. **`GTAModAssistant-Portable-win-x64.exe` *(Recommended — No Installation Required)***
   - Self-contained single-file 64-bit Windows desktop app (~71 MB).
   - Includes the .NET 8 runtime inside the `.exe` — just download and double-click.
2. **`GTAModAssistant-Setup-win-x64.exe` *(Optional Lightweight Installer)***
   - Installs into `%LOCALAPPDATA%\Programs\GTAModAssistant` with Desktop & Start Menu shortcuts (requires [.NET 8 Windows Desktop Runtime x64](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)).

> **Windows SmartScreen Note:** Because this is an independent community tool without a paid Extended Validation (EV) Microsoft certificate yet, Windows Defender SmartScreen may show *"Windows protected your PC"* on first launch. Click **More info -> Run anyway** after verifying the file's SHA-256 hash against `SHA256SUMS.txt` on the Release page.

---

## Key Features

- **Auto-Detects Epic Games, Steam & Rockstar Games Launcher GTA V** across all drives (`C:\`–`Z:\`), plus OpenIV (`%LOCALAPPDATA%\New Technology Studio\Apps\OpenIV\OpenIV.exe`) and `ScriptHookV` / `ScriptHookVDotNet`.
- **Supports Folders, `.zip`, `.rar`, `.7z`, and Nested `.oiv` Packages:** Handles Add-On `dlc.rpf` car packs, `.yft` / `_hi.yft` models, `.ytd` / `+hi.ytd` textures, `.meta` / `.xml` / `.dat` configs, and `.asi` / `.dll` scripts.
- **100% Safe `mods\` Folder Isolation:** Never overwrites vanilla GTA V files or archives directly. Automatically initializes `mods\update\update.rpf` when needed, updates `dlclist.xml` cleanly, and creates instant per-entry SHA-256 backups for 1-click uninstall/rollback.
- **Hybrid AI + Binary File Forensics:** Works immediately offline out of the box, or connects in 1 click to **Local Ollama** (free, offline) or **OpenAI / Groq** (using your own locally DPAPI-encrypted API key) — even when a mod comes with **no README**!

---

## Single-Player Notice

GTA Mod Assistant is strictly for **offline GTA V Single-Player modding**. Remove or disable `dinput8.dll` / ASI loaders before launching GTA Online.
