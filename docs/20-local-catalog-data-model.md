# Локальный catalog и модель данных

Статус: **Draft 0.1**  
Связанный ADR: [ADR-012](adr/ADR-012-local-catalog.md)

## 1. Назначение

Локальный catalog Fortiq хранит конфигурацию, состояние оркестрации и производные сведения о recovery points. Он ускоряет работу продукта, но **не является корнем восстановления**.

Полная потеря catalog не должна лишать пользователя возможности расшифровать и восстановить backup. Авторитетными источниками для disaster recovery остаются:

- repository и его собственные snapshot metadata;
- recovery kit и recovery envelope;
- подписанные recovery-point manifests и audit checkpoints;
- параметры доступа к storage, полученные независимо от потерянного устройства.

Catalog ДОЛЖЕН быть восстанавливаемым индексом. Если объект нельзя заново получить из перечисленных источников, он считается конфигурацией или локальным секретом и требует отдельной стратегии экспорта, а не скрытой зависимости от базы.

## 2. Выбор хранилища V1

V1 использует **SQLite** в каталоге данных Windows Service на локальном NTFS-томе.

Профиль подключения:

```text
journal_mode = WAL
synchronous = FULL
foreign_keys = ON
busy_timeout = bounded product value
trusted_schema = OFF, если используемый SQLite и ORM это поддерживают
```

Обязательные ограничения:

1. Файл БД запрещено размещать на SMB, NFS, синхронизируемом cloud drive или removable media. WAL требует, чтобы участники работали на одном хосте, и не поддерживает network filesystem.
2. Только `Fortiq.Service` открывает catalog на запись. Desktop, CLI и AI adapter получают данные через аутентифицированный IPC.
3. Запись сериализуется одной очередью writer operations. Транзакции короткие; сетевые вызовы, запуск restic, VSS и ожидание пользователя внутри транзакции запрещены.
4. Каждое соединение применяет и проверяет runtime pragmas. Нельзя полагаться на значение только при создании файла.
5. `SQLITE_BUSY`, disk-full и I/O failures являются штатными отказами с ограниченным retry и явным terminal result, а не бесконечным ожиданием.
6. Поставляемая SQLite должна включать исправление WAL-reset race: минимум 3.51.3 либо официальную исправленную backport-ветку 3.50.7/3.44.6. Конкретный native binary фиксируется SBOM и проходит dependency gate.

`synchronous=FULL` выбран намеренно: потеря последнего принятого изменения после отключения питания важнее небольшой экономии latency. Профиль можно ослабить только новым ADR и fault-injection evidence.

## 3. Владение и защита файла

Рекомендуемый layout:

```text
%ProgramData%\Fortiq\
  catalog\fortiq.db
  catalog\quarantine\
  catalog-backups\
```

ACL ДОЛЖЕН предоставлять полный доступ только `SYSTEM`, service SID Fortiq и отдельной maintenance identity. Интерактивный пользователь, включая Desktop UI, не получает прямого доступа к БД. Installer создаёт каталог и ACL до первого запуска Service; Service отказывается стартовать в normal mode при более широких effective permissions.

Файлы `fortiq.db`, `fortiq.db-wal` и `fortiq.db-shm` рассматриваются как единый runtime state. Нельзя копировать только основной файл при открытом catalog.

## 4. Что хранить запрещено

Catalog никогда не содержит:

- EUS, KEK, DEK или производный `EnginePasswordV1`;
- mnemonic words, recovery password или распечатанный recovery kit;
- TPM unsealed plaintext;
- KMS client secret, refresh token или долговременный storage credential;
- presigned URL с ещё действующим сроком;
- полный текст пользовательских документов или prompt context для AI;
- секреты repository command line или environment в job logs.

`KeyEnvelopeMetadata` хранит только безопасные descriptors: envelope ID, формат, key slot type, fingerprint публичной части/политики, даты и состояние проверки. Сам envelope хранится в определённом [ADR-002](adr/ADR-002-recovery-envelope.md) формате и переносится как независимый recovery artifact.

Чувствительные метаданные, которые нужны UX — display name, локальный path, account label — шифруются отдельным **Catalog Metadata Key (CMK)**. CMK генерируется случайно, защищается Windows platform key provider и не выводится из EUS. AEAD associated data связывает ciphertext как минимум с `schema_version`, `table`, `row_id`, `column` и `revision`.

Потеря CMK может лишить удобных названий и локальной конфигурации, но не возможности восстановить backup. Это свойство проверяется disaster-recovery тестом.

## 5. Идентификаторы и общие поля

Внешне переносимые сущности используют случайный UUID. Локальные surrogate integer keys допустимы только для join/performance и не экспортируются как identity.

