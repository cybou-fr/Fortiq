# 17 — Protocol Map and Resource Limits

## Small RPCs

Use request/response-style bounded protocols for:
- Genesis;
- membership/control;
- manifests;
- heads;
- inventory;
- receipts.

CBOR messages have explicit maximum sizes.

## Large transfer

Do not send multi-megabyte shard payloads through APIs that require buffering the whole request/response.

Use a streaming substream protocol:

```text
SHARD_OPEN
SHARD_DATA*
SHARD_END
ACK
```

with:
- length limits;
- idle timeout;
- total timeout;
- backpressure;
- expected shard hash.

## Suggested initial limits

```text
Control object     <= 64 KiB
Head advertisement <= 16 KiB
EventPack          <= 256 KiB
Manifest           <= 1 MiB
Shard              <= 4 MiB initial target
```

Limits are negotiated/versioned by policy.

## Protocol families

```text
/fortiq/genesis/1
/fortiq/control/1
/fortiq/head/1
/fortiq/inventory/1
/fortiq/object/1
/fortiq/shard/1
/fortiq/ticket/next
/fortiq/shell/next
```

Unknown protocol versions fail closed or negotiate an explicitly compatible lower version.
