# ADR-005: VSS — источник консистентности, USN — подсказка

- Статус: **Accepted for Windows V1**
- Дата: **3 сентября 2026**
- Область: Windows source capture и incremental discovery

## Контекст

Fortiq должна копировать открытые файлы и уменьшать стоимость повторного обнаружения
изменений. VSS и USN решают разные задачи: VSS создаёт стабильное point-in-time
представление, а USN сообщает о файловых изменениях на NTFS/ReFS volume.

Использование USN как полного списка backup без строгой обработки reset/truncation может
без предупреждения пропустить данные. Создание VSS snapshot без writer validation также
не доказывает application consistency.

## Решение

1. VSS является Windows source-capture mechanism V1.
2. Fortiq выступает VSS requester через Privileged Windows Broker.
3. `ApplicationConsistent` выдаётся только при выполнении writer policy.
4. Snapshot без writer guarantee маркируется `CrashConsistent`.
5. USN Journal является optimisation/advisory input, не source of truth V1.
6. Любая недоказанная непрерывность USN приводит к полному scan.
7. Restic читает shadow device path и выполняет собственный scan.

## Почему не FileSystemWatcher

Он требует постоянной работы, может переполнять buffer и не предоставляет durable
volume-wide history. Он может использоваться для UI hints, но не для backup correctness.

## Почему USN пока не управляет списком restic input

Restic уже отвечает за traversal и сравнение snapshot content. Попытка подать только
изменённые пути требует корректного моделирования rename, directory movement, deletion,
hard links, reparse points и journal gaps. Эта оптимизация откладывается до отдельного
proof с differential tests против полного scan.

## VSS wrapper

ADR не фиксирует AlphaVSS как обязательную зависимость. Выбор между поддерживаемым wrapper
и узким собственным COM interop принимается после prototype проверки:

- совместимости с целевой .NET/Windows matrix;
- полноты requester lifecycle;
- обработки async/abort/status/metadata;
- лицензии, сопровождения и supply-chain риска;
- fault-injection testability.

## Последствия

Положительные:

- correctness не зависит от сохранности USN history;
- consistency становится измеряемым свойством receipt;
- открытые файлы читаются из стабильного source view;
- privileged raw-volume API изолирован в broker.

Отрицательные:

- полный engine scan остаётся стоимостью V1;
- application-aware backup требует сложной writer/component policy;
- необходимо сохранять VSS metadata для полноценного application restore;
- VSS остаётся Windows-only механизмом.

## Источники

- [Microsoft: VSS architecture and roles](https://learn.microsoft.com/en-us/windows-server/storage/file-server/volume-shadow-copy-service)
- [Microsoft: processing a backup under VSS](https://learn.microsoft.com/en-us/windows/win32/vss/overview-of-processing-a-backup-under-vss)
- [Microsoft: VSS writers and crash consistency](https://learn.microsoft.com/en-us/windows/win32/vss/writers)
- [Microsoft: aborting VSS operations](https://learn.microsoft.com/en-us/windows/win32/vss/aborting-vss-operations)
- [Microsoft: VSS metadata components](https://learn.microsoft.com/en-us/windows/win32/vss/vss-metadata-components)
- [Microsoft: Change Journals](https://learn.microsoft.com/en-us/windows/win32/fileio/change-journals)
- [Microsoft: using the journal identifier](https://learn.microsoft.com/en-us/windows/win32/fileio/using-the-change-journal-identifier)
- [Microsoft: USN_JOURNAL_DATA_V2](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-usn_journal_data_v2)

