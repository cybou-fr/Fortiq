using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Fortiq.Domain;
using Microsoft.Win32.SafeHandles;

namespace Fortiq.Platform.Windows;

public sealed record UsnJournalInfo(
    ulong VolumeSerial,
    ulong JournalId,
    long FirstUsn,
    long NextUsn,
    long LowestValidUsn);

public interface IUsnJournalReader
{
    bool IsSupported(string volumePath);
    UsnJournalInfo QueryJournal(string volumePath);
    IReadOnlyList<UsnChangeEntry> ReadChanges(string volumePath, ulong journalId, long startUsn, long maxRecords = 10000);
}

/// <summary>
/// Parser for raw binary USN records returned by FSCTL_READ_USN_JOURNAL.
/// Pure and safe for memory buffer evaluation and testing.
/// </summary>
public static class NtfsUsnRecordParser
{
    [StructLayout(LayoutKind.Sequential)]
    public struct UsnRecordHeader
    {
        public uint RecordLength;
        public ushort MajorVersion;
        public ushort MinorVersion;
        public ulong FileReferenceNumber;
        public ulong ParentFileReferenceNumber;
        public long Usn;
        public long TimeStamp;
        public uint Reason;
        public uint SourceInfo;
        public uint SecurityId;
        public uint FileAttributes;
        public ushort FileNameLength;
        public ushort FileNameOffset;
    }

    public static IReadOnlyList<UsnChangeEntry> ParseRecords(ReadOnlySpan<byte> buffer, out long nextUsn)
    {
        if (buffer.Length < sizeof(long))
        {
            nextUsn = 0;
            return [];
        }

        nextUsn = MemoryMarshal.Read<long>(buffer);
        var entries = new List<UsnChangeEntry>();
        var offset = sizeof(long);
        var headerSize = Marshal.SizeOf<UsnRecordHeader>();

        while (offset + headerSize <= buffer.Length)
        {
            var headerSpan = buffer.Slice(offset, headerSize);
            var header = MemoryMarshal.Read<UsnRecordHeader>(headerSpan);

            if (header.RecordLength == 0)
            {
                break;
            }

            if (offset + header.RecordLength > buffer.Length)
            {
                break;
            }

            string fileName = string.Empty;
            if (header.FileNameOffset > 0 && header.FileNameLength > 0 &&
                offset + header.FileNameOffset + header.FileNameLength <= buffer.Length)
            {
                var nameBytes = buffer.Slice(offset + header.FileNameOffset, header.FileNameLength);
                fileName = Encoding.Unicode.GetString(nameBytes);
            }

            var timestamp = DateTimeOffset.FromFileTime(header.TimeStamp);
            entries.Add(new UsnChangeEntry(
                header.FileReferenceNumber,
                header.ParentFileReferenceNumber,
                header.Usn,
                fileName,
                (UsnChangeReasons)header.Reason,
                timestamp));

            offset += (int)header.RecordLength;
        }

        return entries;
    }
}

