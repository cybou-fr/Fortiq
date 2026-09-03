using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Fortiq.Platform.Windows;

/// <summary>Identifies a file by the object it is, not by the path that led to it.</summary>
public readonly record struct FileIdentity(uint VolumeSerialNumber, ulong FileIndex);

/// <summary>
/// A file held open so it cannot be replaced, together with its identity. Holding the handle with
/// <see cref="FileShare.Read"/> denies writes, deletes and renames; comparing the identity catches
/// the case where a directory above the file is repointed so the same path leads elsewhere.
/// </summary>
public sealed class PinnedFile : IDisposable
{
    private readonly FileStream _stream;

    private PinnedFile(FileStream stream, FileIdentity? identity)
    {
        _stream = stream;
        Identity = identity;
    }

    public string Path => _stream.Name;

    public long Length => _stream.Length;

    /// <summary>Null where the platform does not expose a file identity, in which case it is not faked.</summary>
    public FileIdentity? Identity { get; }

    public static PinnedFile Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            return new PinnedFile(stream, ReadIdentity(stream.SafeFileHandle));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public Stream Content
    {
        get
        {
            _stream.Position = 0;
            return _stream;
        }
    }

    /// <summary>True when <paramref name="path"/> currently resolves to the very file that is pinned.</summary>
    public bool IsSameFileAs(string path)
    {
        if (Identity is not { } pinned)
        {
            return true;
        }

        try
        {
            using var candidate = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return ReadIdentity(candidate.SafeFileHandle) == pinned;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose() => _stream.Dispose();

    private static FileIdentity? ReadIdentity(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return GetFileInformationByHandle(handle, out var information)
            ? new FileIdentity(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow)
            : throw new IOException("Failed to read the identity of a pinned file.");
    }

    // DllImport rather than LibraryImport: the generated marshalling code requires unsafe blocks.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
