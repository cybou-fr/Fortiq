# Открытые архитектурные решения

Каждый принятый пункт оформляется отдельным ADR.

| ID | Решение | Почему блокирует работу |
|---|---|---|
| DEC-001 | Основной engine: **restic для V1** | Принято в ADR-001; пересмотр после V1 |
| DEC-002 | Лицензия Community и граница коммерческих модулей | Влияет на совместимость лицензий и упаковку |
| DEC-003 | Бинарный формат recovery envelope и параметры KDF | Baseline принят в ADR-002; кандидат Argon2 dependency и release gates определены в ADR-013, внешний review остаётся security gate |
| DEC-004 | TPM sealing и service identity model | Определяет unattended backup и recovery |
| DEC-005 | S3 providers и обязательный Object Lock profile | Определяет ransomware guarantees |
| DEC-006 | IPC transport, authentication и authorization | Named Pipes profile принят в ADR-004; SDDL фиксируется при создании service installer |
| DEC-007 | Метод расчёта Recovery Confidence | Метрика должна быть честной и проверяемой |
| DEC-008 | Packaging Windows App SDK для Phi Silica adapter | Зависит от deployment и hardware matrix |
| DEC-009 | Telemetry defaults и data residency | Часть sovereignty promise |
| DEC-010 | Поддерживаемые версии Windows в V1 | VSS, UI и AI имеют разные ограничения |
| DEC-011 | Native VSS interop или поддерживаемый wrapper | Должно быть решено prototype spike и lifecycle tests |
| DEC-012 | Direct locked repository или immutable mirror по provider | Требует restic/Object Lock compatibility test |
| DEC-013 | Audit signing suite и trust anchor | COSE_Sign1 принят; алгоритм выбирается после platform/KMS review |
| DEC-014 | TUF client implementation для .NET | Требует dependency review; собственная криптография запрещена |
| DEC-015 | Windows packaging: MSIX/MSI/другое | Определяет updater и enterprise deployment |
| DEC-016 | Точный Recovery Confidence formula/thresholds | Требует данных прототипа и user research |
| DEC-017 | Терминология и локализация recovery material | Требует usability test без подсказок команды |
| DEC-018 | Control-plane hosting и data residency cells | Требует коммерческого deployment решения |
| DEC-019 | Device certificate issuer/rotation implementation | Требует PKI и managed/offline profile review |
| DEC-020 | Tenant isolation: shared DB/RLS или dedicated cells | Выбирается после threat/performance prototype |
| DEC-021 | Начальные SLO targets по сегментам | Утверждаются после prototype/load/failure data |
| DEC-022 | Persistent job/event store implementation | SQLite baseline принят в ADR-012; crash-consistency suite остаётся release gate |
| DEC-023 | Catalog Metadata Key provider и rotation protocol | Требует Windows platform-key spike и recovery UX test |
| DEC-024 | ORM/query layer для SQLite | Выбирается после проверки migrations, native dependency pinning и ARM64 packaging |

## Ближайшая последовательность

1. Выполнить Argon2 candidate spike и review gates из ADR-013; до этого P0 использует только simulated envelope.
2. Реализовать минимальный `ResticRepositoryEngine` spike.
3. Реализовать `fortiq-recover inspect/unlock/restore` spike.
4. Сделать исполняемый end-to-end prototype по сценарию DR-001.
5. Проверить отдельную endpoint и maintenance identity на реальном S3 target.
6. Только после proof зафиксировать публичные обещания V1.

## Принятые решения

- [ADR-001: restic как основной repository engine V1](adr/ADR-001-primary-repository-engine.md)
- [ADR-002: recovery envelope и key derivation](adr/ADR-002-recovery-envelope.md)
- [ADR-003: границы процессов V1](adr/ADR-003-process-boundaries.md)
- [ADR-004: Windows Named Pipes](adr/ADR-004-windows-ipc.md)
- [ADR-005: VSS — источник консистентности, USN — подсказка](adr/ADR-005-vss-usn.md)
- [ADR-006: immutable S3 recovery points](adr/ADR-006-immutable-storage.md)
- [ADR-007: tamper-evident audit ledger](adr/ADR-007-audit-ledger.md)
- [ADR-008: TUF-модель доверия к релизам](adr/ADR-008-update-trust.md)
- [ADR-009: recovery-first UX](adr/ADR-009-recovery-first-ux.md)
- [ADR-010: metadata-only control plane](adr/ADR-010-control-plane.md)
- [ADR-011: evidence-based health model](adr/ADR-011-reliability-model.md)
- [ADR-012: SQLite как восстанавливаемый локальный catalog](adr/ADR-012-local-catalog.md)
- [ADR-013: Argon2id dependency и криптографическая dependency policy](adr/ADR-013-argon2-dependency-policy.md)
