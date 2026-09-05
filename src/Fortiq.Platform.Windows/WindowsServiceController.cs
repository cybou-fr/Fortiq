using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Fortiq.Platform.Windows;

/// <summary>Status information queried from the Windows Service Control Manager.</summary>
public sealed record WindowsServiceInfo(
    bool Exists,
    bool Running,
    uint CurrentState,
    uint ServiceSidType,
    string? BinaryPath,
    string? AccountName,
    uint StartType);

/// <summary>
/// Provides native access to the Windows Service Control Manager (SCM) to query, create,
/// configure Service SIDs, start, stop and delete Windows Services without shell scripts or batch files.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsServiceController
{
    public const string DefaultServiceName = "Fortiq";
    public const string DefaultDisplayName = "Fortiq Protection Service";
    public const string DefaultDescription = "Continuous disaster recovery protection service for Fortiq.";

    // SCM Access Rights
    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerCreateService = 0x0002;
    private const uint ScManagerEnumerateService = 0x0004;
    private const uint ScManagerAllAccess = 0xF003F;

    // Service Access Rights
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStartAccess = 0x0010;
    private const uint ServiceStopAccess = 0x0020;
    private const uint StandardDelete = 0x00010000;
    private const uint ServiceAllAccess = 0xF01FF;

    // Service Types
    public const uint ServiceWin32OwnProcess = 0x00000010;

    // Service Start Types
    public const uint ServiceAutoStart = 0x00000002;
    public const uint ServiceDemandStart = 0x00000003;
    public const uint ServiceDisabled = 0x00000004;

    // Service Error Control
    private const uint ServiceErrorNormal = 0x00000001;

    // Service States
    public const uint ServiceStopped = 0x00000001;
    public const uint ServiceStartPending = 0x00000002;
    public const uint ServiceStopPending = 0x00000003;
    public const uint ServiceRunning = 0x00000004;

    // Controls
    private const uint ServiceControlStop = 0x00000001;

    // Config Levels
    private const uint ServiceConfigDescription = 1;
    private const uint ServiceConfigServiceSidInfo = 5;

    // Service SID Types
    public const uint ServiceSidTypeNone = 0;
    public const uint ServiceSidTypeUnrestricted = 1;
    public const uint ServiceSidTypeRestricted = 3;

    private const int ScStatusProcessInfo = 0;
    private const int ErrorServiceDoesNotExist = 1060;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_SID_INFO
    {
        public uint dwServiceSidType;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SERVICE_DESCRIPTION
    {
        public string? lpDescription;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct QUERY_SERVICE_CONFIG
    {
        public uint dwServiceType;
        public uint dwStartType;
        public uint dwErrorControl;
        public nint lpBinaryPathName;
        public nint lpLoadOrderGroup;
        public uint dwTagId;
        public nint lpDependencies;
        public nint lpServiceStartName;
        public nint lpDisplayName;
    }

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeServiceHandle() : base(true) { }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(nint hSCObject);

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeServiceHandle OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeServiceHandle CreateServiceW(
        SafeServiceHandle hSCManager,
        string lpServiceName,
        string? lpDisplayName,
        uint dwDesiredAccess,
        uint dwServiceType,
        uint dwStartType,
        uint dwErrorControl,
        string lpBinaryPathName,
        string? lpLoadOrderGroup,
        nint lpdwTagId,
        string? lpDependencies,
        string? lpServiceStartName,
        string? lpPassword);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeServiceHandle OpenServiceW(SafeServiceHandle hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool StartServiceW(SafeServiceHandle hService, uint dwNumServiceArgs, nint lpServiceArgVectors);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(SafeServiceHandle hService, uint dwControl, ref SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(SafeServiceHandle hService);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ChangeServiceConfig2W(SafeServiceHandle hService, uint dwInfoLevel, nint lpInfo);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(SafeServiceHandle hService, int infoLevel, nint lpBuffer, uint cbBufSize, out uint pcbBytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceConfigW(SafeServiceHandle hService, nint lpBuffer, uint cbBufSize, out uint pcbBytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceConfig2W(SafeServiceHandle hService, uint dwInfoLevel, nint lpBuffer, uint cbBufSize, out uint pcbBytesNeeded);

    /// <summary>
    /// Computes the deterministic Service SID (S-1-5-80-...) for a service name,
    /// according to the Windows Service SID algorithm: SHA-1 of the upper-cased UTF-16LE service name.
    /// </summary>
    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "Windows Service SID algorithm specification mandates SHA-1.")]
    public static SecurityIdentifier ComputeServiceSid(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        // Try NTAccount resolution first (if service already registered and active in LSA)
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var account = new NTAccount(@"NT SERVICE\" + serviceName);
                if (account.Translate(typeof(SecurityIdentifier)) is SecurityIdentifier resolved)
                {
                    return resolved;
                }
            }
            catch
            {
                // Fall back to direct algorithmic calculation
            }
        }

        var bytes = Encoding.Unicode.GetBytes(serviceName.ToUpperInvariant());
        var hash = SHA1.HashData(bytes);
        var u0 = BitConverter.ToUInt32(hash, 0);
        var u1 = BitConverter.ToUInt32(hash, 4);
        var u2 = BitConverter.ToUInt32(hash, 8);
        var u3 = BitConverter.ToUInt32(hash, 12);
        var u4 = BitConverter.ToUInt32(hash, 16);

        return new SecurityIdentifier($"S-1-5-80-{u0}-{u1}-{u2}-{u3}-{u4}");
    }

    /// <summary>Queries the current registration and run status of the named service.</summary>
    public static WindowsServiceInfo QueryStatus(string serviceName = DefaultServiceName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsServiceInfo(false, false, 0, 0, null, null, 0);
        }

        using var scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm.IsInvalid)
        {
            return new WindowsServiceInfo(false, false, 0, 0, null, null, 0);
        }

        using var service = OpenServiceW(scm, serviceName, ServiceQueryStatus | ServiceQueryConfig);
        if (service.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorServiceDoesNotExist)
            {
                return new WindowsServiceInfo(false, false, 0, 0, null, null, 0);
            }

            // Retry with only ServiceQueryStatus if query config was denied
            using var queryOnly = OpenServiceW(scm, serviceName, ServiceQueryStatus);
            if (queryOnly.IsInvalid)
            {
                return new WindowsServiceInfo(false, false, 0, 0, null, null, 0);
            }

            var statusOnly = QueryStatusCore(queryOnly);
            return new WindowsServiceInfo(true, statusOnly.dwCurrentState == ServiceRunning, statusOnly.dwCurrentState, 0, null, null, 0);
        }

        var status = QueryStatusCore(service);
        var isRunning = status.dwCurrentState == ServiceRunning;
        var (binaryPath, account, startType) = QueryConfigCore(service);
        var sidType = QuerySidTypeCore(service);

        return new WindowsServiceInfo(true, isRunning, status.dwCurrentState, sidType, binaryPath, account, startType);
    }

    /// <summary>
    /// Creates and configures the Windows Service with least-privilege Service SID (SERVICE_SID_TYPE_UNRESTRICTED).
    /// Requires administrative elevation.
    /// </summary>
    public static void CreateAndConfigureService(
        string serviceName,
        string displayName,
        string binaryPath,
        string? description = null,
        uint startType = ServiceAutoStart,
        bool setServiceSidUnrestricted = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryPath);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Service creation is only supported on Windows.");
        }

        // Quote binary path if not quoted
        var formattedBinaryPath = binaryPath.StartsWith('\"') ? binaryPath : $"\"{binaryPath}\"";

        using var scm = OpenSCManagerW(null, null, ScManagerConnect | ScManagerCreateService);
        if (scm.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to open Service Control Manager for service creation. Ensure the process is running elevated.");
        }

        using var service = CreateServiceW(
            scm,
            serviceName,
            displayName,
            ServiceAllAccess,
            ServiceWin32OwnProcess,
            startType,
            ServiceErrorNormal,
            formattedBinaryPath,
            null,
            nint.Zero,
            null,
            null, // null = LocalSystem; with SERVICE_SID_TYPE_UNRESTRICTED, NT SERVICE\ServiceName token is added
            null);

        if (service.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to create service '{serviceName}'.");
        }

        if (setServiceSidUnrestricted)
        {
            SetServiceSidType(service, ServiceSidTypeUnrestricted);
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            SetServiceDescription(service, description);
        }
    }

    /// <summary>Starts the named service and awaits SERVICE_RUNNING up to the specified timeout.</summary>
    public static bool StartService(string serviceName, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to open Service Control Manager.");
        }

        using var service = OpenServiceW(scm, serviceName, ServiceStartAccess | ServiceQueryStatus);
        if (service.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to open service '{serviceName}' for start.");
        }

        var status = QueryStatusCore(service);
        if (status.dwCurrentState == ServiceRunning)
        {
            return true;
        }

        if (status.dwCurrentState == ServiceStopped)
        {
            if (!StartServiceW(service, 0, nint.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                // 1056 = ERROR_SERVICE_ALREADY_RUNNING
                if (error != 1056)
                {
                    throw new Win32Exception(error, $"Failed to start service '{serviceName}'.");
                }
            }
        }

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            status = QueryStatusCore(service);
            if (status.dwCurrentState == ServiceRunning)
            {
                return true;
            }
            if (status.dwCurrentState == ServiceStopped && stopwatch.ElapsedMilliseconds > 500)
            {
                throw new InvalidOperationException($"Service '{serviceName}' stopped immediately after starting. Check Event Log or service logs.");
            }

            Thread.Sleep(200);
        }

        return false;
    }

    /// <summary>Stops the named service and awaits SERVICE_STOPPED up to the specified timeout.</summary>
    public static bool StopService(string serviceName, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm.IsInvalid)
        {
            return false;
        }

        using var service = OpenServiceW(scm, serviceName, ServiceStopAccess | ServiceQueryStatus);
        if (service.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorServiceDoesNotExist)
            {
                return true;
            }
            return false;
        }

        var status = QueryStatusCore(service);
        if (status.dwCurrentState == ServiceStopped)
        {
            return true;
        }

        var serviceStatus = new SERVICE_STATUS();
        _ = ControlService(service, ServiceControlStop, ref serviceStatus);

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            status = QueryStatusCore(service);
            if (status.dwCurrentState == ServiceStopped)
            {
                return true;
            }
            Thread.Sleep(200);
        }

        return false;
    }

    /// <summary>Deletes the named service registration. Requires administrative elevation.</summary>
    public static bool DeleteService(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm.IsInvalid)
        {
            return false;
        }

        using var service = OpenServiceW(scm, serviceName, StandardDelete);
        if (service.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            return error == ErrorServiceDoesNotExist;
        }

        return DeleteService(service);
    }

    private static void SetServiceSidType(SafeServiceHandle service, uint sidType)
    {
        var sidInfo = new SERVICE_SID_INFO { dwServiceSidType = sidType };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<SERVICE_SID_INFO>());
        try
        {
            Marshal.StructureToPtr(sidInfo, ptr, false);
            if (!ChangeServiceConfig2W(service, ServiceConfigServiceSidInfo, ptr))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to configure Service SID type.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static void SetServiceDescription(SafeServiceHandle service, string description)
    {
        var desc = new SERVICE_DESCRIPTION { lpDescription = description };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<SERVICE_DESCRIPTION>());
        try
        {
            Marshal.StructureToPtr(desc, ptr, false);
            _ = ChangeServiceConfig2W(service, ServiceConfigDescription, ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static SERVICE_STATUS_PROCESS QueryStatusCore(SafeServiceHandle service)
    {
        var size = Marshal.SizeOf<SERVICE_STATUS_PROCESS>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryServiceStatusEx(service, ScStatusProcessInfo, ptr, (uint)size, out _))
            {
                return default;
            }
            return Marshal.PtrToStructure<SERVICE_STATUS_PROCESS>(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static (string? BinaryPath, string? Account, uint StartType) QueryConfigCore(SafeServiceHandle service)
    {
        QueryServiceConfigW(service, nint.Zero, 0, out var needed);
        if (needed == 0)
        {
            return (null, null, 0);
        }

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!QueryServiceConfigW(service, buffer, needed, out _))
            {
                return (null, null, 0);
            }

            var config = Marshal.PtrToStructure<QUERY_SERVICE_CONFIG>(buffer);
            var binaryPath = Marshal.PtrToStringUni(config.lpBinaryPathName);
            var account = Marshal.PtrToStringUni(config.lpServiceStartName);
            return (binaryPath, account, config.dwStartType);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static uint QuerySidTypeCore(SafeServiceHandle service)
    {
        QueryServiceConfig2W(service, ServiceConfigServiceSidInfo, nint.Zero, 0, out var needed);
        if (needed == 0)
        {
            return 0;
        }

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!QueryServiceConfig2W(service, ServiceConfigServiceSidInfo, buffer, needed, out _))
            {
                return 0;
            }

            var sidInfo = Marshal.PtrToStructure<SERVICE_SID_INFO>(buffer);
            return sidInfo.dwServiceSidType;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
