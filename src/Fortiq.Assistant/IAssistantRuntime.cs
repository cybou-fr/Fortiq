namespace Fortiq.Assistant;

/// <summary>What the assistant said, and nothing that has happened because of it.</summary>
/// <param name="Text">The model's answer. Shown to a person; never parsed into an action.</param>
/// <param name="Truncated">True when the model hit its limit rather than finishing its sentence.</param>
/// <param name="Response">
/// The same answer as separate statements, when the model was held to the response schema.
/// </param>
/// <remarks>
/// Both, rather than one or the other. The structured form is what a screen can treat differently -
/// showing a Fact as something Fortiq recorded and a Recommendation as an opinion - and the text is
/// what remains when a model, a schema or a parse did not cooperate. A caller that only ever reads
/// <paramref name="Text"/> keeps working.
/// </remarks>
public sealed record AssistantReply(string Text, bool Truncated, Fortiq.CommunityModel.AssistantResponse? Response = null);

/// <summary>
/// Somewhere a local model can be asked a question.
/// </summary>
/// <remarks>
/// An interface because the runtime underneath is expected to change - a subprocess today, something
/// in-process later, something else on a platform that needs it - and because the rest of Fortiq
/// should never be able to tell which. Spec 28 requires that the model stay replaceable, and a type
/// that named one would be the first thing to make it not.
///
/// Nothing here returns an action, a command, or anything the application will run. That is the
/// design, not an omission: a reply is text for a person to read, and every path that changes
/// something on the machine is reached through deterministic code and an explicit confirmation.
/// </remarks>
public interface IAssistantRuntime : IAsyncDisposable
{
    /// <summary>Whether the runtime is ready to answer. False while it is still starting.</summary>
    bool IsReady { get; }

    /// <summary>Asks the model, and gives up rather than hanging if it does not answer.</summary>
    Task<AssistantReply> AskAsync(AssistantAsk ask, CancellationToken cancellationToken);
}
