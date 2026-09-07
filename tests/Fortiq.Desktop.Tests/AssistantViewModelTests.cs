using Fortiq.Assistant;
using Fortiq.Desktop.ViewModels;

namespace Fortiq.Desktop.Tests;

/// <summary>
/// The assistant screen's behaviour, against a runtime that answers instantly.
/// </summary>
/// <remarks>
/// The real runtime is a child process and a gigabyte of weights; none of what is checked here is
/// about the model, and all of it is about what somebody sees while and after they ask.
/// </remarks>
public sealed class AssistantViewModelTests
{
    [Fact]
    public async Task NothingIsStartedUntilSomebodyAsks()
    {
        // Most sessions never ask. Loading a model on the chance that this one will is seconds and a
        // gigabyte spent on somebody who opened the app to check last night's backup.
        var starts = 0;
        _ = new AssistantViewModel(_ => { starts++; return Task.FromResult<IAssistantRuntime>(new Fake()); });

        await Task.Yield();

        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task TheRuntimeIsStartedOnceAndReusedForLaterQuestions()
    {
        var starts = 0;
        var model = new AssistantViewModel(_ => { starts++; return Task.FromResult<IAssistantRuntime>(new Fake()); });

        model.Question = "First?";
        await model.AskAsync(CancellationToken.None);
        model.Question = "Second?";
        await model.AskAsync(CancellationToken.None);

        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task AnAnswerIsKeptWithTheQuestionItAnswers()
    {
        // They arrive after a wait. A paragraph on its own is one somebody has to remember a
        // question for, and on a screen with suggestion buttons it is easy to lose track of which
        // was pressed.
        var model = new AssistantViewModel(_ => Task.FromResult<IAssistantRuntime>(new Fake("Because it is locked.")));

        await model.AskAsync(
            new AssistantSuggestion("Why is Documents not recoverable?", []),
            CancellationToken.None);

        Assert.Equal("Why is Documents not recoverable?", model.AnsweredQuestion);
        Assert.Equal("Because it is locked.", model.Answer);
    }

    [Fact]
    public async Task PressingASuggestionPutsItInTheQuestionBox()
    {
        var model = new AssistantViewModel(_ => Task.FromResult<IAssistantRuntime>(new Fake()));

        await model.AskAsync(new AssistantSuggestion("What is a recovery phrase?", []), CancellationToken.None);

        Assert.Equal("What is a recovery phrase?", model.Question);
    }

    [Fact]
    public async Task AnEmptyQuestionIsNotAsked()
    {
        var starts = 0;
        var model = new AssistantViewModel(_ => { starts++; return Task.FromResult<IAssistantRuntime>(new Fake()); });

        model.Question = "   ";
        await model.AskAsync(CancellationToken.None);

        Assert.Equal(0, starts);
        Assert.False(model.CanAsk);
    }

    [Fact]
    public async Task AFailureIsShownInWordsAndTheAnswerIsNotLeftBehind()
    {
        var model = new AssistantViewModel(_ => Task.FromResult<IAssistantRuntime>(new Fake("First answer.")));
        model.Question = "First?";
        await model.AskAsync(CancellationToken.None);

        var broken = new AssistantViewModel(_ => throw new InvalidOperationException("The model file is missing."));
        broken.Question = "Second?";
        await broken.AskAsync(CancellationToken.None);

        Assert.Null(broken.Answer);
        Assert.NotNull(broken.Failure);
        Assert.False(broken.Busy);
    }

    [Fact]
    public async Task ARuntimeThatFailedIsNotAskedAgain()
    {
        // A process that failed a question is not the one to ask the next one; the next attempt
        // starts a fresh one, which is also how a crashed child gets replaced.
        var starts = 0;
        var model = new AssistantViewModel(_ =>
        {
            starts++;
            return Task.FromResult<IAssistantRuntime>(new Fake(throws: true));
        });

        model.Question = "First?";
        await model.AskAsync(CancellationToken.None);
        model.Question = "Second?";
        await model.AskAsync(CancellationToken.None);

        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task DisposingEndsTheRuntimeThatWasStarted()
    {
        var runtime = new Fake();
        var model = new AssistantViewModel(_ => Task.FromResult<IAssistantRuntime>(runtime));
        model.Question = "Anything?";
        await model.AskAsync(CancellationToken.None);

        await model.DisposeAsync();

        Assert.True(runtime.Disposed);
    }

    [Fact]
    public async Task DisposingWithoutEverAskingIsHarmless()
    {
        var model = new AssistantViewModel(_ => throw new InvalidOperationException("Should never start."));

        await model.DisposeAsync();
    }

    [Fact]
    public async Task AnAnswerCutOffAtItsLimitIsFlaggedSoTheScreenCanSaySo()
    {
        var model = new AssistantViewModel(_ => Task.FromResult<IAssistantRuntime>(new Fake("Half a sen", truncated: true)));
        model.Question = "Why?";

        await model.AskAsync(CancellationToken.None);

        Assert.True(model.AnswerTruncated);
    }

    [Fact]
    public async Task AMachineWithoutAModelSaysSoBeforeTakingAQuestion()
    {
        // Not after taking one. A screen that accepts a question, thinks, and then reports a missing
        // file has spent somebody's time telling them something it knew before they typed.
        var model = new AssistantViewModel(
            _ => throw new InvalidOperationException("Should never start."),
            _ => Task.FromResult<string?>("Fortiq's assistant model is missing."));

        await model.CheckAsync(CancellationToken.None);

        Assert.True(model.Checked);
        Assert.False(model.Available);
        Assert.Contains("missing", model.Unavailable!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AMachineWithAModelIsAvailable()
    {
        var model = new AssistantViewModel(
            _ => Task.FromResult<IAssistantRuntime>(new Fake()),
            _ => Task.FromResult<string?>(null));

        await model.CheckAsync(CancellationToken.None);

        Assert.True(model.Available);
        Assert.Null(model.Unavailable);
    }

    [Fact]
    public async Task CheckingAgainAfterARepairFindsTheAssistant()
    {
        // The Check again button. Somebody who has just run the acquisition script should not have
        // to restart Fortiq to be told it worked.
        var repaired = false;
        var model = new AssistantViewModel(
            _ => Task.FromResult<IAssistantRuntime>(new Fake()),
            _ => Task.FromResult<string?>(repaired ? null : "The model is missing."));

        await model.CheckAsync(CancellationToken.None);
        Assert.False(model.Available);

        repaired = true;
        await model.CheckAsync(CancellationToken.None);

        Assert.True(model.Available);
    }

    [Fact]
    public async Task ACheckThatThrowsIsReportedRatherThanCrashingTheScreen()
    {
        var model = new AssistantViewModel(
            _ => Task.FromResult<IAssistantRuntime>(new Fake()),
            _ => throw new UnauthorizedAccessException("Access to the model folder is denied."));

        await model.CheckAsync(CancellationToken.None);

        Assert.True(model.Checked);
        Assert.NotNull(model.Unavailable);
    }

    [Fact]
    public async Task WhatFortiqKnowsAboutThePcGoesInFrontOfTheQuestion()
    {
        var runtime = new Fake();
        var model = new AssistantViewModel(
            _ => Task.FromResult<IAssistantRuntime>(runtime),
            prepareContext: _ => Task.FromResult("FORTIQ CONTEXT v1\ntask Documents: daily at 02:00"));
        model.Question = "Is anything at risk?";

        await model.AskAsync(CancellationToken.None);

        var context = Assert.Single(runtime.LastAsk!.Evidence);
        Assert.Contains("task Documents", context.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheContextComesBeforeTheEvidenceASuggestionCarries()
    {
        var runtime = new Fake();
        var model = new AssistantViewModel(
            _ => Task.FromResult<IAssistantRuntime>(runtime),
            prepareContext: _ => Task.FromResult("FORTIQ CONTEXT v1"));

        await model.AskAsync(
            new AssistantSuggestion("Why?", [new AssistantEvidence("engine error", "locked")]),
            CancellationToken.None);

        Assert.Equal(2, runtime.LastAsk!.Evidence.Count);
        Assert.Contains("FORTIQ CONTEXT", runtime.LastAsk.Evidence[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheContextIsRebuiltForEveryQuestion()
    {
        // A backup finishes, a drill fails, a disk is unplugged. An assistant answering from the
        // machine as it was when the screen opened would confidently describe one that is gone.
        var built = 0;
        var model = new AssistantViewModel(
            _ => Task.FromResult<IAssistantRuntime>(new Fake()),
            prepareContext: _ => { built++; return Task.FromResult("context"); });

        model.Question = "First?";
        await model.AskAsync(CancellationToken.None);
        model.Question = "Second?";
        await model.AskAsync(CancellationToken.None);

        Assert.Equal(2, built);
    }

    [Fact]
    public async Task AContextThatCannotBeBuiltIsLeftOutRatherThanFailingTheQuestion()
    {
        // "What is a recovery phrase?" does not need this machine's configuration.
        var runtime = new Fake();
        var model = new AssistantViewModel(
            _ => Task.FromResult<IAssistantRuntime>(runtime),
            prepareContext: _ => throw new IOException("The schedule folder is unreadable."));
        model.Question = "What is a recovery phrase?";

        await model.AskAsync(CancellationToken.None);

        Assert.Null(model.Failure);
        Assert.NotNull(model.Answer);
        Assert.Empty(runtime.LastAsk!.Evidence);
    }

    private sealed class Fake(string answer = "An answer.", bool truncated = false, bool throws = false) : IAssistantRuntime
    {
        public bool IsReady => true;

        public bool Disposed { get; private set; }

        public AssistantAsk? LastAsk { get; private set; }

        public Task<AssistantReply> AskAsync(AssistantAsk ask, CancellationToken cancellationToken)
        {
            LastAsk = ask;
            return throws
                ? Task.FromException<AssistantReply>(new InvalidDataException("The runtime stopped."))
                : Task.FromResult(new AssistantReply(answer, truncated));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
