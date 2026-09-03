# Fleet/MSP control plane

## Цель

Control plane позволяет управлять парком устройств, распространять политики и собирать
health/evidence без доступа к backup contents, EUS, recovery words и customer KMS keys.

Endpoint остаётся системой исполнения и продолжает scheduled backup при временной потере
связи. Restore не зависит от доступности control plane или состояния лицензии.

## Trust statement

Control plane может:

- знать, что repository/device/job существует;
- получать минимизированные статусы и evidence;
- подписывать/распространять policy и typed commands;
- управлять enrollment и device certificate lifecycle;
- уведомлять о нарушениях RPO/restore-test/immutability.

Control plane не может:

- получить EUS/KEK/DEK, mnemonic или KMS plaintext;
- прочитать filenames/content из repository;
- самостоятельно выполнить restore содержимого;
- передать endpoint произвольную shell-команду;
- сократить immutable retention без отдельной customer-authorised policy;
- заблокировать автономный recovery из-за отсутствия подписки.

Термин `metadata-only` не означает отсутствие чувствительных данных: device names,
identities, IP, repository health и incident status защищаются как confidential metadata.

## Topology

```text
                     ┌─────────────────────────────┐
                     │ Customer / MSP Administrators│
                     └──────────────┬──────────────┘
                                    │ OIDC + MFA
                         ┌──────────▼──────────┐
                         │ Fortiq Control Plane│
                         │ policy / fleet / audit│
                         └───────┬───────┬─────┘
                                 │       │
                  outbound mTLS  │       │ tenant-scoped evidence export
                                 │       ▼
                      ┌──────────▼───┐  Customer SIEM/KMS
                      │ Endpoint Agent│
                      │ local policy  │
                      └────┬──────┬───┘
                           │      │
                         VSS    Customer storage

Backup content path никогда не проходит через control plane.
```

## Tenancy model

```text
MSP Organization
├── Customer Tenant A
│   ├── Sites
│   ├── Devices
│   ├── Repositories
│   ├── Policies
│   └── Evidence
└── Customer Tenant B
    └── полностью отдельный authorization scope
```

- Customer Tenant является основной security boundary.
- MSP role получает только явно делегированные capabilities каждого tenant.
- Tenant A identifier нельзя использовать для чтения Tenant B через object reference.
- Все domain queries включают tenant context из verified identity, а не из body request.
- Cross-tenant aggregate не содержит drill-down без отдельного разрешения.
- Global support access отсутствует по умолчанию и требует customer-approved time-bound
  support session с audit.

## Roles

| Role | Scope | Основные возможности |
|---|---|---|
| Tenant Owner | customer | delegation, sovereignty policy, break-glass governance |
| Security Admin | customer | identities, KMS references, holds, evidence |
| Backup Admin | customer/site | backup sets, schedules, storage profiles |
| Recovery Officer | customer | recovery workflows без автоматического key access |
| Auditor | customer/read-only | evidence packs и policy history |
| MSP Operator | delegated tenants | health/remediation в пределах delegation |
| Fortiq Support | none by default | только approved diagnostic session |

`Tenant Owner` не получает автоматически plaintext recovery material. Role и key custody
являются разными плоскостями.

## Device identity

Каждое устройство имеет отдельную cryptographic identity:

- private key генерируется локально и предпочтительно TPM-backed;
- control plane хранит public key/certificate и status;
- сертификат короткоживущий либо регулярно ротируется;
- device ID не является секретом;
- clone detection учитывает одновременное использование одной identity;
- revocation блокирует control-plane access, но не удаляет локальные backup/recovery data.

Attestation может повышать assurance, но не является единственным recovery/enrollment path
и не должна ломать endpoint после обычного firmware update без понятной remediation.

## Enrollment

```text
Admin creates one-time enrollment intent
  → short-lived token/QR with tenant + profile binding
  → endpoint generates device key locally
  → authenticated enrollment exchange
  → control plane issues device credential
  → endpoint verifies tenant/control-plane identity
  → signed baseline policy is installed
  → token is consumed and cannot be replayed
```

Enrollment token:

- одноразовый и короткоживущий;
- привязан к tenant/site/profile;
- не содержит customer storage/KMS credentials;
- хранится только в redacted audit form;
- не позволяет перевести уже enrolled device в другой tenant.

Перенос устройства между tenants — отдельный offboarding + enrollment с очисткой старого
control-plane state; локальные repositories не перепривязываются автоматически.

## Signed policy distribution

```text
PolicyEnvelope {
  schemaVersion,
  tenantId,
  policyId,
  revision,
  issuedAt,
  validFrom,
  expiresAt,
  minimumAgentVersion,
  payloadDigest,
  signer/keyId,
  signature
}
```

- Endpoint проверяет signature, tenant/device scope, revision, expiry и agent compatibility.
- Revision не уменьшается; controlled rollback публикуется как новая revision.
- Policy применяется транзакционно после local validation/dry run.
- Последняя принятая policy сохраняется локально и работает offline.
- Неизвестное critical field приводит к reject, а не игнорированию.
- Policy не содержит EUS, raw cloud secret или arbitrary executable/script.

## Policy classes и offline behavior

| Класс | При потере control plane |
|---|---|
| Backup schedule | продолжать по последней valid policy |
| Integrity/restore-test | продолжать локально |
| Immutable replication | продолжать, если credentials доступны |
| Status/evidence upload | ставить в bounded persistent queue |
| New destructive action | не начинать без valid approval |
| Уже approved maintenance | только если approval не истёк и state digest совпадает |
| Key revocation/retention reduction | fail closed |
| Safe restore в новый target | разрешить локальному authorised operator |
| Disaster recovery | разрешить независимо от control plane/license |

