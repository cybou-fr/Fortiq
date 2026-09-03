using Fortiq.Application;
using Fortiq.Infrastructure.Keys;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// The engine running on a real password instead of the P0 no-password seam: the secret reaches
/// restic only through the one-shot helper pipe, and a wrong secret fails as a single UnlockFailed.
/// </summary>
public sealed class PasswordHandoverTests
{
    private static string HelperPath => Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");

    [SkippableFact]
    public async Task PasswordProtectedRepositoryCompletesTheFullRoundTrip()
    {
        using var workspace = await RecoveryWorkspace.CreateAsync("password-round-trip", CancellationToken.None);
        var source = Path.Combine(workspace.Root, "source");
        var repository = workspace.EnsureDirectory("repository");
        var target = workspace.EnsureDirectory("restore");
        var expected = TestDataset.Create(source);

        using var lease = new TestOnlyKeyLease(Secret(1));
        var adapter = workspace.Adapter("state", new TestOnlyPasswordCredentialProvider(HelperPath, lease));

        var descriptor = await adapter.InitializeAsync(new InitializeRepository(repository), CancellationToken.None);
        var backup = await adapter.CreateSnapshotAsync(new CreateSnapshot(descriptor, source, "test-source"), CancellationToken.None);
        var check = await adapter.CheckAsync(new CheckRepository(descriptor), CancellationToken.None);
        Assert.True(check.IsHealthy);

        await adapter.RestoreAsync(new RestoreSnapshot(descriptor, backup.SnapshotId, target, source), CancellationToken.None);

        foreach (var entry in expected)
        {
            var restored = Path.Combine(target, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal(entry.Sha256, TestDataset.HashFile(restored));
        }
    }

    [SkippableFact]
    public async Task WrongSecretFailsAsUnlockFailedWithoutRevealingSnapshots()
    {
        using var workspace = await RecoveryWorkspace.CreateAsync("e2e-002", CancellationToken.None);
        var source = Path.Combine(workspace.Root, "source");
        var repository = workspace.EnsureDirectory("repository");
        TestDataset.Create(source);

        using var correct = new TestOnlyKeyLease(Secret(1));
        var owner = workspace.Adapter("state-owner", new TestOnlyPasswordCredentialProvider(HelperPath, correct));
        var descriptor = await owner.InitializeAsync(new InitializeRepository(repository), CancellationToken.None);
        var backup = await owner.CreateSnapshotAsync(new CreateSnapshot(descriptor, source, "test-source"), CancellationToken.None);

        using var wrong = new TestOnlyKeyLease(Secret(2));
        var attacker = workspace.Adapter("state-wrong", new TestOnlyPasswordCredentialProvider(HelperPath, wrong));

        var failure = await Assert.ThrowsAsync<UnlockFailedException>(
            () => attacker.ListSnapshotsAsync(new ListSnapshots(descriptor), CancellationToken.None));

        Assert.Equal("UnlockFailed", failure.Message);
        Assert.DoesNotContain(backup.SnapshotId, failure.ToString(), StringComparison.OrdinalIgnoreCase);

        // The same unified failure applies to every repository operation, not just listing.
        await Assert.ThrowsAsync<UnlockFailedException>(
            () => attacker.CheckAsync(new CheckRepository(descriptor), CancellationToken.None));
    }

    private static byte[] Secret(byte seed) =>
        [.. Enumerable.Range(0, EnginePasswordV1Encoder.EngineUnlockSecretSize).Select(index => (byte)(index + seed))];
}
