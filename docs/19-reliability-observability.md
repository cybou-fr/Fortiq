# Reliability, observability и SLO

## Цель

Fortiq должен отличать четыре разных результата:

1. service/process доступен;
2. backup job завершился;
3. recovery point сохранился и доступен;
4. восстановление проверено.

Один зелёный `service healthy` не компенсирует провал любого последующего уровня.

## Health hierarchy

```text
Process Health
  └── Capture Health
       └── Backup Freshness
            └── Repository Integrity
                 └── Unlock Availability
                      └── Immutable Recovery Point
                           └── Verified Restore
```

Итоговый status определяется худшим обязательным уровнем customer policy. Среднее
арифметическое не может скрыть отсутствие ключа или проверенного restore.

## Состояния health

- `Healthy`: все обязательные evidence свежие.
- `Degraded`: защита продолжается, но один нефатальный signal нарушен.
- `AtRisk`: recovery objective или независимая защита нарушены.
- `Failed`: текущая операция завершилась доказанным failure.
- `Unknown`: evidence отсутствуют, противоречат друг другу или audit chain нарушена.
- `NotApplicable`: capability не требуется данной policy.

`Unknown` не преобразуется в `Healthy` из-за отсутствия ошибок.

## SLI

### Backup Freshness Compliance

```text
protected backup sets with a successful snapshot newer than configured RPO
──────────────────────────────────────────────────────────────────────────
all enabled backup sets expected to run in the observation window
```

Плановое maintenance/exclusion учитывается только если существовало до окна и имеет
audited approval. Постфактум исключить неуспешный job из знаменателя нельзя.

### Verified Recovery Freshness

Доля repositories, для которых restore-test моложе policy interval и проверил требуемый
scope. Structural `check` не засчитывается как restore-test.

### Restore Success Rate

Успешные user/drill restores делятся на все начатые restores, кроме явно отменённых до
чтения repository. Partial restore считается отдельным outcome, не success.

### Immutable Window Compliance

Доля критичных repositories с последним `Immutable-Verified` RPM, реальный retain-until
которого покрывает требуемое recovery window.

### Key Path Availability

Доля repositories с минимум одним проверенным независимым unlock path. Наличие envelope
без успешного controlled verification недостаточно.

### Evidence Anchor Freshness

Возраст последнего подтверждённого external/immutable audit checkpoint относительно
policy. Попытка upload без acknowledgement не считается anchor.

### Job Start Delay

Распределение задержки между scheduled time и фактическим start. Отдельно учитываются
sleep/offline, scheduler contention и policy block.

## SLO policy

SLO разделяются на:

- customer recovery objectives: RPO, RTO, restore-test interval, immutable window;
- product/service objectives: scheduler, control plane/API, event ingestion;
- provider dependencies: storage/KMS/IdP availability.

Fortiq не подменяет customer RTO произвольным vendor SLO. Начальные численные targets
утверждаются после измерений прототипа. До этого документация использует `TO_BE_BASELINED`,
а UI не показывает ложное обещание.

Пример структуры, не production defaults:

```yaml
objectives:
  backupRpo: customer-defined
  verifiedRestoreMaxAge: customer-defined
  immutableWindow: customer-defined
  schedulerStartP95: TO_BE_BASELINED
  controlPlaneAvailability: TO_BE_BASELINED
  evidenceIngestionDelayP95: TO_BE_BASELINED
```

## Error budget

Error budget применяется к product/service SLO, но не разрешает нарушать security
invariants. Нельзя «потратить budget» на:

- незамеченную потерю данных;
- restore с неверной целостностью;
- обход authorization;
- удаление immutable history;
- утечку ключа;
- ложный статус `Verified`.

Если reliability work исчерпал budget, feature rollout замедляется; backup/recovery
correctness gates не ослабляются.

## Job identity и идемпотентность

Каждый запуск имеет `JobRunId`, logical `ScheduleOccurrenceId` и `AttemptId`.

