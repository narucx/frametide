# Third-party notices

Frametide is licensed under the GPL-3.0 (see [LICENSE](LICENSE)). The installer and the update packages also contain
the following components, each under its own license.

| Component | Used for | License |
|---|---|---|
| [.NET runtime](https://github.com/dotnet/runtime), [WPF](https://github.com/dotnet/wpf), [Windows Forms](https://github.com/dotnet/winforms) | The app is published self-contained, so the runtime ships with it | MIT, (c) .NET Foundation and Contributors |
| [C#/WinRT](https://github.com/microsoft/CsWinRT) (`WinRT.Runtime.dll`, `Microsoft.Windows.SDK.NET.dll`) | Windows APIs (removing preinstalled apps) | MIT, (c) Microsoft Corporation |
| [Velopack](https://github.com/velopack/velopack) (`Velopack.dll`, `Update.exe`) | Installer and updates | MIT, (c) Velopack Ltd. and contributors |

Not shipped with Frametide:

- [PresentMon](https://github.com/GameTechDev/PresentMon) (MIT, (c) Intel Corporation) is downloaded from Intel's
  GitHub releases the first time a benchmark is recorded; its Intel signature is checked before every start.
- NVML and NvAPI are part of the installed NVIDIA driver and are only loaded from the Windows system folder.

The full license texts are in the linked repositories.
