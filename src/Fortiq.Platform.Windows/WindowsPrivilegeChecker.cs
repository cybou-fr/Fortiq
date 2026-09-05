using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Fortiq.Platform.Windows;

/// <summary>
/// Probes Windows process token elevation and specific privileges (such as SeBackupPrivilege)
/// needed for Volume Shadow Copy Service (VSS) snapshots.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsPrivilegeChecker
{
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenPrivileges = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint ProcessHandle, uint DesiredAccess, out SafeProcessTokenHandle TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeProcessTokenHandle TokenHandle, int TokenInformationClass, nint TokenInformation, uint TokenInformationLength, out uint ReturnLength);

    private sealed class SafeProcessTokenHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeProcessTokenHandle() : base(true) { }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(nint hObject);

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    /// <summary>True when running in an elevated administrative security context.</summary>
    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks whether the current process token possesses the named privilege (such as "SeBackupPrivilege").
    /// </summary>
    public static bool HasPrivilege(string privilegeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privilegeName);

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            if (!LookupPrivilegeValueW(null, privilegeName, out var targetLuid))
            {
                return false;
            }

            using var process = Process.GetCurrentProcess();
            if (!OpenProcessToken(process.Handle, TOKEN_QUERY, out var tokenHandle) || tokenHandle.IsInvalid)
            {
                return false;
            }

            using (tokenHandle)
            {
                // First call to determine buffer size
                GetTokenInformation(tokenHandle, TokenPrivileges, nint.Zero, 0, out var lengthNeeded);
                if (lengthNeeded == 0)
                {
                    return false;
                }

                var buffer = Marshal.AllocHGlobal((int)lengthNeeded);
                try
                {
                    if (!GetTokenInformation(tokenHandle, TokenPrivileges, buffer, lengthNeeded, out _))
                    {
                        return false;
                    }

                    var privilegeCount = Marshal.ReadInt32(buffer);
                    var offset = nint.Size == 8 ? 8 : 4; // LUID_AND_ATTRIBUTES array offset

                    var attrSize = Marshal.SizeOf<LUID_AND_ATTRIBUTES>();
                    for (var i = 0; i < privilegeCount; i++)
                    {
                        var entryPtr = buffer + offset + (i * attrSize);
                        var entry = Marshal.PtrToStructure<LUID_AND_ATTRIBUTES>(entryPtr);
                        if (entry.Luid.LowPart == targetLuid.LowPart && entry.Luid.HighPart == targetLuid.HighPart)
                        {
                            return true;
                        }
                    }

                    return false;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        catch
        {
            return false;
        }
    }
}
