# Документация Fortiq

Статус комплекта: **Draft 0.1**  
Дата: **3 сентября 2026**

## Карта документов

1. [Видение и границы продукта](01-product-vision.md)
2. [Системная архитектура](02-system-architecture.md)
3. [Threat model и границы доверия](03-threat-model.md)
4. [Управление ключами и восстановление доступа](04-key-management.md)
5. [Recovery Assurance](05-recovery-assurance.md)
6. [Fortiq Intelligence и Phi Silica](06-on-device-ai.md)
7. [Roadmap](07-roadmap.md)
8. [Открытые решения](08-open-decisions.md)
9. [Контракт repository engine](09-repository-engine-contract.md)
10. [ADR-001: restic как основной engine V1](adr/ADR-001-primary-repository-engine.md)
11. [Сценарий автономного disaster recovery](10-disaster-recovery-sequence.md)
12. [ADR-002: recovery envelope и key derivation](adr/ADR-002-recovery-envelope.md)
13. [План исполняемого прототипа](11-executable-prototype.md)
14. [ADR-003: границы процессов V1](adr/ADR-003-process-boundaries.md)
15. [IPC protocol и security profile](12-ipc-security-profile.md)
16. [ADR-004: Windows Named Pipes](adr/ADR-004-windows-ipc.md)
17. [Windows capture: VSS и USN Journal](13-windows-capture.md)
18. [ADR-005: VSS — источник консистентности, USN — подсказка](adr/ADR-005-vss-usn.md)
19. [Storage immutability и ransomware resilience](14-storage-immutability.md)
20. [ADR-006: immutable S3 recovery points](adr/ADR-006-immutable-storage.md)
21. [Audit, evidence и compliance mapping](15-audit-compliance.md)
22. [ADR-007: tamper-evident audit ledger](adr/ADR-007-audit-ledger.md)
23. [Supply-chain security и обновления](16-supply-chain-updates.md)
24. [ADR-008: TUF-модель доверия к релизам](adr/ADR-008-update-trust.md)
25. [Product UX и безопасные пользовательские сценарии](17-product-ux.md)
26. [ADR-009: recovery-first UX](adr/ADR-009-recovery-first-ux.md)
27. [Fleet/MSP control plane](18-fleet-control-plane.md)
28. [ADR-010: metadata-only control plane](adr/ADR-010-control-plane.md)
29. [Reliability, observability и SLO](19-reliability-observability.md)
30. [ADR-011: evidence-based health model](adr/ADR-011-reliability-model.md)
31. [Локальный catalog и модель данных](20-local-catalog-data-model.md)
32. [ADR-012: SQLite как восстанавливаемый локальный catalog](adr/ADR-012-local-catalog.md)
33. [ADR-013: Argon2id dependency и криптографическая dependency policy](adr/ADR-013-argon2-dependency-policy.md)

## Нормативные слова

- **MUST / ДОЛЖЕН** — обязательное свойство продукта.
- **SHOULD / СЛЕДУЕТ** — ожидаемое свойство, отклонение требует ADR.
- **MAY / МОЖЕТ** — опциональная возможность.

Утверждения о безопасности должны быть связаны с проверяемым требованием или тестом.
Утверждения о соответствии нормативам формулируются как поддерживаемые технические меры,
а не как автоматическая юридическая гарантия.
