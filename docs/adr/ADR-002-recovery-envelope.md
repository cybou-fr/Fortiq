# ADR-002: recovery envelope и key derivation

- Статус: **Accepted as design baseline; external review required**
- Дата: **3 сентября 2026**
- Область: Engine Unlock Secret, recovery kit, password, mnemonic, TPM и KMS

## Контекст

Restic управляет внутренним master key репозитория. Fortiq должна безопасно хранить или
воспроизводить пароль, который открывает restic key entry. В Fortiq он называется
**Engine Unlock Secret (EUS)**.

Нужно поддержать несколько независимых unlock methods без создания разных данных и без
привязки ежедневной работы к recovery secret.

## Решение

При создании репозитория Fortiq генерирует CSPRNG-значение `EUS` длиной 256 бит. Оно не
создаётся из пользовательского пароля и не является Repository ID.

Для передачи restic используется `EnginePasswordV1 = Base64UrlNoPadding(EUS)`. Алфавит,
отсутствие padding и ASCII encoding являются частью versioned protocol. Сырые байты EUS
никогда не пишутся в stdout: они могут содержать newline или другие неоднозначные байты.

Один EUS защищается несколькими независимыми envelopes:

```text
EUS (256 random bits)
├── PasswordEnvelopeV1
├── Bip39RecoveryEnvelopeV1
├── WindowsTpmEnvelopeV1
└── EnterpriseKmsEnvelopeV1
```

Включение или удаление unlock method не требует перешифрования backup data.

## Общий формат envelope

Envelope V1 сериализуется deterministic CBOR по RFC 8949. Parser принимает только
определённые типы и размеры, отклоняет duplicate keys, indefinite-length values и
неизвестные critical fields.

Логическая схема:

```text
EnvelopeV1 {
  schema:              "fortiq.key-envelope",
  version:             1,
  envelopeId:          16 random bytes,
  repositoryId:        32 bytes,
  engineId:            "restic",
  purpose:             "engine-unlock-secret",
  providerType:        password | bip39 | windows-tpm | enterprise-kms,
  suite:               algorithm suite identifier,
  providerParameters:  bounded map,
  wrappedSecret:       bounded byte string,
  createdAt:           integer Unix time,
  critical:            array of understood field identifiers
}
```

`repositoryId`, `engineId`, `purpose`, `providerType`, `suite`, `version` и `envelopeId`
криптографически привязываются как authenticated context. Envelope другого репозитория
не должен успешно открываться.

Размер полного envelope ограничивается. Конкретный лимит фиксируется реализацией после
учёта максимального opaque KMS ciphertext и покрывается fuzz tests.

## Domain separation

Для HKDF-SHA-256 используется UTF-8 context без локализованного текста:

```text
fortiq/v1/<provider-type>/<purpose>/<repository-id>/<envelope-id>
```

Компоненты кодируются однозначно: binary identifiers имеют фиксированную длину, а
строковые identifiers берутся из закрытого ASCII enum. Простая конкатенация
произвольных пользовательских строк запрещена.

HKDF не используется непосредственно для слабого пользовательского пароля: сначала
применяется password KDF.

## PasswordEnvelopeV1

```text
password
  → explicit UTF-8 normalization policy
  → Argon2id(password, unique salt, stored parameters)
  → HKDF-SHA-256(context)
  → KEK
  → AEAD-wrap(EUS, authenticated context)
```

Baseline:

- Argon2id version 1.3 (`0x13`);
- salt: 128 random bits;
- derived input key material: 256 bits;
- минимальный совместимый профиль: `m=64 MiB, t=3, p=4`;
- preferred profile калибруется на устройстве выше минимума с заданным latency budget;
- фактические параметры сохраняются в envelope и никогда не уменьшаются автоматически;
- output encryption: AES-256-GCM, 96-bit nonce, 128-bit authentication tag;
- nonce генерируется CSPRNG и никогда не повторяется для одного KEK.

64 MiB / 3 passes — второй рекомендуемый профиль RFC 9106 для memory-constrained
окружений, а не целевой максимум Fortiq. Для новых desktop envelopes следует выбирать
максимально доступный профиль, не нарушающий установленный UX/SLA budget.

Fortiq не изменяет Unicode password молча между версиями. Политика преобразования и её
версия сохраняются. Password никогда не записывается в лог и не используется как EUS.

## Bip39RecoveryEnvelopeV1

BIP-39 используется только как человекочитаемое кодирование recovery entropy.

- 24 слова / 256 бит entropy — значение по умолчанию;
- 12 слов / 128 бит MAY быть разрешено policy для совместимости;
- checksum и словарь проверяются до попытки unwrap;
- применяется нормализация NFKD, определённая BIP-39;
- BIP-39 seed получается стандартным PBKDF2-HMAC-SHA512 процессом BIP-39;
- из seed через HKDF-SHA-256 и Fortiq context выводится отдельный KEK;
- KEK открывает EUS через AES-256-GCM;
- optional BIP-39 passphrase не хранится в recovery kit.

PBKDF2 iteration count BIP-39 не заменяется, иначе теряется совместимость стандарта.
Устойчивость обеспечивается случайной mnemonic entropy; пользовательская passphrase
рассматривается как дополнительный фактор, а не как замена качественной mnemonic.

