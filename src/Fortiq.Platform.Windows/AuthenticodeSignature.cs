using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Fortiq.Platform.Windows;

/// <summary>What Windows says about a file's Authenticode signature.</summary>
public enum SignatureStatus
{
    /// <summary>Signed, and the signature and its certificate chain are trusted by this machine.</summary>
    Trusted,

    /// <summary>The file carries no signature at all.</summary>
    NotSigned,

    /// <summary>A signature is present but Windows does not trust it: broken, expired or untrusted root.</summary>
    Untrusted
}

/// <summary>
/// Asks Windows itself whether a file is validly signed, through the same trust provider the loader
/// and SmartScreen use. A signature that this machine does not trust is reported as untrusted rather
/// than quietly treated as absent.
/// </summary>
public static class AuthenticodeSignature
{
    private const uint TrustEProvider_Unknown = 0x800B0001;
    private const uint TrustEnoSignature = 0x800B0100;
    private const uint TrustEBadDigest = 0x80096010;
    private const uint TrustEExplicitDistrust = 0x800B0111;
    private const uint CertEUntrustedRoot = 0x800B0109;

    [SupportedOSPlatform("windows")]
    public static SignatureStatus Verify(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Authenticode verification is available on Windows only.");
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The file to verify does not exist.", fullPath);
        }

        var fileInfo = new WinTrustFileInfo
        {
            StructureSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = fullPath,
            FileHandle = IntPtr.Zero,
            KnownSubject = IntPtr.Zero
        };

        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);

            var data = new WinTrustData
            {
                StructureSize = (uint)Marshal.SizeOf<WinTrustData>(),
                PolicyCallbackData = IntPtr.Zero,
                SipClientData = IntPtr.Zero,
                // No prompting and no revocation network calls: verification must not block on a
                // machine that has no route to a revocation endpoint.
                UiChoice = UiNone,
                RevocationChecks = RevokeNone,
                UnionChoice = ChoiceFile,
                UnionData = fileInfoPointer,
                StateAction = StateActionVerify,
                StateData = IntPtr.Zero,
                UrlReference = null,
                ProviderFlags = SaferFlag,
                UiContext = 0
            };

            var action = ActionGenericVerifyV2;
            var result = unchecked((uint)WinVerifyTrust(IntPtr.Zero, ref action, ref data));

            data.StateAction = StateActionClose;
            _ = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            return result switch
            {
                0 => SignatureStatus.Trusted,
                TrustEnoSignature or TrustEProvider_Unknown => SignatureStatus.NotSigned,
                TrustEBadDigest or TrustEExplicitDistrust or CertEUntrustedRoot => SignatureStatus.Untrusted,
                _ => SignatureStatus.Untrusted
            };
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    private const uint UiNone = 2;
    private const uint RevokeNone = 0;
    private const uint ChoiceFile = 1;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;
    private const uint SaferFlag = 0x100;

    private static Guid ActionGenericVerifyV2 => new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructureSize;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructureSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr UnionData;
        public uint StateAction;
        public IntPtr StateData;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
    }
}
