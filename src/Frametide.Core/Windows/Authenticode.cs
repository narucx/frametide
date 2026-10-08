using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Frametide.Core.Windows;

/// <summary>Checks the Authenticode signature of a file (WinVerifyTrust, like the "Digital signatures" tab in Explorer).</summary>
public static partial class Authenticode
{
    /// <summary>Subject of the signing certificate when the signature is valid and trusted, otherwise null.</summary>
    public static string? TrustedSigner(string path)
    {
        if (!File.Exists(path)) return null;
        var pathPtr = Marshal.StringToHGlobalUni(path);
        var fileInfo = new FileInfo { Size = (uint)Marshal.SizeOf<FileInfo>(), FilePath = pathPtr };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePtr, false);
            var data = new TrustData
            {
                Size = (uint)Marshal.SizeOf<TrustData>(),
                UiChoice = 2,              // WTD_UI_NONE
                RevocationChecks = 0,      // WTD_REVOKE_NONE: no network access
                UnionChoice = 1,           // WTD_CHOICE_FILE
                File = filePtr,
                StateAction = 0,
            };
            var action = GenericVerifyV2;
            if (WinVerifyTrust(IntPtr.Zero, ref action, ref data) != 0) return null;
        }
        finally
        {
            Marshal.FreeHGlobal(filePtr);
            Marshal.FreeHGlobal(pathPtr);
        }
#pragma warning disable SYSLIB0057   // reads the signer of a signed file, not a certificate file
        using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
        return cert.Subject;
    }

    /// <summary>True when the file has a valid signature whose certificate names <paramref name="organization"/> (O=...).</summary>
    public static bool IsSignedBy(string path, string organization)
    {
        try { return TrustedSigner(path) is { } subject && subject.Contains($"O={organization}", StringComparison.Ordinal); }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
    }

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfo
    {
        public uint Size;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