Каждая syntactically valid passphrase создаёт seed, поэтому только успешная AEAD
authentication подтверждает правильность recovery material.

## WindowsTpmEnvelopeV1

- Создаётся неэкспортируемый ключ через Microsoft Platform Crypto Provider/CNG.
- Envelope хранит provider/key reference, public key fingerprint, algorithm identifiers
  и opaque wrapped material, но не private key.
- Доступ разрешается конкретной service identity с минимальными ACL.
- PCR binding не включается по умолчанию: изменение firmware/boot state не должно
  неожиданно уничтожать ежедневный unlock path.
- Windows Hello/PIN или PCR policy могут быть отдельными усиленными профилями.
- TPM envelope никогда не считается единственным recovery path.

DPAPI-NG является отдельным provider profile, а не синонимом TPM. Его principal-based
protection и domain recovery semantics документируются отдельным ADR при добавлении.

## EnterpriseKmsEnvelopeV1

KMS выполняет wrap/unwrap EUS или provider-supported data-key operation. Возвращённый
ciphertext рассматривается как opaque value.

Envelope хранит:

- provider type и endpoint identity без embedded credentials;
- immutable key identifier/version, а не только mutable alias;
- opaque ciphertext;
- provider algorithm/version;
- authenticated repository context, если provider поддерживает encryption context;
- policy reference для audit/approval.

KMS credentials хранятся отдельно от recovery kit. Plaintext EUS не кэшируется на диск.
При недоступности KMS применяется заданная repository policy; скрытый fail-open запрещён.

## Key lifetime

- EUS и KEK представлены `IKeyLease`, а не обычным возвращаемым `byte[]` в публичном API.
- Lease ограничен одной операцией или короткой сессией.
- Буферы очищаются при Dispose в пределах гарантий используемого runtime/library.
- Crash dump policy процесса Key Manager должна исключать секретные страницы, насколько
  это поддерживается ОС; полная защита от admin/kernel compromise не обещается.
- Секреты не проходят через UI process, telemetry, exception messages или shell.

## Ротация

### Ротация wrapper

1. Создать новый envelope вокруг того же EUS.
2. Проверить unlock и test restore новым методом.
3. Атомарно активировать новый envelope manifest.
4. Отозвать старый метод согласно grace period.

### Ротация EUS

1. Сгенерировать новый EUS.
2. Добавить в restic новую key entry, не удаляя старую.
3. Создать новые Fortiq envelopes.
4. Выполнить clean-machine restore test.
5. Активировать новый generation.
6. После grace period удалить старую restic key entry и старые envelopes.

Ротация EUS не означает перешифрование всех repository packs: внутренний restic master
key остаётся тем же. Full re-key/re-encryption является отдельной миграцией.

## Отзыв и quorum

Envelope manifest содержит поколения и статус `active`, `retiring`, `revoked`. Удаление
локального файла envelope не доказывает отзыв копии, ранее экспортированной пользователем.

Схема `2-of-3` не входит в V1. Если она будет добавлена, применяется проверенная threshold
secret-sharing реализация; разделение ciphertext-файла вручную не считается quorum.

## Ошибки и oracle resistance

Внешнему вызывающему коду возвращается единая ошибка `UnlockFailed` для неверного
password/passphrase и AEAD failure. Детальная причина доступна только в защищённой
локальной диагностике без раскрытия key material. Попытки интерактивного unlock
ограничиваются rate limit/backoff, но offline envelope всё равно предполагает offline
guessing threat — поэтому критичны Argon2id и сильный password.

## Verification requirements

До production обязательны:

- независимый cryptographic design review;
- test vectors для каждого suite и context;
- cross-platform round-trip для password и BIP-39 envelopes;
- negative tests: modified field, nonce, ciphertext, tag и repository ID;
- fuzzing CBOR parser с allocation/depth limits;
- forced-crash tests на каждом шаге rotation;
- clean-machine recovery с опубликованным recovery tool;
- TPM tests после firmware/Windows update и смены service account;
- KMS tests для rotation, disabled key, timeout и audit event.

## Не решено этим ADR

- точная библиотека Argon2 и её supply-chain review;
- окончательный UX latency budget и preferred memory profile;
- TPM RSA/ECC wrapping construction и attestation policy;
- конкретные Vault/OVH/Azure KMS mappings;
- подпись всего recovery kit и trust anchor обновлений;
- threshold recovery.

Эти пункты не допускается заполнять произвольной реализацией без следующего ADR/review.

## Источники

- [RFC 9106: Argon2](https://www.rfc-editor.org/rfc/rfc9106.html)
- [RFC 5869: HKDF](https://www.rfc-editor.org/rfc/rfc5869.html)
- [RFC 8949: CBOR и deterministic encoding](https://www.rfc-editor.org/rfc/rfc8949.html)
- [BIP-39 specification](https://github.com/bitcoin/bips/blob/master/bip-0039.mediawiki)
- [Microsoft: CNG Key Storage Providers](https://learn.microsoft.com/en-us/windows/win32/seccertenroll/cng-key-storage-providers)
- [Microsoft: использование TPM в Windows](https://learn.microsoft.com/en-us/windows/security/hardware-security/tpm/how-windows-uses-the-tpm)
- [Microsoft: DPAPI-NG](https://learn.microsoft.com/en-us/windows/win32/seccng/cng-dpapi)
