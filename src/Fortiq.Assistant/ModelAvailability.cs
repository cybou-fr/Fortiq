namespace Fortiq.Assistant;

/// <summary>What this machine has, and whether the assistant can run at all.</summary>
public enum ModelPresence
{
    /// <summary>The model is where the manifest says, and it is the size the manifest says.</summary>
    Present,

    /// <summary>No manifest was found, so this build does not know what it should be running.</summary>
    NoManifest,

    /// <summary>The manifest is unreadable or does not describe a model this build will run.</summary>
    ManifestUnusable,

    /// <summary>The manifest names a file that is not there.</summary>
    Missing,

    /// <summary>The file is there and is not the one the manifest describes.</summary>
    Wrong
}

/// <summary>Whether the assistant's model is on this machine, and what to say when it is not.</summary>
public sealed record ModelStatus(ModelPresence Presence, string? Detail, string? Path, ModelManifestEntry? Entry)
{
    /// <summary>True only when a model this build will run is present and the right size.</summary>
    public bool Usable => Presence == ModelPresence.Present;
}

/// <summary>
/// Finds the assistant's model and says plainly when it is not usable.
/// </summary>
/// <remarks>
/// The model is a required part of the desktop application: it is either in the installation package
/// or fetched during installation, and Fortiq does not offer a reduced experience without it. This
/// exists so that a machine where the file is missing or truncated says so at once, in words, rather
/// than failing somewhere inside the assistant later - the same reason <c>EngineAvailability</c>
/// exists for the backup engine, and the same failure it was written to stop.
///
/// The one thing this does not gate is recovery. <c>Fortiq.Recover</c> is a separate binary carried to
/// a machine that has never had Fortiq, on whatever the person had to hand; it has no model, needs
/// none, and nothing here is consulted on the path that gets somebody's data back.
///
/// The hash is checked when the model is acquired, not here. Re-hashing the whole file would add
/// seconds to every launch to re-answer a question installation already answered, and the failure
/// that actually happens afterwards - an interrupted copy - is caught by the length.
/// </remarks>
public static class ModelAvailability
{
    /// <summary>The model directory beside an installation, by convention.</summary>
    public const string DirectoryName = "models";

    public static async Task<ModelStatus> InspectAsync(string modelRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelRoot);

        var manifestPath = Path.Combine(modelRoot, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return new ModelStatus(
                ModelPresence.NoManifest,
                $"Fortiq cannot find the description of its assistant model at '{manifestPath}'. "
                + "Install the Fortiq release again - the model is part of the package.",
                null,
                null);
        }

        ModelManifest manifest;
        try
        {
            manifest = await ModelManifestReader.ReadAsync(manifestPath, cancellationToken);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return new ModelStatus(
                ModelPresence.ManifestUnusable,
                $"Fortiq could not read the description of its assistant model: {error.Message} "
                + "This copy of Fortiq is damaged; install the release again.",
                null,
                null);
        }

        var entry = manifest.Models[0];
        var path = Path.GetFullPath(Path.Combine(modelRoot, entry.RelativePath));
        var root = Path.GetFullPath(modelRoot);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return new ModelStatus(
                ModelPresence.ManifestUnusable,
                $"The model description at '{manifestPath}' points outside the model folder. "
                + "This copy of Fortiq is damaged; install the release again.",
                null,
                entry);
        }

        if (!File.Exists(path))
        {
            return new ModelStatus(
                ModelPresence.Missing,
                $"Fortiq's assistant model is missing. It should be at '{path}'. Install the Fortiq "
                + "release again, or run the model acquisition step, and it will be fetched and verified.",
                path,
                entry);
        }

        var actual = new FileInfo(path).Length;
        return actual == entry.FileLength
            ? new ModelStatus(ModelPresence.Present, null, path, entry)
            : new ModelStatus(
                ModelPresence.Wrong,
                $"Fortiq's assistant model at '{path}' is {actual} bytes rather than {entry.FileLength}, "
                + "so the copy is incomplete. Install the Fortiq release again.",
                path,
                entry);
    }
}
