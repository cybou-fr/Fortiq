# ADR-012: SQLite как восстанавливаемый локальный catalog

- Статус: **Принято**
- Дата: **3 сентября 2026**
- Владельцы: Architecture, Recovery, Security

## Контекст

Fortiq требуется локальное хранилище для policies, job orchestration, receipts, assurance evidence и производных recovery-point views. Оно должно переживать process crash и отключение питания, обеспечивать понятные migrations и не превращаться в скрытую зависимость disaster recovery.

Рассматривались embedded SQLite, набор отдельных JSON/CBOR files, Windows ESE и локальный client/server database. Главный критерий — не максимальная распределённая масштабируемость, а предсказуемая atomicity на одном endpoint при минимальной operational surface.

## Решение

V1 использует SQLite на локальном NTFS как **rebuildable catalog** со следующими нормативными свойствами:

1. Catalog не является источником ключей и не требуется для расшифровки repository.
2. Только `Fortiq.Service` открывает DB; остальные процессы используют IPC.
3. Используются WAL, `synchronous=FULL`, foreign keys и bounded busy handling.
4. Все writes проходят через одну writer queue; внешние операции запрещены внутри DB transaction.
5. Job state, terminal receipt и outbox event фиксируются атомарно; границы с repository закрываются idempotency и reconciliation.
6. DB хранится только локально и защищается service SID ACL.
7. Sensitive UX metadata шифруются независимым CMK; recovery secrets в catalog запрещены.
8. Backup создаётся согласованным SQLite backup mechanism, а corruption приводит к quarantine и rebuild, не к in-place repair.
9. Production WAL разрешён только с SQLite 3.51.3+ либо официальной исправленной backport-веткой 3.50.7/3.44.6; approved binary фиксируется в SBOM.
10. Схема и rebuild contract определены в [локальной модели данных](../20-local-catalog-data-model.md).

## Последствия

Положительные:

- простая поставка без отдельного database service;
- ACID transactions и зрелые integrity/backup interfaces;
- одновременное чтение UI projections и запись Service;
- catalog можно удалить и восстановить из repository/evidence;
- единая точка контроля доступа и migrations.

Отрицательные:

- один writer ограничивает write throughput, что приемлемо для endpoint workload;
- WAL требует checkpoint observability и запрещает network filesystem;
- native SQLite становится явно управляемой supply-chain dependency;
- поля, существующие только в local configuration, требуют отдельного export/backup UX;
- CMK loss может удалить удобные метаданные, хотя restore остаётся возможен.

## Отклонённые альтернативы

### JSON/CBOR files на сущность

Отклонено из-за сложной multi-object atomicity, migration и crash recovery. Формат остаётся пригодным для подписанных переносимых artifacts, но не для mutable orchestration state.

### Windows ESE

Отклонено для V1: сильнее platform coupling, меньше переносимость recovery tooling и уже pool знакомых инструментов у команды.

### PostgreSQL/SQL Server LocalDB

Отклонено для endpoint V1 из-за installation, patching и service lifecycle overhead. Решение control plane остаётся отдельным.

### Catalog как авторитетный backup index

Отклонено принципиально: потеря endpoint database не должна уничтожать возможность восстановления.

## Проверка решения

Решение пересматривается, если:

- endpoint write load устойчиво превышает возможности single-writer profile;
- обязательная функция требует multi-host access к одной БД;
- fault-injection показывает потерю terminal receipts при заявленном durability profile;
- rebuild без catalog не проходит независимый recovery drill.

