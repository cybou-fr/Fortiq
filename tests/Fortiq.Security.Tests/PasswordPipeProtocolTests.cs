using Fortiq.Application;
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

/// <summary>
/// What the caller is told when the secret never reaches the engine.
/// </summary>
/// <remarks>
/// Every plumbing failure used to arrive as the bare word "UnlockFailed" - the same thing a wrong
/// recovery phrase says. The constant message exists so nobody can tell a wrong secret from a
/// missing key; a handover that never happened tested no secret and so reveals none, and collapsing
/// the two made the fixable fault indistinguishable from the unfixable one.
/// </remarks>
public sealed class CredentialHandoverFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fortiq-handover-tests", Guid.NewGuid().ToString("N"));

    public CredentialHandoverFailureTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task AHelperThatNeverCollectsTheSecretSaysSo()
    {
        var helper = Path.Combine(_root, "Fortiq.PasswordHelper.exe");
        await File.WriteAllTextAsync(helper, "a helper that is never run");

        using var lease = new BufferKeyLease(new byte[32]);
        using var provider = new PasswordPipeCredentialProvider(helper, lease, TimeSpan.FromMilliseconds(200));
        await using var session = await provider.BeginAsync(Guid.NewGuid(), CancellationToken.None);

        var error = await Assert.ThrowsAsync<CredentialHandoverException>(
            () => session.CompleteAsync(CancellationToken.None));

        Assert.NotEqual("UnlockFailed", error.Message);
        Assert.Contains("password helper", error.Message, StringComparison.OrdinalIgnoreCase);

        // Still an unlock failure to every handler that already catches one.
        Assert.IsAssignableFrom<UnlockFailedException>(error);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }
}
