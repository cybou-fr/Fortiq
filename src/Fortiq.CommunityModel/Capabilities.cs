namespace Fortiq.CommunityModel;

/// <summary>
/// What this build of Fortiq can actually do.
/// </summary>
/// <param name="Engine">The one repository engine, named and versioned.</param>
/// <param name="Backends">The storage backends that work.</param>
/// <param name="Triggers">The trigger kinds that work, by type name.</param>
/// <remarks>
/// One source of truth, consulted by two things that must never disagree: the validator that refuses
/// a proposal, and the context that tells the assistant what it may propose. Two separate lists
/// would drift within a release, and the symptom would be an assistant confidently offering a
/// file-change trigger that is then rejected the moment somebody accepts it - which reads as the
/// product being broken rather than the feature being absent.
///
/// The model deliberately contains more than this. <see cref="FileChangeTrigger"/> and
/// <see cref="StorageBackend.Sftp"/> exist because that is where this is going, and a model that
/// could only express what is already built would have to be rewritten to build anything.
/// </remarks>
public sealed record CommunityCapabilities(
    RepositoryEngineRef Engine,
    IReadOnlyList<StorageBackend> Backends,
    IReadOnlyList<string> Triggers)
{
    /// <summary>What the shipping Community build supports today.</summary>
    public static CommunityCapabilities Current { get; } = new(
        new RepositoryEngineRef("engine-restic", "restic", "0.19.1"),
        [StorageBackend.FileSystem, StorageBackend.S3],
        [nameof(ManualTrigger), nameof(OnceTrigger), nameof(IntervalTrigger), nameof(DailyTrigger)]);

    public bool Supports(StorageBackend backend) => Backends.Contains(backend);

    public bool Supports(Trigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        return Triggers.Contains(trigger.GetType().Name, StringComparer.Ordinal);
    }
}
