using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;

namespace Fortiq.Setup;

/// <summary>
/// The one file somebody downloads. It carries the whole package inside it, unpacks it, and starts
/// Fortiq - which then offers to install itself or to run from where it is.
/// </summary>
/// <remarks>
/// A single file rather than a folder because that is what people expect to download, and because a
/// person who has just lost data should not have to understand a directory layout first.
///
/// It unpacks beside the user's own profile rather than into a temporary directory that is deleted
/// on exit: the files are in use for as long as Fortiq is open, and a setup program that deletes
/// what is running has to either wait around or leave rubbish behind. Unpacking to a stable place
/// keyed by version also makes a second run nearly instant, and makes it obvious what to delete.
/// </remarks>
internal static class Program
{
    private const string PayloadResource = "fortiq-payload.zip";

    private static int Main()
    {
        Console.WriteLine($"Fortiq {Version()}");
        Console.WriteLine();

        try
        {
            var target = Unpack();
            var desktop = Path.Combine(target, "desktop", "Fortiq.Desktop.exe");
            if (!File.Exists(desktop))
            {
                return Fail($"The package unpacked, but {desktop} is not in it. This download is incomplete.");
            }

            Console.WriteLine("Starting Fortiq…");
            Process.Start(new ProcessStartInfo(desktop)
            {
                WorkingDirectory = Path.GetDirectoryName(desktop)!,
                UseShellExecute = true
            });

            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Fail(error.Message);
        }
    }

    /// <summary>Writes the payload out, unless a complete copy of this version is already there.</summary>
    private static string Unpack()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Fortiq",
            "package",
            Version());

        // Written only when the last file is out. A run interrupted half way leaves no marker, so
        // the next run unpacks again rather than starting a partial copy.
        var marker = Path.Combine(root, ".complete");
        if (File.Exists(marker))
        {
            Console.WriteLine($"Already unpacked at {root}");
            return root;
        }

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource)
            ?? throw new InvalidDataException(
                "This installer was built without its payload, so there is nothing to install. "
                + "Download the release build rather than one produced from a bare checkout.");

        Console.WriteLine($"Unpacking to {root}");
        Directory.CreateDirectory(root);

        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        var written = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/'))
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(root, entry.FullName));

            // A zip entry naming its way out of the directory it is extracted into is the oldest
            // trick there is. This payload is our own, and it is still checked.
            if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The package contains an entry pointing outside it: {entry.FullName}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);

            if (++written % 100 == 0)
            {
                Console.WriteLine($"  {written} files…");
            }
        }

        File.WriteAllText(marker, Version());
        Console.WriteLine($"  {written} files.");
        return root;
    }

    private static string Version()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return "0.0.0";
        }

        // Trimmed at the source-revision suffix MSBuild appends: it is not part of the version a
        // person reads, and it would make a new directory out of every rebuild.
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine(message);
        Console.Error.WriteLine();
        Console.Error.WriteLine("Press any key to close.");
        if (!Console.IsInputRedirected)
        {
            Console.ReadKey(intercept: true);
        }

        return 1;
    }
}
