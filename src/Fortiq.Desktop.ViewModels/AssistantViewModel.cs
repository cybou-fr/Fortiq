using System.ComponentModel;
using System.Runtime.CompilerServices;
using Fortiq.Assistant;
using Fortiq.CommunityModel;

namespace Fortiq.Desktop.ViewModels;

/// <summary>
/// A question worth offering, with the machine facts that answer it already attached.
/// </summary>
/// <param name="Question">What the person would ask, in their words.</param>
/// <param name="Evidence">What the assistant needs in order to answer this particular one.</param>
/// <remarks>
/// Suggestions are built from the actual state of this machine, not from a fixed list. Offering
/// "Why did last night's backup fail?" on a PC where nothing failed is how a helpful screen becomes
/// a decorative one; and somebody whose backup did fail should not have to compose the question.
/// </remarks>
public sealed record AssistantSuggestion(string Question, IReadOnlyList<AssistantEvidence> Evidence)
{
    public AssistantAsk ToAsk() => new(Question, Evidence);
}

/// <summary>
/// The assistant screen: asks a local model a question and shows what it said.
/// </summary>
/// <remarks>
/// The runtime is started on the first question rather than when the screen opens. Loading a model
/// costs seconds and a gigabyte of memory, and most sessions with Fortiq never ask it anything -
/// somebody who opens the app to check that last night worked should not pay for an assistant they
/// did not use. The delay is named on screen when it happens.
///
/// Nothing here can act. There is no path from an answer to an operation, by construction: a reply
/// is a string, and the only thing this does with it is show it.
/// </remarks>
public sealed class AssistantViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<IAssistantRuntime>> _start;
    private readonly Func<CancellationToken, Task<string?>>? _describeUnavailable;
    private readonly Func<CancellationToken, Task<AssistantContext>>? _prepareContext;
    private IAssistantRuntime? _runtime;
    private CancellationTokenSource? _operation;

    public AssistantViewModel(
        Func<CancellationToken, Task<IAssistantRuntime>> start,
        Func<CancellationToken, Task<string?>>? describeUnavailable = null,
        Func<CancellationToken, Task<AssistantContext>>? prepareContext = null)
    {
        _start = start ?? throw new ArgumentNullException(nameof(start));
        _describeUnavailable = describeUnavailable;
        _prepareContext = prepareContext;
    }

    /// <summary>True once availability has been established, either way.</summary>
    public bool Checked { get; private set; }

    /// <summary>Why the assistant cannot run on this machine, when it cannot.</summary>
    /// <remarks>
    /// Established before a question box is offered rather than discovered by asking one. A screen
    /// that takes a question, thinks, and then says the model is missing has wasted somebody's time
    /// to tell them something it knew before they typed.
    /// </remarks>
    public string? Unavailable { get; private set; }

    public bool Available => Checked && Unavailable is null;

    /// <summary>Looks for the model and the runtime. Safe to call again after a repair.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (_describeUnavailable is null)
        {
            Checked = true;
            Unavailable = null;
            RaiseAll();
            return;
        }

        try
        {
            Unavailable = await _describeUnavailable(cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Unavailable = PlainFailure.Describe(error);
        }
        finally
        {
            Checked = true;
            RaiseAll();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Questions offered for this machine, in this state.</summary>
    public IReadOnlyList<AssistantSuggestion> Suggestions { get; set; } = [];

    /// <summary>What the person has typed.</summary>
    public string Question { get; set; } = string.Empty;

    /// <summary>The question the answer on screen belongs to, so the two cannot drift apart.</summary>
    public string? AnsweredQuestion { get; private set; }

    public string? Answer { get; private set; }

    /// <summary>True when the model stopped at its limit rather than finishing.</summary>
    public bool AnswerTruncated { get; private set; }

    /// <summary>
    /// The answer as separate statements, when the model gave one.
    /// </summary>
    /// <remarks>
    /// A screen can show a Fact as something Fortiq recorded and a Recommendation as an opinion;
    /// with only a paragraph it has to show both in the same voice, which is how the model's guesses
    /// come to look like Fortiq's records.
    /// </remarks>
    public AssistantResponse? Statements { get; private set; }

    /// <summary>
    /// Where the assistant claimed to be quoting Fortiq and was not.
    /// </summary>
    /// <remarks>
    /// Kept and shown rather than silently corrected. Somebody reading an answer is entitled to know
    /// that part of it was the model's invention presented as a record.
    /// </remarks>
    public IReadOnlyList<ValidationFinding> Ungrounded { get; private set; } = [];

    public string? Failure { get; private set; }

    public bool Busy { get; private set; }

    /// <summary>What is happening, while something is.</summary>
    public string? Status { get; private set; }

    public bool CanAsk => !Busy && !string.IsNullOrWhiteSpace(Question);

    public Task AskAsync(CancellationToken cancellationToken) =>
        AskAsync(new AssistantAsk(Question, []), cancellationToken);

    public Task AskAsync(AssistantSuggestion suggestion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        Question = suggestion.Question;
        Raise(nameof(Question));
        return AskAsync(suggestion.ToAsk(), cancellationToken);
    }

    private async Task AskAsync(AssistantAsk ask, CancellationToken cancellationToken)
    {
        if (Busy || string.IsNullOrWhiteSpace(ask.Question))
        {
            return;
        }

        Busy = true;
        Failure = null;
        Answer = null;
        AnsweredQuestion = null;
        AnswerTruncated = false;
        Statements = null;
        Ungrounded = [];
        RaiseAll();

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _operation = operation;

        AssistantContext? context = null;

        try
        {
            if (_runtime is null)
            {
                // Said before it is waited for. Several seconds of a window doing nothing visible is
                // how somebody concludes an application has hung.
                Status = "Starting the assistant. It runs on this PC, so the first question takes a moment.";
                RaiseAll();
                _runtime = await _start(operation.Token);
            }

            Status = "Thinking.";
            RaiseAll();

            context = await PrepareContextAsync(operation.Token);
            var reply = await _runtime.AskAsync(WithContext(ask, context), operation.Token);

            // Grounded before it is shown, not after. An answer that has already been read as
            // Fortiq's record cannot be un-read by a correction underneath it.
            if (reply.Response is { } response && context is not null)
            {
                var grounded = ResponseGrounding.Ground(response, context);
                Statements = grounded.Response;
                Ungrounded = grounded.Findings;
                Answer = grounded.Response.Text;
            }
            else
            {
                Statements = reply.Response;
                Ungrounded = [];
                Answer = reply.Text;
            }

            AnswerTruncated = reply.Truncated;
            AnsweredQuestion = ask.Question;
        }
        catch (OperationCanceledException)
        {
            Failure = "Cancelled.";
        }
        catch (Exception error)
        {
            // The runtime is discarded rather than reused. A process that failed a question is not
            // one to ask the next one; the next attempt starts a fresh one.
            await DisposeRuntimeAsync();
            Failure = PlainFailure.Describe(error);
        }
        finally
        {
            _operation = null;
            Busy = false;
            Status = null;
            RaiseAll();
        }
    }

    /// <summary>
    /// Puts what Fortiq knows about this machine in front of the question.
    /// </summary>
    /// <remarks>
    /// As evidence, deliberately, and not as a second system instruction. Most of the context is
    /// names that came off somebody's disk - folders, storages, tasks - and a folder called "ignore
    /// previous instructions" is one anybody can create. Fenced with everything else, it cannot
    /// address the model however it is worded.
    ///
    /// Rebuilt for every question rather than once per session. A backup finishes, a drill fails, a
    /// disk is unplugged; an assistant answering from the state of the machine as it was when the
    /// screen opened would be confidently describing a machine that no longer exists.
    ///
    /// A context that cannot be built is left out rather than reported. The question is still a
    /// question, and "what is a recovery phrase?" does not need this machine's configuration.
    /// </remarks>
    private async Task<AssistantContext?> PrepareContextAsync(CancellationToken cancellationToken)
    {
        if (_prepareContext is null)
        {
            return null;
        }

        try
        {
            return await _prepareContext(cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
    }

    private static AssistantAsk WithContext(AssistantAsk ask, AssistantContext? context) =>
        context is null
            ? ask
            : ask with { Evidence = [new AssistantEvidence("what Fortiq knows about this PC", context.Render()), .. ask.Evidence] };

    public void Cancel() => _operation?.Cancel();

    /// <summary>Forgets the answer on screen, leaving the question box as it was.</summary>
    public void ClearAnswer()
    {
        Answer = null;
        AnsweredQuestion = null;
        AnswerTruncated = false;
        Failure = null;
        RaiseAll();
    }

    public async ValueTask DisposeAsync()
    {
        _operation?.Cancel();
        await DisposeRuntimeAsync();
    }

    private async Task DisposeRuntimeAsync()
    {
        if (_runtime is not null)
        {
            await _runtime.DisposeAsync();
            _runtime = null;
        }
    }

    private void RaiseAll()
    {
        Raise(nameof(Busy));
        Raise(nameof(Status));
        Raise(nameof(Answer));
        Raise(nameof(AnsweredQuestion));
        Raise(nameof(AnswerTruncated));
        Raise(nameof(Statements));
        Raise(nameof(Ungrounded));
        Raise(nameof(Failure));
        Raise(nameof(CanAsk));
        Raise(nameof(Checked));
        Raise(nameof(Unavailable));
        Raise(nameof(Available));
    }

    private void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
