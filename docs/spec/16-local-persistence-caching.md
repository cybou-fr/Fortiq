# 16 — Local Persistence and Caching

## Canonical data

Canonical data is the signed distributed object graph.

A local database is only an implementation cache/index.

## On-disk layout

Recommended separation:

```text
store/
  objects/      encrypted state packs/manifests
  shards/       assigned ciphertext shards
  control/      genesis/membership/tombstones
  index/        disposable local KV index
  temp/         bounded transfer staging
```

## Local index

Use an embedded KV engine only for:
- object -> file/path metadata;
- shard availability;
- head cache;
- retry queue;
- materialized view cache.

Do not treat its rows as canonical network truth.

A pure-Rust embedded KV such as redb is a reasonable implementation candidate, but the architecture depends only on a `LocalIndex` trait.

## Decrypted operator cache

Default:
- memory-only;
- cleared on Operator Lock.

Optional trusted-device mode may use an encrypted local cache, but this is not required for MVP.

## Secrets

Private keys:
- root mnemonic/root signing key: never persist plaintext;
- client keys: protected local service storage;
- operator segment master: memory only during unlocked session;
- zeroize secrets best-effort;
- use OS memory-locking where practical.

No software can protect a mnemonic typed into a fully compromised host OS.
