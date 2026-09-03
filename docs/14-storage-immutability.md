# Storage immutability и ransomware resilience

## Цель

Компрометация backup endpoint не должна позволить атакующему уничтожить все пригодные
для восстановления recovery points до обнаружения атаки и реакции оператора.

Шифрование не обеспечивает это свойство: клиент, имеющий право удаления, может уничтожить
зашифрованные объекты, не зная ключа расшифрования.

## Термины

- **Operational repository:** репозиторий, в который engine выполняет обычный backup.
- **Immutable copy:** версия объектов, защищённая WORM/retention control.
- **Recovery point manifest (RPM):** неизменяемый список точных object versions одного
  проверенного состояния репозитория.
- **Backup identity:** credential endpoint для обычной записи backup.
- **Maintenance identity:** credential для approved retention/prune.
- **Security identity:** отдельная административная роль для bucket/retention/legal hold.
- **Administrative domain:** отдельный account/project/tenant и набор credentials.

## Гарантия

Fortiq может показывать `Immutable recovery point: Verified` только если доказано:

1. bucket versioning и Object Lock включены;
2. все объекты RPM имеют version IDs;
3. фактический retain-until покрывает policy window;
4. backup identity не может удалить version, сократить retention или bypass governance;
5. manifest сам защищён не меньше перечисленных объектов;
6. recovery test способен материализовать репозиторий именно из RPM;
7. account/project deletion risk явно отражён deployment policy.

## Retention window

Минимальный срок immutability вычисляется не только из количества snapshots:

```text
minimum immutable window >=
  maximum detection delay
  + incident response delay
  + credential recovery delay
  + full restore initiation delay
  + safety margin
```

Пример `30 days` является policy choice, а не универсальной рекомендацией. Слишком длинный
Compliance retention может сделать стоимость и ошибочные загрузки необратимыми.

## Режимы Object Lock

### Governance

Подходит для пилота и гибкой эксплуатации, но identity с bypass permission способна
сократить retention или удалить version. Такая identity не выдаётся endpoint, Service или
обычной maintenance automation.

### Compliance

Предпочтителен для строго определённых production recovery points: retention нельзя
сократить даже административной ролью в пределах возможностей provider. Перед включением
обязательны cost forecast, legal approval и тестовый bucket.

### Legal hold

Используется для incident/audit hold и не заменяет обычную time-based retention. Право
снятия hold отделяется от backup и maintenance identities.

## Identity separation

| Identity | Put | Get/List | Delete current | Delete version | Retention/BYPASS | Bucket admin |
|---|---:|---:|---:|---:|---:|---:|
| Backup | да | минимально | только `locks/`, если доказано необходимо | нет | нет | нет |
| Restore | нет | да | нет | нет | read-only status | нет |
| Maintenance | да | да | approved scope | только expired/unlocked по policy | без bypass | нет |
| Security admin | обычно нет | audit | policy-specific | policy-specific | отдельное approval | ограниченно |
| Replicator | put-only target | source read | нет | нет | только extend/set allowed profile | нет |

Одна identity не совмещает backup и bypass-governance. Long-lived static credentials на
endpoint запрещены, если provider поддерживает короткоживущую federation.

## Почему delete marker опасен

В versioned S3-compatible storage простой DELETE без `versionId` может завершиться `200`
и создать delete marker. Защищённая старая version остаётся физически, но обычный GET
перестаёт её видеть. Поэтому:

- backup identity не получает общий `DeleteObject`;
- recovery inventory работает с version IDs;
- health probe проверяет скрытие объектов delete markers;
- recovery tool умеет материализовать RPM без доверия к current version;
- наличие locked bytes ещё не означает доступность обычного repository endpoint.

## Recovery Point Manifest

RPM создаётся только после успешного backup receipt и минимальной integrity validation.

```json
{
  "schema": "fortiq.immutable-recovery-point",
  "version": 1,
  "recoveryPointId": "UUID",
  "repositoryId": "...",
  "engine": { "name": "restic", "repositoryFormat": 2 },
  "snapshotIds": ["..."],
  "createdAt": "RFC3339",
  "objects": [
    {
      "key": "data/ab/...",
      "versionId": "provider-version-id",
      "size": 0,
      "checksum": { "algorithm": "provider-or-fortiq", "value": "..." },
      "retainUntil": "RFC3339",
      "retentionMode": "COMPLIANCE"
    }
  ],
  "previousRecoveryPointId": "UUID-or-null",
  "signature": { "suite": "TO_BE_DECIDED", "keyId": "...", "value": "..." }
}
```

