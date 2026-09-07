# Specification 28: Local Conversational Assistant & Draft Entity Authoring

> **Implementation status: Design intent.**  
> The assistant is cross-platform by design. It is not tied to Windows, Phi Silica, Copilot+ hardware, WinML, DirectML or any single accelerator/runtime.

## 1. Purpose

Fortiq Community SHOULD include a small local conversational language model that acts as a natural-language interface over the Fortiq resource and policy model.

The assistant is allowed to:

- explain Fortiq concepts and current configuration;
- answer questions using prepared Fortiq context;
- advise on backup/recovery strategy;
- create proposals for Fortiq entities;
- create and edit Draft Tasks;
- create Draft Resources needed by those Tasks;
- explain deterministic validation errors;
- summarize sanitized health/evidence;
- suggest corrective actions.

The assistant is **not** a privileged execution authority.

Central invariant:

> **LLM-created or LLM-modified persistent objects are proposals/drafts until deterministic validation and explicit user acceptance.**

For Backup Tasks specifically:

> **LLM-generated Tasks are never activated automatically.**

---

## 2. Cross-platform architecture

```text
Fortiq UI / CLI
      │
      ▼
Fortiq Assistant API
      │
      ├── Prepared Context Builder
      ├── Conversation State
      ├── Entity Proposal Builder
      ├── Draft Store
      ├── Deterministic Validator
      └── Local Inference Runtime
               │
               └── GGUF-compatible model
```

The domain layer MUST NOT depend on an operating-system-specific inference API.

Platform-specific acceleration MAY be used opportunistically.

A CPU-capable cross-platform runtime MUST remain a supported baseline where practical.

---

## 3. Preferred model profile

Initial Community target:

```text
Model class: Qwen3.5-2B-class instruct model
Format: GGUF
Quantization target: Q4_K_M-class
Model package target: approximately 1.0–1.5 GB
Runtime: llama.cpp-compatible abstraction
Default mode: non-thinking / concise structured output
Default context: 8K–16K
Optional larger context: implementation-dependent
```

The exact model MUST remain replaceable.

Fortiq domain schemas MUST NOT contain model-vendor or model-family identifiers.

The model manifest SHOULD include:

- model ID;
- model version;
- format;
- quantization;
- SHA-256;
- license;
- context limits;
- minimum runtime version;
- supported languages/capabilities.

Fortiq MAY ship alternative smaller/larger compatible model packs.

---

## 4. Runtime abstraction

Fortiq SHOULD define a narrow runtime contract such as:

```text
ILocalLanguageModel
├── GenerateStructuredAsync(...)
├── StreamTextAsync(...)
├── GetCapabilities()
└── GetModelManifest()
```

A llama.cpp/GGUF implementation is a suitable initial runtime direction because the architecture must support Windows, Linux and macOS without changing Fortiq entity logic.

Runtime selection, hardware acceleration and quantization are implementation details.

---

## 5. Assistant works over the full Fortiq logical model

The assistant MAY propose or edit the following logical entities:

```text
Source
Storage
Storage Credential reference
Identity
Identity Key public metadata
Encryption Profile
Backup Task
Trigger
Route
Retention Policy
Recovery Policy
```

The assistant MUST NOT generate/store secret credential material or private encryption keys.

Examples:

### Source proposal

```text
User:
Protect my Projects folder too.

Assistant proposal:
Source: Active Projects
Path: D:\Projects
Kind: folder
Status: Draft
```

### Storage proposal

```text
User:
I also want an SFTP copy.

Assistant proposal:
Storage: Company SFTP
Backend: SFTP
Host: backup.example.com
Path: /fortiq
Credential: not configured
Status: Incomplete Draft
```

### Identity / Encryption Profile proposal

```text
User:
I want me and my paper recovery identity to be able to restore it.

Assistant proposal:
Encryption Profile: Personal Recovery
Recipients:
- Stanislav
- Paper Recovery
Writer:
- This Device
Status: Draft
```

### Task proposal

```text
User:
Back up Documents and Projects every night to MinIO,
and Documents to SFTP every Sunday.

Assistant:
I prepared two Draft Tasks.
```

---

## 6. Draft entity lifecycle

Canonical lifecycle:

```text
Conversation
    ↓
LLM Proposal
    ↓
Typed Draft / Proposal
    ↓
Deterministic Validation
    ↓
User Review / Edit
    ↓
Explicit Accept / Activate
    ↓
Persistent/Active Configuration
```

Suggested generic proposal states:

```text
Draft
InvalidDraft
ReadyForReview
Accepted
Archived
```

Suggested Task states:

```text
Draft
InvalidDraft
ReadyForReview
Active
Paused
Archived
```

The LLM MUST NOT transition a Task into `Active`.

---

## 7. Structured generation contract

The LLM SHOULD emit typed proposals rather than executable commands.

Example:

```json
{
  "type": "DraftProposal",
  "operation": "create",
  "entityType": "BackupTask",
  "entity": {
    "name": "Daily Cloud Backup",
    "sourceRefs": ["source-documents", "source-projects"],
    "trigger": {
      "type": "dailyAt",
      "time": "02:30",
      "timeZone": "Europe/Paris"
    },
    "routeRefs": ["route-home-minio"]
  }
}
```