Для изменяемых агрегатов обязательны:

| Поле | Назначение |
|---|---|
| `id` | стабильный UUID |
| `tenant_id` | scope; в standalone V1 — локальный tenant UUID |
| `created_at_utc` | время создания |
| `updated_at_utc` | время последнего изменения |
| `revision` | optimistic concurrency counter |
| `deleted_at_utc` | tombstone, если сущность синхронизируется |

Wall-clock timestamp не доказывает порядок security events. Для job/event ordering дополнительно сохраняются монотонный sequence, process boot ID и audit-ledger position.

## 6. Логическая модель

### 6.1 Конфигурация

| Сущность | Содержимое | Авторитетность |
|---|---|---|
| `Device` | installation/device ID, version, capability flags | локальная конфигурация |
| `Repository` | engine type, stable repo ID, policy refs | repo ID сверяется с engine |
| `RepositoryLocation` | provider, endpoint descriptor, immutable profile | credential не хранится |
| `BackupSet` | имя набора и policy binding | экспортируемая конфигурация |
| `BackupSource` | защищённый path descriptor, VSS mode | локальная конфигурация |
| `Policy` | расписание, retention, verify/restore-test policy | versioned configuration |
| `KeyEnvelopeMetadata` | slot descriptors и validation status | envelope остаётся отдельно |

### 6.2 Исполнение

| Сущность | Содержимое |
|---|---|
| `Job` | желаемая операция и immutable input snapshot |
| `JobAttempt` | lease, state, process result, error taxonomy |
| `CaptureReceipt` | VSS snapshot IDs, writer status, source scope |
| `SnapshotReceipt` | engine snapshot ID, repository ID, timestamps, summary |
| `UsnCheckpoint` | volume identity, journal ID, last observed USN; только hint |
| `OutboxMessage` | committed event для audit/control-plane publication |

`Job` не меняет исходные параметры после запуска. Повтор создаёт новый `JobAttempt`, а изменение scope — новый `Job`.

### 6.3 Assurance

| Сущность | Содержимое |
|---|---|
| `IntegrityCheck` | тип проверки, scope, engine evidence, результат |
| `RestoreTest` | isolated target, selected files, assertions, evidence refs |
| `RecoveryPoint` | нормализованное представление snapshot + assurance state |
| `Approval` | субъект, действие, scope, expiry, evidence ref |
| `AuditCheckpoint` | ledger range, hash, signature/key ID, publication refs |

`RecoveryPoint` — read model. Его status вычисляется из receipts и evidence; UI не может записать `Protected` напрямую.

## 7. Состояния и инварианты

### JobAttempt

```text
Queued -> Leasing -> Running -> Finalizing -> Succeeded
                               \-> Failed
                     \-> CancelRequested -> Cancelled | Failed
Running/Finalizing --crash--> Interrupted -> Reconcile -> Succeeded | Failed | Retryable
```

Инварианты:

- один active lease на job;
- lease имеет owner, boot ID и expiry;
- `Succeeded` требует terminal receipt;
- наличие exit code `0` без подтверждённого snapshot ID недостаточно;
- после crash Service сначала выполняет reconciliation с repository, а не слепо запускает backup повторно;
- cancellation не означает rollback уже созданного snapshot.

### RecoveryPoint

```text
Observed -> Captured -> Stored -> Verified -> RestoreTested
                         \-> Degraded
                         \-> Unavailable
```

Пользовательское состояние `Protected` разрешено только если policy-defined freshness соблюдена и имеется требуемый уровень evidence согласно [health model](19-reliability-observability.md).

## 8. Транзакционные границы

1. Создание `Job`, initial `JobAttempt` и `OutboxMessage(JobQueued)` — одна транзакция.
2. Получение lease — compare-and-swap по `revision`, state и expiry.
3. После внешней операции Service валидирует результат, затем одной транзакцией записывает receipt, terminal state и outbox event.
4. Если snapshot создан, а commit catalog не состоялся, reconciliation обнаруживает snapshot по operation marker/repository metadata и создаёт receipt идемпотентно.
5. Outbox consumer отмечает delivery отдельно. Повторная доставка допустима; consumers дедуплицируют по `event_id`.
6. Audit event не считается опубликованным только потому, что он появился в UI. Связь с signed ledger подтверждается `ledger_position`/checkpoint evidence.

Запрещены distributed transactions между SQLite, repository, storage provider и control plane. Согласованность достигается idempotency keys, receipts, reconciliation и append-only evidence.

## 9. Миграции

Таблица `SchemaMigration` хранит version, checksum, application version, started/completed timestamps и result.

