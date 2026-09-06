using System.IO.Compression;
using System.Security.Cryptography;

namespace Fortiq.Setup;

/// <summary>Checks cached bytes against the embedded archive, never against a writable manifest.</summary>
public static class PackageCache
{
    public static string Prepare(Stream payload, string cacheDirectory)
    {
        var cache = Path.GetFullPath(cacheDirectory);
        Directory.CreateDirectory(cache);
        if ((File.GetAttributes(cache) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The package cache must not be a link.");
        // Serialise setup writers. A second launcher can retry once the first has finished.
        using var gate = new FileStream(Path.Combine(cache, "extract.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);
        var entries = archive.Entries.Where(entry => !entry.FullName.EndsWith('/')).ToArray();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var relative = Path.GetRelativePath(cache, Destination(cache, entry.FullName));
            if (!names.Add(relative)) throw new InvalidDataException("The package contains duplicate paths.");
        }

        var pointer = Path.Combine(cache, "current.complete");
        if (File.Exists(pointer))
        {
            var name = File.ReadAllText(pointer);
            if (Guid.TryParseExact(name, "N", out _))
            {
                var existing = Path.Combine(cache, name);
                if (Matches(existing, entries, names)) return existing;
            }
        }

        // Never delete an old copy: a desktop or a portable repository may still use it.
        var generation = Guid.NewGuid().ToString("N");
        var root = Path.Combine(cache, generation);
        Directory.CreateDirectory(root);
        foreach (var entry in entries)
        {
            var target = Destination(root, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target);
        }
        if (!Matches(root, entries, names)) throw new InvalidDataException("The extracted package failed verification.");
        File.WriteAllText(pointer, generation);
        return root;
    }

    private static string Destination(string root, string name)
    {
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || name.Contains(':'))
            throw new InvalidDataException("The package contains a path outside its directory.");
        return path;
    }

    private static bool Matches(string root, ZipArchiveEntry[] entries, HashSet<string> names)
    {
        if (!Directory.Exists(root)) return false;
        try
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out var directory))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                    else if (!names.Contains(Path.GetRelativePath(root, path))) return false;
                }
            }
            foreach (var entry in entries)
            {
                var path = Destination(root, entry.FullName);
                if (!File.Exists(path) || new FileInfo(path).Length != entry.Length) return false;
                using var actual = File.OpenRead(path);
                using var expected = entry.Open();
                if (!SHA256.HashData(actual).AsSpan().SequenceEqual(SHA256.HashData(expected))) return false;
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
