# Fortiq

<p align="center">
  <img src="assets/icon.png" alt="Fortiq Logo" width="128" height="128" />
</p>

Fortiq — sovereign recovery platform для организаций, которым нужны проверяемые,
зашифрованные и устойчивые к уничтожению резервные копии под собственным контролем.

Главное обещание продукта:

> Данные можно восстановить без зависимости от облака Fortiq, аккаунта Fortiq или
> существования компании Fortiq.

Проект находится на стадии архитектурного проектирования. Текущие документы описывают
целевую модель; они не являются заявлением о готовности функций или автоматической
сертификации по GDPR, NIS2 либо ISO 27001.

Исполняемый P0-каркас создан на .NET 10 LTS. Сейчас он содержит Domain value objects,
state machine backup job, application-порт repository engine и первые unit tests. Следующий
milestone — `Fortiq.Recover` CLI из
[плана исполняемого прототипа](docs/11-executable-prototype.md). Production Argon2
integration отдельно заблокирована review gates из
[ADR-013](docs/adr/ADR-013-argon2-dependency-policy.md).

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
P0 test seam; его нельзя экспортировать в Service или recovery CLI. Проверяемая подпись всех
Windows EXE/DLL согласно supply-chain policy остаётся требованием production activation.

Неправильный secret даёт единый `UnlockFailedException` с константным сообщением для всех операций:
отличить неверный secret от отсутствующего ключа по ошибке нельзя, и metadata снапшотов не
раскрывается.

Test-only key lease хранит собственную копию EUS, отзывает доступ и обнуляет доступный
буфер при `Dispose`. `EnginePasswordV1` кодируется напрямую в предоставленный mutable
буфер без создания immutable secret-string.

P0 password helper использует одноразовый `CurrentUserOnly` Named Pipe, получает через
аргументы только operation ID и пишет password с единственным завершающим newline.
Challenge-response проверяет целостность protocol round-trip, но не заменяет обязательную
P1-проверку client PID/service identity и installer-defined SDDL.

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

Каждая операция движка пишет JSON-receipt схемы `fortiq.operation-receipt`: operation ID, repository
ID, идентичность engine с pinned SHA-256, время начала и конца, result, snapshot ID, source и метрики.
Failed и cancelled операции тоже оставляют receipt — со статусом `failed`/`cancelled` и диагностикой
движка в `warnings`; статус `succeeded` не выдаётся авансом. Receipt не нужен для автономного
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

Версии пакетов заданы централизованно в `Directory.Packages.props`, как требует dependency policy
ADR-013; диапазоны и floating versions запрещены.

`Fortiq.Recover inspect` уже проверяет schema manifest, pinned restic binary и наличие
repository config, возвращая machine-readable JSON. CLI намеренно не принимает password,
secret или recovery phrase через command line; unlock-команды пока fail closed.

## Принципы

- **Recoverable:** успешный backup недостаточен — восстановление должно регулярно проверяться.
- **Sovereign:** клиент контролирует данные, ключи, размещение и сетевые зависимости.
- **Resilient:** компрометация endpoint не должна позволять уничтожить историю backup.
- **Deterministic core:** криптография, policy enforcement и restore не зависят от AI.
- **Portable recovery:** формат и recovery-инструмент должны пережить основной продукт.

## Документация

Начать с [индекса документации](docs/README.md).

## Предварительный технологический профиль

- C# / .NET
- Avalonia UI
- Windows Service и минимально привилегированный Windows broker
- VSS и NTFS USN Journal
- один основной repository engine в первой версии
- S3-compatible Object Lock и локальные хранилища
- TPM, recovery secret и Enterprise KMS как независимые способы разблокировки
- Microsoft Phi Silica как опциональный on-device AI provider
