# Audit, evidence и compliance mapping

## Назначение

Fortiq создаёт проверяемые технические evidence о backup, restore, ключах, политиках и
административных действиях. Эти evidence помогают организации выполнять собственные
security/compliance процессы, но не являются юридической сертификацией.

Соответствие зависит также от применимого права, организационных процедур, scope,
договоров, обучения, incident response и решений контролёра/процессора данных.

## Свойства audit-контура

- **Completeness within instrumentation:** все определённые security events проходят через
  обязательный audit writer.
- **Tamper evidence:** изменение, удаление или перестановка записанных событий обнаруживается.
- **Truncation evidence:** потеря хвоста обнаруживается после сравнения с внешним anchor.
- **Attribution:** событие связывается с проверенной service/user identity.
- **Data minimisation:** содержимое и имена файлов не логируются по умолчанию.
- **Portability:** evidence можно проверить open-source verifier без control-plane.

Термин `tamper-evident` используется намеренно: локальный журнал не объявляется
tamper-proof.

## Audit event schema

Логическая схема; wire format — deterministic CBOR.

```text
AuditEventV1 {
  schema:              "fortiq.audit-event",
  version:             1,
  ledgerId:            16 bytes,
  sequence:            uint64,
  eventId:             16 bytes,
  previousEventHash:   32 bytes,
  eventType:           closed enum,
  outcome:             success | partial | denied | failed | unknown,
  occurredAtUtc:       integer timestamp,
  monotonicOffset:     uint64,
  bootSessionId:       16 bytes,
  actor:               bounded identity reference,
  resource:            bounded resource reference,
  correlationId:       16 bytes,
  policyVersion:       optional digest,
  payload:             event-specific bounded map
}
```

`occurredAtUtc` полезен для человека, но сам по себе не считается trusted timestamp.
`sequence`, `bootSessionId`, hash chain и external anchors используются для проверки
порядка и обнаружения rollback.

## Категории событий

### Backup и recovery

- job created/started/completed/interrupted;
- VSS requested/created/writer status/cleanup;
- snapshot created/listed/restored;
- integrity check и restore-test;
- Recovery Confidence recalculated;
- recovery kit created/exported/verified;
- immutable RPM created/verified/materialized.

### Keys и identities

- envelope created/activated/retired/revoked;
- unlock success/failure без различения неверного фактора;
- KMS wrap/unwrap reference и внешний audit correlation;
- service/user identity или role changed;
- signing key rotation;
- secret-access policy denied.

### Policy и destructive operations

- policy proposed/approved/activated;
- retention plan created/approved/applied;
- snapshot/object deletion attempt;
- governance bypass attempt;
- legal hold placed/removed;
- maintenance identity issued/expired;
- privileged broker command denied/executed.

### Security и platform health

- IPC authentication/authorization failure;
- audit chain verification failure;
- external anchor success/failure;
- binary/hash/signature mismatch;
- USN discontinuity;
- AI proposal rejected by deterministic validator;
- update installed/rolled back.

## Что запрещено логировать

- plaintext EUS, KEK, DEK, passwords, mnemonic и tokens;
- KMS private credentials;
- `--password-command` output;
- file content;
- полные пользовательские пути и filenames по умолчанию;
- raw prompts/document fragments Phi Silica;
- full access tokens, presigned URLs и connection strings;
- exception dumps без redaction policy.

Repository, device, user и path могут представляться scoped pseudonymous IDs. Они не
являются анонимными данными и всё равно управляются retention/access policy.

## Hash chain

```text
eventHash[0] = SHA-256(domain || ledgerHeader || event[0])
eventHash[n] = SHA-256(domain || eventHash[n-1] || event[n])
```

Domain separator и exact encoding version фиксируются wire specification. Segment
закрывается checkpoint-записью с first/last sequence, count, root/final hash и ссылкой на
предыдущий checkpoint.

Hash chain обнаруживает редактирование уже записанных событий, но не предотвращает:

- omission до записи;
- подделку новых событий после компрометации активного signing key;
- rollback локального ledger вместе с локальным anchor;
- ложное wall-clock time на скомпрометированном host.

## Signed checkpoints и anchors

Checkpoint подписывается `COSE_Sign1`. Конкретный signature algorithm и key provider
фиксируются следующим cryptographic/platform ADR.

Anchor публикуется:

- после критических key/retention/restore событий;
- при закрытии segment;
- периодически по времени и количеству событий;
- перед и после обновления/ротации signing key.

Цели anchor:

- immutable storage в отдельном administrative domain;
- enterprise KMS/audit system;
- customer-owned SIEM/log collector;
- offline exported evidence pack.

Успешная локальная подпись без независимого anchor не получает уровень
`ExternallyAnchored`.

## Audit assurance levels

