# Fortiq

<p align="center">
  <img src="assets/icon.png" alt="Fortiq Logo" width="128" height="128" />
</p>

Fortiq — sovereign recovery platform для организаций, которым нужны проверяемые,
зашифрованные и устойчивые к уничтожению резервные копии под собственным контролем.

Главное обещание продукта:

> Данные можно восстановить без зависимости от облака Fortiq, аккаунта Fortiq или
> существования компании Fortiq.

Проект находится в стадии разработки и не является заявлением о готовности функций или об
автоматической сертификации по GDPR, NIS2 либо ISO 27001. Проектные документы и ADR ведутся вне
этого репозитория, поэтому README ссылается на них по имени, а не ссылкой.

Что работает сейчас на .NET 10 LTS: pinned restic-движок с проверкой бинарника, backup, snapshots,
check, restore и reconciliation; envelope-формат ADR-002 с BIP-39 и TPM-методами; recovery kit;
автономный `Fortiq.Recover`; operation receipts. Сценарий восстановления E2E-001 проходит целиком —
отдельный процесс восстанавливает датасет после удаления локального состояния Fortiq.

Чего нет: VSS, планировщика, Windows Service, S3 и Object Lock, GUI. Production
`PasswordEnvelopeV1` заблокирован review gates ADR-013 по выбору Argon2-зависимости — отдельно от
уже реализованных методов, которые обходятся платформенной криптографией.

## Сборка и тесты

Требуется .NET SDK версии, заданной в `global.json`. Из корня проекта:

```powershell
dotnet test Fortiq.sln --configuration Release
```

Сборка использует nullable reference types, рекомендованные анализаторы и рассматривает
все предупреждения как ошибки.

Pinned metadata restic хранится в `engines/manifest.json`. Сам бинарник не входит в исходный
репозиторий; его получает `scripts/Get-Engine.ps1`, который проверяет SHA-256 архива до распаковки,
а затем длину и SHA-256 самого бинарника. Несовпадение — фатально, ничего не остаётся на диске.
Глобально установленной версией restic подменять его нельзя.

```powershell
./scripts/Get-Engine.ps1
```

Проверенный бинарник остаётся тем же бинарником: verifier считает хеш через дескриптор, который
держит открытым с `FileShare.Read` (файл нельзя перезаписать, удалить или переименовать, пока движок
используется), а непосредственно перед запуском сверяет идентичность файла по пути (volume serial +
file index). Это закрывает TOCTOU между verify и execution, включая случай, когда каталог выше
бинарника подменён через junction.

Что обеспечено кодом и что остаётся открытым gate — в [SECURITY.md](SECURITY.md).

CI (`.github/workflows/ci.yml`) выполняет тот же acquisition step на windows-latest, поэтому
интеграционные тесты в CI выполняются, а не пропускаются, и падают, если engine не совпал с
манифестом. Receipts прогона и результаты тестов выгружаются артефактом `fortiq-evidence` в том
числе при провале.

Version-pinned fixtures команд `version`, `init`, `backup`, `snapshots`, `check` и
`restore` находятся в `test-assets/restic-output/0.19.1`. Parser требует согласованности
exit code и обязательного terminal event: один progress или summary не создаёт успешный
receipt.

`ResticRepositoryEngine` имеет internal visibility и получает credential через порт
`IEngineCredentialProvider`. Рабочий путь — одноразовый `--password-command`, который запускает
password helper: в командной строке присутствуют только pinned путь helper и несекретный operation
ID. `--insecure-no-password` остался отдельной internal-реализацией порта и является исключительно
тестовым seam; наружу — ни в Service, ни в recovery CLI — он не выходит. Проверяемая подпись всех
Windows EXE/DLL согласно supply-chain policy остаётся требованием production activation.

Неправильный secret даёт единый `UnlockFailedException` с константным сообщением для всех операций:
отличить неверный secret от отсутствующего ключа по ошибке нельзя, и metadata снапшотов не
раскрывается.

Key lease хранит собственную копию EUS, отзывает доступ и обнуляет буфер при `Dispose`; распакованный
секрет покидает assembly только лизом, сырых массивов публичный API не отдаёт. `EnginePasswordV1`
кодируется напрямую в предоставленный mutable-буфер, без создания immutable secret-строки.

Password broker выдаёт engine password ровно один раз и только одному процессу. До записи пароля
брокер резолвит процесс подключившегося клиента, требует, чтобы его образ был **тем самым** файлом
helper, который брокер держит открытым, и чтобы он выполнялся под ожидаемым аккаунтом. Проверка
процесса выполняется в момент подключения, до выдачи challenge; проверка аккаунта — после первого
чтения (Windows разрешает impersonation только после него) и всё равно до записи любой части пароля.
Installer может задать SDDL канала; без него ОС ограничивает канал текущим пользователем. Оставшиеся
ограничения перечислены в [SECURITY.md](SECURITY.md).

