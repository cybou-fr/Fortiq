# План исполняемого прототипа

## Цель

Прототип должен доказать один полный сценарий, а не продемонстрировать отдельные API:

```text
source files
  → consistent source view
  → encrypted restic repository
  → удаление локального состояния Fortiq
  → unlock через recovery envelope
  → restore на чистой машине
  → проверка содержимого и metadata
```

Prototype считается успешным только при прохождении DR-001. UI, fleet management,
KMS, P2P и AI не входят в critical path прототипа.

## Scope P0

### Включено

- Windows x64 как первая платформа разработки;
- локальный каталог как repository backend;
- restic с pinned version и SHA-256 manifest;
- обычный source directory;
- password envelope и simulated recovery envelope;
- `backup`, `snapshots`, `check`, `restore`;
- удаление временного Fortiq state между backup и restore;
- machine-readable operation report;
- автоматический end-to-end test.

### Не включено

- VSS, TPM, KMS и S3 в P0;
- production mnemonic UI;
- Avalonia Desktop;
- Windows Service installation;
- Object Lock;
- scheduling;
- Phi Silica;
- собственная криптографическая реализация без выбранной библиотеки.

P0 специально использует simulated envelope provider: он проверяет архитектуру и lifetime
секретов, но не считается security proof.

Выбор production Argon2 dependency и обязательные review gates определены в
[ADR-013](adr/ADR-013-argon2-dependency-policy.md). Production password envelope не должен
появляться в P0 под видом временной реализации.

## Scope P1

После прохождения P0 последовательно добавляются:

1. VSS source provider;
2. реальный PasswordEnvelopeV1;
3. Bip39RecoveryEnvelopeV1;
4. WindowsTpmEnvelopeV1;
5. S3 backend с разделёнными identities;
6. append-only/Object Lock сценарий;
7. запуск orchestration как Windows Service.

Каждый пункт добавляется в тот же end-to-end test, а не проверяется отдельным demo.

## Предлагаемая структура solution

```text
Fortiq.sln
src/
  Fortiq.Domain/                 # value objects, policies, state machines
  Fortiq.Application/            # use cases и ports
  Fortiq.Infrastructure.Restic/  # process adapter и parsers
  Fortiq.Infrastructure.Keys/    # envelopes и key leases
  Fortiq.Platform.Windows/       # VSS/USN, позднее privileged IPC client
  Fortiq.Service/                # orchestration host
  Fortiq.Recover/                # автономная CLI
tests/
  Fortiq.Domain.Tests/
  Fortiq.Restic.ContractTests/
  Fortiq.Recovery.IntegrationTests/
  Fortiq.Security.Tests/
test-assets/
  restic-output/<version>/        # sanitized golden JSON/text fixtures
  recovery-vectors/v1/            # без production secrets
engines/
  manifest.json                   # разрешённые binary versions и hashes
```

`Fortiq.Recover` зависит только от Domain, минимального key-envelope runtime и restic
adapter. Он не зависит от Service, Desktop, control-plane SDK или лицензирования.

## Use cases P0

```csharp
public interface IBackupRepository
{
    Task<RepositoryDescriptor> InitializeAsync(
        InitializeRepository command,
        CancellationToken cancellationToken);

    Task<BackupReceipt> CreateSnapshotAsync(
        CreateSnapshot command,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SnapshotDescriptor>> ListSnapshotsAsync(
        ListSnapshots query,
        CancellationToken cancellationToken);

    Task<CheckReceipt> CheckAsync(
        CheckRepository command,
        CancellationToken cancellationToken);

    Task<RestoreReceipt> RestoreAsync(
        RestoreSnapshot command,
        CancellationToken cancellationToken);
}
```

Domain contracts не содержат restic flags или raw JSON. Infrastructure adapter переводит
их в version-pinned invocation и нормализованный результат.

## Process invocation P0

Разрешённые операции задаются enum, а не произвольной строкой. `ProcessStartInfo`:

- `UseShellExecute = false`;
- executable path берётся только из проверенного engine manifest;
- аргументы добавляются через `ArgumentList`;
- stdout и stderr перенаправлены и читаются параллельно;
- рабочий каталог создаётся Fortiq с ограниченными ACL;
- environment формируется allowlist-подходом;
- process tree ограничивается Windows Job Object в P1.

Минимальные команды adapter:

```text
restic init --json
restic backup <source> --json
restic snapshots --json
restic check --json
restic restore <snapshot-id> --target <staging> --json
```

Фактическая поддержка `--json` проверяется для каждой pinned версии. Команда или флаг не
считаются доступными только потому, что присутствуют в документации другой версии.

## Password helper protocol

P0 не передаёт EUS в аргументах или environment. Restic получает `--password-command`,
который запускает минимальный helper.