A deterministic validator resolves:

- referenced resource existence;
- schema validity;
- backend support in the installed build;
- engine/backend compatibility;
- credential presence/configuration;
- encryption profile validity;
- writer availability for recurring repositories;
- recurrence validity;
- source capability requirements;
- retention/recovery policy safety;
- activation prerequisites.

Model output is always treated as untrusted input.

---

## 8. Unsupported capability handling

The assistant MUST distinguish:

```text
logical possibility
from
capability available in this installed Fortiq build
```

Example:

```text
User:
Create an SFTP backup every Sunday.

Assistant:
I can prepare that configuration, but this installed Fortiq build does
not currently support SFTP execution.

I created it as an inactive future Draft.
```

The LLM MUST NOT claim unsupported execution capability merely because the entity schema allows it.

---

## 9. Conversational editing

Conversation and structured forms MUST edit the same typed Draft.

Example:

```text
User:
Make the MinIO task every 6 hours instead.

Assistant:
Updated the Draft Task:
Documents + Projects
Every 6 hours
→ Home MinIO

It remains inactive.
```

The same object can then be edited in the GUI without conversion or regeneration.

---

## 10. Review and activation boundary

Before activation Fortiq SHOULD show a deterministic summary, generated from validated entity data rather than free-form LLM text.

```text
Daily Cloud Backup

Sources
Documents
Projects

Trigger
Every 6 hours

Destination
Home MinIO

Engine
restic

Encryption
Personal Recovery

Recipients
Stanislav
Paper Recovery

Writer
This Device

[Edit]   [Activate Task]
```

Task activation MUST occur outside the inference process and require explicit user intent.

---

## 11. Assistant permissions

The assistant MAY read sanitized configuration/evidence and write Draft/Proposal objects.

It MUST NOT directly:

- activate a Task;
- start or stop a backup run;
- delete active configuration;
- perform destructive retention;
- remove the last sovereign recovery recipient;
- rotate/delete keys;
- modify plaintext storage credentials;
- clear repository locks;
- perform in-place restore;
- change a recovery verdict;
- mark recovery as proven.

It MAY recommend these actions and create proposals for review where appropriate.

---

## 12. Advice and explanation

The assistant SHOULD be able to answer questions such as:

```text
How well protected am I?
Why did this backup fail?
What would you recommend?
What does Writer mean?
Why do I need a second storage?
Can I back this up only when the disk is connected?
```

Advice MUST be grounded in prepared Fortiq context.

Example:

```text
FACT
All three active Routes use Home MinIO.

FACT
The last successful recovery proof was 45 days ago.

RECOMMENDATION
Consider adding a second independent Storage.

RECOMMENDATION
Consider a weekly or biweekly Recovery Drill.
```

The assistant may explain facts, but it does not create their truth value.

---

## 13. Fact / finding / recommendation separation

Assistant responses SHOULD carry semantic response types:

```text
Fact
Finding
Explanation
Recommendation
DraftProposal
Question
Warning
```

This allows the UI to distinguish evidence-backed facts from generated advice.

A Recommendation MUST NOT be rendered with the same visual semantics as a deterministic health verdict.

---

## 14. Secret and content boundaries

Assistant context MUST NOT contain:

- plaintext recovery mnemonics;
- private recipient keys;
- EUS;
- storage secret credentials;
- password-command payloads;
- raw protected file contents by default.

The assistant MAY receive sanitized metadata:

```text
Source name / type / path policy
Storage name / backend / capabilities
Task and Route configuration
Identity labels / public metadata
Encryption Profile membership
validation errors
health findings
receipt summaries
engine/backend support matrix
```

Sensitive paths/file names MAY be redacted by user policy.

---

## 15. Relationship to deterministic assurance

```text
Receipts / Repository facts / Health facts
                │
                ├── Deterministic assurance
                │         ↓
                │   Recoverable / Unproven / AtRisk
                │
                └── Assistant context
                          ↓
                    Explanation / Advice
```

The LLM MUST NOT determine:

- whether a repository is recoverable;
- whether a backup succeeded;
- whether a recovery proof is valid;
- whether an immutable-storage guarantee currently holds.

Those remain deterministic.

---

## 16. Conversation memory

Conversation history SHOULD be local and user-controlled.

Fortiq SHOULD distinguish:

```text
ephemeral conversation
persistent local conversation history
Draft/Proposal store
active product configuration
```

Conversation text does not become configuration until materialized into a typed Draft/Proposal.

Deleting conversation history MUST NOT delete accepted configuration.

---

## 17. Packaging

Suggested packaging:

```text
Fortiq Core
Fortiq Assistant Runtime
Fortiq Model Pack (~1.0–1.5 GB)
```

The model MAY be:

- bundled in a larger package;
- downloaded after installation;
- replaced by another compatible verified model.

Model acquisition and update MUST follow Fortiq supply-chain verification rules.

---

## 18. Product identity

The assistant is not a generic chatbot attached to a backup application.

Its role is:

> **A local semantic control layer over Fortiq's typed sovereign backup configuration.**

Natural language produces understandable, reviewable typed configuration; deterministic Fortiq components validate and execute only accepted policy.
