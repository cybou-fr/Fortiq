# Сценарий автономного disaster recovery

## DR-001: полная потеря устройства и control-plane

Это канонический acceptance scenario Fortiq V1.

### Предусловия

- исходный компьютер уничтожен или недоступен;
- TPM и локальная Fortiq catalog DB потеряны;
- Fortiq Service и аккаунт производителя недоступны;
- сохранились backup repository и recovery kit;
- оператор располагает recovery secret/passphrase либо разрешённым KMS path;
- доступен совместимый Windows/Linux/macOS компьютер.

### Ожидаемый результат

Оператор восстанавливает выбранные данные с помощью open-source `fortiq-recover`, не
обращаясь к Fortiq control-plane и не создавая аккаунт.

## Последовательность

```text
Operator       fortiq-recover       Storage       Key Provider       restic
   │                  │                  │                │               │
   │ inspect kit      │                  │                │               │
   ├─────────────────►│                  │                │               │
   │                  │ validate schema/checksum          │               │
   │                  ├─────────────────►│ read config    │               │
   │                  │◄─────────────────┤                │               │
   │ review identity  │                  │                │               │
   │◄─────────────────┤                  │                │               │
   │ unlock method    │                  │                │               │
   ├─────────────────►│ unwrap EUS       │                │               │
   │                  ├──────────────────────────────────►│               │
   │                  │◄──────────────────────────────────┤               │
   │                  │ launch pinned/compatible restic + password helper │
   │                  ├──────────────────────────────────────────────────►│
   │                  │                  │◄──── authenticated reads ──────┤
   │ choose snapshot  │◄──────────────────────────────────────────────────┤
   ├─────────────────►│                  │                │               │
   │ confirm target   │                  │                │               │
   ├─────────────────►│ restore to empty staging directory               │
   │                  ├──────────────────────────────────────────────────►│
   │                  │◄──────── progress + verified result ──────────────┤
   │ final report     │                  │                │               │
   │◄─────────────────┤ zeroize lease    │                │               │
```

## Recovery kit

Recovery kit состоит из публичной manifest-части и одного или нескольких envelopes.

### Публичная manifest-часть

```json
{
  "schema": "fortiq.recovery-kit",
  "schemaVersion": 1,
  "repositoryId": "engine-defined-or-fortiq-id",
  "engine": {
    "name": "restic",
    "repositoryFormat": 2,
    "minimumCompatibleVersion": "TO_BE_PINNED"
  },
  "locations": [],
  "unlockMethods": [],
  "createdAt": "RFC3339 timestamp",
  "integrity": {
    "algorithm": "TO_BE_DECIDED",
    "value": "..."
  }
}
```

Manifest не содержит plaintext credentials. Поля `TO_BE_PINNED/DECIDED` запрещено
оставлять при выпуске production kit.

### Envelopes

Каждый envelope содержит:

- уникальный envelope ID и type;
- cryptographic suite и version;
- KDF parameters/salt, если применимо;
- key context, связанный с repository ID;
- wrapped Engine Unlock Secret;
- authenticated metadata;
- дату создания и необязательную дату отзыва.

Точный binary encoding, algorithms и параметры определяются cryptographic ADR. До этого
формат не считается стабильным.

## Команды recovery tool

```text
fortiq-recover inspect <kit-or-repository>
fortiq-recover unlock <kit> --method recovery|kms
fortiq-recover snapshots <repository>
fortiq-recover verify <repository> --mode metadata|sample|full
fortiq-recover restore <snapshot> --target <empty-directory>
```

Секрет не допускается в аргументах командной строки. CLI запрашивает его через
защищённый интерактивный ввод либо platform credential UI.

## Проверки до восстановления

- schema и версия поддерживаются;
- manifest не содержит неизвестных обязательных полей;
- repository identity соответствует recovery kit;
- выбранный restic binary имеет разрешённую версию и проверенный hash/signature;
- target directory пуст и не совпадает с repository/cache/source;
- доступного пространства достаточно либо оператор явно принимает предупреждение;
- overwrite выключен по умолчанию;
- symbolic links/reparse points не позволяют выйти за пределы target;
- restore не запускает содержимое.

## Результаты и коды завершения

- `0`: операция завершена и verification policy выполнена;
- отдельный код: восстановлено частично;
- отдельный код: неверный recovery secret;
- отдельный код: repository/kit identity mismatch;
- отдельный код: неподдерживаемая версия;
- отдельный код: нарушение безопасного target path;
- отдельный код: integrity failure.

Точные численные значения фиксируются при проектировании CLI и не копируют неустойчивые
внутренние коды внешнего engine.

## Acceptance test

Тест выполняется на чистой VM без Fortiq Desktop и Service:

1. VM получает только release recovery tool, repository locator и recovery kit.
2. Локальная catalog DB намеренно отсутствует.
3. Оператор разблокирует EUS независимым recovery method.
4. Tool перечисляет snapshots и восстанавливает заранее неизвестную тестирующему выборку.
5. Хеши и обязательная filesystem metadata сверяются с signed test manifest.
6. Network capture подтверждает отсутствие обращений к Fortiq infrastructure.
7. После завершения проверяется отсутствие plaintext secrets в logs, arguments и files.

V1 не может называться sovereign recovery product, пока DR-001 не проходит автоматически.

