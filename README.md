# Frametide

Windows gaming tweaks, Game Boost, GPU undervolting and real-session benchmarks in one open source app.
Every change is backed up and can be reverted exactly; nothing is hidden, no telemetry, no account.

> **Personal project.** Built by Henri for himself and friends, "vibecoded" with AI help. No support, no feature
> requests, no warranty: it changes Windows settings, GPU clocks and voltages. Use it at your own risk and create
> a restore point first.

## Install

Download `Frametide-win.msi` from the [latest release](https://github.com/narucx/frametide/releases/latest)
and run it. It installs Frametide for all users into `C:\Program Files\Frametide`. Frametide asks for administrator
rights when it starts (tweaks, services, GPU tuning).
Requires Windows 10 version 2004 or newer, or Windows 11 (64-bit). GPU tuning needs an NVIDIA graphics card.

Frametide runs with administrator rights and can start itself at sign-in without a UAC prompt. That is only safe
when standard users cannot change its files, so it is installed for all users, and sign-in tasks are only created
for a copy in such a folder.

## Update

Frametide checks for updates (feed on GitHub Pages, built with each release) and installs them on request
(with a UAC prompt). Releases are
cumulative: always install the newest one.

## Uninstall

Settings > Apps > Frametide. Uninstalling stops a running Game Boost, removes the launch priorities of Auto Game
Boost and the sign-in tasks (it asks for administrator rights once more for that). Tweaks are not reverted: revert
them in Frametide first if you want the Windows defaults back. Profiles, benchmarks and the backup of the original
values stay in `C:\ProgramData\Frametide`; delete that folder to remove everything.

## Development

Requires the .NET 10 SDK.

```
dotnet test --solution Frametide.slnx
dotnet build src/Frametide -o preview
preview/Frametide.exe --preview page.png "Tweaks & profiles"
powershell -ExecutionPolicy Bypass -File tools/Find-MissingTranslations.ps1 -Lang de
```
`--preview` renders a page to a PNG without administrator rights (read-only, temporary data folder).
The last command lists UI texts without a German translation.

## Code signing policy

Releases are not code-signed yet, so Windows SmartScreen warns on the first start ("More info" > "Run anyway").
Signing is planned with free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org) (application in progress).

- Committers and reviewers: [narucx](https://github.com/narucx)
- Approvers: [narucx](https://github.com/narucx)
- Only binaries built by the public [release workflow](.github/workflows/release.yml) from this repository get
  signed; every signing request is approved manually.
- Privacy policy: see [docs/PRIVACY.md](docs/PRIVACY.md). This program will not transfer any information to other
  networked systems unless specifically requested by the user or the person installing or operating it.

## Privacy

No accounts, no telemetry. Frametide only goes online for update checks, the PresentMon download and the CS2 server
list: see [docs/PRIVACY.md](docs/PRIVACY.md).

## License

GPL-3.0, see [LICENSE](LICENSE). Included third-party components: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
