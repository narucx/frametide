# Privacy

Frametide has no accounts, no telemetry and no analytics. It does not send any data about you, your PC or your
games anywhere. Everything it records (journal, profiles, benchmarks, log) stays in `C:\ProgramData\Frametide`.

It connects to the internet only for these requests:

| When | Where | What is sent |
|---|---|---|
| Update check (at start and on "Check for updates") | `narucx.github.io` (GitHub Pages) | The installed version, CPU architecture and OS (`win-x64`), as part of the request |
| Downloading an update (on request) | `narucx.github.io` | Nothing beyond the file request |
| First benchmark: downloading PresentMon | `api.github.com`, `github.com` (Intel's PresentMon releases) | Nothing beyond the file request |
| CS2 server blocker: loading the server list | `api.steampowered.com` (Valve) | Nothing beyond the request for CS2's server list |
| "Download HWiNFO" button | `www.hwinfo.com`, opened in your browser | Whatever your browser sends |

As with any web request, these services see your IP address and may log requests according to their own privacy
policies ([GitHub](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement),
[Valve](https://store.steampowered.com/privacy_agreement/)).
