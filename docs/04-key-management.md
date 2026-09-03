# Управление ключами и восстановление доступа

## Модель

Способы unlock не являются взаимоисключающими режимами. Один Repository Master Key
может иметь несколько key envelopes.

```text
Repository Master Key (RMK)
├── TPM/device envelope
├── password-derived envelope
├── recovery-secret envelope
└── Enterprise KMS/HSM envelope

RMK + versioned KDF context
├── metadata encryption key
├── content/engine credential
├── audit integrity key
└── future purpose-specific keys
```

## Правила derivation

- Все derivation domains имеют стабильные строковые context labels.
- Repository ID и версия схемы входят в context.
- Password protection использует memory-hard KDF с сохранёнными параметрами и salt.
- BIP-39, если выбран, кодирует recovery entropy, но не определяет всю key hierarchy.
- Из BIP-39 seed ключи выводятся отдельным versioned KDF с domain separation.
- Миграция алгоритма создаёт новый envelope и не уничтожает старый до проверки restore.

Baseline алгоритмов, формат context и правила миграции определены в
[ADR-002](adr/ADR-002-recovery-envelope.md). До production реализация проходит независимый
cryptographic review.

## Предлагаемые интерфейсы

```csharp
public interface IDataKeyProvider
{
    Task<IKeyLease> AcquireAsync(
        RepositoryIdentity repository,
        KeyPurpose purpose,
        CancellationToken cancellationToken);
}

public interface IKeyEnvelopeProvider
{
    Task<KeyEnvelope> WrapAsync(
        KeyReference wrappingKey,
        ReadOnlyMemory<byte> plaintextKey,
        KeyContext context,
        CancellationToken cancellationToken);

    Task<IKeyLease> UnwrapAsync(
        KeyEnvelope envelope,
        KeyContext context,
        CancellationToken cancellationToken);
}
```

`IKeyLease` имеет ограниченный lifetime и уничтожает доступное key material при Dispose.
Ключи не логируются и не передаются через command line или environment variables.

## Recovery invariant

Для каждого активного репозитория Fortiq ДОЛЖНА доказать хотя бы один независимый путь
восстановления. Wizard не завершается, пока пользователь не подтвердил recovery material
или администратор явно не принял policy exception.

Recovery kit содержит только необходимую открытую metadata:

- repository locator и ID;
- версия формата и derivation scheme;
- список поддерживаемых unlock methods;
- checksum/QR для обнаружения ошибок;
- инструкцию для `fortiq-recover`.

Секретная часть хранится отдельно либо защищается дополнительной passphrase.
