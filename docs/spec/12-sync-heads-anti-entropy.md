# 12 — Sync, Heads and Anti-Entropy

## Problem

A distributed immutable graph still needs a cheap way to learn what is new.

## Writer heads

Each writer stream has a latest PackId.

A signed `HeadAdvertisement` contains:

```text
network_id
segment_id
writer_stream_id
writer_seq
head_pack_id
expires_at
signature
```

HeadAdvertisement is routing state, not canonical application state.

It may be replaced frequently.

## Sync

Peer compares known head with remote head.

If different:
- walk `prev_pack_id` backwards until a known PackId;
- fetch missing packs/manifests;
- reduce forward.

## Fresh operator

1. discover Genesis/control state;
2. load Segment Descriptors;
3. request current heads;
4. download latest encrypted snapshots;
5. fetch tail packs only.

## Anti-entropy

Periodic peer-to-peer inventory exchange reconciles:
- missing manifests;
- missing packs;
- shard availability;
- tombstones/control epochs.

Start simple with bounded head/inventory requests.

Do not introduce a DHT or global consensus unless measurements justify it.

## Large network optimization

Storage peers may return compact `SegmentHeadIndex` pages keyed by random SegmentId.

The index contains signed head records, not plaintext ticket metadata.
