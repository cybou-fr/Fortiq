using System.Text;
using Fortiq.Application;
using Fortiq.Infrastructure.Keys;

namespace Fortiq.Security.Tests;

public sealed class EngineCredentialTests
{
    [Fact]
    public async Task PasswordCommandCarriesOnlyTheHelperPathAndOperationId()
    {
        var helper = Path.Combine(AppContext.BaseDirectory, "Fortiq.Security.Tests.dll");
        var material = Enumerable.Range(0, EnginePasswordV1Encoder.EngineUnlockSecretSize).Select(x => (byte)x).ToArray();
        using var lease = new TestOnlyKeyLease(material);
        var encoded = new byte[EnginePasswordV1Encoder.EncodedSize];
        EnginePasswordV1Encoder.Encode(lease, encoded);
        var password = Encoding.ASCII.GetString(encoded);

        var provider = new TestOnlyPasswordCredentialProvider(helper, lease);
        await using var session = await provider.BeginAsync(CancellationToken.None);

        var arguments = session.EngineArguments;
        Assert.Equal("--password-command", arguments[0]);
        Assert.Equal(2, arguments.Count);
        Assert.Contains(helper, arguments[1], StringComparison.Ordinal);
        Assert.DoesNotContain(password, string.Join(' ', arguments), StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(arguments[1].Split(' ')[^1], "D", out var operationId) && operationId != Guid.Empty);
    }

    [Fact]
    public async Task AHandoverThatNeverHappensIsReportedAsUnlockFailed()
    {
        var helper = Path.Combine(AppContext.BaseDirectory, "Fortiq.Security.Tests.dll");
        using var lease = new TestOnlyKeyLease(new byte[EnginePasswordV1Encoder.EngineUnlockSecretSize]);
        var provider = new TestOnlyPasswordCredentialProvider(helper, lease, TimeSpan.FromMilliseconds(200));

        await using var session = await provider.BeginAsync(CancellationToken.None);

        await Assert.ThrowsAsync<UnlockFailedException>(() => session.CompleteAsync(CancellationToken.None));
    }
}
