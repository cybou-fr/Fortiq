# Supply-chain security и обновления

## Цели

Update-контур должен предотвращать:

- установку произвольного или повреждённого бинарника;
- mix-and-match компонентов из разных релизов;
- rollback к известной уязвимой версии;
- бесконечное удержание клиента на устаревшем metadata;
- подмену restic, recovery tool, native helper или AI adapter;
- автоматическую миграцию repository format без отдельного approval;
- получение updater-ом backup keys или storage credentials.

## Состав release unit

Релиз устанавливается как единый проверяемый комплект:

```text
Fortiq Release
├── Desktop
├── Service
├── Privileged Windows Broker
├── Update Agent
├── fortiq-recover
├── fortiq-audit
├── restic binaries per RID
├── optional Windows AI adapter
├── protocol/schema compatibility manifest
├── SBOM
├── third-party notices
└── signed provenance/evidence
```

Нельзя обновить один privileged компонент независимо, если release manifest не объявляет
совместимость с установленными protocol versions.

## Два уровня проверки Windows release

### Authenticode

Все PE/MSI/MSIX/installer binaries подписываются Authenticode с SHA-256 и RFC 3161
timestamp. Проверяются chain, EKU/policy, digest, publisher identity и timestamp.

Authenticode подтверждает происхождение конкретного файла, но не связывает весь release
set, не задаёт compatibility и не является полной rollback protection.

### Update metadata

Update repository использует TUF-роли:

- `root`: доверенные ключи, thresholds и делегирование;
- `targets`: hashes, sizes и custom compatibility metadata артефактов;
- `snapshot`: согласованный набор версий metadata;
- `timestamp`: freshness и защита от freeze/replay в online flow.

Root trust bootstrap встраивается в installer и recovery media. Root keys хранятся offline
с threshold policy; online compromise одного timestamp/hosting credential не должен
позволять подписать произвольный release.

## Fortiq release manifest

Release manifest является signed TUF target:

```json
{
  "schema": "fortiq.release",
  "version": 1,
  "releaseId": "UUID",
  "productVersion": "SemVer",
  "releaseSequence": 1,
  "channel": "stable",
  "minimumOs": {},
  "components": [
    {
      "id": "fortiq.service",
      "rid": "win-x64",
      "path": "...",
      "length": 0,
      "sha256": "...",
      "authenticodePublisher": "expected identity",
      "protocols": { "ipc": [1], "audit": [1] }
    }
  ],
  "repositoryCompatibility": {
    "read": [{ "engine": "restic", "formats": [1, 2] }],
    "write": [{ "engine": "restic", "formats": [2] }]
  },
  "stateSchema": { "minimum": 1, "maximum": 1 },
  "migration": { "required": false, "id": null },
  "sbom": { "path": "...", "sha256": "..." },
  "provenance": { "path": "...", "sha256": "..." }
}
```

Все lengths имеют верхние policy limits. Target скачивается только после проверки
metadata; hash проверяется до Authenticode и до execution.

## Release sequence и rollback

Client хранит последнюю доверенную TUF metadata и максимальный `releaseSequence` каждого
channel. Обычная установка target с меньшим sequence запрещена.

Контролируемый rollback оформляется как **новый** подписанный release с большим sequence,
который содержит предыдущие binaries и явную compatibility/migration policy. Старый
metadata никогда не становится доверенным снова.

Rollback приложения не выполняет downgrade repository format или необратимой state schema.
Если state несовместим, применяется forward fix либо восстановление заранее созданного
transactional state snapshot по отдельному signed plan.

## Update Agent

Update Agent — минимальный отдельный процесс/service:

- не имеет EUS, KMS или storage credentials;
- не читает пользовательские backup sources;
- загружает только targets, разрешённые update metadata;
- staging выполняет в каталоге с закрытой ACL;
- проверяет metadata, size, hash и Authenticode до остановки сервисов;
- проверяет достаточно ли места для staging и rollback copy;
- применяет атомарный installation plan;
- запускает post-install health check;
- при failure возвращает предыдущий application set, если state совместим;
- пишет audit evidence через ограниченный интерфейс.

Service не может приказать updater-у запустить произвольный файл или URL. Request содержит
только channel/release ID, который updater независимо разрешает через доверенный metadata.

## Transactional update flow

```text
Check metadata
  → verify TUF root/timestamp/snapshot/targets
  → select compatible release
  → download bounded targets to staging
  → verify length + SHA-256 + Authenticode
  → verify release-set compatibility
  → create application-state rollback point
  → stop components in dependency order
  → install/activate as one release
  → start components
  → run IPC/schema/keyless health checks
  → commit active release pointer
  → audit + external checkpoint
```

Repository migration не является post-install health step и никогда не запускается
неявно. Engine binary update сначала проверяется на копии тестового repository.

