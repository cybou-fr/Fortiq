# ADR-003: границы процессов V1

- Статус: **Accepted**
- Дата: **3 сентября 2026**
- Область: Desktop, Service, privileged Windows operations, AI и recovery

## Контекст

Исходная концепция помещала orchestration, VSS/USN, network access, key management и
внешние engines в Windows Service под `SYSTEM`. Компрометация одного сложного компонента
давала бы атакующему максимальные локальные полномочия и доступ ко всему backup-контуру.

## Решение

V1 использует пять логических/процессных границ:

| Процесс | Полномочия | Сеть | Ключи | Назначение |
|---|---|---:|---:|---|
| Desktop | пользователь | опционально | нет | UI и подтверждения |
| Service | service account | storage only | leases | orchestration и policy |
| Windows Broker | минимально повышенные | нет | нет | VSS/USN operations |
| AI Broker | пользователь/ограниченный | нет по умолчанию | нет | локальные proposals |
| Recover CLI | оператор | repository only | краткий lease | автономное recovery |

Внешний restic process запускается дочерним процессом Service или Recover CLI с
минимальным environment и краткоживущим credential channel.

## Инварианты

- Desktop не соединяется с privileged broker напрямую.
- AI Broker не соединяется с restic, Key Manager или privileged broker.
- Windows Broker не имеет storage credentials и исходящего network access.
- Service не работает как `LocalSystem`, если конкретная операция этого не требует.
- Повышенная операция выражается закрытым типизированным набором команд.
- Recover CLI не зависит от установленной службы.
- IPC peer identity и authorization проверяются на принимающей стороне для каждой команды.

## Windows identity baseline

Основная служба должна использовать dedicated virtual/service account с ограниченными
ACL. Privileged broker может работать с более высокими правами только как минимальный
Windows-specific компонент. Конкретные service SID, privileges и pipe ACL фиксируются
после P0 в IPC security ADR и проверяются integration tests.

## Последствия

Положительные:

- сокращается blast radius сетевого и UI-кода;
- AI физически исключается из trusted computing base backup;
- VSS-права не распространяются на engine adapter;
- recovery остаётся независимым.

Отрицательные:

- больше процессов, IPC contracts и deployment complexity;
- сложнее диагностика и обновление совместимых версий;
- требуется end-to-end tracing без утечки секретов.

Эта сложность принимается как стоимость проверяемого privilege separation.

## Не решено

- Named Pipes или иной transport;
- mutual authentication protocol и replay protection;
- Windows service SID/ACL manifest;
- sandbox/job object профиль restic;
- обновление нескольких binaries как атомарного release unit.