| Уровень | Свойства |
|---|---|
| `Local` | bounded append log, без cryptographic chain |
| `Chained` | deterministic events и проверенная hash chain |
| `Signed` | chain segments закрыты валидной подписью |
| `ExternallyAnchored` | последний checkpoint подтверждён независимой системой |
| `ImmutableAnchored` | anchor защищён проверенным retention/WORM profile |

UI показывает фактический уровень и возраст последнего anchor.

## Degraded mode

Если chain, signing key или anchor нарушены:

- backup MAY продолжаться, чтобы не ухудшать доступность данных;
- restore в новый пустой target MAY продолжаться с явным warning;
- deletion, prune, key revocation, retention reduction и policy relaxation блокируются;
- создаётся out-of-band alert;
- новый ledger не скрывает повреждение старого: он начинается с incident reference;
- возврат в normal mode требует documented reconciliation/approval.

## Evidence pack

Экспортируемый evidence pack содержит:

- signed manifest и диапазон audit checkpoints;
- версии Fortiq/restic и binary hashes;
- активные policy digests;
- backup receipts;
- VSS consistency/writer summaries;
- integrity checks и restore-test reports;
- immutable RPM/retention evidence;
- key-management events без secret material;
- список известных gaps/degraded intervals;
- public verification keys/certificate chain;
- инструкцию и версию offline verifier.

Evidence pack шифруется для получателя при наличии персональных или инфраструктурных
данных. Экспорт сам создаёт audit event.

## Retention и privacy

- Audit retention задаётся по категориям и legal purpose, отдельно от backup retention.
- Срок не устанавливается бесконечным по умолчанию.
- Доступ предоставляется по need-to-know и журналируется.
- Поиск по человеку возможен только при наличии необходимой mapping information.
- Pseudonymous identifiers и audit IP/identity могут оставаться персональными данными.
- Legal hold должен иметь владельца, основание и процесс снятия.
- Экспорт в SIEM не отменяет обязанности настроить retention/delete там.

Право на удаление и обязательность хранения audit evidence оцениваются организацией по
применимому праву; Fortiq предоставляет механизмы policy, hold и документированного
удаления после истечения срока, но не принимает юридическое решение автоматически.

## Compliance evidence mapping

Таблица показывает техническую поддержку, а не подтверждение соответствия.

| Требование/тема | Evidence Fortiq | Остаточная ответственность клиента |
|---|---|---|
| GDPR Art. 32(1)(a): защита/шифрование | repository encryption, key envelopes, access denials | lawful basis, scope, key custody procedures |
| GDPR Art. 32(1)(b): confidentiality/integrity/availability/resilience | integrity checks, immutable RPM, identity policy | общая архитектура и организационные меры |
| GDPR Art. 32(1)(c): своевременное восстановление | measured restore duration, DR-001 reports | требуемые RTO/RPO и business validation |
| GDPR Art. 32(1)(d): регулярное тестирование | scheduled restore-test, signed reports | оценка достаточности и устранение findings |
| NIS2 Art. 21(2)(b): incident handling | anomaly/audit events, evidence export | CSIRT/process/roles и национальные требования |
| NIS2 Art. 21(2)(c): continuity, backup, DR | immutable backup, recovery kit, drills | BCP/crisis plan и операционные учения |
| NIS2 Art. 21(2)(d): supply-chain security | SBOM/update/binary evidence hooks | vendor risk-management programme |
| NIS2 Art. 21(2)(f): effectiveness assessment | recovery confidence и control tests | независимая оценка и management review |
| NIS2 Art. 21(2)(h): cryptography policies | versioned suites, rotation evidence | утверждение корпоративной crypto policy |
| NIS2 Art. 21(2)(i/j): access/MFA | identity/approval audit hooks | IdP, HR lifecycle и MFA deployment |

NIS2 применяется через национальную имплементацию и зависит от типа организации. Mapping
должен проверяться юридическим/комплаенс-специалистом соответствующей юрисдикции.

## Verification CLI

```text
fortiq-audit verify <ledger-or-evidence-pack>
fortiq-audit checkpoints <ledger>
fortiq-audit explain-gap <ledger> --sequence <n>
fortiq-audit export --from <time> --to <time> --profile auditor
```

Verifier работает offline, не требует лицензии и возвращает machine-readable result.

## Обязательные тесты

- изменение байта события обнаруживается;
- удаление/перестановка/дублирование события обнаруживается;
- truncation относительно внешнего anchor обнаруживается;
- rollback ledger обнаруживается;
- неверный/отозванный signing key корректно классифицируется;
- ротация ключа сохраняет цепочку доверия;
- clock rollback не нарушает sequence ordering;
- crash между event write и checkpoint восстанавливается детерминированно;
- disk full не даёт destructive operation продолжиться без audit;
- секреты отсутствуют в golden logs и evidence pack;
- oversized/malformed CBOR отвергается bounded parser;
- offline verifier воспроизводит результат на чистой машине.

