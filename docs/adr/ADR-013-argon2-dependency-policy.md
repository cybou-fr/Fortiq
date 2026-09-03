# ADR-013: Argon2id dependency и криптографическая dependency policy

- Статус: **Proposed; implementation blocked until review gates pass**
- Дата: **3 сентября 2026**
- Область: `PasswordEnvelopeV1`, .NET runtime, supply chain

## Контекст

ADR-002 требует Argon2id v1.3, но намеренно не выбирает реализацию. Выбор нельзя делать
только по популярности NuGet-пакета: Fortiq должен проверить корректность параметров,
происхождение native binaries, воспроизводимость восстановления и способность очищать
секретные буферы.

P0 использует simulated envelope и не зависит от Argon2. Реальный
`PasswordEnvelopeV1` относится к P1 и не может попасть в release build до выполнения
gate из этого ADR.

## Рассмотренные варианты

### `Sodium.Core` / libsodium

Преимущества:

- Argon2id реализован широко используемым native crypto runtime;
- пакет поставляет platform-specific libsodium и имеет активные релизы;
- native implementation уменьшает риск собственной реализации примитива.

Ограничения:

- high-level `crypto_pwhash` принимает memory/op limits, но не предоставляет Fortiq
  явный контроль всех параметров Argon2 envelope, включая `p`;
- wrapper и вложенные native assets образуют отдельную supply-chain поверхность;
- необходимо доказать одинаковую поддержку Windows x64/ARM64 и автономного recovery tool.

### `Konscious.Security.Cryptography.Argon2`

Преимущества:

- managed .NET API предоставляет явные memory, iteration и parallelism параметры;
- проще упаковать в self-contained recovery binary.

Ограничения:

- наличие удобного API не является независимым security audit;
- требуется отдельная проверка constant-time свойств, memory clearing, maintenance и
  соответствия официальным Argon2id vectors;
- managed runtime не гарантирует полного контроля над копиями секретов в памяти.

### Собственная реализация или малоизвестный fork

Отклонено. Fortiq не реализует Argon2 самостоятельно и не принимает fork только ради
удобного API без provenance, review и долгосрочной maintenance-модели.

## Решение

1. **P0 остаётся на simulated envelope.** Он не содержит production Argon2 dependency и
   маркируется как test-only на уровне assembly/package policy.
2. **Основной кандидат P1 — `Sodium.Core` поверх pinned libsodium**, но только если
   prototype подтвердит, что сериализованный suite честно и однозначно описывает
   фактические параметры примитива.
3. Если libsodium API не позволяет выполнить параметрический контракт ADR-002 без
   двусмысленности, команда не маскирует расхождение: либо вводится новый versioned suite
   с параметрами libsodium, либо после review выбирается managed candidate. ADR-002 в
   таком случае обновляется до реализации.
4. Точная версия пакета и native library фиксируется только в central package management
   и lock file после прохождения spike. Диапазоны версий и floating versions запрещены.
5. `PasswordEnvelopeV1` не считается production-ready, пока статус ADR не изменён на
   Accepted и все release gates ниже не автоматизированы.

Это решение намеренно отделяет выбор кандидата от заявления об аудированности. Fortiq не
называет wrapper или итоговую композицию audited без ссылки на audit нужной версии и
покрытого scope.

## Release gates

- сверка Argon2id v1.3 с официальными и RFC 9106 test vectors;
- negative и boundary tests для salt, output length, memory, iterations и parallelism;
- минимум Windows x64 и ARM64 packaging test для Service и `fortiq-recover`;
- проверка SBOM, NuGet signature/provenance, package hash и состава native assets;
- запрет unexpected native library resolution вне application directory;
- vulnerability/license scan с зафиксированным результатом;
- benchmark на минимально поддерживаемом устройстве и проверка memory-pressure failure;
- review поведения cancellation, exception paths и очистки доступных secret buffers;
- cross-implementation vector: созданный envelope открывается независимым verifier;
- независимый cryptographic design/dependency review.

## Dependency policy для security-critical пакетов

- версии задаются централизованно и восстанавливаются через lock file в locked mode;
- package sources перечисляются явно; dependency confusion предотвращается source mapping;
- restore и build разделены; CI build не выполняет произвольный network restore;
- обновление создаёт отдельный change с SBOM diff, advisory review и повтором vectors;
- critical package нельзя обновлять автоматически только потому, что версия новее;
- hashes опубликованных recovery artifacts и dependency manifest входят в release evidence;
- recovery tool хранит suite/version metadata и выдаёт понятную ошибку для неизвестного
  suite, не пытаясь угадать параметры.

## Последствия

- реализация Restic P0 может начаться без ожидания криптографического review;
- реальный password envelope остаётся явно заблокированным gate, а не скрытой TODO;
- возможное изменение suite после spike потребует нового ADR/update ADR-002, но не
  несовместимого тихого изменения существующего envelope.

## Источники

- [RFC 9106: Argon2 Memory-Hard Function](https://www.rfc-editor.org/rfc/rfc9106.html)
- [libsodium password hashing API](https://doc.libsodium.org/password_hashing/default_phf)
- [Sodium.Core в NuGet](https://www.nuget.org/packages/Sodium.Core/)
- [Argon2 reference implementation](https://github.com/P-H-C/phc-winner-argon2)