Истечение обычной policy при длительном offline не останавливает backup автоматически:
endpoint переходит в `PolicyStale`, продолжает защитные операции и блокирует ослабляющие.

## Command model

Control plane использует закрытый enum typed commands:

- `RunBackupNow`;
- `RunIntegrityCheck`;
- `RunRestoreTest`;
- `CollectSanitizedDiagnostics`;
- `ApplyPolicyRevision`;
- `BeginApprovedMaintenance`;
- `RotateDeviceCredential`.

Запрещены `RunShell`, arbitrary executable/URL, raw PowerShell, upload-and-execute и общий
filesystem read. Добавление нового command type требует protocol/security review.

Каждая команда содержит:

- tenant/device/resource scope;
- unique command ID;
- issue/expiry timestamps;
- expected state/policy digest;
- idempotency semantics;
- signer/approval evidence;
- bounded typed payload.

Endpoint инициирует outbound connection/poll/stream. Входящий listening internet port не
требуется.

## High-risk approvals

Deletion, prune, retention reduction, legal-hold removal, recovery-method revocation и
in-place fleet restore требуют approval artifact:

```text
Approval {
  operationDigest,
  tenant/resource scope,
  approver identities,
  required quorum,
  issuedAt/expiresAt,
  expected state version,
  reason/ticket reference,
  signatures
}
```

Control plane не может заменить operation после approval. Break-glass не снимает Object
Lock Compliance retention и не создаёт отсутствующие key permissions.

## Endpoint status contract

По умолчанию отправляются агрегаты:

- device/platform/agent version;
- last backup and verified restore timestamps;
- Recovery Confidence factors/categories;
- repository/storage capability state;
- consistency level и writer status codes;
- immutable window/RPM status;
- policy/audit/update health;
- bounded error reason codes.

Не отправляются filenames, file content, raw local paths, mnemonic, EUS, document classes,
AI prompts или raw diagnostic dumps. Расширенная диагностика требует отдельного scope,
preview/redaction и time-bound consent/policy.

## Event delivery

- Каждое событие имеет tenant/device ID, local sequence и idempotency key.
- Endpoint хранит bounded encrypted queue.
- Повторная доставка не создаёт дубликат semantic event.
- Server acknowledgement фиксирует contiguous sequence watermark.
- Gap отображается как `Telemetry/Evidence gap`, а не молча закрывается.
- Переполнение сохраняет critical security/audit events по приоритетной policy и создаёт
  локальный gap record.

Control-plane event store не заменяет локальный tamper-evident ledger; он является одним
из внешних anchors/представлений.

## Data residency

- Tenant закрепляется за region/cell.
- Metadata и backups имеют независимые residency policies.
- Cross-region DR control plane не включается без policy/contract.
- Support access из другой юрисдикции учитывается отдельно от storage location.
- Backups никогда не копируются вслед за control-plane metadata автоматически.
- Export/delete tenant metadata имеет documented lifecycle и audit evidence.

## Licensing behavior

- License влияет на создание новых premium configurations и managed services.
- Истечение license не блокирует существующий local backup немедленно без grace policy.
- Restore, recovery kit export/verification и `fortiq-recover` никогда не блокируются.
- Customer data не удерживаются как средство продления подписки.
- Offboarding предоставляет machine-readable policy/evidence export.

## Control-plane compromise

При компрометации control plane attacker не должен получить возможность:

- расшифровать repositories;
- отправить произвольный код;
- снизить policy revision;
- выполнить destructive operation без required customer approval;
- подменить update target вне TUF trust;
- пересечь tenant boundary.

Endpoint может перейти в `ControlPlaneUntrusted`, продолжить backup по cached policy,
заблокировать новые high-risk commands и уведомить через альтернативный канал.

## Tenant offboarding

1. Остановить новые control-plane mutations.
2. Экспортировать policies, device inventory и evidence.
3. Проверить независимые recovery kits и storage ownership.
4. Отозвать device control-plane certificates.
5. Удалить/анонимизировать control-plane metadata по retention/legal hold.
6. Не удалять customer repositories и KMS keys.
7. Выдать signed offboarding receipt.

## Isolation/security tests

- IDOR/cross-tenant query and mutation suite;
- forged tenant ID в request body;
- MSP delegation removal во время active session;
- support session expiry/replay;
- cloned/revoked device certificate;
- enrollment token replay/tenant substitution;
- signed policy rollback/expiry/wrong tenant;
- arbitrary command/payload/oversize rejection;
- compromised control plane отправляет deletion без quorum;
- endpoint offline 1/7/30 дней;
- event duplication/reordering/gap/queue overflow;
- license expiry во время backup и disaster restore;
- region/cell routing mismatch;
- control-plane loss при продолжающемся local schedule;
- clean-machine recovery без DNS/network к Fortiq.

## Exit criteria V3

- control plane не присутствует в backup content path;
- tenant isolation проверена automated tests и external assessment;
- endpoint выполняет backup минимум 30 дней offline по cached policy;
- high-risk command без valid approval fail closed;
- истечение лицензии не блокирует DR-001;
- compromise simulation не раскрывает EUS и не уничтожает immutable recovery point;
- tenant получает полный evidence/offboarding export.

