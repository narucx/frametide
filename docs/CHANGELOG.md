# Changelog

## Unreleased

### Fixed
- Smart undervolt says when it stopped at MAX OFFSET without finding the GPU's limit (more may be possible)
- "Installed apps" in Windows Settings shows the installed version after an update (it kept showing the first version)

## v0.1.0-beta.6

### Fixed
- Cleanup: never cleans a drive root or a top-level system folder; Windows Update downloads are only removed after the update services stopped
- Ghost devices: only entries not seen for over 30 days are pre-selected, with a "last seen" date and a warning before removal
- Preinstalled apps: exact package names (nothing similar gets removed); a failing app no longer stops the others; Teams entries clearly named
- Autostart list: folders of scheduled tasks that cannot be read are skipped instead of failing the whole list
- CS2 server blocker: rules follow changed relay addresses, regions removed by Valve get unblocked, only public IPv4 addresses are blocked
- Success messages only appear when the action actually worked
- Game Boost: the state is saved before every change, so a crash during START never leaves apps frozen without a way back; STOP keeps what it could not undo and can be pressed again
- Game Boost: anti-cheat services and game launchers are never closed or suspended; priorities and affinities changed for the game are put back on STOP
- Auto Game Boost no longer starts again right after a manual STOP while the game keeps running
- GPU tests: a crash or power loss mid-test no longer leaves the GPU locked or at the maximum power limit (reset at the next start)
- GPU profiles made on another GPU are refused; after a driver change the sign-in profile is skipped until re-tested
- Exiting while an automatic Game Boost START runs waits for it first
- Backup of original values: written to disk before it replaces the old file (the previous version is kept as .bak); a damaged backup is never overwritten, the previous version is used or an error is shown
- Revert only restores settings Frametide actually changed; tweaks that were already active before have no Revert button, and boot/system services are never touched
- Network adapter tweaks: a property that did not exist before is removed again on revert instead of being set to the driver default; adapters are recognized by their id, so renamed or unplugged adapters keep their backup
- USB selective suspend: a failed revert keeps the backup instead of losing it

### Security
- Native libraries and Windows tools are only loaded from System32, never from the current folder or PATH; .NET startup hooks are off
- Cleanup and shader cache clearing never delete through junctions or links (the app runs elevated, these folders are user-writable)
- The data folder is checked again after creating it and gets Administrators as owner
- The update program (Update.exe) and its folder are checked too before sign-in tasks are kept
- Sign-in tasks are only removed when the program they start is certainly unsafe, not during an update
- PresentMon's signer must be exactly "Intel Corporation"

### Notes
- Not code-signed yet: Windows SmartScreen shows a warning on the first start ("More info" > "Run anyway").
- Tweaks that were already active before Frametide changed them have no backup and therefore no "Revert" any more.

## v0.1.0-beta.5

### Changed
- Updates come from a feed on GitHub Pages; releases only contain the installer (Frametide-win.msi)

### Notes
- Not code-signed yet: Windows SmartScreen shows a warning on the first start ("More info" > "Run anyway").

## v0.1.0-beta.4

### Changed
- Installed for all users into C:\Program Files\Frametide (Frametide-win.msi); updates ask for administrator rights
- "Start with Windows" and "GPU profile at sign-in" only start a copy that standard users cannot change; existing
  sign-in tasks are moved to the new installation

### Added
- Uninstalling stops a running Game Boost and removes the Auto Game Boost launch priorities and the sign-in tasks

### Fixed
- A data folder in ProgramData that someone else created before Frametide is no longer trusted

### Notes
- Coming from beta.1 to beta.3: exit Frametide (tray icon > Exit), uninstall it in Settings > Apps, then install
  Frametide-win.msi. Settings, profiles and benchmarks are kept.
- Not code-signed yet: Windows SmartScreen shows a warning on the first start ("More info" > "Run anyway").

## v0.1.0-beta.3

### Added
- "Check for updates" button below the language; it also says when Frametide is up to date

### Notes
- Not code-signed yet: Windows SmartScreen shows a warning on the first start ("More info" > "Run anyway").

## v0.1.0-beta.2

### Added
- Option "Beta updates" (below the language): also offer test versions; a beta always gets the next stable version

### Fixed
- Beta versions now find newer beta versions as updates

### Notes
- Not code-signed yet: Windows SmartScreen shows a warning on the first start ("More info" > "Run anyway").
- Version 0.1.0-beta.1 cannot find this update by itself: install this version once from the release page.

## v0.1.0-beta.1

### Added
- Tweaks: 31 optimizations (gaming, power, network, privacy, ads and AI, Explorer), each on/off with an exact revert
- Profiles: Recommended, Minimal, Competitive; optional restore point before applying
- Detection and repair of harmful tweaks left by other tools (BCD timers, disabled HPET, Spectre mitigations off, ...)
- Overview: system information, GPU and CPU live readings, hints
- System check: XMP/EXPO, memory channels, CPU microcode, Resizable BAR, PCIe link, memory integrity
- Game Boost: power plan, game priority and CPU cores, closing or suspending background apps while you play; STOP rolls everything back, also after a crash
- Auto Game Boost: starts with a game from the list and stops after it closed, without opening the game process
- Installed games scan (Steam, Epic, GOG, Riot, EA, Ubisoft, Battle.net) and Process Lasso conflict check
- Benchmark: records games from the list with Intel PresentMon (downloaded from Intel, signature checked) and compares sessions without and with Game Boost: average FPS, 1% and 0.1% lows, stutters, GPU power; alt-tab periods are left out, FPS limits are detected
- GPU and undervolt (NVIDIA): live readings, smart automatic undervolt with a built-in stress test that checks every result, manual clock offset, clock lock and power limit, profiles applied on Game Boost START or at sign-in, profile stress test
- NVIDIA game profiles: prefer maximum performance and low latency mode for the games in the list, unlimited shader cache; Undo restores the previous driver values
- CS2: launch option check, video settings overview, NVIDIA driver profile, server blocker (Windows Firewall rules per relay region)
- Maintenance: restore point, system file check (DISM + SFC), autostart manager, disk cleanup, preinstalled apps (removed for all users), ghost devices
- Notification area icon and start with Windows
- Backup of every original value in an administrator-only data folder
- English and German user interface
- Updates via GitHub Releases

### Notes
- Test release. The files are not code-signed yet: Windows SmartScreen shows a warning on the first start ("More info" > "Run anyway").
- Requires Windows 10 version 2004 or newer, or Windows 11 (64-bit).
