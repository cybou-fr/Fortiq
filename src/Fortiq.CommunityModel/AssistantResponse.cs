namespace Fortiq.CommunityModel;

/// <summary>What kind of thing the assistant just said.</summary>
/// <remarks>
/// The distinction that matters is between the kinds that claim to be Fortiq's truth -
/// <see cref="Fact"/> and <see cref="Finding"/> - and the kinds that are only the model talking.
/// A screen may present the first two as recorded, and must never present the rest that way.
/// </remarks>
public enum SemanticItemKind
{
    /// <summary>Something Fortiq recorded. Must point at the fact it came from.</summary>
    Fact,

    /// <summary>Something Fortiq's health model concluded. Must point at the finding.</summary>
    Finding,

    /// <summary>What something means, in plain words. The model's own sentence.</summary>
    Explanation,

    /// <summary>What the person might do. The model's own opinion.</summary>
    Recommendation,

    /// <summary>A draft the model prepared. Points at the proposal, which is validated separately.</summary>
    DraftProposal,

    /// <summary>Something the model needs to know before it can answer.</summary>
    Question,

    /// <summary>Something the person should be careful about.</summary>
    Warning
}

/// <summary>
/// One thing the assistant said.
/// </summary>
/// <param name="Kind">What sort of statement it is.</param>
/// <param name="Text">The sentence, for a person to read.</param>
/// <param name="FactRef">Which recorded fact this rests on, for a Fact or a Finding.</param>
/// <param name="ProposalRef">Which draft this refers to, for a DraftProposal.</param>
public sealed record SemanticItem(
    SemanticItemKind Kind,
    string Text,
    string? FactRef = null,
    string? ProposalRef = null);

/// <summary>An answer as a list of statements rather than a paragraph.</summary>
public sealed record AssistantResponse(IReadOnlyList<SemanticItem> Items)
{
    public static AssistantResponse Empty { get; } = new([]);

    /// <summary>Everything the assistant said, as one readable answer.</summary>
    public string Text => string.Join("\n\n", Items.Select(item => item.Text));
}

/// <summary>What grounding did to an answer, and why.</summary>
public sealed record GroundedResponse(AssistantResponse Response, IReadOnlyList<ValidationFinding> Findings);

/// <summary>
/// Checks that what the assistant presented as recorded was actually recorded.
/// </summary>
/// <remarks>
/// Spec 29 §7 lists what the assistant must not fabricate: existing resources, successful runs,
/// recovery proofs, immutable-storage status. A language model asked about backups will produce
/// exactly those sentences whether or not it was given them, in the same confident register as the
/// true ones, and no amount of instruction fixes that reliably.
///
/// So a claim to be stating Fortiq's truth is checked against the facts Fortiq actually supplied. An
/// item that does not check out is not deleted - deleting it would hide that the model said it, and
/// the person asked a question they still deserve an answer to - it is demoted to an
/// <see cref="SemanticItemKind.Explanation"/>. The sentence survives as the model's own words; what
/// it loses is the right to be displayed as something Fortiq recorded.
/// </remarks>
public static class ResponseGrounding
{
    public static GroundedResponse Ground(AssistantResponse response, AssistantContext context)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(context);

        var known = new HashSet<string>(
            context.OperationalFacts.Select(fact => fact.Reference),
            StringComparer.OrdinalIgnoreCase);

        var items = new List<SemanticItem>(response.Items.Count);
        var findings = new List<ValidationFinding>();

        foreach (var item in response.Items)
        {
            if (item.Kind is not (SemanticItemKind.Fact or SemanticItemKind.Finding))
            {
                items.Add(item);
                continue;
            }

            if (item.FactRef is { Length: > 0 } reference && known.Contains(reference))
            {
                items.Add(item);
                continue;
            }

            findings.Add(new ValidationFinding(
                "assistant-ungrounded-fact",
                item.FactRef is { Length: > 0 } named
                    ? $"The assistant stated a fact referring to '{named}', which Fortiq did not record."
                    : "The assistant stated a fact without saying what Fortiq recorded it from.",
                ValidationSeverity.Warning));

            items.Add(item with { Kind = SemanticItemKind.Explanation, FactRef = null });
        }

        return new GroundedResponse(new AssistantResponse(items), findings);
    }
}
