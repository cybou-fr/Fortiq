using Fortiq.Infrastructure.Restic;

namespace Fortiq.Desktop;

/// <summary>
/// Whether this copy of Fortiq has the engine it needs, asked at start-up rather than discovered
/// during somebody's first backup.
/// </summary>
/// <remarks>
/// The engine is not committed to the repository and is not part of the desktop's own build output: a
/// working copy has it because a script fetched it, and a released package has it because the bundle
/// carried it. That leaves several ordinary ways to end up without one - copying the desktop folder
/// out of the package on its own, unpacking an archive that did not finish, running a development
/// build on a machine where nobody ran the acquisition script.
///
/// Every one of those used to look completely normal until the first backup, which then failed
/// somewhere inside provisioning with a message about a manifest file. This says the same thing at the
/// moment the application opens, in a sentence that names what to do, because the difference between a
/// clear refusal and a confusing one is the whole of somebody's first impression.
///
/// It deliberately does not verify the binary's hash. That is <c>EngineBinaryVerifier</c>'s job, it is
/// done immediately before the engine is executed where it cannot be raced, and repeating it here
/// would be a slow start-up that proves nothing about the moment that matters.
/// </remarks>
public static class EngineAvailability
{
    /// <summary>The pinned engine this build runs, and the only one a package is expected to carry.</summary>
    private const string EngineName = "restic";
    private const string EngineRid = "win-x64";

    /// <summary>
    /// Null when the engine is where it should be; otherwise what is wrong, in words.
    /// </summary>
    public static async Task<string?> DescribeMissingAsync(string engineRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineRoot);

        var manifestPath = Path.Combine(engineRoot, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return $"Fortiq cannot find the backup engine it runs. It expected a description of it at "
                + $"'{manifestPath}'. If you copied one folder out of the Fortiq package, copy the whole "
                + "package instead - the engine lives beside the application and Fortiq cannot back "
                + "anything up without it.";
        }

        EngineManifest manifest;
        try
        {
            manifest = await EngineManifestReader.ReadAsync(manifestPath, cancellationToken);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return $"Fortiq found the description of its backup engine at '{manifestPath}' but could not "
                + $"read it: {error.Message} This copy of Fortiq is damaged; install the release again.";
        }

        var entry = manifest.Engines.SingleOrDefault(candidate => candidate.Name == EngineName && candidate.Rid == EngineRid);
        if (entry is null)
        {
            return $"The engine description at '{manifestPath}' does not name the {EngineName} build for "
                + $"{EngineRid} that this version of Fortiq runs. This copy of Fortiq is damaged; install "
                + "the release again.";
        }

        // The path is resolved and checked against the root it came from. A relative path that climbs
        // out of the engine directory is not something a package should contain, and reporting it as
        // "missing" would hide that.
        var enginePath = Path.GetFullPath(Path.Combine(engineRoot, entry.RelativePath));
        var root = Path.GetFullPath(engineRoot);
        if (!enginePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return $"The engine description at '{manifestPath}' points outside the engine folder. This copy "
                + "of Fortiq is damaged; install the release again.";
        }

        if (!File.Exists(enginePath))
        {
            return $"Fortiq's backup engine is missing. It should be at '{enginePath}', and the file is not "
                + "there. Install the Fortiq release again - the engine is part of the package.";
        }

        // Length only. The hash is checked immediately before execution, where the answer cannot go
        // stale between the check and the run; here it is enough to tell a truncated copy from a file.
        var actual = new FileInfo(enginePath).Length;
        return actual == entry.BinaryLength
            ? null
            : $"Fortiq's backup engine at '{enginePath}' is not the size it should be ({actual} bytes rather "
                + $"than {entry.BinaryLength}), so the copy is incomplete. Install the Fortiq release again.";
    }
}
