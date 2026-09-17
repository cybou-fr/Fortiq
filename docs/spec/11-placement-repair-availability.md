# 11 — Placement, Repair and Availability

## Eligible storage peer

A peer advertises a signed storage capability:
- capacity;
- max shard size;
- current free budget;
- optional uptime class.

The network never assumes every client is a storage peer.

Always-on VPS/relay nodes may also be storage peers while remaining unable to decrypt payloads.

## Placement

Use weighted rendezvous hashing as a candidate selector.

Then enforce failure-domain rules:
- distinct PeerIds;
- distinct machine identity;
- avoid same physical node for multiple critical shards;
- optionally avoid same site/subnet where known.

## Manifest vs placement

Do not bake mutable holder locations into the immutable content identity.

Separate:

```text
BlobManifest
  describes shard hashes and RS profile

PlacementRecord / ShardReceipt
  describes where copies currently live
```

Repair can move a shard without changing BlobManifest/ObjectId.

## Receipts

A receipt proves only that a peer acknowledged storage at that time.

It does not prove long-term possession.

## Retrieval audits

Periodically sample shard GETs.

Availability score is based on observed retrieval success.

## Health thresholds

For profile `(k,m)`:

```text
healthy       reachable >= k+m
degraded      k <= reachable < k+m
critical      reachable == k
unrecoverable reachable < k
```

Repair begins before critical state.

## Repair

Any authorized storage/maintenance peer may repair:
1. fetch any k valid shards;
2. verify hashes;
3. reconstruct;
4. regenerate missing shard;
5. place on new eligible peer.

Duplicate concurrent repair is harmless because shard identity is content-addressed.
