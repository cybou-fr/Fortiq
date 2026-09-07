# Initial Local Assistant Model Profile

Status: **Recommended implementation starting point, not a permanent domain dependency.**

## Preferred initial profile

```text
Model family/class: Qwen3.5-2B-class instruct model
Format: GGUF
Quantization: Q4_K_M-class
Target model file: ~1.0–1.5 GB
Runtime: llama.cpp-compatible cross-platform implementation
Default context: 8K–16K
Default behavior: non-thinking, concise, structured
```

## Why this profile

Fortiq needs a model that is:

- conversational;
- multilingual;
- good at structured output;
- small enough for local CPU use;
- cross-platform;
- replaceable;
- suitable for Draft entity generation and explanation.

## What is normative vs replaceable

Normative:

- local/cross-platform assistant architecture;
- Prepared Context;
- typed proposals;
- deterministic validation;
- explicit Task activation;
- no secret material in model context;
- no LLM authority over recovery truth.

Replaceable:

- exact model family;
- quantization;
- inference runtime implementation;
- hardware acceleration backend.

Before selecting the release model, Fortiq SHOULD benchmark candidates on a dedicated multilingual Task/Entity authoring evaluation set rather than relying only on general-purpose benchmarks.
