using System.Runtime.InteropServices;

// The app runs elevated: native libraries (nvml, nvapi, Windows DLLs) are only loaded from System32, never from the
// current folder or PATH, where a standard user could place a DLL with the same name.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
