# 03 — Genesis and Owner Key Lifecycle

## Genesis body

Recommended fixed-order logical fields:

```text
GenesisV1 [
  version,
  network_id,
  owner_id,
  owner_root_signing_public_key,
  recovery_public_key_or_null,
  initial_crypto_profile,
  initial_policy_hash,
  created_at
]
```

The Owner Root signature is outside the TBS body.

## IDs

```text
OwnerId =
  SHA3-256("FORTIQ-OWNER-ID-v1" || owner_root_signing_public_key)

GenesisId =
  SHA3-256(
    "FORTIQ-GENESIS-ID-v1" ||
    canonical_cbor(TBS) ||
    signature
  )
```

`NetworkId` is secure-random 256-bit data generated before Genesis signing.

## 24-word mnemonic

Use 256 bits of mnemonic entropy.

Derive independent secrets with domain separation:

```text
OwnerRootSigningSeed
OwnerSegmentMasterSeed
OperatorSessionSeed
DiscoverySecret (optional)
```

Never use mnemonic bytes directly as protocol keys.

## Root usage

Owner Root signing key should be used only to:
- sign Genesis;
- sign membership/capability roots;
- sign Operator Session Certificates;
- sign owner-key rotation/recovery objects.

Routine ticket/chat operations use temporary session signing keys.

The root secret is wiped from memory best-effort after session creation.

`OwnerSegmentMasterSeed` remains only for the unlocked operator session because per-client decrypt keys are derived from it on demand.

## Optional recovery root

Production deployments SHOULD support a separate offline Recovery public key in Genesis.

It is intentionally different from the daily operator mnemonic.

A RecoveryOverride may replace a compromised Owner Root according to a monotonic root epoch.

MVP may leave this field null, but the wire format reserves it now.

## Owner key rotation

Normal rotation object:

```text
OwnerKeyRotation [
  network_id,
  old_epoch,
  new_epoch,
  new_owner_root_public_key,
  activation_point,
  old_owner_signature,
  new_owner_signature
]
```

Both signatures are required for ordinary rotation.

Emergency recovery is a separate recovery-root flow.
