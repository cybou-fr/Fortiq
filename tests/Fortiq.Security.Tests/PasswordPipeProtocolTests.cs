using System.Text;
using Fortiq.Infrastructure.Keys;
using Fortiq.PasswordHelper;
using Fortiq.Platform.Windows;

namespace Fortiq.Security.Tests;

public sealed class PasswordPipeProtocolTests
{
    [Fact]
    public async Task TransfersPasswordOnceToTheApprovedClient()
    {
        var id = Guid.NewGuid();
        using var lease = new TestOnlyKeyLease([.. Enumerable.Range(0, 32).Select(index => (byte)index)]);
        await using var output = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // The client here is the test host itself, so that is the image the broker is told to expect.
        using var approved = PinnedFile.Open(Environment.ProcessPath!);
        var server = new PasswordPipeServer(id, lease, approved, new PasswordBrokerOptions(Environment.ProcessPath!));

        await Task.WhenAll(server.ServeOnceAsync(timeout.Token), PasswordHelperClient.RunAsync(id, output, timeout.Token));

        Assert.Equal("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8\n", Encoding.ASCII.GetString(output.ToArray()));
    }

    [Fact]
    public async Task AClientThatIsNotTheApprovedHelperGetsNothing()
    {
        var id = Guid.NewGuid();
        using var lease = new TestOnlyKeyLease([.. Enumerable.Range(0, 32).Select(index => (byte)index)]);
        await using var output = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // A different image is approved, so the connecting test host must be refused.
        var helper = Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");
        Skip.IfNot(File.Exists(helper), "The password helper was not built next to the tests.");
        using var approved = PinnedFile.Open(helper);
        var server = new PasswordPipeServer(id, lease, approved, new PasswordBrokerOptions(helper));

        var served = server.ServeOnceAsync(timeout.Token);
        var client = PasswordHelperClient.RunAsync(id, output, timeout.Token);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => served);
        await Assert.ThrowsAnyAsync<Exception>(() => client);
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public void PipeNameContainsOnlyOperationId()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");

        Assert.Equal("fortiq-password-v1-11111111222233334444555555555555", PasswordPipeProtocol.PipeName(id));
    }
}
