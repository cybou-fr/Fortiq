# Recommended Documentation and Implementation Migration Sequence

## P0 — Documentation decision layer

1. Add Specs 24–27.
2. Add ADR-016/017 as **Proposed**.
3. Update `docs/README.md`.
4. Update `08-open-decisions.md`.
5. Add compatibility notes to Specs 01, 02, 04, 17 and 20.

Goal: establish canonical vocabulary without pretending the code has changed.

## P1 — Community read model

Introduce code models for:

```text
Source
Storage
Identity
EncryptionProfile
Task
Route
```

Initially populate them by projecting current schedule files.

No repository recreation.  
No cryptographic migration.  
No scheduler replacement yet.

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
