# Контракт repository engine

## Цель

Fortiq использует внешний backup engine как заменяемый исполнитель, но не пытается
скрыть различия между форматами за ложной универсальной моделью. Контракт описывает
минимальные возможности, необходимые для гарантий V1.

## Capability model

```csharp
[Flags]
public enum RepositoryCapabilities
{
    None = 0,
    Snapshots = 1 << 0,
    SelectiveRestore = 1 << 1,
    StructuralCheck = 1 << 2,
    FullDataCheck = 1 << 3,
    RetentionPlanning = 1 << 4,
    Pruning = 1 << 5,
    MultipleUnlockCredentials = 1 << 6,
    ReadOnlyAccess = 1 << 7
}
```

Object Lock и append-only являются свойствами всей цепочки storage, identity и
maintenance, а не только engine. Они проверяются отдельным storage capability probe.

## Интерфейс V1

```csharp
public interface IRepositoryEngine
{
    EngineIdentity Identity { get; }
    RepositoryCapabilities Capabilities { get; }

    Task<RepositoryInfo> InspectAsync(
        RepositoryAccess access,
        CancellationToken cancellationToken);

    Task<BackupResult> BackupAsync(
        BackupRequest request,
        IProgress<BackupProgress> progress,
        CancellationToken cancellationToken);

    IAsyncEnumerable<SnapshotDescriptor> ListSnapshotsAsync(
        SnapshotQuery query,
        CancellationToken cancellationToken);

    Task<RestoreResult> RestoreAsync(
        RestoreRequest request,
        IProgress<RestoreProgress> progress,
        CancellationToken cancellationToken);

    Task<IntegrityCheckResult> CheckAsync(
        IntegrityCheckRequest request,
        IProgress<CheckProgress> progress,
        CancellationToken cancellationToken);

    Task<RetentionPlan> PlanRetentionAsync(
        RetentionPolicy policy,
        CancellationToken cancellationToken);

    Task<RetentionResult> ApplyRetentionAsync(
        ApprovedRetentionPlan plan,
        CancellationToken cancellationToken);
}
```

## Разделение обязанностей

### Fortiq отвечает за

- расписание, VSS lifecycle и выбор согласованного source path;
- выдачу краткоживущего repository credential;
- policy approval и запрет опасных операций;
- нормализацию событий и audit trail;
- sandbox для restore-test;
- pinning версии engine и проверку подписи/хеша бинарника.

### Engine отвечает за

- chunking, deduplication, compression и repository encryption;
- формат snapshot и content addressing;
- запись, чтение и cryptographic integrity check;
- восстановление файлов и metadata в пределах возможностей платформы.

### Storage layer отвечает за

- durability и availability объектов;
- transport authentication;
- versioning/Object Lock/WORM;
- разделение write-only endpoint identity и privileged maintenance identity.

## Запуск внешнего процесса

- Аргументы формируются типизированным builder без shell interpolation.
- Секрет не передаётся через command line или environment variable.
- Для restic используется контролируемый `--password-command` helper/IPC protocol.
- `stdout` и `stderr` читаются независимо во избежание deadlock.
- JSON проверяется по поддерживаемой Fortiq schema; неизвестные события сохраняются
  безопасно, но не превращаются в успешный результат.
- Exit code интерпретируется совместно со структурированными событиями.
- Cancellation сначала запрашивает graceful stop, затем ограниченно завершает process tree.
- Логи проходят secret redaction и имеют ограничение размера.

## Retention safety

`PlanRetentionAsync` не удаляет данные. Он возвращает снимки, причины выбора, ожидаемый
объём освобождения и необходимые полномочия. `ApplyRetentionAsync` принимает только
подписанный/утверждённый план с TTL и повторно проверяет repository state.

Endpoint backup identity не получает право prune. Maintenance выполняется отдельной
identity в выделенном окне после проверки независимой immutable-копии.

## Версионирование

Каждый job фиксирует:

- engine name, binary version и SHA-256;
- repository format version;
- adapter protocol/schema version;
- source snapshot identity (например, VSS snapshot ID);
- набор фактически обнаруженных capabilities.

Автоматическая миграция repository format запрещена. Она выполняется отдельной операцией
после integrity check, recovery drill и создания rollback/independent copy.

