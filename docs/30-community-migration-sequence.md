# Recommended Documentation and Implementation Migration Sequence

## P0 — Documentation decision layer

1. Add Specs 24–27.
2. Add ADR-016/017 as **Proposed**.
3. Update `docs/README.md`.
4. Update `08-open-decisions.md`.
5. Add compatibility notes to Specs 01, 02, 04, 17 and 20.

Goal: establish canonical vocabulary without pretending the code has changed.

## P1 — Community read model

> **Implemented.** `src/Fortiq.CommunityModel` holds the model; `LegacyScheduleProjector` in
> `Fortiq.Scheduling` populates it from the current schedule files. Nothing writes it yet.

Code models for:

```text
Source
Storage
StorageCredentialRef
RepositoryEngineRef
Identity
IdentityKey
EncryptionProfile
Task
Trigger
Route
```

Initially populate them by projecting current schedule files.

No repository recreation.  
No cryptographic migration.  
No scheduler replacement yet.

Two constraints the implementation added, both learned from what the projection has to survive:

- `Fortiq.CommunityModel` has **no project references**. It is the model the product moves onto, so
  it must not depend on the layers it outlives, and the projector therefore lives in the scheduling
  assembly - the dependency runs legacy to new and only that way, leaving no cycle to unpick at P3.
- A `Route` carries the **drill and retention triggers** although §4 of Spec 25 lists only a
  retention policy. They exist per schedule today; dropping them would make the projection lossy,
  and a read model that lost the drill schedule would let a screen show a source as covered while
  nothing proved it could be restored.

## P2 — GUI projection

Move toward:

```text
Home
Tasks
Sources
Storage
Recovery
Settings
```

Advanced/Diagnostics continues to expose repository/engine details.

## P3 — Native task writer

New configuration writes decomposed resource/task documents.

Legacy `fortiq.backup-schedule` v1 remains readable.

Migration MUST be idempotent.

## P4 — Trigger expansion

Add incrementally:

1. manual/one-shot;
2. daily/weekly/monthly;
3. watcher with settle/coalescing/minimum interval;
4. optional storage-connected/event triggers later.

## P5 — Storage expansion

Add SFTP only after Storage abstraction exists.

Do not add it as another special branch in repository-location parsing.

## P6 — Identity expansion

First separate Recipient, Writer and Storage Credential in the domain.

Implement asymmetric recipient envelopes only after an accepted crypto profile and stable test vectors.

## P7 — Health and receipts

Extend evidence with task/route references while preserving repository-level truth.

Task aggregation must never hide route-level failures.

## Release boundary

Do not block `0.1.0-beta.1` on the full Resource/Task redesign if the current repository-centric flow is stable and documentation states the boundary clearly.

Use Specs 24–27 as the architectural contract for the next Community evolution.

## Draft and validation layer

> **Implemented**, between P1 and P3, and before anything the assistant can act through.
> `Draft<T>`, `TaskProposal`, `DraftValidator` and `ActivationValidator` live in
> `src/Fortiq.CommunityModel`. What is not built is a store to keep drafts in and a screen to review
> them on; both wait for the native task documents of P3, so that a draft and a saved task have one
> shape rather than two.

A proposal reaches live configuration only along one path:

```text
proposal → validate → ReadyForReview → a person accepts → Accepted → activation allowed
```

Every step is enforced by the type rather than by convention. `Accept` is reachable only from
`ReadyForReview`; re-validating clears an earlier acceptance, so a task accepted last week whose
folder has since been deleted does not stay accepted; and `ActivationValidator` re-checks the
blocking findings even on a draft somebody constructed directly in the `Accepted` state, because
that is precisely how a validation step gets skipped by accident.

There is no argument to any of it that would let a caller bypass review. That absence is what
enforces RULE-TASK-002: an assistant holding a draft has no expressible way to activate it.

## P8 — Local Assistant foundation

Introduce the assistant only after the Resource/Task schemas are stable enough to serve as typed targets.

1. Implement Prepared Context Builder.
2. Implement semantic response schema.
3. Add local model runtime abstraction.
4. Benchmark a Qwen3.5-2B-class GGUF model around Q4_K_M size.
5. Add Draft entity proposal generation.
6. Add conversational editing over the same Draft objects used by forms.
7. Keep activation, execution and destructive operations outside inference.

Initial assistant context target: 8K–16K, composed from relevant structured context rather than the full documentation corpus.

## P9 — Assistant advice and grounding

After Draft generation is reliable:

- expose evidence-backed Facts;
- explanations;
- recommendations;
- validation assistance;
- optional anomaly explanations.

The assistant must never become the source of `Recoverable`, `Unproven` or `AtRisk` truth.
