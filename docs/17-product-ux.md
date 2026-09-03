# Product UX и безопасные пользовательские сценарии

## UX-цель

Fortiq помогает пользователю ответить на четыре вопроса без знания backup-терминологии:

1. Что защищено?
2. Где находятся независимые копии?
3. Кто и каким способом может их открыть?
4. Когда восстановление последний раз действительно проверялось?

Интерфейс оптимизируется не под создание job, а под уверенность в восстановлении.

## Информационная архитектура Desktop

```text
Home
├── Protection status
├── Recovery Confidence
├── Last backup / Last verified restore
└── Required actions

Protected Data
├── Backup sets
├── Sources
└── Exclusions

Repositories
├── Storage locations
├── Immutability
├── Retention
└── Health

Recovery
├── Browse snapshots
├── Guided restore
├── Recovery kit
└── Recovery drills

Security
├── Unlock methods
├── Devices and identities
├── KMS
└── Audit evidence

Activity
├── Jobs
├── Alerts
└── Audit timeline

Settings
├── Updates
├── Privacy / Local AI
└── Advanced diagnostics
```

Engine names, KDF parameters и VSS internals доступны в Advanced details, но не являются
основной навигацией.

## Статус продукта на Home

Верхний блок показывает один из состояний:

- `Protected` — RPO выполнен и проверенный recovery path свежий;
- `Attention required` — backup существует, но restore устарел или есть policy warning;
- `At risk` — нет immutable/independent recovery point либо недоступен unlock path;
- `Not protected` — нет успешной резервной копии;
- `Unknown` — evidence недостаточно или audit integrity нарушена.

Зелёный цвет не используется, если проверен только upload, но не recovery.

## Onboarding flow

### Шаг 1 — Что защищать

Пользователь выбирает понятные категории или каталоги:

- документы и рабочие проекты;
- рабочий стол;
- пользовательские каталоги;
- application-specific profile;
- расширенный выбор путей.

UI заранее показывает exclusions, приблизительный объём и unsupported objects.

### Шаг 2 — Куда сохранять

Карточки:

- локальный/сетевой диск;
- S3-compatible storage;
- managed provider profile;
- настроить вторую независимую копию.

Для target показываются регион, administrative domain, versioning, immutability status и
результат capability probe. Логотип provider не заменяет технический статус.

### Шаг 3 — Как открывать ежедневно

Рекомендуемый default:

> Использовать защищённый ключ этого устройства для автоматических резервных копий.

Расширенные варианты: master password, enterprise KMS. UI объясняет, что ежедневный unlock
и аварийное восстановление — разные задачи.

### Шаг 4 — Как восстановиться после потери компьютера

Recovery method обязателен до окончания onboarding:

- recovery words;
- корпоративный KMS с подтверждённым emergency path;
- администраторское policy exception с audit reason.

Формулировка:

> Этот способ понадобится, если компьютер, TPM и настройки Fortiq будут потеряны.

### Шаг 5 — Проверка recovery material

- слова показываются только в защищённом transient view;
- copy/screenshot/print policy явно обозначается;
- пользователь подтверждает случайно выбранные позиции, а не просто нажимает checkbox;
- optional passphrase проверяется отдельным повторным вводом;
- UI предупреждает: Fortiq не сможет восстановить потерянные слова/passphrase;
- secret никогда не появляется в telemetry или диагностическом export.

Проверка слов доказывает запись материала пользователем, но не полное восстановление.

### Шаг 6 — Первый backup

Показываются этапы:

```text
Preparing a consistent snapshot
Encrypting and storing data
Checking repository structure
Creating recovery evidence
Protecting immutable recovery point
```

Технические события агрегируются; предупреждения доступны без имитации успеха.

### Шаг 7 — Первый test restore

Onboarding завершается двумя отдельными результатами:

- `First backup completed`;
- `Recovery verified`.

Если restore-test отложен, Home остаётся `Attention required`, а не `Protected`.

## Recovery Confidence card

Карточка показывает:

```text
Recovery Confidence: High
Last verified restore: 7 hours ago
Expected recovery time: about 42 minutes (measured estimate)
Independent immutable copy: verified
Unlock paths: Device + Recovery Kit
```

Одна итоговая оценка всегда сопровождается причинами. Процент не показывается до
валидации формулы на реальных данных: ложная точность опаснее категориальной оценки.

Нажатие открывает факторы:

- snapshot freshness;
- integrity coverage;
- restore-test scope;
- key-path availability;
- immutable recovery window;
- audit/evidence health;
- known warnings и последний успешный proof.

## Guided restore

### 1. Намерение

Пользователь выбирает:

- вернуть отдельный файл/каталог;
- восстановить состояние каталога на дату;
- восстановить данные на новый компьютер;
- выполнить disaster recovery;
- провести тест без изменения рабочих данных.

### 2. Snapshot selection

Timeline показывает дату, consistency level, repository health, immutable status и наличие
требуемого unlock method. `Latest` не выбирается вслепую: после ransomware более ранний
проверенный snapshot может быть безопаснее.

