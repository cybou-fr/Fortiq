using Fortiq.Platform.Windows;

namespace Fortiq.Desktop.Tests;

public sealed class InstallationInspectorTests
{
    [Fact]
    public async Task InspectAsyncReturnsPopulatedStatus()
    {
        var inspector = new InstallationInspector();
        var status = await inspector.InspectAsync();

        Assert.NotNull(status);
        Assert.False(string.IsNullOrWhiteSpace(status.ExecutablePath));
        Assert.NotNull(status.Service);
        Assert.NotNull(status.Engine);
        Assert.NotNull(status.PasswordHelper);
        Assert.NotNull(status.Platform);
        Assert.NotNull(status.Findings);

        // .NET Runtime must be valid (.NET 10+)
        Assert.True(status.Platform.DotNetRuntimeValid);
        Assert.False(string.IsNullOrWhiteSpace(status.Platform.DotNetVersion));

        // Engine name should be restic
        Assert.Equal("restic", status.Engine.Name);
    }
}
