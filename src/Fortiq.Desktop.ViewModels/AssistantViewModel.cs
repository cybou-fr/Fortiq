using System.ComponentModel;
using System.Runtime.CompilerServices;
using Fortiq.Assistant;

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
    private IAssistantRuntime? _runtime;
    private CancellationTokenSource? _operation;

    public AssistantViewModel(Func<CancellationToken, Task<IAssistantRuntime>> start) =>
        _start = start ?? throw new ArgumentNullException(nameof(start));

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
        RaiseAll();

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _operation = operation;

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

            var reply = await _runtime.AskAsync(ask, operation.Token);
            Answer = reply.Text;
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
        Raise(nameof(Failure));
        Raise(nameof(CanAsk));
    }

    private void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