Правила:

- migrations нумеруются, immutable после release и тестируются на копиях всех поддерживаемых схем;
- Service проверяет checksum применённых migrations;
- обычный runtime выполняет только forward migration;
- перед рискованной migration создаётся консистентный catalog backup;
- DDL выполняется транзакционно, где SQLite это гарантирует; внешние преобразования используют resumable phases;
- новая версия Service не запускает jobs до успешного `quick_check` и завершения migration;
- downgrade запрещён, если binary не объявляет совместимость со schema version;
- migration catalog никогда не изменяет repository format или recovery envelope незаметно.

## 10. Проверка, backup и восстановление catalog

### Проверка

- при clean startup: schema validation и `PRAGMA quick_check`;
- после unclean shutdown, I/O error или migration: полный `PRAGMA integrity_check` до возобновления write jobs;
- периодически: quick check, foreign-key check и контролируемый checkpoint;
- размер WAL, checkpoint latency, busy rate и disk free space экспортируются как локальные operational metrics без пользовательского содержимого.

Проверка целостности БД не заменяет проверку repository.

### Консистентная копия

Catalog backup создаётся SQLite Online Backup API либо эквивалентной согласованной snapshot operation. Копирование открытого `fortiq.db` без связанного WAL запрещено. Backup catalog — удобство восстановления настроек, а не обязательный recovery artifact.

### Corruption handling

При обнаружении corruption:

1. остановить новые write jobs;
2. закрыть соединения;
3. переместить точный набор DB/WAL/SHM в timestamped quarantine без изменения оригинальных bytes;
4. записать Windows Event Log событие без секретов;
5. предложить restore последнего проверенного catalog backup или rebuild;
6. не выполнять автоматический repair поверх единственной копии.

### Rebuild после полной потери

Rebuild создаёт новый catalog и импортирует:

1. локальную экспортированную конфигурацию, если она доступна;
2. repository identity и snapshot list через engine adapter;
3. подписанные recovery-point manifests и immutable mirror receipts;
4. audit checkpoints/evidence;
5. recovery envelope descriptors без импорта plaintext keys.

Каждый импортированный факт получает provenance (`source_type`, `source_id`, `observed_at`, verification result). Сведения, которые нельзя доказать, маркируются `Unknown`, а не синтезируются.

## 11. Версионирование SQLite dependency

SQLite является security/reliability dependency:

- native library pin входит в lockfile и SBOM;
- startup логирует runtime `sqlite_version()`;
- CI запрещает версии вне approved range;
- security feed отслеживается отдельно от .NET wrapper package;
- supported version matrix проверяет реальные native binaries для x64 и ARM64;
- WAL concurrency tests включают одновременный commit/checkpoint workload.

Из-за опубликованного в 2026 году WAL-reset bug Fortiq MUST использовать исправленную версию до включения WAL в production. Один writer queue снижает exposure, но не заменяет исправление dependency.

## 12. Обязательные тесты

| ID | Сценарий | Критерий |
|---|---|---|
| CAT-001 | kill процесса в каждой смене JobAttempt | после restart состояние reconciliation-safe |
| CAT-002 | power-loss simulation после commit | принятый terminal receipt не исчезает |
| CAT-003 | disk full во время receipt commit | нет ложного `Succeeded` |
| CAT-004 | snapshot создан, DB commit не выполнен | reconciliation создаёт ровно один receipt |
| CAT-005 | удалить весь catalog и CMK | restore через recovery kit и repository возможен |
| CAT-006 | повредить страницу DB/WAL | normal writes блокируются, файлы quarantined |
| CAT-007 | оборвать каждую migration phase | повтор безопасен или требуется явный rollback artifact |
| CAT-008 | долгий reader + writer/checkpoint load | bounded busy handling, контролируемый WAL growth |
| CAT-009 | подменить encrypted metadata/AAD | AEAD validation fails closed |
| CAT-010 | попытка открыть catalog через UI identity | ACL и process boundary запрещают доступ |
| CAT-011 | запустить с уязвимой SQLite version | dependency/startup gate блокирует production mode |
| CAT-012 | rebuild из неполных evidence sources | неизвестные поля остаются `Unknown` |

## 13. Критерий готовности

Local catalog готов к V1, когда одновременно доказано:

- crash-consistent прохождение CAT-001–CAT-012;
- восстановление данных без исходного catalog;
- отсутствие recovery secrets в DB, WAL, diagnostics и backups;
- schema migration проходит на всех поддерживаемых версиях;
- patched SQLite version закреплена и воспроизводимо поставляется;
- security review подтверждает ACL, CMK protection и IPC-only access.

