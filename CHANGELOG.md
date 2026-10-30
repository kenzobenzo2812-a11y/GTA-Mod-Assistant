# Changelog

## v1.2.0-beta
- **No-README Disclaimer & AI Deep File Analysis:** Automatically detects mods packaged without a README, warns about missing author notes, and inspects raw `dlc.rpf` (`setup2.xml`/`content.xml`), `RSC7` (`.yft`/`_hi.yft`/`.ytd`), `.meta` `<modelName>` spawn names, and PE script headers.
- **Clear Auto-Mapped Target Status:** Distinguished `Auto-Mapped` paths from optional `Override RPF...` and uncertain `Choose RPF...` steps.
- **Multi-Platform Auto-Detection:** Automatic GTA V discovery across Epic Games, Steam (`libraryfolders.vdf`), and Rockstar Games Launcher across all drives.
- **Real-World RPF7 & Nested `.oiv` Reliability:** Preserves per-entry encrypted Rockstar files (`branches.meta`) inside `OPEN` `mods\update\update.rpf` containers, unpacks nested `.oiv` archives inside `.rar`/`.zip` downloads, and uses instant per-entry stash backups for multi-GB `.rpf` archives.
