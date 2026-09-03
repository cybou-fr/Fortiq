# ADR-008: TUF-модель доверия к релизам

- Статус: **Accepted architecture; implementation dependency pending**
- Дата: **3 сентября 2026**
- Область: online/offline updates и release trust

## Контекст

TLS и Authenticode недостаточны для полного update threat model. TLS endpoint может быть
скомпрометирован, а ранее корректно подписанный уязвимый binary остаётся корректно
подписанным. Один online signing key создаёт единую точку компрометации.

## Решение

Использовать TUF security model для update metadata вместе с обязательным Authenticode
для Windows executables/installers.

- Root trust поставляется с installer/recovery media.
- Root role использует offline threshold keys.
- Targets, Snapshot и Timestamp разделены.
- Client проверяет signatures, expiry, versions, hashes, lengths и consistent release set.
- Rollback выпускается как новый target с возрастающим release sequence.
- Offline bundle сохраняет signature/sequence verification.
- Update Agent не имеет доступа к backup secrets.

Реализация не должна представлять собой частичную самописную имитацию TUF. До начала
online updater выбирается поддерживаемая библиотека или изолированный reference-compatible
client после security review и interoperability tests.

## Authenticode как второй независимый gate

Windows target принимается только если одновременно:

1. target разрешён доверенным update metadata;
2. size/hash совпадают;
3. Authenticode signature и RFC 3161 timestamp проходят policy;
4. publisher identity совпадает с release manifest;
5. release compatibility checks успешны.

Один успешный gate не компенсирует другой.

## Repository safety

Application update и repository-format migration разделены. Updater не получает EUS и не
открывает repository. Миграция требует отдельного approved operation, integrity check,
compatible recovery tool и immutable rollback recovery point.

## Последствия

Положительные:

- защита от rollback, freeze, mix-and-match и malicious mirror;
- разделение online/offline signing roles;
- единая модель online и offline release trust;
- engine/helper binaries входят в общий signed release set.

Отрицательные:

- сложнее key ceremony, metadata publication и expiry operations;
- offline environments требуют процесса обновления root/targets metadata;
- нужен зрелый TUF-compatible client для .NET/Windows;
- потеря operational timestamp publication может блокировать новые online updates, хотя
  уже установленный продукт и recovery продолжают работать.

## Источники

- [The Update Framework specification](https://theupdateframework.github.io/specification/)
- [Microsoft: Authenticode timestamping](https://learn.microsoft.com/en-us/windows/win32/seccrypto/time-stamping-authenticode-signatures)
- [Microsoft: NuGet Package Source Mapping](https://learn.microsoft.com/en-us/nuget/consume-packages/package-source-mapping)
- [Microsoft: NuGet dependency resolution and lock files](https://learn.microsoft.com/en-us/nuget/concepts/dependency-resolution)
- [SPDX specifications](https://spdx.dev/use/specifications/)
- [SLSA specification](https://slsa.dev/spec/)

