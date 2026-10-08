# Changelog

## Unreleased

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
- Notification area icon and start with Windows
- Backup of every original value in an administrator-only data folder
- English and German user interface
- Updates via GitHub Releases
