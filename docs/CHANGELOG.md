# Changelog

## Unreleased

### Added
- "Check for updates" button below the language; it also says when Frametide is up to date

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