RPM может быть chunked для больших repositories. Exact encoding, signature и scalable
inventory algorithm определяются отдельным format ADR. JSON выше — логическая схема.

Создание RPM не должно требовать скачивания содержимого каждого объекта: checksum может
быть взят только из доверенного upload receipt/provider response либо вычислен локально
до upload. ETag нельзя универсально считать хешем содержимого.

## Два deployment profile

### Profile A — Direct Locked Repository

Restic работает непосредственно с Object Lock bucket. Профиль допускается только после
provider-specific integration test, потому что restic создаёт временные lock objects, а
prune создаёт и удаляет repository objects.

Требования:

- default retention применяется ко всем новым durable objects;
- delete разрешается backup identity только на точном `locks/` prefix при необходимости;
- data/snapshot/index/key/config version deletion endpoint-у запрещено;
- maintenance не имеет bypass permission;
- lifecycle удаляет expired noncurrent versions по утверждённой политике;
- операции backup/check/restore/prune проверены с locked lock-object versions.

### Profile B — Operational Repository + Immutable Mirror

Restic работает с operational repository, а отдельный replicator публикует завершённый
repository state в locked bucket и последним записывает RPM.

Преимущества:

- ephemeral restic locks не попадают в immutable copy;
- endpoint не имеет credentials immutable target;
- recovery point можно связать с точным version inventory;
- immutable copy может находиться в другом administrative domain/provider.

Недостатки:

- дополнительное хранилище, задержка и сложность inventory;
- recovery point не считается готовым до завершения replication/verification;
- materialization tool обязан восстановить точный object set RPM.

Profile B является безопасным fallback, если direct compatibility не доказана.

## Retention и restic prune

Prune логически удаляет старые объекты и может записывать перепакованные данные. В
versioned locked storage старые versions продолжают занимать место до expiry/lifecycle.

Перед prune Fortiq:

1. строит retention plan без удаления;
2. доказывает наличие другого immutable recovery point;
3. оценивает временный и retained storage growth;
4. получает approval с operation digest и expiry;
5. использует maintenance identity;
6. после prune запускает check;
7. создаёт новый RPM;
8. не считает пространство освобождённым до подтверждения provider inventory/lifecycle.

## Storage capability probe

Для каждого endpoint probe проверяет фактическое поведение, а не только S3 API label:

- versioning state;
- Object Lock configuration и default retention;
- Governance/Compliance semantics;
- permission на GetObjectRetention/GetObjectLegalHold;
- запрет DeleteObjectVersion;
- запрет BypassGovernanceRetention;
- поведение simple DELETE и delete markers;
- list/read конкретной noncurrent version;
- multipart upload retention;
- lifecycle и noncurrent-version expiration;
- clock/retain-until behavior;
- возможности account/project deletion;
- region и storage class.

Probe не выполняет destructive проверку на production objects. Для onboarding создаётся
изолированный canary prefix/bucket с известными объектами.

## Provider certification levels

- `Compatible`: backup/restore работает, immutability не обещается.
- `Versioned`: versions доступны, но WORM guarantee не доказана.
- `Immutable-Tested`: provider/profile прошёл automated adversarial suite.
- `Immutable-Verified`: конкретный deployment продолжает проходить periodic canary probe.

Маркетинговое название provider не заменяет runtime verification.

## Adversarial test IM-001

1. Создать backup и verified immutable RPM.
2. Сымитировать кражу endpoint/backup credentials.
3. Попытаться удалить current objects, exact versions, изменить retention и поставить
   delete markers.
4. Удалить operational repository state в тестовом окружении.
5. На чистой машине прочитать RPM и materialize exact object versions.
6. Запустить restic check и выборочный restore.
7. Подтвердить, что attacker credential не сократил реальный recovery window.

Тест обязан учитывать компрометацию endpoint clock/config и выполняться для каждого
provider/region/profile перед присвоением `Immutable-Tested`.

## Ограничения гарантии

Object Lock не является полной защитой от:

- удаления всего cloud account/project там, где provider это допускает;
- прекращения оплаты или договора;
- потери всех restore credentials;
- региональной/provider-wide недоступности;
- ошибочно слишком короткой retention;
- повреждения, записанного до создания recovery point;
- компрометации отдельно защищённой security/admin identity.

Для уровня `Sovereign Resilient` требуется минимум одна копия в независимом
administrative domain; для критичных workloads — в другом provider/регионе согласно
политике локальности данных.

