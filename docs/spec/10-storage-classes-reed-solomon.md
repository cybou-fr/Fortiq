# 10 — Storage Classes and Reed–Solomon

## Storage classes

### CONTROL

Examples:
- Genesis;
- membership certificates;
- revocations;
- owner-key rotations;
- tombstones;
- purge authorizations;
- policy checkpoints.

These objects are tiny and bootstrapping-critical.

Policy:
- replicate in full to many/all eligible peers;
- no Reed–Solomon by default.

### STATE

Examples:
- encrypted EventPacks;
- encrypted snapshots;
- manifests.

Policy:
- if ciphertext < 64 KiB: replicate to R=3 distinct peers;
- if >= 64 KiB: Reed–Solomon.

### BLOB

Examples:
- file ciphertext blocks.

Policy:
- streaming Reed–Solomon.

## Why not RS every tiny record

Erasure coding has:
- shard metadata;
- network fan-out;
- matrix/reconstruction cost.

For a few KiB, bounded replication is cheaper and simpler.

The system still avoids "every peer stores everything".

## RS profiles

Use adaptive profiles with ~1.5x storage overhead.

Example policy:

```text
eligible storage peers  profile
1                       k=1,m=0
2                       k=1,m=1
3–5                     k=2,m=1
6–8                     k=4,m=2
9+                      k=6,m=3 or k=8,m=4
```

Profile is recorded in each Blob/Pack Manifest.

## Stripe sizing

Large blobs are streamed in independent stripes.

Recommended initial target:

```text
logical stripe: 4–8 MiB
```

Shard size becomes roughly `stripe/k`.

Bound shard transfer frame sizes to avoid giant in-memory request/response messages.

## Error detection

Reed–Solomon is an erasure code, not an integrity mechanism.

Every shard has an independent cryptographic checksum.

Corrupt shards are treated as missing before reconstruction.
