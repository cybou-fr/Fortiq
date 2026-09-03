# Fortiq Intelligence и Phi Silica

## Назначение

Fortiq Intelligence — опциональный локальный AI-контур. На совместимых Windows 11
устройствах предпочтительным provider является Microsoft Phi Silica через Windows AI
APIs. Доступность определяется во время выполнения; продукт остаётся полностью рабочим
без AI.

## Разрешённые сценарии

- объяснение ошибок backup и результатов restore-test;
- резюме изменений между snapshots;
- объяснение ransomware/anomaly signals;
- преобразование естественного запроса в предложение поиска или восстановления;
- локальная классификация документов и поиск чувствительных категорий;
- черновик retention/sovereignty policy для проверки администратором.

## Запрещённые полномочия

AI не может:

- получать DEK, RMK, mnemonic, пароли и KMS credentials;
- самостоятельно запускать restore или backup deletion;
- изменять retention, key policy либо audit records;
- выполнять команды, найденные в именах или содержимом файлов;
- обращаться к repository engine или privileged broker напрямую.

## Поток команды

```text
User request
  → prompt/data minimization
  → local AI proposal
  → strict schema parser
  → deterministic validation
  → policy evaluation
  → human confirmation when required
  → execution by Fortiq Service
```

Свободный текст модели никогда не исполняется. Идентификаторы snapshot и repository
разрешаются заново из доверенного catalog. Пути нормализуются и проверяются.

## Privacy modes

- `Disabled`: AI полностью отключён.
- `MetadataOnly`: используются только агрегаты и техническая metadata.
- `LocalContent`: разрешён локальный анализ содержимого по явной policy.
- Cloud AI не является неявным fallback и требует отдельного будущего provider/policy.

## Provider abstraction

```csharp
public interface ILocalAiProvider
{
    Task<AiAvailability> GetAvailabilityAsync(CancellationToken cancellationToken);
    Task<BackupInsight> ExplainAsync(
        BackupResultSummary summary,
        CancellationToken cancellationToken);
    Task<RecoveryProposal> ProposeRecoveryAsync(
        SanitizedRecoveryRequest request,
        CancellationToken cancellationToken);
}
```

Windows-specific Phi Silica adapter изолирован от Avalonia UI и основного service. Для
других устройств может использоваться локальный provider на базе Foundry Local/ONNX,
но его установка не является обязательной.

## Ограничения платформы

Phi Silica должна рассматриваться как capability, а не системное требование: поддержка
зависит от версии Windows, Windows App SDK, hardware и доступности модели. До выпуска
каждой версии Fortiq матрица совместимости проверяется по актуальной документации
Microsoft и automated capability tests.

