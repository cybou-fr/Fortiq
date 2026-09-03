using Fortiq.Domain;

namespace Fortiq.Infrastructure.Restic;

/// <summary>
/// The metadata Fortiq stores inside the repository, as engine tags. Recovery reads the identity of
/// a source from the repository itself; it never needs an operation receipt or any other local file
/// that could be lost with the machine that produced it.
/// </summary>
internal static class ResticSnapshotMetadata
{
    /// <summary>Marks a snapshot as carrying version 1 of the Fortiq metadata.</summary>
    internal const string SchemaTag = "fortiq.v1";

    private const string SourcePrefix = "fortiq.source=";

    /// <summary>
    /// Builds the value of a single <c>--tag</c> argument. Restic splits that value on commas, which
    /// is why a source identifier may not contain one.
    /// </summary>
    internal static string TagArgument(string sourceStableId) =>
        $"{SchemaTag},{SourcePrefix}{SourceIdentifier.Require(sourceStableId, nameof(sourceStableId))}";

    /// <summary>
    /// Reads the stable source identity back. A snapshot without Fortiq metadata, or with a value
    /// that is not a valid identifier, reports null rather than a guess.
    /// </summary>
    internal static string? ReadSourceStableId(IReadOnlyList<string> tags)
    {
        if (!tags.Contains(SchemaTag, StringComparer.Ordinal))
        {
            return null;
        }

        var values = tags
            .Where(tag => tag.StartsWith(SourcePrefix, StringComparison.Ordinal))
            .Select(tag => tag[SourcePrefix.Length..])
            .ToArray();

        // Two different source identities on one snapshot is a contradiction, not a choice to make.
        return values.Length == 1 && SourceIdentifier.IsValid(values[0]) ? values[0] : null;
    }
}
