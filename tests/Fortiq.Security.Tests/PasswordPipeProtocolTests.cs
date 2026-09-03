using System.Text;
using Fortiq.Infrastructure.Keys;
using Fortiq.PasswordHelper;

namespace Fortiq.Security.Tests;

public sealed class PasswordPipeProtocolTests
{
    [Fact]
    public async Task TransfersPasswordOnce()
    {
        var id = Guid.NewGuid(); using var lease = new TestOnlyKeyLease(Enumerable.Range(0, 32).Select(x => (byte)x).ToArray());
        var server = new TestOnlyPasswordPipeServer(id, lease); await using var output = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(server.ServeOnceAsync(timeout.Token), PasswordHelperClient.RunAsync(id, output, timeout.Token));
        Assert.Equal("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8\n", Encoding.ASCII.GetString(output.ToArray()));
    }

    [Fact]
    public void PipeNameContainsOnlyOperationId()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal("fortiq-password-v1-11111111222233334444555555555555", PasswordPipeProtocol.PipeName(id));
    }
}
