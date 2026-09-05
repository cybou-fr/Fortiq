using System.Text;
using Fortiq.Infrastructure.Updates;

namespace Fortiq.Security.Tests;

/// <summary>
/// The seam where a verified release becomes installed files, and the ways a hostile server tries to
/// get an unverified byte through it.
/// </summary>
public sealed class ComponentUpdaterTests : IDisposable
{
    private const string ServiceTarget = "win-x64/Fortiq.Service.exe";
    private const string EngineTarget = "win-x64/engines/restic.exe";

    private static readonly byte[] NewService = Encoding.UTF8.GetBytes("release 2 service binary");
    private static readonly byte[] NewEngine = Encoding.UTF8.GetBytes("release 2 engine binary");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "fortiq-updater-tests",
        Guid.NewGuid().ToString("N"));

    private readonly TufTestKey _rootKey = new();
    private readonly TufTestKey _roleKey = new();

    private string Install => Path.Combine(_root, "install");

    public ComponentUpdaterTests()
    {
        Directory.CreateDirectory(Install);
        File.WriteAllText(Path.Combine(Install, "Fortiq.Service.exe"), "release 1 service binary");
    }

    [Fact]
    public async Task AVerifiedComponentIsInstalledAndReported()
    {
        var trusted = TrustedRelease(1, (ServiceTarget, NewService));
        var updater = new ComponentUpdater(new Source((ServiceTarget, NewService)));

        var outcome = await updater.ApplyAsync(trusted, Install, [new(ServiceTarget, "Fortiq.Service.exe")]);

        Assert.Equal("release 2 service binary", File.ReadAllText(Path.Combine(Install, "Fortiq.Service.exe")));

        var component = Assert.Single(outcome.Components);
        Assert.Equal(NewService.Length, component.Length);
        Assert.Equal(BinarySignatureStatus.NotChecked, component.Signature);
        Assert.Equal(1, outcome.TargetsVersion);
    }

    [Fact]
    public async Task AComponentTheReleaseDoesNotNameIsRefusedBeforeAnythingIsFetched()
    {
        var trusted = TrustedRelease(1, (ServiceTarget, NewService));
        var source = new Source((ServiceTarget, NewService));
        var updater = new ComponentUpdater(source);

        await Assert.ThrowsAsync<TufMetadataException>(
            () => updater.ApplyAsync(trusted, Install, [new("win-x64/Fortiq.Desktop.exe", "Fortiq.Desktop.exe")]));

        Assert.Empty(source.Requested);
    }

    [Fact]
    public async Task ContentThatDoesNotMatchTheReleaseNeverReachesTheInstallation()
    {
        var trusted = TrustedRelease(1, (ServiceTarget, NewService));

        // The server answers with something other than what the signed release describes - the case
        // where the metadata is genuine and the delivery is not.
        var updater = new ComponentUpdater(new Source((ServiceTarget, Encoding.UTF8.GetBytes("an attacker's binary"))));

        await Assert.ThrowsAsync<TufMetadataException>(
            () => updater.ApplyAsync(trusted, Install, [new(ServiceTarget, "Fortiq.Service.exe")]));

        Assert.Equal("release 1 service binary", File.ReadAllText(Path.Combine(Install, "Fortiq.Service.exe")));
    }

    [Fact]
    public async Task TheSourceIsToldHowMuchItIsAllowedToReturn()
    {
        var trusted = TrustedRelease(1, (ServiceTarget, NewService));
        var source = new Source((ServiceTarget, NewService));
        var updater = new ComponentUpdater(source);

        await updater.ApplyAsync(trusted, Install, [new(ServiceTarget, "Fortiq.Service.exe")]);

        // Without a bound, a server answering an update request with an endless stream fills the disk
        // of the machine it was meant to protect, and does so before any hash is computed.
        Assert.Equal(NewService.Length, Assert.Single(source.Requested).MaximumLength);
    }

    [Fact]
    public async Task OneBadComponentLeavesEveryOtherComponentUntouched()
    {
        var trusted = TrustedRelease(1, (ServiceTarget, NewService), (EngineTarget, NewEngine));

        var updater = new ComponentUpdater(new Source(
            (ServiceTarget, NewService),
            (EngineTarget, Encoding.UTF8.GetBytes("a substituted engine"))));

        await Assert.ThrowsAsync<TufMetadataException>(() => updater.ApplyAsync(
            trusted,
            Install,
            [new(ServiceTarget, "Fortiq.Service.exe"), new(EngineTarget, "engines/restic.exe")]));

        // Everything is proven before the transaction opens, so a component that turns out wrong costs
        // nothing: there is no rollback to perform and no moment where a binary is missing.
        Assert.Equal("release 1 service binary", File.ReadAllText(Path.Combine(Install, "Fortiq.Service.exe")));
        Assert.False(File.Exists(Path.Combine(Install, "engines", "restic.exe")));
        Assert.False(Directory.Exists(ComponentUpdater.WorkingDirectoryFor(Install)));
    }

    [Fact]
    public async Task ABinaryWithABrokenSignatureIsRefused()
    {
        var trusted = TrustedRelease(1, (ServiceTarget, NewService));
        var updater = new ComponentUpdater(
            new Source((ServiceTarget, NewService)),
            new FixedSignaturePolicy(BinarySignatureStatus.Invalid));

        var error = await Assert.ThrowsAsync<TufMetadataException>(
            () => updater.ApplyAsync(trusted, Install, [new(ServiceTarget, "Fortiq.Service.exe")]));

        Assert.Contains("does not verify", error.Message, StringComparison.Ordinal);
        Assert.Equal("release 1 service binary", File.ReadAllText(Path.Combine(Install, "Fortiq.Service.exe")));
    }

    [Fact]
    public async Task AnUnsignedBinaryIsInstalledAndItsAbsenceOfSignatureIsRecorded()
    {
        var trusted = TrustedRelease(1, (ServiceTarget, NewService));
        var updater = new ComponentUpdater(
            new Source((ServiceTarget, NewService)),
            new FixedSignaturePolicy(BinarySignatureStatus.Absent));

        var outcome = await updater.ApplyAsync(trusted, Install, [new(ServiceTarget, "Fortiq.Service.exe")]);

        // ADR-008 Revision 1: Fortiq holds no code-signing certificate, so requiring a valid signature
        // would refuse every build the project produces. The absence is written down instead.
        Assert.Equal(BinarySignatureStatus.Absent, Assert.Single(outcome.Components).Signature);
        Assert.Equal("release 2 service binary", File.ReadAllText(Path.Combine(Install, "Fortiq.Service.exe")));
    }

    [Fact]
    public async Task AnInterruptedUpdateIsRecoveredBeforeTheNextOneStarts()
    {
        // A leftover intent from a machine that was switched off mid-update. Left alone it would block
        // every future update; picked up, it decides what the installation is before anything new runs.
        var working = ComponentUpdater.WorkingDirectoryFor(Install);
        Directory.CreateDirectory(working);
        await File.WriteAllTextAsync(
            Path.Combine(working, "update-intent.json"),
            """
            {
              "schema": "fortiq.update-intent",
              "version": 1,
              "installDirectory": "ignored",
              "relativePaths": [ "Fortiq.Service.exe" ],
              "startedAt": "2026-09-05T12:00:00+00:00",
              "state": "staging"
            }
            """);

        var trusted = TrustedRelease(1, (ServiceTarget, NewService));
        var updater = new ComponentUpdater(new Source((ServiceTarget, NewService)));

        var outcome = await updater.ApplyAsync(trusted, Install, [new(ServiceTarget, "Fortiq.Service.exe")]);

        Assert.Single(outcome.Components);
        Assert.Equal("release 2 service binary", File.ReadAllText(Path.Combine(Install, "Fortiq.Service.exe")));
    }

    private TufTrustedMetadata TrustedRelease(long version, params (string Target, byte[] Content)[] targets)
    {
        var client = TufTrustedMetadata.LoadTrustedRoot(
            TufRepositoryBuilder.Sign(TufRepositoryBuilder.Root(1, [_rootKey], _roleKey), _rootKey));

        client.UpdateTimestamp(
            TufRepositoryBuilder.Sign(TufRepositoryBuilder.Timestamp(version, version), _roleKey),
            TufRepositoryBuilder.Now);
        client.UpdateSnapshot(
            TufRepositoryBuilder.Sign(TufRepositoryBuilder.Snapshot(version, version), _roleKey),
            TufRepositoryBuilder.Now);
        client.UpdateTargets(
            TufRepositoryBuilder.Sign(TufRepositoryBuilder.TargetsFor(version, targets), _roleKey),
            TufRepositoryBuilder.Now);

        return client;
    }

    private sealed class Source(params (string Target, byte[] Content)[] available) : IUpdateContentSource
    {
        public List<(string TargetPath, long MaximumLength)> Requested { get; } = [];

        public Task<byte[]> FetchAsync(string targetPath, long maximumLength, CancellationToken cancellationToken)
        {
            Requested.Add((targetPath, maximumLength));

            foreach (var (target, content) in available)
            {
                if (string.Equals(target, targetPath, StringComparison.Ordinal))
                {
                    return Task.FromResult(content);
                }
            }

            throw new InvalidOperationException($"The test source holds no '{targetPath}'.");
        }
    }

    private sealed class FixedSignaturePolicy(BinarySignatureStatus status) : IBinarySignaturePolicy
    {
        public BinarySignatureStatus Inspect(string targetPath, ReadOnlySpan<byte> content) => status;
    }

    public void Dispose()
    {
        _rootKey.Dispose();
        _roleKey.Dispose();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
