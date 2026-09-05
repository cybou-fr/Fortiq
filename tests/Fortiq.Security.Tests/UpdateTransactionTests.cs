using System.Text;
using Fortiq.Infrastructure.Updates;

namespace Fortiq.Security.Tests;

/// <summary>
/// What an interrupted update leaves behind, and what picking it up again must produce.
/// </summary>
/// <remarks>
/// The failure this guards against is not a crash - crashes are expected. It is the start-up after the
/// crash, where an installation holding some binaries from one release and some from another looks
/// exactly like a working one, and every check that would have caught the mixture already ran.
/// </remarks>
public sealed class UpdateTransactionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "fortiq-update-tests",
        Guid.NewGuid().ToString("N"));

    private string Install => Path.Combine(_root, "install");

    private string Working => Path.Combine(_root, "updates");

    public UpdateTransactionTests()
    {
        Directory.CreateDirectory(Install);
        Directory.CreateDirectory(Working);
    }

    [Fact]
    public async Task ACommittedUpdateReplacesEveryDeclaredFile()
    {
        Installed("Fortiq.Service.exe", "release 1 service");
        Installed("engines/restic.exe", "release 1 engine");

        var transaction = await UpdateTransaction.BeginAsync(
            Working, Install, ["Fortiq.Service.exe", "engines/restic.exe"]);

        await transaction.StageAsync("Fortiq.Service.exe", Bytes("release 2 service"));
        await transaction.StageAsync("engines/restic.exe", Bytes("release 2 engine"));
        await transaction.CommitAsync();

        Assert.Equal("release 2 service", InstalledText("Fortiq.Service.exe"));
        Assert.Equal("release 2 engine", InstalledText("engines/restic.exe"));

        // Nothing is left to recover, and nothing is left on disk to grow without bound.
        Assert.Equal(UpdateRecoveryOutcome.NothingToRecover, await UpdateTransaction.RecoverAsync(Working));
        Assert.Empty(Directory.GetFileSystemEntries(Working));
    }

    [Fact]
    public async Task AnUpdateThatStagedOnlySomeOfItsFilesRefusesToCommit()
    {
        Installed("Fortiq.Service.exe", "release 1 service");
        Installed("Fortiq.Desktop.exe", "release 1 desktop");

        var transaction = await UpdateTransaction.BeginAsync(
            Working, Install, ["Fortiq.Service.exe", "Fortiq.Desktop.exe"]);

        await transaction.StageAsync("Fortiq.Service.exe", Bytes("release 2 service"));

        // Half a release installed is the mix-and-match state the metadata checks refuse to be served.
        // Arriving at it by giving up halfway would be no better.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync());

        Assert.Contains("Fortiq.Desktop.exe", error.Message, StringComparison.Ordinal);
        Assert.Equal("release 1 service", InstalledText("Fortiq.Service.exe"));
        Assert.Equal("release 1 desktop", InstalledText("Fortiq.Desktop.exe"));
    }

    [Fact]
    public async Task AFileTheUpdateNeverDeclaredCannotBeStaged()
    {
        var transaction = await UpdateTransaction.BeginAsync(Working, Install, ["Fortiq.Service.exe"]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => transaction.StageAsync("Fortiq.Desktop.exe", Bytes("smuggled")));
    }

    [Theory]
    [InlineData("../outside.exe")]
    [InlineData("engines/../../outside.exe")]
    public async Task AComponentPathThatEscapesTheInstallationIsRefused(string relativePath)
    {
        // The path comes from a signed targets document, but a signature says who wrote a name, not
        // that the name is safe to use as a path.
        await Assert.ThrowsAsync<ArgumentException>(
            () => UpdateTransaction.BeginAsync(Working, Install, [relativePath]));
    }

    [Fact]
    public async Task AnAbsoluteComponentPathIsRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => UpdateTransaction.BeginAsync(Working, Install, [Path.Combine(Path.GetTempPath(), "outside.exe")]));
    }

    // --- Interruption -------------------------------------------------------------------------

    [Fact]
    public async Task AnUpdateInterruptedBeforeAnythingWasSwappedLeavesTheInstallationAlone()
    {
        Installed("Fortiq.Service.exe", "release 1 service");

        var transaction = await UpdateTransaction.BeginAsync(Working, Install, ["Fortiq.Service.exe"]);
        await transaction.StageAsync("Fortiq.Service.exe", Bytes("release 2 service"));

        // The process ends here. The next start finds staged files and an intent that never reached
        // the swap, so there is nothing to put back - only work to throw away.
        Assert.Equal(UpdateRecoveryOutcome.StagingDiscarded, await UpdateTransaction.RecoverAsync(Working));
        Assert.Equal("release 1 service", InstalledText("Fortiq.Service.exe"));
    }

    [Fact]
    public async Task AnUpdateInterruptedMidSwapIsRolledBackToTheReleaseItStartedFrom()
    {
        Installed("a.exe", "release 1 a");
        Installed("b.exe", "release 1 b");

        var transaction = await UpdateTransaction.BeginAsync(Working, Install, ["a.exe", "b.exe"]);
        await transaction.StageAsync("a.exe", Bytes("release 2 a"));
        await transaction.StageAsync("b.exe", Bytes("release 2 b"));

        await SimulateCrashDuringSwap(transaction, afterFile: "a.exe");

        Assert.Equal("release 2 a", InstalledText("a.exe"));
        Assert.Equal("release 1 b", InstalledText("b.exe"));

        Assert.Equal(UpdateRecoveryOutcome.RolledBack, await UpdateTransaction.RecoverAsync(Working));

        // Both files are release 1 again. Leaving 'a' at release 2 would be the mixture that has no
        // corresponding signed release anywhere.
        Assert.Equal("release 1 a", InstalledText("a.exe"));
        Assert.Equal("release 1 b", InstalledText("b.exe"));
    }

    [Fact]
    public async Task AFileMissingWhenTheSwapWasInterruptedIsPutBack()
    {
        Installed("a.exe", "release 1 a");

        var transaction = await UpdateTransaction.BeginAsync(Working, Install, ["a.exe"]);
        await transaction.StageAsync("a.exe", Bytes("release 2 a"));

        // The narrowest window there is: the original has been moved aside and the replacement has not
        // arrived, so the installation is missing a binary altogether.
        await SimulateCrashDuringSwap(transaction, afterFile: null);
        Assert.False(File.Exists(Path.Combine(Install, "a.exe")));

        Assert.Equal(UpdateRecoveryOutcome.RolledBack, await UpdateTransaction.RecoverAsync(Working));
        Assert.Equal("release 1 a", InstalledText("a.exe"));
    }

    [Fact]
    public async Task RecoveryRunTwiceLeavesTheSameResultAsRunningItOnce()
    {
        Installed("a.exe", "release 1 a");

        var transaction = await UpdateTransaction.BeginAsync(Working, Install, ["a.exe"]);
        await transaction.StageAsync("a.exe", Bytes("release 2 a"));
        await SimulateCrashDuringSwap(transaction, afterFile: null);

        Assert.Equal(UpdateRecoveryOutcome.RolledBack, await UpdateTransaction.RecoverAsync(Working));

        // Recovery can itself be interrupted and run again. If the second pass did anything other than
        // nothing, a machine that crashed twice would end up worse than one that crashed once.
        Assert.Equal(UpdateRecoveryOutcome.NothingToRecover, await UpdateTransaction.RecoverAsync(Working));
        Assert.Equal("release 1 a", InstalledText("a.exe"));
    }

    [Fact]
    public async Task RollingBackAnUpdateThatAddedANewComponentRemovesIt()
    {
        Installed("a.exe", "release 1 a");

        var transaction = await UpdateTransaction.BeginAsync(Working, Install, ["a.exe", "new.exe"]);
        await transaction.StageAsync("a.exe", Bytes("release 2 a"));
        await transaction.StageAsync("new.exe", Bytes("release 2 addition"));

        await SimulateCrashDuringSwap(transaction, afterFile: "new.exe");
        Assert.True(File.Exists(Path.Combine(Install, "new.exe")));

        Assert.Equal(UpdateRecoveryOutcome.RolledBack, await UpdateTransaction.RecoverAsync(Working));

        // A component that release 1 never had must not survive a rollback to release 1.
        Assert.False(File.Exists(Path.Combine(Install, "new.exe")));
        Assert.Equal("release 1 a", InstalledText("a.exe"));
    }

    [Fact]
    public async Task ASecondUpdateCannotStartWhileOneIsUnrecovered()
    {
        var transaction = await UpdateTransaction.BeginAsync(Working, Install, ["a.exe"]);
        await transaction.StageAsync("a.exe", Bytes("release 2 a"));

        // Two intents in one directory means recovery cannot tell which update it is undoing.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => UpdateTransaction.BeginAsync(Working, Install, ["b.exe"]));
    }

    /// <summary>
    /// Performs the swap the way <c>CommitAsync</c> does and stops partway, leaving on disk exactly
    /// what a process killed at that moment would leave.
    /// </summary>
    /// <remarks>
    /// The real commit is not called here because it cannot be interrupted from outside without a seam
    /// that exists only for tests, and a seam like that changes the code being tested. Reproducing the
    /// moves instead keeps the production path exactly as it ships; what is asserted afterwards is
    /// recovery, which is the part that has to cope.
    /// </remarks>
    private async Task SimulateCrashDuringSwap(UpdateTransaction transaction, string? afterFile)
    {
        var staging = Path.Combine(Working, "staging");
        var backup = Path.Combine(Working, "backup");

        var intentPath = Path.Combine(Working, "update-intent.json");
        var intent = await File.ReadAllTextAsync(intentPath);
        await File.WriteAllTextAsync(intentPath, intent.Replace("\"staging\"", "\"swapping\"", StringComparison.Ordinal));

        foreach (var relativePath in Directory
                     .EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                     .Select(file => Path.GetRelativePath(staging, file))
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var installed = Path.Combine(Install, relativePath);
            var backupPath = Path.Combine(backup, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);

            if (File.Exists(installed))
            {
                File.Move(installed, backupPath, overwrite: true);
            }
            else
            {
                await File.WriteAllBytesAsync(backupPath + ".fortiq-absent", []);
            }

            if (afterFile is null)
            {
                return;
            }

            File.Move(Path.Combine(staging, relativePath), installed, overwrite: true);

            if (string.Equals(relativePath.Replace('\\', '/'), afterFile, StringComparison.Ordinal))
            {
                return;
            }
        }

        GC.KeepAlive(transaction);
    }

    private void Installed(string relativePath, string content)
    {
        var path = Path.Combine(Install, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string InstalledText(string relativePath) => File.ReadAllText(Path.Combine(Install, relativePath));

    private static byte[] Bytes(string content) => Encoding.UTF8.GetBytes(content);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
