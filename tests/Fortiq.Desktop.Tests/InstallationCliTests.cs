using Fortiq.Desktop;

namespace Fortiq.Desktop.Tests;

public sealed class InstallationCliTests
{
    private static readonly string[] HelpArgs = ["--help"];
    private static readonly string[] UnknownArgs = ["--invalid-command-xyz"];

    [Theory]
    [InlineData("--status", true)]
    [InlineData("--install", true)]
    [InlineData("--uninstall", true)]
    [InlineData("--worker-install", true)]
    [InlineData("--worker-uninstall", true)]
    [InlineData("--help", true)]
    [InlineData("-h", true)]
    [InlineData("/?", true)]
    [InlineData("--random-flag", false)]
    [InlineData("run", false)]
    public void IsCliInvocationDetectsCommandFlags(string flag, bool expected)
    {
        var args = new[] { flag };
        var result = InstallationCli.IsCliInvocation(args);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsCliInvocationReturnsFalseForEmptyArgs()
    {
        Assert.False(InstallationCli.IsCliInvocation(Array.Empty<string>()));
    }

    [Fact]
    public async Task RunAsyncWithHelpReturnsZero()
    {
        var exitCode = await InstallationCli.RunAsync(HelpArgs);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task RunAsyncWithUnknownCommandReturnsSyntaxError64()
    {
        var exitCode = await InstallationCli.RunAsync(UnknownArgs);
        Assert.Equal(64, exitCode);
    }
}