### 3. Поиск данных

Доступны дерево, фильтр и локальный natural-language assistant. AI возвращает только
предложение query; результаты получает deterministic catalog/search component.

### 4. Destination

Default — новый пустой staging directory. Опции:

- `Restore to a new folder` — рекомендуемая;
- `Compare with current files`;
- `Restore in place` — advanced/high-risk.

### 5. Plan review

До запуска UI показывает:

- source snapshot/repository;
- target;
- число файлов и объём;
- overwrite/rename behavior;
- conflicts и reparse policy;
- ожидаемое время на основе измерений;
- required credentials;
- application-consistency limitations.

### 6. Confirmation

Восстановление в staging требует обычного подтверждения. In-place overwrite требует
step-up authorization и подтверждения конкретного operation digest. Пользователь не
вводит бессмысленные фразы вроде `DELETE`; он подтверждает понятный результат.

### 7. Verification

После restore Fortiq отдельно показывает:

- данные записаны;
- cryptographic/integrity verification выполнена;
- metadata восстановлена полностью/частично;
- application validation выполнена/не поддерживается;
- какие элементы пропущены и почему.

`Completed with warnings` визуально не равен `Verified`.

## Destructive operations

### Принципы

- preview всегда предшествует mutation;
- policy показывает earliest irreversible effect;
- approval связан с digest и имеет короткий срок;
- изменение plan аннулирует approval;
- массовая операция не маскируется как housekeeping;
- immutable objects не обещаются удалёнными до provider-confirmed expiry/lifecycle.

### Retention flow

```text
Policy → Dry-run plan → Impact/cost → Independent-copy check
       → Approval → Maintenance window → Apply → Integrity check → New RPM
```

UI показывает отдельно логическое удаление snapshots и физическое освобождение storage.

## Alerts

Alert должен содержать:

- что произошло;
- какое пользовательское обещание нарушено;
- последний известный безопасный recovery point;
- что Fortiq уже сделала;
- одно рекомендуемое следующее действие;
- ссылку на evidence/technical details.

Пример:

> **Recovery verification is overdue.** Последний проверенный restore выполнен 12 дней
> назад; политика требует 7 дней. Backup продолжается. Запустить безопасный test restore.

Не использовать тревожные уведомления для нормального progress или маркетинга.

## Phi Silica UX

On-device AI обозначается явно:

- `Processed on this device`;
- какой privacy mode активен;
- использовалась ли file content или только metadata;
- результат является предложением, а не выполненным действием.

При недоступности Phi Silica основной интерфейс не ломается. AI-кнопки становятся
недоступны с понятным объяснением; deterministic поиск/restore остаются.

AI никогда не генерирует зелёный security status и не изменяет Recovery Confidence.

## Multi-user и роли

- `Viewer`: status и отчёты без раскрытия чувствительной metadata.
- `Operator`: backup и safe restore в staging.
- `Recovery Officer`: recovery kit/KMS emergency operations.
- `Security Admin`: policies, identities, holds и evidence.
- `Maintenance Operator`: утверждённый prune без key-policy полномочий.

UI показывает активную роль и причину запрета операции. Локальный Windows administrator
не считается автоматически Recovery Officer без policy.

## Accessibility и локализация

- все состояния доступны не только цветом;
- keyboard-only navigation и видимый focus;
- screen-reader labels для status/progress;
- progress не объявляется слишком часто;
- масштабирование текста не скрывает warnings/actions;
- recovery words не зависят от визуального расположения карточек;
- даты показывают timezone, а evidence хранит UTC;
- термины проверяются носителями языка и не переводят protocol identifiers;
- destructive consequences формулируются простым языком.

## UX research tests

Участник без помощи команды должен:

1. объяснить разницу между backup и verified recovery;
2. создать repository и независимый recovery method;
3. определить, где физически расположена копия;
4. восстановить файл на чистом компьютере по recovery kit;
5. понять, что `CrashConsistent` не равно application-aware restore;
6. отказаться от небезопасного in-place restore, если staging достаточен;
7. распознать, что AI proposal ещё не выполнен;
8. найти evidence последнего restore-test.

Метрики:

- task completion без подсказки;
- critical error rate;
- время до первого verified recovery;
- понимание key-loss consequences;
- доля пользователей, сохранивших единственный recovery kit рядом с устройством;
- false confidence rate после частичного restore.

## UX acceptance criteria V1

- onboarding нельзя завершить с единственным TPM path без явного audited exception;
- UI не показывает `Protected` до первого restore-test;
- destructive plan всегда доступен до approval;
- отсутствие AI не блокирует ни один backup/recovery workflow;
- технический raw log не является единственным объяснением ошибки;
- каждый warning связан с конкретным remediation action;
- screen-reader и keyboard smoke tests включены в release gate;
- clean-machine usability test DR-001 проходит целевой пользователь, не разработчик.

