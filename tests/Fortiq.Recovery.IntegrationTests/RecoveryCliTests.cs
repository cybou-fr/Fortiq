using Fortiq.Recover;
namespace Fortiq.Recovery.IntegrationTests;

public sealed class RecoveryCliTests
{
    [Fact]
    public void ParsesRestore()
    {
        var command = RecoveryCli.Parse(["restore", "--repository", "repo", "--engine-root", "engines", "--snapshot", "abc", "--target", "restore"]);
        Assert.Equal(RecoveryOperation.Restore, command.Operation); Assert.NotNull(command.Target); Assert.True(Path.IsPathFullyQualified(command.Target));
    }

    [Theory]
    [InlineData("--password")]
    [InlineData("--secret")]
    [InlineData("--recovery-phrase")]
    public void RejectsSecretOptions(string option) => Assert.Throws<ArgumentException>(() => RecoveryCli.Parse(["inspect", "--repository", "repo", "--engine-root", "engines", option, "secret"]));

    [Fact]
    public async Task EmitsJson()
    {
        var output = new StringWriter(); var error = new StringWriter();
        var code = await RecoveryCli.RunAsync(["inspect", "--repository", "repo", "--engine-root", "engines"], new FakeExecutor(), output, error, CancellationToken.None);
        Assert.Equal(0, code); Assert.Contains("\"ok\":true", output.ToString(), StringComparison.Ordinal); Assert.Equal(string.Empty, error.ToString());
    }

    private sealed class FakeExecutor : IRecoveryCommandExecutor
    {
        public Task<object> ExecuteAsync(RecoveryCommand command, CancellationToken token) => Task.FromResult<object>(new { ok = true });
    }
}
