# Roadmap

Roadmap организован вокруг снижения риска, а не количества интеграций.

## Phase 0 — Architecture Proof

- выбрать основной repository engine;
- проверить VSS → backup → restore end-to-end;
- утвердить threat model и key hierarchy;
- создать spike автономного recovery tool;
- доказать immutable retention на одном S3-compatible target.

**Exit criteria:** файл восстанавливается после удаления локальной catalog DB и исходного
устройства с помощью документированного recovery kit.

## V1 — Sovereign Backup

- Avalonia Desktop и Fortiq Service;
- файловый backup Windows, VSS и USN-assisted change discovery;
- local и S3-compatible targets;
- password/TPM unlock и независимый recovery secret;
- retention, Object Lock/WORM и audit log;
- metadata check и sample restore;
- `fortiq-recover`.

## V1.5 — Recovery Assurance

- Recovery Confidence;
- scheduled application checks;
- recovery drill reports;
- ransomware anomaly signals;
- fleet health для небольшой организации.

## V2 — Enterprise Custody

- HashiCorp Vault Transit;
- OIDC/mTLS/AppRole согласно deployment profile;
- multi-envelope key rotation;
- approvals и audit export;
- централизованные sovereignty policies.

## V2.5 — Fortiq Intelligence

- Phi Silica capability detection;
- локальное объяснение событий;
- natural-language recovery proposal;
- metadata-only privacy mode;
- red-team tests prompt injection и unsafe actions.

AI-функции могут экспериментально появиться раньше, но не блокируют V1.

## V3+

- MSP multi-tenancy;
- дополнительные KMS/KMIP providers;
- bare-metal imaging;
- отдельный Fortiq Vault для P2P collaboration.

