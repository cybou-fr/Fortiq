using Fortiq.Domain;

namespace Fortiq.Domain.Tests;

public sealed class BackupJobTests
{
    [Fact]
    public void HappyPathReachesSucceededOnlyAfterReceiptVerification()
    {
        var job = new BackupJob(Guid.NewGuid());

        job.TransitionTo(BackupJobState.PreparingSource);
        job.TransitionTo(BackupJobState.AcquiringKey);
        job.TransitionTo(BackupJobState.RunningEngine);
        job.TransitionTo(BackupJobState.VerifyingReceipt);
        job.TransitionTo(BackupJobState.Succeeded);

        Assert.Equal(BackupJobState.Succeeded, job.State);
    }

    [Fact]
    public void RunningEngineCannotSkipReceiptVerification()
    {
        var job = RunningJob();

        var error = Assert.Throws<InvalidOperationException>(() => job.TransitionTo(BackupJobState.Succeeded));

        Assert.Contains("RunningEngine", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CrashRequiresReconciliation()
    {
        var job = RunningJob();

        job.TransitionTo(BackupJobState.Interrupted);
        job.TransitionTo(BackupJobState.ReconciliationRequired);

        Assert.Equal(BackupJobState.ReconciliationRequired, job.State);
    }

    [Fact]
    public void TerminalStateRejectsFurtherTransition()
    {
        var job = RunningJob();
        job.TransitionTo(BackupJobState.Failed);

        Assert.Throws<InvalidOperationException>(() => job.TransitionTo(BackupJobState.Created));
    }

    private static BackupJob RunningJob()
    {
        var job = new BackupJob(Guid.NewGuid());
        job.TransitionTo(BackupJobState.PreparingSource);
        job.TransitionTo(BackupJobState.AcquiringKey);
        job.TransitionTo(BackupJobState.RunningEngine);
        return job;
    }
}