E2E-001 выполняется на pinned restic: dataset builder создаёт пустой, текстовый, бинарный,
Unicode-, длинный и read-only файлы, backup и restore выполняются в разных working directory,
а временное состояние Fortiq удаляется между ними. Тест пропускается, если pinned binary
отсутствует в `engines/`, и никогда не использует глобально установленный restic.

Engine получает не унаследованное окружение, а минимальный allowlist: `TEMP`/`TMP`
указывают на подкаталог рабочей директории Fortiq, `SystemRoot` берётся из процесса.
Restore одного источника использует subfolder-селектор restic (`<snapshot>:/C/...`),
поэтому содержимое попадает прямо в target и промежуточные каталоги исходного пути не
воссоздаются.

Negative-сценарии E2E-003 (повреждённый pack), E2E-004 (отмена backup, reconciliation через
`unlock` и последующий корректный snapshot) и E2E-005 (reparse point не выводит restore за пределы
staging directory) выполняются на том же pinned binary. E2E-002 (неправильный secret) выполняется на password helper.

Restore никогда не пишет прямо в target: движок восстанавливает в staging-каталог на том же томе,
проверяет получившееся дерево (reparse point, symlink или выход за пределы staging — отказ) и только
затем продвигает его одним rename. Отклонённый или прерванный restore оставляет target нетронутым, а
непустой target отклоняется до запуска движка.

Один `OperationId` проходит операцию насквозь: команда → процесс движка → operation ID в
`--password-command` helper → receipt → возвращённый результат. Если вызывающий его не задал, id
назначается один раз и именно он попадает во все три места.

Каждая операция движка пишет JSON-receipt схемы `fortiq.operation-receipt`: operation ID, repository
ID, идентичность engine с pinned SHA-256, время начала и конца, result, snapshot ID, source и метрики.
Failed и cancelled операции тоже оставляют receipt — с `engineResult` `failed`/`cancelled` и
диагностикой движка в `warnings`; `succeeded` не выдаётся авансом. `engineResult` описывает только
то, что сделал движок: отмена вызывающего уже после завершения движка не переписывает результат и не
отменяет запись evidence — она выполняется собственным токеном. Успешность записи evidence
фиксируется отдельно от результата движка и отдаётся через `IOperationEvidenceObserver`, поэтому
потерянный receipt виден, а не проглочен. Receipt не нужен для автономного
restore. Переменная окружения `FORTIQ_TEST_ARTIFACTS` заставляет тесты сохранить receipts прогона в
указанный каталог.

Реализован формат `KeyEnvelopeV1` из ADR-002: deterministic CBOR (RFC 8949), строгий декодер
(отклоняет дубликаты полей, indefinite-length, trailing data, неизвестные critical fields и
превышение лимитов) и AEAD-обёртка EUS с authenticated context из всех публичных полей envelope.
`RecoverySecretEnvelope` выводит KEK через HKDF-SHA-256 из 256-битной recovery entropy и
заворачивает EUS в AES-256-GCM. Он использует только платформенную криптографию, поэтому не
затрагивает gate ADR-013 на Argon2 — production `PasswordEnvelopeV1` по-прежнему заблокирован.
Неизвестный suite приводит к явной ошибке, а не к попытке угадать параметры; любой отказ unwrap —
единый `UnlockFailed`.

`Bip39RecoveryEnvelopeV1` реализован: мнемоника проверяется по словарю и checksum до любой
derivation, seed выводится стандартным PBKDF2-HMAC-SHA512 (2048 итераций, salt `mnemonic` +
опциональная passphrase), а KEK — отдельным HKDF с Fortiq-контекстом. Encode, decode и seed
сверяются со всеми официальными English-векторами BIP-39 (`test-assets/bip39/`). Английский словарь
встроен в assembly, чтобы recovery работал офлайн; его происхождение и normalized SHA-256
зафиксированы в `src/Fortiq.Infrastructure.Keys/Bip39/english.provenance.json` и проверяются тестом.

Версии пакетов заданы централизованно в `Directory.Packages.props`, как требует dependency policy
ADR-013; диапазоны и floating versions запрещены.

`Fortiq.Recover` работает поверх recovery kit и больше не fail-closed. Команды:

```powershell
Fortiq.Recover inspect   --repository <repo> --engine-root <engines> [--kit <kit dir>]
Fortiq.Recover snapshots --repository <repo> --engine-root <engines> --kit <kit dir>
Fortiq.Recover check     --repository <repo> --engine-root <engines> --kit <kit dir>
Fortiq.Recover restore   --repository <repo> --engine-root <engines> --kit <kit dir> `
                         --snapshot <id> --target <dir> [--source <original path>]
