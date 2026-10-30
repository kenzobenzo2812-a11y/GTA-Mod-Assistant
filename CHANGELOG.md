# Changelog

## v1.2.8
- **Repair pending package metadata:** Startup recovery upgrades previously generated `.oiv` descriptions to OpenIV's required CDATA format before retrying them.
- **Prevent repeated installer windows:** Startup retries the newest pending package first and stops after the first unsuccessful commit instead of launching every pending installer.

## v1.2.7
- **Fix OpenIV package metadata:** Generated `.oiv` descriptions are now written as CDATA, as required by OpenIV's 2.1 package metadata parser.

## v1.2.6
- **Background OpenIV launch:** OpenIV now starts hidden and directly targets GTA V without showing the game picker. UI Automation runs with a hard timeout so an unresponsive OpenIV window cannot leave the app stuck at “Waiting for OpenIV.”
- **Automatic pending-install recovery:** On startup, the app retries eligible encrypted-RPF packages left pending from an earlier run, without requiring the user to click “Commit .OIV.”
- **Clear install completion state:** Starting a new install clears stale warning banners, and an unsuccessful automatic commit now ends in an explicit pending state instead of looking like an endless install.

## v1.2.5
- **Hands-free OpenIV package install:** The app now advances through recognized OpenIV installer steps, selects the `mods` folder target, and starts installation automatically for encrypted RPF packages. It still verifies the resulting archive and leaves unsupported installer layouts pending rather than clicking blindly.

## v1.2.4
- **Install readiness feedback:** The app now explains when Install is unavailable because GTA V is not detected or no install steps are selected, and refreshes the status as those conditions change.

## v1.2.0-beta
- **Safe Memory & Modding Junk Cleaner:** Dedicated **Memory & Junk Cleanup** tab that cleans leftover mod staging caches, orphaned uninstalled mod stashes, large GTA V / OpenIV `.dmp` crash dumps, stale `%TEMP%` archive remnants, SQLite WAL bloat, and trims idle user-space RAM working sets — while strictly protecting `mods\`, installed mods, active backups, and Windows boot/system memory settings.
- **No-README Disclaimer & AI Deep File Analysis:** Automatically detects mods packaged without a README, warns about missing author notes, and inspects raw `dlc.rpf` (`setup2.xml`/`content.xml`), `RSC7` (`.yft`/`_hi.yft`/`.ytd`), `.meta` `<modelName>` spawn names, and PE script headers.
- **Clear Auto-Mapped Target Status:** Distinguished `Auto-Mapped` paths from optional `Override RPF...` and uncertain `Choose RPF...` steps.
- **Multi-Platform Auto-Detection:** Automatic GTA V discovery across Epic Games, Steam (`libraryfolders.vdf`), and Rockstar Games Launcher across all drives.
- **Real-World RPF7 & Nested `.oiv` Reliability:** Preserves per-entry encrypted Rockstar files (`branches.meta`) inside `OPEN` `mods\update\update.rpf` containers, unpacks nested `.oiv` archives inside `.rar`/`.zip` downloads, and uses instant per-entry stash backups for multi-GB `.rpf` archives.
