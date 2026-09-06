using Fortiq.Monitoring;

namespace Fortiq.Monitoring.Tests;

/// <summary>
/// The receipts as a history rather than a summary. What a person needs from a history is exactly
/// what a verdict is designed to drop: the attempt that failed before the one that worked.
/// </summary>
public sealed class ReceiptTimelineTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fortiq-timeline-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AFailureThatWasFollowedByASuccessIsStillInTheHistory()
    {
        // The whole reason this exists. Monitoring keeps the most recent success per kind, so the
        // failed drill from Tuesday disappears the moment Wednesday's works - which is the event
        // somebody opening a history is most likely looking for.
        await WriteAsync("a", "restoreProof", Now.AddDays(-2), succeeded: false, warning: "the restored tree did not match");
        await WriteAsync("a", "restoreProof", Now.AddDays(-1), succeeded: true);

        var events = await ReceiptTimeline.ReadAsync(_directory, CancellationToken.None);

        Assert.Equal(2, events.Count);
        Assert.Contains(events, entry => !entry.Succeeded && entry.Detail == "the restored tree did not match");
    }

    [Fact]
    public async Task EventsComeBackNewestFirst()
    {
        await WriteAsync("a", "backup", Now.AddDays(-3), succeeded: true);
        await WriteAsync("a", "backup", Now.AddHours(-1), succeeded: true);
        await WriteAsync("a", "check", Now.AddDays(-2), succeeded: true);

        var events = await ReceiptTimeline.ReadAsync(_directory, CancellationToken.None);

        Assert.Equal(
            [Now.AddHours(-1), Now.AddDays(-2), Now.AddDays(-3)],
            events.Select(entry => entry.CompletedAt));
    }

    [Fact]
    public async Task AReceiptWrittenBeforeTheChainedSchemaIsShownAndMarkedUnverifiable()
    {
        // Never dropped and never trusted. A version 1 receipt carries no hash, so a file claiming a
        // restore succeeded cannot be told apart from one somebody typed - and a repository whose
        // only history is unverifiable has a different problem from one with no history.
        await WriteAsync("a", "restoreProof", Now.AddDays(-5), succeeded: true, version: 1);
        await WriteAsync("a", "backup", Now.AddDays(-1), succeeded: true);

        var events = await ReceiptTimeline.ReadAsync(_directory, CancellationToken.None);

        Assert.Equal(2, events.Count);
        Assert.False(events.Single(entry => entry.Operation == "restoreProof").Verifiable);
        Assert.True(events.Single(entry => entry.Operation == "backup").Verifiable);
    }

    [Fact]
    public async Task ADamagedReceiptDoesNotTakeTheRestOfTheHistoryWithIt()
    {
        await WriteAsync("a", "backup", Now.AddHours(-2), succeeded: true);
        await File.WriteAllTextAsync(Path.Combine(_directory, "broken.json"), "{ not json");
        await File.WriteAllTextAsync(Path.Combine(_directory, "other.json"), """{ "schema": "something.else" }""");

        var events = await ReceiptTimeline.ReadAsync(_directory, CancellationToken.None);

        Assert.Equal("backup", Assert.Single(events).Operation);
    }

    [Fact]
    public async Task ReceiptsInSubdirectoriesAreFound()
    {
        // Receipts are written per repository, in directories of their own.
        var nested = Path.Combine(_directory, "repository-a");
        Directory.CreateDirectory(nested);
        await WriteAsync("a", "backup", Now.AddHours(-1), succeeded: true, directory: nested);

        Assert.Single(await ReceiptTimeline.ReadAsync(_directory, CancellationToken.None));
    }

    [Fact]
    public async Task OnlyTheMostRecentEventsAreRead()
    {
        for (var index = 0; index < 12; index++)
        {
            await WriteAsync("a", "backup", Now.AddHours(-index), succeeded: true);
        }

        var events = await ReceiptTimeline.ReadAsync(_directory, CancellationToken.None, limit: 5);

        Assert.Equal(5, events.Count);
        Assert.Equal(Now, events[0].CompletedAt);
    }

    [Fact]
    public async Task ADirectoryThatIsNotThereIsAnEmptyHistoryRatherThanAFailure()
    {
        Assert.Empty(await ReceiptTimeline.ReadAsync(Path.Combine(_directory, "never-written"), CancellationToken.None));
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private async Task WriteAsync(
        string repositoryId,
        string operation,
        DateTimeOffset completedAt,
        bool succeeded,
        string? warning = null,
        int version = 2,
        string? directory = null)
    {
        var target = directory ?? _directory;
        Directory.CreateDirectory(target);
        var warnings = warning is null ? "[]" : $"""["{warning}"]""";

        await File.WriteAllTextAsync(
            Path.Combine(target, Guid.NewGuid().ToString("N") + ".json"),
            $$"""
            {
              "schema": "fortiq.operation-receipt",
              "version": {{version}},
              "repositoryId": "{{repositoryId}}",
              "operation": "{{operation}}",
              "engineResult": "{{(succeeded ? "succeeded" : "failed")}}",
              "completedAt": "{{completedAt:O}}",
              "snapshotId": "snapshot-1",
              "warnings": {{warnings}}
            }
            """);
    }
}
