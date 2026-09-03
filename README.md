# Fortiq

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
milestone — golden-output parsers и `ResticRepositoryEngine` local adapter из
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
