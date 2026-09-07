using Fortiq.Assistant;

namespace Fortiq.Assistant.Tests;

/// <summary>
/// The runtime, against the real llama-server and the real model where both are present.
/// </summary>
/// <remarks>
/// The end-to-end facts are skipped rather than failed when the pinned binaries are not on the
/// machine. Neither is committed - the runtime is forty-five megabytes and the model is over a
/// gigabyte - so a checkout without them is the normal state of a fresh clone, and a test suite that
/// went red for it would teach everybody to ignore a red suite. Run scripts/Get-Model.ps1 and
/// scripts/Get-Runtime.ps1 and these become real.
///
/// The facts that need no binaries are not skipped, because refusing to start on a missing file is
/// exactly the case a machine without them can check.
/// </remarks>
public sealed class LlamaServerRuntimeTests
{
    [Fact]
    public async Task AMissingRuntimeIsNamedAlongWithHowToGetIt()
    {
        var options = new AssistantRuntimeOptions(
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.exe"),
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.gguf"),
            ContextTokens: 4096);

        var error = await Assert.ThrowsAsync<FileNotFoundException>(
            () => LlamaServerRuntime.StartAsync(options, CancellationToken.None));

        Assert.Contains("Get-Runtime.ps1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingModelIsReportedSeparatelyFromAMissingRuntime()
    {
        // Two different problems with two different fixes; one message for both would send somebody
        // to reinstall the runtime they already have.
        var server = Server();
        if (server is null)
        {
            return;
        }

        var options = new AssistantRuntimeOptions(
            server,
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.gguf"),
            ContextTokens: 4096);

        var error = await Assert.ThrowsAsync<FileNotFoundException>(
            () => LlamaServerRuntime.StartAsync(options, CancellationToken.None));

        Assert.Contains("model is missing", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AZeroContextWindowIsAProgrammingMistake() =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => LlamaServerRuntime.StartAsync(
                new AssistantRuntimeOptions("server.exe", "model.gguf", ContextTokens: 0),
                CancellationToken.None));

    [Fact]
    public async Task ItStartsAnswersAndLeavesNoProcessBehind()
    {
        if (Server() is not { } server || Model() is not { } model)
        {
            return;
        }

        var runtime = await LlamaServerRuntime.StartAsync(
            new AssistantRuntimeOptions(server, model, ContextTokens: 4096, Threads: 4, MaxReplyTokens: 200),
            CancellationToken.None);

        int processId;
        try
        {
            Assert.True(runtime.IsReady);

            var reply = await runtime.AskAsync(
                AssistantAsk.About(
                    "In one sentence, what should the person do?",
                    new AssistantEvidence("engine error", "repository is locked by another process")),
                CancellationToken.None);

            Assert.NotEmpty(reply.Text);
            processId = runtime.ProcessId;
        }
        finally
        {
            await runtime.DisposeAsync();
        }

        Assert.False(runtime.IsReady);
        Assert.True(IsGone(processId), "The runtime process outlived the object that owned it.");
    }

    [Fact]
    public async Task TheRealModelHeldToTheSchemaAnswersInStatements()
    {
        // The end-to-end fact that matters for the structured path: llama.cpp compiles the schema
        // into a grammar, so the shape is not something the model may decline. If this ever fails,
        // the schema is too large for a two-billion-parameter model rather than merely unlucky.
        if (Server() is not { } server || Model() is not { } model)
        {
            return;
        }

        var runtime = await LlamaServerRuntime.StartAsync(
            new AssistantRuntimeOptions(server, model, ContextTokens: 4096, Threads: 4, MaxReplyTokens: 300),
            CancellationToken.None);

        try
        {
            var reply = await runtime.AskAsync(
                AssistantAsk.About(
                    "What should the person do, and what did Fortiq record?",
                    new AssistantEvidence(
                        "what Fortiq knows about this PC",
                        "WHAT FORTIQ RECORDED\nCite one of these by its [reference] when you state a fact.\n"
                        + "[verdict:Documents] Documents: At risk: this may not be recoverable today.")),
                CancellationToken.None);

            Assert.NotNull(reply.Response);
            Assert.NotEmpty(reply.Response.Items);
            Assert.NotEmpty(reply.Text);
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    [Fact]
    public async Task AskingAfterDisposalFailsRatherThanHanging()
    {
        if (Server() is not { } server || Model() is not { } model)
        {
            return;
        }

        var runtime = await LlamaServerRuntime.StartAsync(
            new AssistantRuntimeOptions(server, model, ContextTokens: 4096, Threads: 4),
            CancellationToken.None);
        await runtime.DisposeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.AskAsync(AssistantAsk.About("Anything?"), CancellationToken.None));
    }

    private static bool IsGone(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static string? Server() => Existing(Path.Combine(
        "runtimes", "llama", "b10830", "win-x64", "llama-server.exe"));

    private static string? Model() => Existing(Path.Combine(
        "models", "fortiq-assistant", "0.1.0", "model.gguf"));

    private static string? Existing(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
