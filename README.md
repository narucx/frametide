# Frametide

Windows gaming tweaks, Game Boost, GPU undervolting and real-session benchmarks in one open source app.
Every change is backed up and can be reverted exactly; nothing is hidden, no telemetry, no account.

> **Personal project.** Built by Henri for himself and friends, "vibecoded" with AI help. No support, no feature
> requests, no warranty: it changes Windows settings, GPU clocks and voltages. Use it at your own risk and create
> a restore point first.

## Install

Download `Frametide-win-Setup.exe` from the [latest release](https://github.com/narucx/frametide/releases/latest)
and run it. Frametide asks for administrator rights when it starts (tweaks, services, GPU tuning).
Requires Windows 10 version 2004 or newer, or Windows 11 (64-bit). GPU tuning needs an NVIDIA graphics card.

## Update

Frametide checks GitHub Releases for updates and installs them on request. Releases are cumulative: always
install the newest one.

## Development

Requires the .NET 10 SDK.

```
dotnet test --solution Frametide.slnx
dotnet build src/Frametide -p:FtPreview=true -o preview
preview/Frametide.exe --preview page.png "Tweaks & profiles"
powershell -ExecutionPolicy Bypass -File tools/Find-MissingTranslations.ps1 -Lang de
```
The preview build has no administrator manifest and renders a page to a PNG (read-only, temporary data folder).
The last command lists UI texts without a German translation.

## License

GPL-3.0, see [LICENSE](LICENSE).
