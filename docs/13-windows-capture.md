# Windows capture: VSS и USN Journal

## Цель

Fortiq должна читать набор файлов из стабильного представления тома и честно сообщать,
какой уровень консистентности фактически достигнут. VSS создаёт source view; USN Journal
помогает обнаруживать изменения, но не является доказательством полноты snapshot.

## Термины

- **Requester:** Fortiq Windows Broker, управляющий VSS backup lifecycle.
- **Writer:** Windows/application component, подготавливающий свои данные.
- **Provider:** VSS provider, создающий shadow copy.
- **Shadow set:** согласованный набор shadow copies для одного или нескольких томов.
- **Backup Components Document:** выбор requester и результаты VSS operation.
- **Writer Metadata Document:** описание компонентов и restore semantics writer.

## Уровни консистентности Fortiq

| Уровень | Условия | Что можно обещать |
|---|---|---|
| `ApplicationConsistent` | Все required writers/component selections успешны до и после snapshot; metadata сохранена | Выбранные writer-aware workloads подготовлены к backup |
| `CrashConsistent` | Shadow copy создана, но required writer guarantee отсутствует | Состояние тома соответствует внезапной остановке питания |
| `LiveRead` | Чтение live filesystem без VSS | Только best-effort; отключено по умолчанию для scheduled backup |
| `Failed` | Policy требует более высокий уровень либо snapshot непригоден | Snapshot не получает успешный receipt |

Сам факт создания VSS snapshot не означает `ApplicationConsistent`. Если writers не
участвуют, Microsoft описывает данные shadow copy как crash-consistent.

## Consistency policy

```yaml
windowsCapture:
  requiredLevel: application-consistent
  allowCrashConsistentFallback: false
  requiredWriters: []
  excludedWriters: []
  writerTimeout: system-default-bounded
  snapshotLeaseTimeout: 2h
```

Policy может различаться по backup set. Для обычных пользовательских документов допустим
`CrashConsistent`; для SQL/Exchange/специализированных workloads администратор указывает
required writers, и их failure делает job неуспешным.

## VSS state machine

```text
Created
  → Initialized
  → WriterMetadataGathered
  → ComponentsSelected
  → SnapshotSetStarted
  → PreparedForBackup
  → SnapshotCreatedAndThawed
  → WriterStatusValidated
  → SourceLeaseIssued
  → EngineCopyRunning
  → BackupResultRecorded
  → BackupCompleteSignalled
  → SnapshotReleased
  → Completed

Prepare/DoSnapshot failure → Aborting → Released → Failed
Process crash             → Reconciliation → Released/Expired
```

Один VSS backup-components instance не переиспользуется для следующего job.

## Последовательность capture

1. Service передаёт broker-у зарегистрированные stable volume IDs и policy reference.
2. Broker создаёт/инициализирует VSS backup components и задаёт backup context/state.
3. Broker собирает Writer Metadata Documents.
4. Policy engine выбирает components и required writers.
5. Broker создаёт snapshot set и добавляет все необходимые volumes.
6. Выполняется preparation и создание shadow set; VSS координирует Freeze/Thaw.
7. Broker собирает writer status и классифицирует consistency.
8. Если policy выполнена, broker возвращает `SourceSnapshotLease`.
9. Service запускает restic только против shadow device paths.
10. Результат component backup записывается в VSS metadata.
11. Broker сигнализирует завершение backup согласно результату.
12. Сохраняются Backup Components Document, Writer Metadata Documents и writer statuses.
13. Shadow set удаляется после завершения lease/timeout.

Приложения заморожены только во время создания shadow copy, не на весь период загрузки
backup в repository.

## Контракт broker

Broker не принимает произвольные COM calls или raw VSS XML от Desktop.

```csharp
public interface IWindowsCaptureBroker
{
    Task<CapturePlan> PrepareAsync(
        RegisteredBackupSet backupSet,
        ConsistencyPolicy policy,
        CancellationToken cancellationToken);

    Task<SourceSnapshotLease> CreateAsync(
        ApprovedCapturePlan plan,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        SnapshotLeaseId leaseId,
        EngineCopyOutcome outcome,
        CancellationToken cancellationToken);

    Task AbortAsync(
        SnapshotLeaseId leaseId,
        CancellationToken cancellationToken);
}
```

`ApprovedCapturePlan` связан с digest исходного plan, expiry и state version. Volume set
нельзя изменить после approval.

## SourceSnapshotLease

Lease содержит только:

- opaque lease ID;
- snapshot set ID;
- mapping `stableVolumeId → shadowDevicePath`;
- фактический consistency level;
- sanitized writer status summary;
- creation/expiry timestamps;
- metadata receipt reference.

Lease не содержит key material или storage credentials. Shadow paths принимаются только
от broker и повторно проверяются как ожидаемый VSS device namespace.

