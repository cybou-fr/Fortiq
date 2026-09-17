# 07 — Ticket Crypto Epochs and EventPacks

## Why this layer exists

Direct PQ-HPKE + ML-DSA per 20–200 byte chat message has excessive overhead.

FORTIQ therefore separates logical message/event granularity from cryptographic storage granularity.

## TicketCryptoEpoch

When a ticket is created or keys rotate, generate:

```text
TicketEpochKey = random 256-bit secret
```

The TicketEpochKey is encrypted exactly once for:
- Client Segment recipient;
- Operator Segment recipient.

The key object is PQ-HPKE protected and signed.

## EventPack

Events with the same:
- Segment;
- ticket crypto epoch;
- author/session;

are accumulated into a bounded EventPack.

Logical example:

```text
EventPack
  ChatMessage
  ChatMessage
  TicketTaken
  ChatMessage
```

## Flush policy

Suggested defaults:

```text
max delay        150 ms
max events       32
target plaintext 64 KiB
hard max         256 KiB
```

These are implementation policy, not fixed forever in wire semantics.

## Pack encryption

Each pack uses a unique PackKey.

Recommended:

```text
PackKey =
  KDF(TicketEpochKey, PackNonce || PackSequence || "FORTIQ-PACK-v1")
```

or a fresh random PackKey wrapped under TicketEpochKey.

Never reuse nonce/key pairs.

## Signature

One author signature covers:
- pack outer header;
- ciphertext digest;
- ticket/segment binding.

## Immediate safety events

The following bypass batching delay and flush immediately:
- Client ticket close;
- Client access revoke;
- key revocation;
- critical membership/security event.

## File keys

Attachments do not need this optimization.

A FileKey is large-payload amortized already and may be directly PQ-HPKE wrapped to Client + Operator Segment, or wrapped by the active ticket epoch according to crypto profile.

Direct dual HPKE envelopes are preferred for independent file-key isolation.
