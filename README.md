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

Pinned metadata restic хранится в `engines/manifest.json`. Сам бинарник не входит в
исходный репозиторий: до появления проверенного acquisition step его нельзя подменять
глобально установленной версией.

Version-pinned fixtures команд `version`, `init`, `backup`, `snapshots`, `check` и
`restore` находятся в `test-assets/restic-output/0.19.1`. Parser требует согласованности
exit code и обязательного terminal event: один progress или summary не создаёт успешный
receipt.

Текущий `ResticRepositoryEngine` имеет internal visibility и использует
`--insecure-no-password` только как P0 test seam. Этот режим нельзя экспортировать в
Service или recovery CLI. Production activation требует одноразовый password helper и
проверяемую подпись всех Windows EXE/DLL согласно supply-chain policy.

Test-only key lease хранит собственную копию EUS, отзывает доступ и обнуляет доступный
буфер при `Dispose`. `EnginePasswordV1` кодируется напрямую в предоставленный mutable
буфер без создания immutable secret-string.

P0 password helper использует одноразовый `CurrentUserOnly` Named Pipe, получает через
аргументы только operation ID и пишет password с единственным завершающим newline.
Challenge-response проверяет целостность protocol round-trip, но не заменяет обязательную
P1-проверку client PID/service identity и installer-defined SDDL.

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
