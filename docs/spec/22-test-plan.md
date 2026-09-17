# 22 — Test Plan

## Crypto segmentation

- derive Operator Segment A/B from same root; keys differ;
- leaking A private key cannot derive B;
- Client A key cannot decrypt B;
- cross-Segment envelope replay fails.

## Envelope invariant

- one ciphertext decrypts through each intended recipient envelope;
- full payload is not duplicated per recipient;
- wrong KeyId/epoch fails;
- envelope digest modification fails AEAD/signature checks.

## EventPack

- deterministic decode;
- one signature protects whole pack;
- pack reorder/tamper rejected;
- max delay flush works;
- safety-critical event flushes immediately.

## AccessEpoch

- OPEN establishes valid epoch;
- client close invalidates immediately before network sync;
- stale operator event cannot revive;
- Admin canonical override cannot revive;
- only client reopen creates new epoch.

## RS

- any k valid shards reconstruct;
- corrupt shard hash causes it to be treated missing;
- fewer than k fails;
- profile downgrade rejected;
- repair restores target redundancy.

## Placement

- distinct peers/failure domains chosen where available;
- holder changes do not change BlobManifest;
- false receipt discovered by retrieval audit.

## Sync

- head tail walk fetches only missing packs;
- fork retained;
- snapshot + tail equals full replay;
- stale head advertisement cannot corrupt canonical state.

## Deletion

- only Admin tombstone accepted;
- purge requires valid purge authorization;
- retained tombstone prevents stale resurrection;
- purge does not revive client shell access.

## Cold operator

- mnemonic restores same OwnerId;
- Node PeerId unchanged;
- Segment A/B keys derive correctly;
- latest snapshots + tails reconstruct workspace;
- lock clears decrypted workspace.

## Files

- nonce uniqueness;
- chunk corruption rejected;
- RS recovery;
- resume;
- recipient rewrap without file re-encryption.

## Resource abuse

- oversized CBOR rejected before allocation explosion;
- excessive nesting rejected;
- quota enforced on opaque ciphertext;
- too many concurrent shard streams throttled;
- slowloris/idle timeout enforced.
