# The model Fortiq ships

Fortiq's assistant runs a local language model. It is not a service, it is not consulted over the
network, and it is not part of any backup or recovery decision — but it is redistributed with Fortiq,
so what it is and what its terms are belong here rather than in a page somebody has to go and find.

## fortiq-assistant 0.1.0

| | |
|---|---|
| Model | Qwen3.5-2B (instruct) |
| Distribution | `unsloth/Qwen3.5-2B-GGUF`, file `Qwen3.5-2B-Q4_K_M.gguf`, revision `f6d5376be1edb4d416d56da11e5397a961aca8ae` |
| Base model | `Qwen/Qwen3.5-2B` by the Qwen team, Alibaba Cloud |
| Format | GGUF, quantised Q4_K_M |
| Size | 1,280,835,840 bytes |
| SHA-256 | `aaf42c8b7c3cab2bf3d69c355048d4a0ee9973d48f16c731c0520ee914699223` |
| Licence | Apache License 2.0 — the full text is in [LICENSE-Apache-2.0.txt](LICENSE-Apache-2.0.txt) |

Those numbers are the ones in [manifest.json](manifest.json), and they are what the file on a
machine is checked against. The source is pinned to a fixed revision rather than to a branch,
because a branch is whatever it points at on the day somebody installs.

The `.gguf` itself is not committed. It arrives in the installation package, or is fetched during
installation by `scripts/Get-Model.ps1`, which verifies the length and the hash before the file is
put where the application will look for it.

## What this does not cover

`Fortiq.Recover` carries no model and needs none. Restoring data never involves anything here.