## Writer policy

- Перед backup сохраняется полный список discovered/selected writers.
- `required writer` должен присутствовать, успешно подготовиться и иметь acceptable
  финальный status.
- Неизвестный writer failure не игнорируется: policy явно определяет fail или downgrade.
- Exclusion документируется в receipt с reason code.
- Writer metadata и Backup Components Document хранятся вместе с recovery evidence.
- Restore application workload требует отдельного writer-aware restore workflow; простое
  копирование файлов не объявляется application restore.

Outlook PST, SQLite и другие открытые файлы не получают специальной гарантии только из-за
VSS. Их уровень определяется участием соответствующего writer или документированным
application-specific validator.

## Abort и cleanup

- После подготовки writers ошибка snapshot creation приводит к VSS abort notification.
- Все async VSS operations имеют bounded wait и cancellation strategy.
- Cleanup идемпотентен и безопасен после partial initialization.
- Broker хранит минимальный crash-recovery journal активных lease без секретов.
- При старте broker перечисляет только собственные orphaned snapshots и удаляет их после
  policy-defined grace period.
- Snapshot другого requester никогда не удаляется по эвристике возраста.
- Failure cleanup отражается отдельным health alert и не маскирует исходную ошибку.

## USN Journal role

USN используется для:

- оценки объёма изменений перед job;
- ускорения inventory/reconciliation там, где это доказано integration test;
- ransomware/anomaly signals;
- выбора приоритетной выборки для restore-test;
- обнаружения rename/delete/change patterns.

USN не используется как единственный список файлов для restic V1. Engine выполняет свой
корректный scan shadow source.

## USN checkpoint

```text
UsnCheckpoint {
  stableVolumeId,
  volumeSerial,
  journalId,
  nextUsn,
  capturedAt,
  recordVersionRange,
  scanGeneration
}
```

Перед чтением Fortiq вызывает `FSCTL_QUERY_USN_JOURNAL` и проверяет:

- volume identity совпадает;
- `UsnJournalID` совпадает с checkpoint;
- сохранённый `nextUsn` не меньше доступной нижней границы;
- нет journal discontinuity;
- parser поддерживает возвращаемую версию USN records.

При несовпадении Journal ID, `ERROR_JOURNAL_ENTRY_DELETED`, усечении журнала,
неподдерживаемой record version или невозможности доказать непрерывность результат
становится `FullScanRequired`. Новый checkpoint сохраняется только после успешного scan.

## Граница VSS и USN

USN checkpoint должен быть связан с конкретной временной границей capture. Изменения,
произошедшие во время создания snapshot, нельзя произвольно отнести к старому или новому
snapshot. Реализация сохраняет pre/post `NextUsn` и документирует правило диапазона.

До доказательства корректности этого сопоставления USN влияет только на оптимизацию и
аналитику, но не на содержимое backup.

## Security constraints

- Только broker открывает raw volume handles и вызывает VSS/USN control codes.
- Service/AI/Desktop не получают raw handle.
- Broker разрешает только заранее зарегистрированные local fixed volumes.
- UNC, removable, CSV/SAN и ReFS требуют отдельных capability profiles.
- Path translation сохраняет относительный путь внутри source root и не следует reparse
  points вне policy boundary.
- VSS XML рассматривается как недоверенный bounded input при сохранении/чтении.
- Writer names и metadata не становятся командами или AI instructions.

## Receipts

Capture receipt содержит:

- requested и actual consistency level;
- snapshot set/volume IDs без чувствительных путей пользователя;
- выбранные writers/components и status codes;
- VSS context/provider identity;
- pre/post timestamps и duration;
- cleanup outcome;
- ссылки/хеши сохранённых VSS metadata documents;
- USN checkpoint status: continuous, reset, truncated, unsupported или unavailable.

## Test matrix

- обычный NTFS volume без application writers;
- required writer success/failure/missing;
- multi-volume snapshot set;
- cancellation до и после PrepareForBackup;
- provider failure и writer timeout;
- broker crash с orphaned lease;
- parallel request collision;
- insufficient shadow storage;
- source rename/reparse point race;
- USN journal reset, truncation и ID change;
- неизвестная/повреждённая USN record;
- system reboot между checkpoint и следующим job;
- restic failure после успешного snapshot;
- проверка отсутствия чужих snapshot deletions.

## Exit criteria P1

- VSS snapshot используется как фактический restic source;
- receipt корректно различает ApplicationConsistent и CrashConsistent;
- required writer failure не может дать success;
- abort/cleanup проходят fault-injection tests;
- потеря USN continuity вызывает full scan, а не пропуск файлов;
- восстановление не требует существования исходного shadow copy;
- DR-001 продолжает проходить после включения VSS.

