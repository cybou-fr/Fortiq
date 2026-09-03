using Fortiq.Infrastructure.Restic;

namespace Fortiq.Restic.ContractTests;

public sealed class ResticJsonParserTests
{
    private static readonly string FixtureRoot = Path.Combine(AppContext.BaseDirectory, "restic-output", "0.19.1");

    [Fact]
    public void ParsesPinnedVersionFixture()
    {
        var parsed = ResticJsonParser.ParseVersion(Success(Read("version.json")));

        Assert.Equal("0.19.1", parsed.Version);
        Assert.Equal("windows", parsed.OperatingSystem);
        Assert.Equal("amd64", parsed.Architecture);
    }

    [Fact]
    public void ParsesInitializedRepositoryFixture()
    {
        var parsed = ResticJsonParser.ParseInitialized(Success(Read("init.jsonl")));

        Assert.Equal(64, parsed.Id.Length);
        Assert.Equal("C:\\fixture\\repo", parsed.Repository);
    }

    [Fact]
    public void ParsesBackupOnlyWhenSummaryIsTerminal()
    {
        var parsed = ResticJsonParser.ParseBackup(Success(Read("backup.jsonl")));

        Assert.Equal(1UL, parsed.TotalFilesProcessed);
        Assert.Equal(14UL, parsed.TotalBytesProcessed);
        Assert.Equal(64, parsed.SnapshotId.Length);
    }

    [Fact]
    public void ParsesSnapshotsFixture()
    {
        var snapshots = ResticJsonParser.ParseSnapshots(Success(Read("snapshots.json")));

        var snapshot = Assert.Single(snapshots);
        Assert.Equal("fixture-host", snapshot.Hostname);
        Assert.Equal("restic 0.19.1", snapshot.ProgramVersion);
    }

    [Fact]
    public void ParsesHealthyCheckFixture()
    {
        var parsed = ResticJsonParser.ParseCheck(Success(Read("check.jsonl")));

        Assert.True(parsed.IsHealthy);
        Assert.Empty(parsed.BrokenPacks);
    }

    [Fact]
    public void ParsesRestoreFixture()
    {
        var parsed = ResticJsonParser.ParseRestore(Success(Read("restore.jsonl")));

        Assert.Equal(1UL, parsed.FilesRestored);
        Assert.Equal(14UL, parsed.BytesRestored);
    }

    [Fact]
    public void RejectsSummaryWhenExitCodeIsFailure()
    {
        var result = new ResticProcessResult(1, Read("restore.jsonl"), "{\"message_type\":\"exit_error\",\"code\":1,\"message\":\"failed\"}");

        Assert.Throws<InvalidDataException>(() => ResticJsonParser.ParseRestore(result));
    }

    [Fact]
    public void RejectsMissingTerminalSummary()
    {
        const string status = "{\"message_type\":\"status\",\"percent_done\":1}";

        Assert.Throws<InvalidDataException>(() => ResticJsonParser.ParseBackup(Success(status)));
    }

    [Fact]
    public void RejectsIntegrityErrorsEvenWithZeroExitCode()
    {
        const string unhealthy = "{\"message_type\":\"summary\",\"num_errors\":1,\"broken_packs\":[\"bad\"],\"suggest_repair_index\":true,\"suggest_prune\":false}";

        Assert.Throws<InvalidDataException>(() => ResticJsonParser.ParseCheck(Success(unhealthy)));
    }

    private static ResticProcessResult Success(string stdout) => new(0, stdout, string.Empty);

    private static string Read(string name) => File.ReadAllText(Path.Combine(FixtureRoot, name));
}