```

Stable source ID пишется внутрь репозитория движковыми тегами (`fortiq.v1` и
`fortiq.source=<id>`), а не только в receipt: восстановление на чистой машине узнаёт, что представляет
собой snapshot, не имея ни одного локального файла Fortiq. `snapshots` возвращает `source` (из
метаданных репозитория, `null` если их нет) и отдельно `path` — filesystem path движка идентичностью
не считается и ею не подменяется. Идентификатор ограничен ASCII-формой без запятых, потому что restic
режет значение `--tag` по запятой; недопустимый id отвергается до запуска движка.

Recovery kit — это каталог: манифест `kit.json` схемы `fortiq.recovery-kit` (repository ID и
локатор, идентичность движка, список unlock-методов с их SHA-256, инструкция) плюс файлы envelope.
Чтение кита проверяет схему, хеш каждого envelope, его декодирование и принадлежность тому же
репозиторию; расхождение манифеста с содержимым — отказ, а не выбор. Мнемоника в кит не пишется.

`WindowsTpmEnvelopeV1` реализован: ключ создаётся в TPM через Microsoft Platform Crypto Provider,
неэкспортируем (`ExportPolicy.None` проверяется после создания), envelope хранит ссылку на ключ,
отпечаток его публичной части и обёрнутый материал — приватный ключ в envelope не попадает. PCR
binding намеренно не используется, чтобы обновление firmware не уничтожало ежедневный unlock.
Отпечаток сверяется при открытии, поэтому другой ключ с тем же именем (переустановка, восстановленный
профиль) отвергается, а не пробуется. Device-путь никогда не единственный: `RecoveryKitStore`
отказывается писать кит, где TPM — единственный метод.

Репозиторий вместе с китом создаёт `RepositoryProvisioner` (`Fortiq.Provisioning`): он генерирует
EUS через CSPRNG, инициализирует репозиторий, заворачивает EUS в envelope и записывает кит **после**
создания репозитория. Мнемоника возвращается ровно один раз — это единственная копия, Fortiq не может
её воспроизвести.

Провижининг транзакционен вокруг одного инварианта: **нет восстановимого кита — нет и уцелевшего
инициализированного репозитория**. Перед успехом кит перечитывается с диска и им реально открывается
репозиторий (доказательство, а не допущение); любой сбой до этого момента откатывает репозиторий,
удаляет частичный кит и созданный TPM-ключ. Откат безопасен потому, что каталоги репозитория и кита
обязаны быть пустыми на входе — удаляется ровно то, что создал этот запуск, чужой репозиторий никогда
не «усыновляется». Убитый процесс откатить себя не может, поэтому до начала работ пишется
`provisioning-intent.json`: он блокирует повторный запуск в том же working directory, а
`RepositoryProvisioner.CleanUpInterruptedAsync` доводит уборку до конца.

`inspect` описывает kit по публичному заголовку envelope и никогда не запрашивает recovery material.
Остальные команды читают мнемонику **только из stdin**: аргументы процесса видны другим процессам и
попадают в историю оболочки и логи, поэтому `--password`, `--secret`, `--recovery-phrase` и
`--mnemonic` отвергаются парсером. Неверная мнемоника даёт единый `UnlockFailed` и exit code 77 без
раскрытия snapshot metadata; неизвестный suite envelope — явную ошибку вместо попытки угадать.

Инструмент зависит только от репозитория, pinned движка и kit: ни Fortiq service, ни локального
состояния, ни сети. Распакованный EUS живёт лишь в пределах команды, внутри лиза с зачисткой буфера,
и доходит до restic через одноразовый pipe helper — не через аргумент или environment.

E2E-001 теперь выполняется в полной форме: после удаления локального состояния Fortiq отдельный
процесс `Fortiq.Recover` восстанавливает датасет, имея только kit, и тест сверяет SHA-256 каждого
файла, а также что ни мнемоника, ни engine password не появились в выводе.

## Принципы

- **Recoverable:** успешный backup недостаточен — восстановление должно регулярно проверяться.
- **Sovereign:** клиент контролирует данные, ключи, размещение и сетевые зависимости.
- **Resilient:** компрометация endpoint не должна позволять уничтожить историю backup.
- **Deterministic core:** криптография, policy enforcement и restore не зависят от AI.
- **Portable recovery:** формат и recovery-инструмент должны пережить основной продукт.

## Документация

Проектные документы, план исполняемого прототипа и ADR ведутся вне репозитория. Здесь опубликовано
только то, что относится к коду: этот README и [SECURITY.md](SECURITY.md).

## Предварительный технологический профиль

- C# / .NET
- Avalonia UI
- Windows Service и минимально привилегированный Windows broker
- VSS и NTFS USN Journal
- один основной repository engine в первой версии
- S3-compatible Object Lock и локальные хранилища
- TPM, recovery secret и Enterprise KMS как независимые способы разблокировки
- Microsoft Phi Silica как опциональный on-device AI provider
