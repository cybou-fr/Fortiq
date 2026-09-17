# 19 — Snapshots, Search and Cold Start

## Problem

An immutable history may become large.

Replaying years of chat/events every time the owner unlocks on a fresh machine is unnecessary.

## SegmentSnapshot

Periodic encrypted snapshot contains:
- reducer version;
- segment/ticket materialized state;
- incorporated writer heads/frontier;
- optional compact chat/file index;
- snapshot creation time inside ciphertext.

It is signed and encrypted to Segment recipients.

## Validation

Snapshot does not replace history.

Client/operator:
1. verify signature;
2. verify referenced frontier exists;
3. accept snapshot as optimization;
4. apply tail events.

## Search

Do not create a plaintext global search index.

Options:
- build local memory index after decryption;
- include encrypted per-Segment search-index snapshots.

MVP should use local memory indexing.

## Snapshot cadence

Suggested:
- every N packs;
- or after ticket close;
- or periodically for long-lived tickets.

Do not snapshot on every message.
