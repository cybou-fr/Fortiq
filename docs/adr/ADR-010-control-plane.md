# ADR-010: metadata-only, offline-first control plane

- Статус: **Accepted for future Fleet/MSP release**
- Дата: **3 сентября 2026**
- Область: fleet management, tenancy и endpoint autonomy

## Контекст

MSP/Enterprise требуют централизованного health, policies и audit. Если control plane
получит backup keys или станет обязательным для scheduled backup/restore, продукт потеряет
суверенность и создаст единую точку компрометации/доступности.

## Решение

1. Control plane хранит только минимизированную metadata и evidence.
2. Backup content и EUS никогда не проходят через control plane.
3. Endpoint выполняет backup/verification по локально cached signed policy.
4. Restore и recovery не требуют control-plane connectivity или valid license.
5. Endpoint устанавливает только outbound mTLS connection.
6. Команды являются закрытыми typed operations без shell/remote execution.
7. High-risk operations требуют customer-scoped signed approval.
8. Customer Tenant является security boundary; MSP получает явную delegation.
9. Control-plane compromise включает fail-closed для новых destructive operations, но не
   останавливает защитный backup.

## Почему не хранить recovery secret для удобства

Central escrow дал бы единый способ расшифровать все customer repositories и противоречил
бы customer-controlled custody. Организация может выбрать собственный KMS/escrow, но
Fortiq control plane хранит только ссылку/status, не plaintext key.

## Почему не inbound management port

Outbound device connection проще ограничить firewall-ом и не выставляет service endpoint
в сеть. Commands доставляются по уже аутентифицированному каналу и всё равно проходят
локальную policy/approval проверку.

## Availability decision

При потере control plane endpoint:

- продолжает backup, checks и immutable replication;
- буферизует bounded status/evidence;
- разрешает safe local restore;
- запрещает новые policy relaxations, key revocation и destructive operations.

Это защитный fail-safe, а не единый fail-open/fail-closed режим для всех функций.

## Последствия

Положительные:

- control-plane breach не раскрывает backup contents;
- ransomware protection работает во время outage;
- customer сохраняет recovery после прекращения сервиса Fortiq;
- MSP delegation можно отозвать без изменения repository encryption.

Отрицательные:

- endpoint содержит больше автономной логики/state;
- offline stale policies требуют понятной merge/reconciliation модели;
- central console не всегда имеет мгновенно актуальный status;
- zero-content architecture ограничивает server-side поиск и диагностику;
- tenant isolation и residency cells повышают operational complexity.

## Security gates

- external tenant-isolation assessment;
- PKI/enrollment protocol review;
- offline policy expiry/usability tests;
- control-plane compromise simulation;
- доказательство DR-001 после удаления tenant/control-plane;
- privacy review полного status/event schema.

