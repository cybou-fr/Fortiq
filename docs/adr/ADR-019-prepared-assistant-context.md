# ADR-019: Prepared Fortiq Context and Typed Assistant Responses

- Status: **Proposed**
- Date: **September 7, 2026**
- Scope: Assistant grounding and response semantics

## Context

A small local model should not be the authoritative source for product capabilities, security rules or operational facts.

Passing the entire Fortiq documentation set to every conversation would be inefficient and would still make factual grounding ambiguous.

## Decision

Build a deterministic, versioned Prepared Context for each assistant interaction.

It contains:

1. Product Context;
2. installed Capability Context;
3. sanitized User Configuration Context;
4. bounded Operational Context.

Assistant responses use typed semantic items:

```text
Fact
Finding
Explanation
Recommendation
DraftProposal
Question
Warning
```

Facts should reference deterministic Fortiq context facts when possible.

No free-form assistant text is executable configuration.

## Consequences

- a 2B-class model can operate with a modest 8K–16K context;
- unsupported capabilities are easier to handle truthfully;
- the UI can visually distinguish fact from recommendation;
- product documentation can evolve without retraining the model;
- no cloud RAG/vector database is required for the core assistant path.