Предварительный протокол:

1. Parent создаёт однократный Named Pipe с ACL для ожидаемой service identity.
2. Helper получает в command только несекретный operation ID; pipe name выводится из
   него по фиксированной схеме и сам по себе не даёт права подключения.
3. Parent проверяет identity и PID клиента, где это поддерживается, а затем выполняет
   одноразовый challenge-response внутри защищённого pipe.
4. Helper получает EUS один раз, кодирует `EnginePasswordV1`, пишет только ASCII password
   и один завершающий newline в stdout, затем завершается.
5. Endpoint закрывается; повторное чтение невозможно.
6. Lease очищается после завершения restic operation.

Строка `--password-command` строится только из pinned абсолютного пути helper и
валидированного operation ID. Произвольный shell input в неё не подставляется.

Security свойства и ограничения helper фиксируются отдельным IPC ADR перед P1. На P0
допустима test-only реализация, явно запрещённая для release build.

## State machine backup job

```text
Created
  → PreparingSource
  → AcquiringKey
  → RunningEngine
  → VerifyingReceipt
  → Succeeded

Любое активное состояние → Cancelling → Cancelled
Любое активное состояние → Failed
RunningEngine после crash → Interrupted → ReconciliationRequired
```

`Succeeded` устанавливается только если restic exit code и обязательные итоговые события
согласованы. Наличие progress event не является доказательством успешного snapshot.

## Operation receipt

Каждая операция сохраняет JSON receipt без секретов:

```json
{
  "schema": "fortiq.operation-receipt",
  "version": 1,
  "operationId": "UUID",
  "operation": "backup",
  "repositoryId": "...",
  "engine": {
    "name": "restic",
    "version": "PINNED",
    "sha256": "PINNED"
  },
  "startedAt": "RFC3339",
  "completedAt": "RFC3339",
  "result": "succeeded",
  "snapshotId": "engine snapshot ID",
  "source": {
    "kind": "directory",
    "stableId": "test-source"
  },
  "metrics": {},
  "warnings": []
}
```

Receipt не подменяет данные самого репозитория и не нужен для автономного restore.

## Test dataset

Тестовый source должен включать:

- пустой и небольшой текстовый файл;
- бинарный файл с детерминированным хешем;
- Unicode-имена из нескольких письменностей;
- длинный допустимый Windows path;
- read-only file;
- sparse file, если filesystem поддерживает;
- файл, меняющийся во время обычного чтения;
- symlink/reparse point согласно выбранной policy;
- намеренно недоступный файл для partial-failure проверки.

Ожидаемая metadata описывается signed/checked test manifest. Не все metadata обязаны
восстанавливаться одинаково на разных OS; поддерживаемый профиль фиксируется явно.

## Acceptance tests

### E2E-001: автономное восстановление

1. Создать test dataset.
2. Инициализировать repository и выполнить backup.
3. Зафиксировать snapshot receipt.
4. Удалить только временную test-state директорию Fortiq.
5. Запустить `Fortiq.Recover` в новом процессе с recovery kit.
6. Восстановить snapshot в пустой target.
7. Сверить содержимое и обязательную metadata.
8. Проверить, что сетевых обращений к Fortiq нет.

### E2E-002: неправильный secret

Unlock завершается единым `UnlockFailed`; snapshot metadata не раскрывается.

### E2E-003: повреждённый repository object

`check` и restore обнаруживают нарушение; receipt не получает статус success.

### E2E-004: отмена backup

После cancellation новый процесс способен открыть repository, выполнить reconciliation и
создать следующий корректный snapshot.

### E2E-005: path traversal

Вредоносное имя/reparse point не позволяет записи за пределы restore staging directory.

## Definition of Done P0

- `dotnet test` поднимает изолированный временный repository и проходит E2E-001..005;
- тесты не требуют установленного глобально restic;
- binary hash проверяется до каждого запуска;
- secrets отсутствуют в process arguments, environment, receipts и captured logs;
- recovery выполняется после удаления test-state Fortiq;
- README содержит одну воспроизводимую команду запуска;
- CI сохраняет receipts и sanitized diagnostics при ошибке;
- известные ограничения перечислены и не маскируются статусом success.

## Порядок реализации

1. Создать solution и Domain value objects. **Выполнено для начального P0-каркаса.**
2. Добавить engine manifest и binary verification.
3. Реализовать process runner и golden-output parsers.
4. Реализовать local repository adapter.
5. Добавить test-only key lease и password helper.
6. Реализовать Recover CLI: inspect, snapshots, check, restore.
7. Добавить dataset builder и E2E-001.
8. Добавить negative/cancellation/path tests.
9. Зафиксировать evidence и перейти к P1/VSS.
