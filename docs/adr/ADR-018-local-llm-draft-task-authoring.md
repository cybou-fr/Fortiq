# ADR-018: Cross-Platform Local LLM as Fortiq Semantic Configuration Layer

- Status: **Proposed**
- Date: **September 7, 2026**
- Scope: Community local assistant, advice and entity authoring

## Context

Fortiq's Resource/Task model can be configured naturally through conversation.

The assistant should be capable of working with the whole logical model, not only Backup Tasks:

- Sources;
- Storages;
- Identities;
- Encryption Profiles;
- Routes;
- Triggers;
- retention/recovery policies;
- Draft Tasks.

It must also explain Fortiq behavior and advise users using deterministic prepared context.

The product must remain cross-platform and must not depend on Windows-only AI runtimes or Copilot+ hardware.

## Decision

Adopt a local replaceable conversational LLM as a **semantic configuration layer**.

Initial preferred model profile:

```text
Qwen3.5-2B-class
GGUF
Q4_K_M-class
~1.0–1.5 GB model package
cross-platform llama.cpp-compatible runtime
```

The exact model is replaceable and is not part of the Fortiq domain contract.

The assistant:

- runs locally;
- is multilingual and conversational;
- receives prepared sanitized Fortiq context;
- explains and advises;
- generates typed Draft/Proposal entities;
- edits existing Drafts;
- never activates Tasks;
- never directly executes privileged/destructive operations;
- never determines recovery truth.

All proposals pass deterministic schema, capability and policy validation.

Only explicit user action transitions a valid Task Draft into `Active`.

## Prepared context

The assistant context is assembled from deterministic sources:

```text
Product Context
Capability Context
User Configuration Context
Operational Context
```

This avoids relying on a large unstructured system prompt or model memory for product truth.

## Consequences

### Positive

- natural-language configuration becomes a first-class UX;
- the assistant can create all major Fortiq logical entities as reviewable proposals;
- advice can be grounded in current configuration and evidence;
- the same behavior works on Windows, Linux and macOS;
- model/runtime can evolve without changing Fortiq schemas;
- small local models become practical because Fortiq supplies compact structured context.

### Constraints

- model output is untrusted input;
- deterministic validation is mandatory;
- secret material never enters model context;
- Task activation is outside inference;
- facts/findings/recommendations must be semantically distinguished;
- model updates follow Fortiq supply-chain verification rules.
