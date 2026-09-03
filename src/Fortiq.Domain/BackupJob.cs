namespace Fortiq.Domain;

public enum BackupJobState
{
    Created,
    PreparingSource,
    AcquiringKey,
    RunningEngine,
    VerifyingReceipt,
    Cancelling,
    Cancelled,
    Interrupted,
    ReconciliationRequired,
    Failed,
    Succeeded
}

public sealed class BackupJob
{
    private static readonly Dictionary<BackupJobState, HashSet<BackupJobState>> AllowedTransitions =
        new Dictionary<BackupJobState, HashSet<BackupJobState>>
        {
            [BackupJobState.Created] = Set(BackupJobState.PreparingSource, BackupJobState.Cancelling, BackupJobState.Failed),
            [BackupJobState.PreparingSource] = Set(BackupJobState.AcquiringKey, BackupJobState.Cancelling, BackupJobState.Failed),
            [BackupJobState.AcquiringKey] = Set(BackupJobState.RunningEngine, BackupJobState.Cancelling, BackupJobState.Failed),
            [BackupJobState.RunningEngine] = Set(BackupJobState.VerifyingReceipt, BackupJobState.Cancelling, BackupJobState.Interrupted, BackupJobState.Failed),
            [BackupJobState.VerifyingReceipt] = Set(BackupJobState.Succeeded, BackupJobState.Cancelling, BackupJobState.Failed),
            [BackupJobState.Cancelling] = Set(BackupJobState.Cancelled, BackupJobState.Failed),
            [BackupJobState.Interrupted] = Set(BackupJobState.ReconciliationRequired),
            [BackupJobState.Cancelled] = Set(),
            [BackupJobState.ReconciliationRequired] = Set(),
            [BackupJobState.Failed] = Set(),
            [BackupJobState.Succeeded] = Set()
        };

    public BackupJob(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("Operation ID cannot be empty.", nameof(operationId));
        }

        OperationId = operationId;
    }

    public Guid OperationId { get; }

    public BackupJobState State { get; private set; } = BackupJobState.Created;

    public void TransitionTo(BackupJobState next)
    {
        if (!AllowedTransitions[State].Contains(next))
        {
            throw new InvalidOperationException($"Backup job cannot transition from {State} to {next}.");
        }

        State = next;
    }

    private static HashSet<BackupJobState> Set(params BackupJobState[] states) => states.ToHashSet();
}