## Recovery path независим от updater

- `fortiq-recover` можно запустить portable без установленного Update Agent.
- Recovery kit фиксирует minimum/known compatible recovery versions.
- Старые recovery binaries и metadata хранятся в offline archive.
- Новый основной продукт не может удалить последний совместимый recovery tool.
- Emergency recovery media проверяет release signatures offline.

## Offline bundle

Enterprise/offline installation bundle содержит:

- complete targets для выбранных RID;
- TUF root/targets/snapshot metadata;
- offline release manifest;
- Authenticode-signed installer/binaries;
- SBOM, notices и provenance;
- checksum/verification utility;
- expiration/sequence policy и инструкцию обновления trust root.

Отсутствие online timestamp metadata не означает отключение проверок. Offline policy
требует trusted operator action, signature threshold, non-decreasing release sequence и
явное подтверждение возраста bundle.

## Dependency policy

### .NET/NuGet

- exact package versions; floating/range versions запрещены в release branch;
- Central Package Management;
- `packages.lock.json` хранится в repository;
- CI использует `RestoreLockedMode=true`;
- один корневой `nuget.config` очищает inherited sources;
- Package Source Mapping связывает package IDs с разрешёнными sources;
- package hashes/cache provenance сохраняются в build evidence;
- transitive dependencies входят в SBOM и vulnerability/license review;
- dependency update выполняется отдельным reviewable change.

### Native/external binaries

Каждый restic/native runtime описан engine manifest:

- upstream project и source URL;
- exact version/commit;
- RID/architecture;
- SHA-256 и upstream signature, если доступна;
- license;
- reproducibility status;
- Fortiq release compatibility;
- vulnerability exceptions с expiry.

Runtime download `latest` запрещён. Self-update внешних engines отключён: они обновляются
только как часть Fortiq release.

### Phi Silica и Windows components

Phi Silica поставляется/обслуживается Windows, а не включается в bundle Fortiq. Audit
фиксирует Windows AI API/runtime/model capability version, доступную во время inference.
Её изменение не может менять deterministic backup/restore behavior.

## SBOM и provenance

- Каждый release публикует machine-readable SPDX SBOM.
- SBOM включает managed, native, bundled tools и installer components.
- OS-provided components указываются как external runtime requirements.
- Build provenance связывает source revision, builder identity, workflow, inputs и outputs.
- Secrets, internal paths и персональные данные удаляются из public provenance.
- Release evidence связывает SBOM/provenance hashes с подписанным target manifest.

Целевой SLSA level принимается отдельной engineering policy после проверки CI platform;
документ не заявляет недоказанный уровень.

## Build isolation

- release builds выполняются на ephemeral controlled runners;
- branch protection и reviewed changes обязательны;
- build и signing разделены;
- signing key недоступен build scripts;
- artifacts после signing immutable;
- сеть build job ограничивается разрешёнными sources либо используется dependency mirror;
- release job собирает из проверенных build outputs, а не пересобирает произвольно;
- reproducible-build comparison применяется к компонентам, где это поддерживается.

## Key roles и rotation

- Root: offline, threshold, редкое использование.
- Targets: offline или strongly protected release signing.
- Snapshot: автоматизированный ограниченный key.
- Timestamp: online short-lived key, частая ротация.
- Authenticode: HSM/managed signing service с audit.

Компрометация любой роли имеет documented playbook: revoke/rotate, publish new root,
invalidate affected targets, notify customers и выпустить signed incident evidence.

## Update channels

`stable`, `preview` и internal channels используют отдельные delegated targets и policies.
Production клиент не принимает preview target без явного администраторского enrollment.
Смена channel журналируется и не снижает minimum trusted sequence автоматически.

## Failure policy

- Signature/hash/length mismatch: quarantine, alert, не выполнять.
- Expired/frozen metadata: оставить текущую версию, alert; не доверять target.
- Incompatible schema/protocol: не устанавливать.
- Недостаточно места: не останавливать текущий service.
- Crash до activation: очистить staging после reconciliation.
- Crash после activation: health check и controlled rollback/forward recovery.
- Authenticode valid, TUF invalid: не устанавливать.
- TUF valid, Authenticode invalid для Windows PE: не устанавливать.

## Обязательные тесты

- повреждённый target;
- validly signed старый metadata/release rollback;
- mixed components двух releases;
- expired/frozen timestamp;
- excessive target/metadata size;
- неизвестный critical manifest field;
- compromised mirror/HTTP endpoint;
- offline bundle с меньшим sequence;
- update при работающем backup job;
- power loss на каждом transactional step;
- incompatible IPC/state/repository format;
- incorrect Authenticode publisher/timestamp;
- restic self-update attempt;
- missing/incorrect SBOM target;
- recovery tool запускается после неуспешного application update.