/// <summary>
/// Low-level NTFS USN Change Journal reader utilizing Win32 FSCTL device control calls.
/// Only opens raw volume handles inside this privileged component.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NtfsUsnJournalReader : IUsnJournalReader
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    private const uint FsctlQueryUsnJournal = 0x000900f4;
    private const uint FsctlReadUsnJournal = 0x000900bb;

    [StructLayout(LayoutKind.Sequential)]
    private struct UsnJournalDataV0
    {
        public ulong UsnJournalID;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReadUsnJournalDataV0
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalID;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            nint lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            nint hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            nint lpInBuffer,
            uint nInBufferSize,
            nint lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            nint lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern unsafe bool GetVolumeInformationW(
            string lpRootPathName,
            char* lpVolumeNameBuffer,
            int nVolumeNameSize,
            out uint lpVolumeSerialNumber,
            out uint lpMaximumComponentLength,
            out uint lpFileSystemFlags,
            char* lpFileSystemNameBuffer,
            int nFileSystemNameSize);
    }

    public static string NormalizeVolumePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root))
        {
            throw new ArgumentException($"Cannot resolve volume root for path: {path}", nameof(path));
        }

        var drive = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return $@"\\.\{drive}";
    }

    public static string GetVolumeRootPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root))
        {
            throw new ArgumentException($"Cannot resolve volume root for path: {path}", nameof(path));
        }
        return root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
    }

    public bool IsSupported(string volumePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var root = GetVolumeRootPath(volumePath);
            unsafe
            {
                var fsName = stackalloc char[260];
                if (!NativeMethods.GetVolumeInformationW(root, null, 0, out _, out _, out _, fsName, 260))
                {
                    return false;
                }

                var fsNameStr = new string(fsName);
                return string.Equals(fsNameStr, "NTFS", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            return false;
        }
    }

    public UsnJournalInfo QueryJournal(string volumePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("NTFS USN Journal is only supported on Windows.");
        }

        var root = GetVolumeRootPath(volumePath);
        uint serial;
        string fsNameStr;
        unsafe
        {
            var fsName = stackalloc char[260];
            if (!NativeMethods.GetVolumeInformationW(root, null, 0, out serial, out _, out _, fsName, 260))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to query volume information for {root}");
            }
            fsNameStr = new string(fsName);
        }

        if (!string.Equals(fsNameStr, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Volume {root} filesystem is '{fsNameStr}', not NTFS.");
        }

        var devicePath = NormalizeVolumePath(volumePath);
        using var handle = NativeMethods.CreateFileW(
            devicePath,
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            0,
            OpenExisting,
            FileFlagBackupSemantics,
            0);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to open volume handle for {devicePath}");
        }

        var data = default(UsnJournalDataV0);
        var dataSize = (uint)Marshal.SizeOf<UsnJournalDataV0>();
        var dataPtr = Marshal.AllocHGlobal((int)dataSize);
        try
        {
            if (!NativeMethods.DeviceIoControl(handle, FsctlQueryUsnJournal, 0, 0, dataPtr, dataSize, out _, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"FSCTL_QUERY_USN_JOURNAL failed on {devicePath}");
            }
            data = Marshal.PtrToStructure<UsnJournalDataV0>(dataPtr);
        }
        finally
        {
            Marshal.FreeHGlobal(dataPtr);
        }

        return new UsnJournalInfo(serial, data.UsnJournalID, data.FirstUsn, data.NextUsn, data.LowestValidUsn);
    }

    public IReadOnlyList<UsnChangeEntry> ReadChanges(string volumePath, ulong journalId, long startUsn, long maxRecords = 10000)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("NTFS USN Journal is only supported on Windows.");
        }

        var devicePath = NormalizeVolumePath(volumePath);
        using var handle = NativeMethods.CreateFileW(
            devicePath,
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            0,
            OpenExisting,
            FileFlagBackupSemantics,
            0);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to open volume handle for {devicePath}");
        }

        const int bufferSize = 65536;
        var outBuffer = Marshal.AllocHGlobal(bufferSize);
        var inDataSize = (uint)Marshal.SizeOf<ReadUsnJournalDataV0>();
        var inBuffer = Marshal.AllocHGlobal((int)inDataSize);

        var allEntries = new List<UsnChangeEntry>();
        var currentStartUsn = startUsn;

        try
        {
            while (allEntries.Count < maxRecords)
            {
                var readData = new ReadUsnJournalDataV0
                {
                    StartUsn = currentStartUsn,
                    ReasonMask = 0xFFFFFFFF,
                    ReturnOnlyOnClose = 1,
                    Timeout = 0,
                    BytesToWaitFor = 0,
                    UsnJournalID = journalId
                };

                Marshal.StructureToPtr(readData, inBuffer, false);

                if (!NativeMethods.DeviceIoControl(
                    handle,
                    FsctlReadUsnJournal,
                    inBuffer,
                    inDataSize,
                    outBuffer,
                    bufferSize,
                    out var bytesReturned,
                    0))
                {
                    var error = Marshal.GetLastWin32Error();
                    // ERROR_HANDLE_EOF (38) means end of journal reached.
                    if (error == 38)
                    {
                        break;
                    }
                    throw new Win32Exception(error, $"FSCTL_READ_USN_JOURNAL failed at USN {currentStartUsn}");
                }

                if (bytesReturned <= sizeof(long))
                {
                    break;
                }

                var managedBuffer = new byte[bytesReturned];
                Marshal.Copy(outBuffer, managedBuffer, 0, (int)bytesReturned);

                var entries = NtfsUsnRecordParser.ParseRecords(managedBuffer, out var nextUsn);
                if (entries.Count == 0 || nextUsn <= currentStartUsn)
                {
                    break;
                }

                allEntries.AddRange(entries);
                currentStartUsn = nextUsn;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inBuffer);
            Marshal.FreeHGlobal(outBuffer);
        }

        return allEntries;
    }
}