- Повтор transport request с тем же idempotency key не создаёт второй job.
- Retry одного job создаёт новый AttemptId, сохраняя JobRunId.
- Snapshot ID записывается до публикации success receipt.
- После crash reconciliation проверяет engine/repository, а не полагается на local state.
- Два scheduler instances не выполняют один occurrence параллельно без explicit policy.

## Persistent job state

State transition и outbox event записываются одной локальной transaction, где это
возможно. Публикация control-plane events использует outbox/inbox pattern.

Минимальные поля:

- state/revision;
- scheduled/start/update/end timestamps;
- boot/session ID и monotonic timings;
- attempt/retry budget;
- engine process identity;
- snapshot/repository/source lease references;
- policy/approval digests;
- last durable progress checkpoint;
- cancellation/cleanup status.

Progress events могут теряться без потери correctness; terminal receipt — нет.

## Startup reconciliation

После restart Service:

1. проверяет local state integrity/audit chain;
2. находит jobs в non-terminal state;
3. проверяет живые process/job objects;
4. сверяет VSS lease и orphaned snapshots через broker;
5. проверяет repository на возможный созданный snapshot;
6. классифицирует `Succeeded`, `Interrupted`, `Failed` или `ManualReview`;
7. выполняет bounded cleanup;
8. только затем разрешает конфликтующий следующий job.

Неизвестный результат не маркируется failed/success без reconciliation evidence.

## Retry taxonomy

| Класс | Примеры | Поведение |
|---|---|---|
| Transient | timeout, temporary DNS/storage throttle | exponential backoff + full jitter + budget |
| DependencyUnavailable | KMS/storage outage | circuit breaker, сохранить local protection state |
| Authentication | expired/revoked credential | один refresh path, затем user/admin action |
| PolicyDenied | missing approval, retention prohibition | не retry до state/policy change |
| DataIntegrity | hash/AEAD/check failure | fail closed, alert, не перезаписывать evidence |
| Capacity | disk full/quota | cleanup только разрешённого cache, затем action |
| PermanentInput | invalid path/unsupported FS | не retry без configuration change |
| Unknown | unclassified error | bounded retry максимум один, затем manual evidence |

Retry никогда не повторяет destructive operation без проверки operation digest и текущего
state. Authentication failure не создаёт password/KMS hammering loop.

## Backoff и retry budget

- exponential backoff с full jitter;
- server/provider `Retry-After` учитывается в пределах policy;
- отдельный budget на job и dependency;
- circuit breaker не является terminal success;
- manual `Retry now` не обходит safety/dependency limits;
- deadline наследуется вниз и отменяет новые attempts после expiry;
- retry state сохраняется через reboot, чтобы restart не сбрасывал storm protection.

Конкретные значения калибруются нагрузочными тестами и provider guidance.

## Disk pressure

Fortiq контролирует отдельно:

- system volume free space;
- VSS shadow storage;
- restic cache/temp;
- restore staging;
- local audit/outbox DB;
- operational/immutable repository quota.

Правила:

- Service не заполняет системный диск до нуля;
- cache имеет hard quota и удаляется только по собственной inventory;
- audit/outbox имеют reserved budget и приоритет над verbose diagnostics;
- restore заранее оценивает required space и не пишет in-place по умолчанию;
- VSS snapshot не создаётся, если safety headroom не выполнен;
- cleanup никогда не использует broad glob/непроверенный path;
- quota error не запускает автоматический prune без approved plan;
- удаление cache не объявляется освобождением repository storage.

## Memory/CPU/network pressure

- Argon2 calibration соблюдает memory budget и не параллелится бесконтрольно;
- число одновременных backups/restores ограничивается resource governor;
- foreground recovery может иметь приоритет над maintenance;
- bandwidth limits задаются по site/device policy;
- metered/battery mode имеет явное поведение;
- AI workload preemptible и никогда не вытесняет backup/restore critical work;
- OOM/CPU starvation воспроизводятся fault tests.

## Scheduler semantics

