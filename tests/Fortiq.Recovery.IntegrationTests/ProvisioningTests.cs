using Fortiq.Domain;
using Fortiq.Infrastructure.Keys;
using Fortiq.Provisioning;

namespace Fortiq.Recovery.IntegrationTests;

public sealed class ProvisioningTests
{
    [Fact]
    public void TheProvisioningResultDoesNotPrintTheRecoveryMnemonic()
    {
        var mnemonic = Bip39Mnemonic.Create();
        var provisioned = new ProvisionedRepository(
            new RepositoryDescriptor(RepositoryId.Create(), "C:/repository"),
            new RecoveryKit(
                new string('a', 64),
                "C:/repository",
                new RecoveryKitEngine("restic", "0.19.1", new string('b', 64)),
                DateTimeOffset.UnixEpoch,
                [],
                "instructions"),
            mnemonic,
            DeviceUnlockAvailable: false);

        // A result object like this one reaches log lines, exception messages and debugger output.
        var printed = provisioned.ToString();

        Assert.DoesNotContain(mnemonic, printed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[redacted]", printed, StringComparison.Ordinal);

        // The value is still available to the caller that asked for it.
        Assert.Equal(mnemonic, provisioned.RecoveryMnemonic);
    }
}
