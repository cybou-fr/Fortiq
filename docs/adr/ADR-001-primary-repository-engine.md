# ADR-001: restic как основной repository engine V1

- Статус: **Accepted for V1**
- Дата: **3 сентября 2026**
- Область: backup repository, restore и integrity verification
- Пересмотр: после выполнения V1 exit criteria

## Контекст

Fortiq требуется один основной engine для файлового backup Windows. Приоритеты V1:

1. автономное восстановление без Fortiq control-plane;
2. зрелый и документированный repository format;
3. надёжная CLI-автоматизация;
4. проверка целостности;
5. работа с local и S3-compatible storage;
6. permissive license и контролируемая поставка бинарника;
7. минимальная operational complexity.

Рассмотрены restic и Kopia. Собственный repository engine исключён из V1 из-за высокой
стоимости криптографической проверки, восстановления после повреждения и долгосрочной
совместимости.

## Матрица решения

Оценка 1–5 отражает пригодность для Fortiq V1, а не абсолютное качество проекта.

| Критерий | Вес | restic | Kopia | Комментарий |
|---|---:|---:|---:|---|
| Простота автономного restore | 5 | 5 | 4 | Оба CLI-first; restic проще использовать как recovery primitive |
| Зрелость и adoption signal | 5 | 5 | 4 | restic старше и имеет более широкое распространение |
| CLI/automation stability | 4 | 4 | 4 | У обоих есть CLI; не все операции полностью JSON |
| Integrity verification | 5 | 5 | 4 | restic предоставляет structural и full-data check |
| Immutable/managed storage | 4 | 3 | 5 | Kopia имеет более явные Object Lock/maintenance возможности |
| Central server/fleet path | 3 | 2 | 5 | Kopia server функционально богаче |
| Licensing/redistribution | 4 | 5 | 5 | BSD-2-Clause против Apache-2.0; обе permissive |
| Operational simplicity V1 | 5 | 5 | 3 | Server mode не нужен для первого локального контура |
| **Взвешенный итог** |  | **158** | **142** | Максимум 175 |

## Решение

Использовать **restic как единственный основной repository engine Fortiq V1**.

Fortiq:

- поставляет проверенный pinned binary restic;
- управляет jobs через CLI adapter;
- предоставляет credential через краткоживущий password-command helper;
- использует VSS snapshot path как источник;
- вызывает `check` для integrity evidence;
- выполняет restore в собственный безопасный staging area;
- сохраняет engine-independent recovery kit и audit evidence.

В V1 Fortiq не реализует собственные chunking, deduplication и repository encryption.

## Key management consequence

Restic генерирует собственный repository master key и защищает его password-based key
entries. Поэтому Fortiq RMK не следует ошибочно называть непосредственным DEK restic.

Fortiq Key Manager хранит или воспроизводит высокоэнтропийный **Engine Unlock Secret**
(EUS). Его каноническое представление `Base64UrlNoPadding(EUS)` передаётся restic как
repository password. Каждый Fortiq unlock method
(TPM/password, recovery secret, KMS) защищает один и тот же EUS собственным envelope.

```text
TPM / Recovery / KMS envelope
              ↓
    Engine Unlock Secret
              ↓
 restic key entry → restic internal master key → repository data
```

Это сохраняет автономность: recovery tool может получить EUS из recovery envelope и
запустить совместимую версию restic без участия Fortiq Service.

## Ransomware consequence

Append-only/Object Lock нельзя считать автоматически решённым выбором restic. Backup
endpoint получает credential без delete/prune. Maintenance identity хранится отдельно.
`forget/prune` выполняются только по утверждённому retention plan и с учётом storage
retention window.

S3 Object Lock profile должен быть подтверждён отдельным ADR и integration tests для
каждого официально поддерживаемого provider.

## Недостатки и компенсации

- Не все команды имеют чистый JSON output. Adapter обязан иметь version-pinned parsers,
  golden fixtures и fail-closed обработку неизвестного вывода.
- Restic остаётся до версии 1.0; совместимость контролируется pinning и recovery matrix.
- Нет богатого встроенного fleet server. Fleet orchestration остаётся ответственностью
  Fortiq Service/Control Plane и не включается в локальный V1.
- Password-command требует собственного безопасного helper protocol на Windows.
- Prune требует повышенных storage permissions и выносится из endpoint job identity.

## Почему не Kopia сейчас

Kopia остаётся сильным кандидатом для managed repository/server edition благодаря server
mode, policy system и Object Lock maintenance. Но эти преимущества относятся главным
образом к будущему fleet/MSP-сценарию и увеличивают число operational concepts в V1.

Kopia может быть добавлена только как явно отдельный engine profile. Fortiq не обещает,
что snapshots или credentials будут переносимы между restic и Kopia.

## Exit criteria решения

ADR считается подтверждённым практикой, когда prototype докажет:

- backup из VSS snapshot;
- restore после удаления Fortiq catalog;
- unlock через минимум два независимых envelope;
- structural check и выборочный data check;
- корректную отмену и recovery после прерванного процесса;
- append-only backup identity и отдельный maintenance flow;
- восстановление через минимальный `fortiq-recover` на чистой машине.

Если два критерия не выполняются без недокументированных обходов, решение пересматривается.

## Источники

- [restic: подготовка репозитория и способы передачи password](https://restic.readthedocs.io/en/stable/030_preparing_a_new_repo.html)
- [restic: integrity checks и repository operations](https://restic.readthedocs.io/en/stable/045_working_with_repos.html)
- [restic: scripting, exit codes и JSON limitations](https://restic.readthedocs.io/en/stable/075_scripting.html)
- [restic: repository format](https://restic.readthedocs.io/en/stable/design.html)
- [restic source и BSD-2-Clause license](https://github.com/restic/restic)
- [Kopia features и server/API positioning](https://kopia.io/docs/features/)
- [Kopia repository server](https://kopia.io/docs/repository-server/)
- [Kopia Object Lock maintenance options](https://kopia.io/docs/reference/command-line/advanced/maintenance-set/)
