# Specification 29: Prepared Assistant Context & Semantic Response Contract

> **Implementation status: context and semantic response contract implemented; drafts not yet
> generated.**
> `AssistantContext`, `AssistantContextBuilder`, `ProductRules` and `CommunityCapabilities` live in
> `src/Fortiq.CommunityModel`. Nothing yet sends the context to the model, and the typed response
> schema of §6 onwards does not exist - today the assistant returns prose.
>
> Two things the implementation settled that this document did not say:
>
> - **Capability facts and capability enforcement read the same object.** `CommunityCapabilities`
>   is what `CapabilityValidator` refuses proposals against and what the context reports. Two lists
>   would drift within a release, and the symptom is an assistant offering a trigger that is then
>   rejected the moment somebody accepts it - which reads as a broken product, not an absent feature.
> - **The context is evidence, not instruction.** Folder, storage and task names in it came off
>   somebody's disk, and a folder called "ignore previous instructions" is one anybody can create.
>   The rendered context therefore goes inside the same fence as any other machine data, and the
>   product rules are phrased as statements about the assistant rather than as instructions a
>   resource name could imitate.

## 1. Purpose

A small local model cannot be expected to infer Fortiq's current capabilities, security invariants and user configuration reliably from a large free-form manual.

Fortiq SHOULD therefore provide a compact, prepared, versioned context assembled from deterministic sources.

The assistant reasons over this context but does not author the underlying facts.

---

## 2. Context layers

Prepared context SHOULD contain four independent layers:

```text
1. Product Context
2. Capability Context
3. User Configuration Context
4. Operational Context
```

### 2.1 Product Context

Defines canonical concepts and invariants:

```text
Source
Storage
Storage Credential
Engine
Identity
Identity Key
Encryption Profile
Task
Trigger
Route
Repository
Recovery Kit
Run
```

Example rules:

```text
RULE-TASK-001
LLM-generated Tasks are Drafts.

RULE-TASK-002
Only explicit user action can activate a Task.

RULE-KEY-001
Storage credentials and recovery identities are separate authorities.

RULE-RESTIC-001
Recurring restic repositories require a Writer unlock.

RULE-RECOVERY-001
Recovery truth is determined by evidence, not by the assistant.
```

### 2.2 Capability Context

Describes what the installed build actually supports.

Example:

```json
{
  "engines": {
    "restic": {
      "enabled": true,
      "version": "0.19.1"
    }
  },
  "storageBackends": {
    "filesystem": true,
    "s3": true,
    "sftp": false
  },
  "triggers": {
    "manual": true,
    "dailyAt": true,
    "weeklyAt": false,
    "fileChange": false
  }
}
```

The assistant MUST use this layer to avoid claiming unavailable functionality.

### 2.3 User Configuration Context

Contains sanitized existing objects:

```text
Sources
Storages
Identities
Encryption Profiles
Tasks
Routes
Drafts
```

It SHOULD expose stable IDs and friendly labels.

Secrets MUST be represented only as state:

```text
credentialConfigured: true
writerAvailable: true
recipientPublicKeyConfigured: true
```

not as secret values.

### 2.4 Operational Context

Contains recent sanitized facts and findings:

```text
last backup
last failure
last recovery proof
storage availability
route health
repository health
deterministic findings
anomaly findings
validation results
```

This layer SHOULD be bounded/relevant rather than dumping complete history.

---

## 3. Context Builder

A deterministic `AssistantContextBuilder` SHOULD:

1. resolve the user's current conversational scope;
2. include relevant product rules;
3. include installed capability facts;
4. select related resources/tasks;
5. include bounded relevant evidence;
6. remove/redact secrets;
7. emit a versioned context envelope.

Example:

```json
{
  "schema": "fortiq.assistant-context",
  "version": 1,
  "productRules": ["RULE-TASK-001", "RULE-KEY-001"],
  "capabilities": {},
  "resources": {},
  "operationalFacts": [],
  "locale": "ru-RU"
}
```

---

## 4. Context budget

Default assistant operation SHOULD target a modest context window such as 8K–16K tokens.

Fortiq SHOULD prefer:

```text
relevant structured context
```

over:

```text
entire documentation corpus
```

Long-term retrieval MAY be added later, but the default configuration authoring path SHOULD not require a vector database or cloud RAG service.

---

## 5. Documentation knowledge

Selected documentation rules MAY be compiled into short versioned knowledge entries.

Example:

```text
KB-STORAGE-OBJECTLOCK-001
Object Lock is a Storage capability and must be probed before a current immutability claim.

KB-IDENTITY-WRITER-001
Recipient authority is separate from unattended writer authority.
```

These entries SHOULD be generated/maintained alongside normative specs and validated for drift.

---

## 6. Semantic response contract

Assistant output SHOULD be represented as structured semantic items.

Example:

```json
{
  "items": [
    {
      "type": "Fact",
      "factRef": "health:repo-123:last-proof",
      "text": "The last successful recovery proof was 45 days ago."
    },
    {
      "type": "Recommendation",
      "text": "Consider scheduling recovery drills more frequently."
    },
    {
      "type": "DraftProposal",
      "proposalRef": "draft-task-42",
      "text": "I prepared a weekly recovery policy draft."
    }
  ]
}
```

Supported semantic types SHOULD include:

- `Fact`
- `Finding`
- `Explanation`
- `Recommendation`
- `DraftProposal`
- `Question`
- `Warning`

---

## 7. Grounding requirements

Facts presented by the assistant SHOULD reference deterministic context facts when possible.

The UI MAY expose a "Why?" or "Details" action showing the supporting Fortiq fact/receipt.

Recommendations need not have a single evidence record, but SHOULD state the basis when important.

The assistant MUST NOT fabricate:

- supported backends;
- existing resources;
- credentials;
- successful runs;
- recovery proofs;
- immutable-storage status.

---

## 8. Advice flow

Example:

```text
User:
How well protected am I?

Prepared context:
- 3 active Routes
- all use Home MinIO
- recovery identity exists
- last proof 45 days ago

Assistant:
FACT
All active Routes use the same Storage.

FACT
Recovery proof: 45 days ago.

RECOMMENDATION
Add an independent second Storage.

RECOMMENDATION
Consider a more frequent Recovery Drill.

QUESTION
Would you like me to prepare those changes as Drafts?
```

---

## 9. Entity proposal flow

```text
User intent
    ↓
Prepared Context
    ↓
LLM
    ↓
Typed Proposal
    ↓
Schema Validator
    ↓
Capability Validator
    ↓
Security/Policy Validator
    ↓
Draft Store
    ↓
User Review
```

No free-form assistant sentence is executable configuration.

---

## 10. Cross-platform invariant

Prepared context, semantic response schemas and Draft schemas MUST be identical across supported platforms.

Only:

- local path representation;
- storage discovery;
- hardware capability reporting;
- inference acceleration

may vary by platform.
