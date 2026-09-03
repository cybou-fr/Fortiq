# Системная архитектура

## Контексты

```text
┌──────────────────────── Fortiq Desktop ────────────────────────┐
│ UI, configuration, recovery workflow, local AI interaction     │
└──────────────────────────────┬───────────────────────────────────┘
                               │ authenticated local IPC
┌──────────────────────────────▼───────────────────────────────────┐
│ Fortiq Service                                                  │
│ scheduler, policy engine, catalog, job orchestration, audit      │
└──────────────┬───────────────────┬───────────────────┬────────────┘
               │                   │                   │
       ┌───────▼────────┐  ┌──────▼──────────┐  ┌─────▼──────────┐
       │ Privileged      │  │ Repository      │  │ Key Manager    │
       │ Windows Broker  │  │ Adapter         │  │                │
       │ VSS / USN       │  │ backup/restore  │  │ key wrappers   │
       └────────────────┘  └──────┬──────────┘  └─────┬──────────┘
                                  │                   │
                         ┌────────▼────────┐  ┌───────▼───────────┐
                         │ Local / S3      │  │ TPM / Recovery / │
                         │ immutable store │  │ KMS              │
                         └─────────────────┘  └───────────────────┘

Optional, read-only path:
Desktop → AI Orchestrator → sanitized catalog/change summaries
```

## Компоненты

### Fortiq Desktop

UI не получает долгоживущих ключей и не выполняет privileged operations. Он формирует
команды, показывает планы действий и запрашивает явное подтверждение опасных операций.

### Fortiq Service

Работает с минимально необходимыми правами. Содержит scheduler, policy engine, repository
catalog и orchestration. Команды IPC аутентифицируются, авторизуются и журналируются.

### Privileged Windows Broker

Отдельный минимальный процесс выполняет только VSS/USN и другие операции, требующие
повышенных прав. Он не имеет сетевого клиента, UI, AI и доступа к recovery mnemonic.

### Repository Adapter

Первая версия использует один выбранный engine. Fortiq различает собственный repository
master key и engine-specific repository password/credential. Контракт адаптера обязан
описывать backup, restore, verify, retention и capability discovery.

### Key Manager

Хранит только metadata и wrapped key material. Unlock methods являются независимыми
обёртками одного Repository Master Key и могут применяться одновременно.

### Recovery Tool

`fortiq-recover` — отдельная open-source CLI без зависимости от UI, лицензии и Fortiq
control-plane. Она умеет inspect, unlock, verify и restore поддерживаемых версий формата.

## Инварианты

- Ни одна AI-модель не входит в backup/restore critical path.
- Потеря локальной catalog DB не делает репозиторий невосстановимым.
- Endpoint credential недостаточно для немедленного уничтожения immutable history.
- Все destructive operations проверяются policy engine.
- Форматы metadata и key envelopes версионируются.
- Windows-only интеграции находятся за платформенными интерфейсами.

## Backup и Sync

Backup и двунаправленная синхронизация являются разными bounded contexts. Будущий
`Fortiq Vault` может переиспользовать identity и key infrastructure, но не retention,
conflict resolution и модель удаления backup-репозитория.

