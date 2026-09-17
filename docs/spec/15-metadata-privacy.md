# 15 — Metadata Privacy

Storage nodes require routing/integrity metadata, not application semantics.

Keep encrypted:
- ticket title/description;
- chat type/body;
- filenames;
- plaintext file hashes;
- hostname/system snapshots where possible;
- human identity labels;
- precise application timestamps.

Outer metadata may expose:
- NetworkId;
- opaque SegmentId;
- opaque writer/key ID;
- coarse storage class;
- ciphertext length;
- hashes;
- RS profile;
- replication timing.

## Segment-scoped pseudonyms

Do not expose global EntityId on every data object if unnecessary.

Use Segment-scoped writer/key identifiers so storage observers cannot trivially correlate the same person across Segments.

## Equality leakage

Content addressing is over ciphertext/storage bytes, not plaintext.

Randomized encryption intentionally prevents global deduplication of identical plaintext files.

Privacy wins over cross-client deduplication.