- Schedule хранит timezone и DST policy.
- Каждое occurrence имеет стабильный ID.
- После sleep/offline применяется явная misfire policy: run once, skip или bounded catch-up.
- Несколько пропущенных запусков не создают unbounded storm.
- Время OS может откатиться; duplicate occurrence предотвращается persistent identity.
- Backup sets одного source/volume координируют VSS и resource locks.
- Maintenance не запускается одновременно с конфликтующим backup/restore.

## Observability signals

### Metrics

- job duration/queue/start delay;
- bytes/files scanned/read/uploaded/restored;
- dedup/compression только как engine-reported informational metrics;
- error/retry/cancellation counts по bounded reason codes;
- VSS writer/capture duration и consistency outcome;
- repository check coverage;
- restore-test age/duration;
- immutable window/RPM age;
- audit anchor age;
- queue/cache/disk pressure;
- control-plane evidence lag.

Metrics не содержат filenames, secrets и unbounded user labels. Repository/device IDs
псевдонимизируются, а high-cardinality labels ограничиваются.

### Traces

Correlation проходит через scheduler → VSS broker → restic adapter → storage → receipt.
Trace spans не содержат raw paths, CLI secrets, prompts или signed URLs.

### Logs

Structured diagnostics отделены от tamper-evident audit ledger. Verbose logs имеют
bounded retention/redaction и не используются как единственный source of truth.

## Alert lifecycle

Alert key:

```text
(tenant, device/repository, promise violated, root reason class)
```

- повторения дедуплицируются и увеличивают count/lastSeen;
- recovery/closure основаны на новом evidence, не на таймере;
- flapping имеет hysteresis;
- downstream symptoms группируются под root dependency incident;
- severity отражает влияние на восстановление, а не громкость ошибки;
- каждое уведомление содержит last known safe recovery point и recommended action;
- unchanged non-actionable state не создаёт новые уведомления.

## Severity

- `Info`: нормальное завершение/изменение без действия.
- `Warning`: objective приближается к нарушению.
- `High`: backup/recovery evidence уже нарушены, есть безопасная копия.
- `Critical`: нет доказанного recovery path или integrity/security invariant нарушен.

`Critical` не используется для маркетинга, обновлений функций или обычного offline endpoint.

## Dependency model

Health каждого dependency включает:

- capability/configuration;
- authentication/authorization;
- reachability/latency;
- last successful operation;
- last verified semantic result;
- circuit/retry state;
- data freshness.

TCP/HTTP success не означает KMS unwrap, S3 retention или restore readiness.

## Fault injection и chaos matrix

- kill Service/restic/broker на каждом state transition;
- power-loss simulation до/после durable receipt;
- disk full/read-only filesystem/quota exceeded;
- corrupted local DB/audit tail/cache;
- DNS timeout, packet loss, throttle, partial upload;
- expired/revoked credentials и KMS key disabled;
- VSS writer freeze/timeout/provider failure;
- USN reset/truncation;
- Object Lock delete marker и delayed lifecycle;
- clock rollback/forward и DST transition;
- control-plane outage/event duplication/reordering;
- updater interruption/version mismatch;
- memory pressure и process handle exhaustion;
- multiple concurrent restore/maintenance requests.

Fault tests проверяют не только возврат ошибки, но и инварианты: отсутствие ложного
success, сохранность recovery point, cleanup scope, отсутствие secret leakage и способность
следующего запуска выполнить reconciliation.

## Reliability acceptance gates

- terminal state всегда имеет durable receipt или explicit evidence gap;
- restart в каждой точке job не создаёт ложный success/duplicate destructive action;
- retry storm ограничен через reboot;
- disk pressure не удаляет snapshots или recovery kits;
- unknown dependency state отображается `Unknown`, не `Healthy`;
- alert закрывается только новым подтверждающим evidence;
- AI failure не влияет на backup/restore SLI;
- control-plane outage не останавливает local schedule;
- DR-001 и IM-001 проходят в fault-injection pipeline;
- SLO targets не публикуются до baseline measurement.

