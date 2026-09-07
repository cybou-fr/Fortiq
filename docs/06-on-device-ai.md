# Fortiq Intelligence & On-Device AI Boundaries

> **Implementation status, layer by layer**, because "implemented" was hiding most of the work:
>
> | Layer | State |
> |---|---|
> | Model supply chain | implemented |
> | Inference runtime | implemented |
> | Prompt / evidence boundary | implemented |
> | Assistant screen | implemented |
> | Prepared context | implemented, not yet sent to the model |
> | Typed responses | implemented, schema-constrained, grounded |
> | Typed proposals | implemented (`TaskProposal`, not yet produced by the model) |
> | Deterministic validator | implemented |
> | Draft store and review | draft lifecycle implemented; no store, no review screen |
>
> Today the assistant reads state and explains it, and every action is still reached by hand. The
> flow further down this document describes where the typed path will go, not what runs now.

## Purpose & Scope

Fortiq's assistant is a strictly local advisory model. It runs on the machine, on the CPU, from a
file that ships in the installation package or is fetched during installation. It is never a
service, it is never consulted over the network, and no part of it is remote.

This replaces an earlier design that named Microsoft Phi Silica and the Windows AI APIs as the
inference provider. That design was gated on a Copilot+ PC with an NPU — hardware the project does
not have and cannot require of anybody who wants to back up their files — and it made the assistant
conditional on the machine. A GGUF model on a llama.cpp-compatible runtime runs on ordinary
hardware, on Windows, Linux and macOS, which is what the Community edition needs. The pinned
profile is in [Spec 28](28-local-conversational-assistant.md) and
[ADR-018](adr/ADR-018-local-llm-draft-task-authoring.md); what ships today is recorded in
[models/NOTICE.md](../models/NOTICE.md).

## The model is required, and it decides nothing

These are two separate statements and both matter.

**Required - of an installation, not of a launch.** A release ships the model and the runtime, and
`New-DeploymentBundle.ps1` refuses to build without them: an installation lacking one did not
finish. But Fortiq opens anyway, and the Assistant screen is what reports the absence and offers to
repair it.

This was the other way round, and it was wrong. Startup checked for the model, so a missing
`model.gguf` stopped the application before its window - which made a component that touches no
backup, no key and no restore into the thing standing between somebody and their data. The
assistant is not a security dependency and it must not become an availability one; for a recovery
product, that trade is indefensible. The backup engine keeps its startup check, because without it
no backup can run at all.

**Decides nothing.** Backup, encryption, scheduling, verification and recovery are complete without
ever consulting the model, and they do not consult it. The assistant drafts and explains; the
deterministic paths do the work. Nothing below the confirmation dialog knows a model exists.

The clearest expression of both: **`Fortiq.Recover` carries no model and needs none.** The tool
somebody copies onto whatever they had to hand, to get their files back on a machine that has never
seen Fortiq, does not contain a gigabyte of model and never asks one anything. If the assistant were
load-bearing, it would be load-bearing there, and it is not.

---

## Allowed Advisory Use Cases

- Plain-language explanation of complex backup warnings or restore errors;
- Semantic summarization of file and directory delta changes between snapshots;
- Explanations of anomaly signals (e.g., sudden entropy shifts or spike in encrypted extensions);
- Translating user natural language requests into structured, previewable restore proposals;
- Draft generation of backup tasks and retention schedules for human review.

---

## Strict Security Prohibitions (Forbidden Capabilities)

Under NO circumstances may AI models or components:

1. Access, request, or handle encryption keys, Engine Unlock Secrets (EUS), mnemonics, or KMS tokens;
2. Directly execute destructive operations, snapshot deletions, or unconfirmed restores;
3. Mutate retention policies, storage immutability profiles, or cryptographic audit logs;
4. Execute instructions or shell scripts embedded within backed-up document contents (defeating prompt injection);
5. Directly interface with the repository engine or privileged Windows platform brokers.

Prohibition 4 is not hypothetical. The assistant's inputs include filenames, paths and log text that
came from somebody's disk, and anything read from a backed-up file is data. Text arriving that way
cannot authorize an action, and an action is authorized by the person in front of the confirmation
dialog or by nobody.

---

## Safe Execution Flow: Deterministic Air-Gap

```text
User Natural-Language Query
  → Input Sanitization & Data Minimization
  → Local model (GGUF, llama-server child process on 127.0.0.1)
  → Strictly Typed JSON Schema Parser
  → Deterministic Validator (Path Bounds & Repository Existence)
  → Policy Engine Authorization
  → Explicit Human Confirmation Dialog
  → Execution via Fortiq Service / Engine Adapter
```

Free-form natural language generated by an LLM is NEVER directly executed. Action proposals must
conform to typed schema structures and pass deterministic validation rules before triggering an
interactive confirmation prompt.

---

## Supply chain

The model and the runtime that runs it are large binaries this product redistributes and then runs
on somebody else's machine, which is exactly what the backup engine is, so both are treated the same
way:

- pinned in `models/manifest.json` by exact length and SHA-256, from a fixed source revision rather
  than a branch that can move;
- acquired by `scripts/Get-Model.ps1`, which downloads to a `.partial`, verifies length and hash,
  and only then renames into place, so an interrupted install never leaves something that looks
  installed;
- the runtime pinned by its *archive* hash in `runtimes/manifest.json` and acquired by
  `scripts/Get-Runtime.ps1`, which verifies the archive before extracting anything - see
  [runtimes/NOTICE.md](../runtimes/NOTICE.md) for why the executable's own hash would verify nothing;
- verified again by `scripts/New-DeploymentBundle.ps1` before publishing and after copying;
- redistributed with their licences: `models/LICENSE-Apache-2.0.txt`, `models/NOTICE.md`,
  `runtimes/LICENSE-llama.cpp.txt` and `runtimes/NOTICE.md` are each required for a bundle to build.

At startup the length is checked and the hash is not. Re-hashing the whole file would add seconds to
every launch to re-answer a question installation already answered, and the failure that actually
happens afterwards — an interrupted copy — is caught by the length.

---

## Where the assistant runs

Not in the desktop's process. `llama-server` is started as a child, bound to `127.0.0.1`, with an
API key generated for that one run, and is killed with the application - process tree included.

The boundary is the point. This is the component that reads text somebody else wrote: file names,
paths, log lines, engine output. It is the one most likely to be fed something hostile, and the one
whose failure should cost the least. A model that exhausts memory, loops, or is talked into
misbehaving takes down a child process that can be restarted, not the window somebody has open
because they are trying to get their data back.

Loopback is not by itself a boundary - every process on the machine shares it - which is why the API
key exists. Without it, anything running as the user could use somebody's Fortiq installation as a
free inference server.

Reasoning is disabled. Measured on the pinned model: with it on, the model spent its entire token
budget thinking and returned an empty answer. Spec 28 asks for non-thinking concise output as the
default mode; `LlamaChatProtocol` is where that is set.
