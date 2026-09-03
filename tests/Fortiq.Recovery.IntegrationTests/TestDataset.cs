using System.Security.Cryptography;
using System.Text;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>Expected content of a single dataset entry, addressed by its path relative to the dataset root.</summary>
public sealed record DatasetEntry(string RelativePath, string Sha256, bool IsReadOnly);

/// <summary>
/// Builds the P0 test dataset described in docs/11-executable-prototype.md and the manifest used to
/// verify a restore. Sparse files, changing files and deliberately inaccessible files belong to the
/// negative and partial-failure tests and are not part of this builder.
/// </summary>
public static class TestDataset
{
    private const string LongPathSegment = "long-path-segment-used-to-exercise-windows-path-length";

    public static IReadOnlyList<DatasetEntry> Create(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Directory.CreateDirectory(root);

        var entries = new List<DatasetEntry>
        {
            WriteBytes(root, "empty.txt", []),
            WriteBytes(root, "small.txt", Encoding.UTF8.GetBytes("fortiq recovery assurance\n")),
            WriteBytes(root, "binary.bin", DeterministicBinary(64 * 1024)),
            WriteBytes(root, "unicode/данные-Ελλάδα-日本語.txt", Encoding.UTF8.GetBytes("multi-script name\n")),
            WriteBytes(
                root,
                string.Join('/', LongPathSegment, LongPathSegment, "deep.txt"),
                Encoding.UTF8.GetBytes("long but valid windows path\n")),
            WriteBytes(root, "read-only.txt", Encoding.UTF8.GetBytes("read only content\n"), readOnly: true)
        };

        return entries;
    }

    /// <summary>Clears the read-only attributes the builder set so the dataset can be deleted.</summary>
    public static void MakeWritable(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var file = new FileInfo(path);
            if (file.IsReadOnly)
            {
                file.IsReadOnly = false;
            }
        }
    }

    public static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static DatasetEntry WriteBytes(string root, string relativePath, byte[] content, bool readOnly = false)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        if (readOnly)
        {
            new FileInfo(path).IsReadOnly = true;
        }

        return new DatasetEntry(relativePath, Convert.ToHexStringLower(SHA256.HashData(content)), readOnly);
    }

    private static byte[] DeterministicBinary(int length)
    {
        var content = new byte[length];
        for (var index = 0; index < length; index++)
        {
            content[index] = (byte)(index * 31 % 251);
        }

        return content;
    }
}
