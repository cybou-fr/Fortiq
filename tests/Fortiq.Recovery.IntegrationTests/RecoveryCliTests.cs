using Fortiq.Application;
using Fortiq.Recover;

namespace Fortiq.Recovery.IntegrationTests;

public sealed class RecoveryCliTests
{
    [Fact]
    public void ParsesRestore()
    {
        var command = RecoveryCli.Parse(
            ["restore", "--repository", "repo", "--engine-root", "engines", "--envelope", "kit.cbor", "--snapshot", "abc", "--target", "restore"]);

        Assert.Equal(RecoveryOperation.Restore, command.Operation);
        Assert.NotNull(command.Target);
        Assert.True(Path.IsPathFullyQualified(command.Target));
        Assert.True(Path.IsPathFullyQualified(command.Envelope!));
    }

    [Theory]
    [InlineData("--password")]
    [InlineData("--secret")]
    [InlineData("--recovery-phrase")]
    [InlineData("--mnemonic")]
    public void RejectsSecretOptions(string option) => Assert.Throws<ArgumentException>(
        () => RecoveryCli.Parse(["inspect", "--repository", "repo", "--engine-root", "engines", option, "secret"]));

    [Theory]
    [InlineData("snapshots")]
    [InlineData("check")]
    [InlineData("restore")]
    public void UnlockCommandsRequireAnEnvelope(string operation)
    {
        string[] args = operation == "restore"
            ? [operation, "--repository", "repo", "--engine-root", "engines", "--snapshot", "abc", "--target", "out"]
            : [operation, "--repository", "repo", "--engine-root", "engines"];

        Assert.Throws<ArgumentException>(() => RecoveryCli.Parse(args));
    }

    [Fact]
    public void InspectDoesNotRequireAnEnvelopeAndDoesNotUnlock()
    {
        var command = RecoveryCli.Parse(["inspect", "--repository", "repo", "--engine-root", "engines"]);

        Assert.Null(command.Envelope);
        Assert.False(command.RequiresUnlock);
    }

    [Fact]
    public async Task EmitsJson()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await RecoveryCli.RunAsync(
            ["inspect", "--repository", "repo", "--engine-root", "engines"],
            new FakeExecutor(),
            new FakeMaterial(),
            output,
            error,
            CancellationToken.None);

        Assert.Equal(RecoveryCli.ExitSuccess, code);
        Assert.Contains("\"ok\":true", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task AFailedUnlockReportsOneUnifiedErrorAndItsOwnExitCode()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await RecoveryCli.RunAsync(
            ["check", "--repository", "repo", "--engine-root", "engines", "--envelope", "kit.cbor"],
            new FakeExecutor(new UnlockFailedException()),
            new FakeMaterial(),
            output,
            error,
            CancellationToken.None);

        Assert.Equal(RecoveryCli.ExitUnlockFailed, code);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal("UnlockFailed", error.ToString().Trim());
    }

    private sealed class FakeExecutor(Exception? failure = null) : IRecoveryCommandExecutor
    {
        public Task<object> ExecuteAsync(RecoveryCommand command, IRecoveryMaterialReader material, CancellationToken token) =>
            failure is null ? Task.FromResult<object>(new { ok = true }) : Task.FromException<object>(failure);
    }

    private sealed class FakeMaterial : IRecoveryMaterialReader
    {
        public Task<string> ReadMnemonicAsync(CancellationToken token) => Task.FromResult("unused");
    }
}
