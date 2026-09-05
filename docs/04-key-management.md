# Key Management & Access Recovery

> **Implementation status: Implemented.** BIP-39 and TPM envelopes, HKDF/AES-256-GCM wrapping and key leases are all in `Fortiq.Infrastructure.Keys`.


## Key Hierarchy Model

Unlock methods are not mutually exclusive operational modes. A single **Engine Unlock Secret (EUS)** / Repository Master Key (RMK) can be simultaneously protected by multiple cryptographic key envelopes:

```text
Engine Unlock Secret (EUS / RMK)
├── Windows TPM Envelope (src/Fortiq.Infrastructure.Keys/WindowsTpmEnvelope.cs)
├── BIP-39 Mnemonic Envelope (src/Fortiq.Infrastructure.Keys/Bip39RecoveryEnvelope.cs)
├── Direct Recovery Secret Envelope (src/Fortiq.Infrastructure.Keys/RecoverySecretEnvelope.cs)
└── (Future) Enterprise KMS/HSM Envelope (HashiCorp Vault Transit / AWS KMS)

EUS + Versioned KDF Context
├── Metadata encryption keys
├── Engine password command credentials (via ephemeral named pipe)
├── Audit integrity and receipt signing keys
└── Purpose-specific subkeys
```

---

## Cryptographic Derivation Rules

- **Strict Domain Separation**: Every key derivation operation MUST specify a stable, explicit context string (e.g. `fortiq/key-envelope/v1/eus`).
- **Context Binding**: Repository ID, suite identifier, and schema version are cryptographically bound into the authenticated data context.
- **Memory-Hard KDF for Passwords**: Password-derived envelopes MUST employ memory-hard key derivation (Argon2id) with unique per-repository salt.
- **BIP-39 Mnemonic Standards**: Standard English 2048-word dictionary (RFC 3986 / BIP-39), 256-bit recovery entropy, PBKDF2-HMAC-SHA512 (2048 iterations), followed by HKDF-SHA-256 for domain-separated subkey derivation.
- **Hardware Platform Security**: `WindowsTpmEnvelopeV1` generates non-exportable keys via the Microsoft Platform Crypto Provider (`ExportPolicy.None`), storing the public key SHA-256 fingerprint in the envelope to detect key substitutions.
- **Envelope Migration**: Algorithm upgrades create a supplementary envelope and do not invalidate existing envelopes until recovery verification passes.

Cryptographic baselines and wire formats are defined in [ADR-002](adr/ADR-002-recovery-envelope.md). Production Argon2 dependencies are governed by [ADR-013](adr/ADR-013-argon2-dependency-policy.md).

---

## Implemented Contracts

```csharp
public interface IKeyLease : IDisposable
{
    int Length { get; }

    void CopyTo(Span<byte> destination);
}

public interface IEngineCredentialSession : IAsyncDisposable
{
    IReadOnlyList<string> EngineArguments { get; }

    Task CompleteAsync(CancellationToken cancellationToken);
}

public interface IEngineCredentialProvider
{
    Task<IEngineCredentialSession> BeginAsync(Guid operationId, CancellationToken cancellationToken);
}
```

### Memory Zeroization & Secret Leases
`IKeyLease` (`BufferKeyLease`) enforces strict RAII lifetime management. Secret bytes are never returned as accessible array or span properties; callers copy what they need via `CopyTo(Span<byte> destination)`. Upon calling `Dispose()`, all pinned plaintext byte buffers are actively overwritten with cryptographic zeroization (`CryptographicOperations.ZeroMemory`). Raw secret arrays are never exposed through public APIs or held in immutable `System.String` objects.

Secrets MUST NEVER travel through:
- Process command-line arguments (`System.Environment.GetCommandLineArgs`);
- Process environment variables;
- Persistent disk logs or exception telemetry.

---

## Recovery Invariants

For every active repository, Fortiq **MUST** guarantee at least one independent, sovereign recovery path.
1. The Desktop Protection Setup Wizard will not terminate successfully until the operator has verified their BIP-39 mnemonic by correctly inputting challenge words.
2. The self-contained recovery kit (`kit.json`) contains only open, verifiable metadata:
   - Repository ID and backend locator (`s3:https://...` or local path);
   - Pinned engine identifier and minimum version requirements;
   - Cryptographic hashes and suite descriptors of attached key envelopes;
   - Clear recovery instructions for the zero-dependency CLI `Fortiq.Recover`.

The secret mnemonic phrase is NEVER written into the recovery kit file.
