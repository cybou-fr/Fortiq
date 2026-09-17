# 08 — State Versioning and Reducers

## No mutable state records

Everything important is an event or immutable version.

## Writer stream

Each writer/session maintains an append chain:

```text
Pack N
  prev_pack_id = Pack N-1
  writer_stream_id
  writer_seq
```

A fork is detectable if the same stream emits competing successors.

## Operator concurrency

Each Operator Session gets a unique `OperatorSessionId`.

It writes through its own stream, avoiding sequence collisions when the same Owner is unlocked on two machines.

## Reducer

Reducer input:
- valid membership/capability;
- valid signatures;
- valid decryptions;
- non-purged/tombstoned state according to policy;
- event graph.

Reducer version is explicit.

## Admin canonical override

Admin cannot edit old bytes.

Admin may append:

```text
CanonicalHeadSet
```

for presentation/business-state conflict resolution.

## Safety exception

`CanonicalHeadSet` MUST NOT grant shell access that a client-local safety state has revoked.

Client shell authorization is a separate safety reducer.

## Snapshots

Encrypted `SegmentSnapshot` / `TicketSnapshot` objects speed cold startup.

A snapshot includes:
- reducer version;
- incorporated frontier/head set;
- derived state;
- optional encrypted search index.

Snapshot is an optimization. Tail events always win according to reducer rules.
