using Fortiq.Application;
using Fortiq.Domain;
using Fortiq.Infrastructure.Restic;

namespace Fortiq.Restic.ContractTests;

/// <summary>
/// The stable source identity lives inside the repository, so a recovery does not depend on receipts
/// or any other file that could be lost with the machine that produced the backup.
/// </summary>
public sealed class SourceMetadataTests
{
    [Fact]
    public async Task BackupWritesTheStableSourceIdIntoTheRepository()
    {
        var runner = new RecordingRunner(new ResticProcessResult(
            0,
            """{"message_type":"summary","snapshot_id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","total_files_processed":1,"total_bytes_processed":1,"backup_start":"2026-09-03T18:54:00.6+02:00","backup_end":"2026-09-03T18:54:01.3+02:00"}""",
            string.Empty));
        var adapter = CreateAdapter(runner);
        var repository = new RepositoryDescriptor(RepositoryId.Create(), Path.GetFullPath("repository"));

        await adapter.CreateSnapshotAsync(
            new CreateSnapshot(repository, Path.GetFullPath("source"), "workstation:documents"),
            CancellationToken.None);

        var arguments = runner.LastRequest!.Arguments;
        var tagIndex = arguments.ToList().IndexOf("--tag");
        Assert.True(tagIndex >= 0, "The backup did not tag the snapshot.");
        Assert.Equal("fortiq.v1,fortiq.source=workstation:documents", arguments[tagIndex + 1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("comma,separated")]
    [InlineData("-leading-dash")]
    [InlineData("control\u0007char")]
    public async Task AnIdentifierTheRepositoryCannotCarryIsRefused(string sourceStableId)
    {
        var adapter = CreateAdapter(new RecordingRunner(new ResticProcessResult(0, string.Empty, string.Empty)));
        var repository = new RepositoryDescriptor(RepositoryId.Create(), Path.GetFullPath("repository"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => adapter.CreateSnapshotAsync(
                new CreateSnapshot(repository, Path.GetFullPath("source"), sourceStableId),
                CancellationToken.None));
    }

    [Fact]
    public async Task ListingReadsTheStableSourceIdBackFromTheRepository()
    {
        var adapter = CreateAdapter(new RecordingRunner(new ResticProcessResult(0, Fixture("snapshots-tagged.json"), string.Empty)));
        var repository = new RepositoryDescriptor(RepositoryId.Create(), Path.GetFullPath("repository"));

        var snapshots = await adapter.ListSnapshotsAsync(new ListSnapshots(repository), CancellationToken.None);

        var snapshot = Assert.Single(snapshots);
        Assert.Equal("test-source", snapshot.SourceStableId);
        Assert.Equal(@"C:\fixture\source", snapshot.SourcePath);
    }

    [Fact]
    public async Task ASnapshotWithoutFortiqMetadataReportsNoStableSourceId()
    {
        var adapter = CreateAdapter(new RecordingRunner(new ResticProcessResult(0, Fixture("snapshots.json"), string.Empty)));
        var repository = new RepositoryDescriptor(RepositoryId.Create(), Path.GetFullPath("repository"));

        var snapshots = await adapter.ListSnapshotsAsync(new ListSnapshots(repository), CancellationToken.None);

        // The filesystem path is not an identity, and is never presented as one.
        var snapshot = Assert.Single(snapshots);
        Assert.Null(snapshot.SourceStableId);
        Assert.Equal(@"C:\fixture\source", snapshot.SourcePath);
    }

    [Fact]
    public void TagsThatContradictEachOtherAreNotResolvedByGuessing()
    {
        Assert.Null(ResticSnapshotMetadata.ReadSourceStableId(["fortiq.v1", "fortiq.source=a", "fortiq.source=b"]));
        Assert.Null(ResticSnapshotMetadata.ReadSourceStableId(["fortiq.source=a"]));
        Assert.Null(ResticSnapshotMetadata.ReadSourceStableId(["fortiq.v1", "fortiq.source=not valid"]));
        Assert.Equal("a", ResticSnapshotMetadata.ReadSourceStableId(["fortiq.v1", "fortiq.source=a", "unrelated"]));
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "restic-output", "0.19.1", name));

    private static ResticRepositoryEngine CreateAdapter(IResticProcessRunner runner) =>
        new(
            new VerifiedEngine("restic", "0.19.1", "win-x64", Path.GetFullPath("restic.exe"), new string('0', 64)),
            runner,
            new InsecureNoPasswordCredentialProvider(),
            Directory.GetCurrentDirectory());

    private sealed class RecordingRunner(ResticProcessResult result) : IResticProcessRunner
    {
        public ResticProcessRequest? LastRequest { get; private set; }

        public Task<ResticProcessResult> RunAsync(VerifiedEngine engine, ResticProcessRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(result);
        }
    }
}
