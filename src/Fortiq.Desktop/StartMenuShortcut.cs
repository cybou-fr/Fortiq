using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Fortiq.Desktop;

/// <summary>
/// Puts Fortiq in the Start menu, and takes it out again.
/// </summary>
/// <remarks>
/// Installing wrote the files, registered the service and left nothing a person could click. The
/// README had to apologise for it - "there is no Start Menu entry yet; run it from Program Files" -
/// which is a poor answer for a backup tool somebody opens once a month, and a worse one on the day
/// they are looking for it in a hurry.
///
/// A .lnk is written through IShellLink because that is the only thing Windows accepts. It is one
/// file, under All Users, so it works for whoever signs in on this PC - the installer is already
/// elevated when this runs.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class StartMenuShortcut
{
    /// <summary>The one entry Fortiq creates. A folder for a single item would be clutter.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        "Fortiq.lnk");

    /// <summary>Writes the shortcut, replacing any earlier one.</summary>
    public static void Create(string targetExePath, string? shortcutPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetExePath);

        var exe = Path.GetFullPath(targetExePath);
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException($"Cannot create a Start menu shortcut to '{exe}': it is not there.", exe);
        }

        var path = shortcutPath ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var link = (IShellLinkW)(object)new ShellLink();
        link.SetPath(exe);
        link.SetWorkingDirectory(Path.GetDirectoryName(exe)!);
        link.SetDescription("Fortiq - backups that prove they can be restored");
        link.SetIconLocation(exe, 0);
        ((IPersistFile)link).Save(path, fRemember: true);
    }

    /// <summary>Removes the shortcut. Returns false if it is still there afterwards.</summary>
    public static bool Remove(string? shortcutPath = null)
    {
        var path = shortcutPath ?? DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return !File.Exists(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink
    {
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int maxPath, IntPtr findData, int flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int maxArgs);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder iconPath, int iconPathLength, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, int reserved);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, int mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}
