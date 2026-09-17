# 09 — Ticket State and Shell Safety

## Ticket creation

Only Client may create a ticket.

Ticket creation establishes:

```text
TicketId
AccessEpoch = random 128/256-bit value
State = OPEN
```

## Shell capability

For MVP:

```text
ticket state is OPEN or IN_PROGRESS
AND AccessEpoch is locally valid
AND operator == authorized Owner/Operator
AND Operator Session valid
=> shell may be opened
```

## Client close

Client close is safety-critical:

1. write/fsync a client-signed close/revoke event locally;
2. atomically invalidate local AccessEpoch;
3. terminate active shell immediately;
4. replicate the event asynchronously.

Distributed convergence is not required before local access disappears.

## Reopen

Only Client can create:

```text
TicketReopened {
  previous_ticket_id,
  new_access_epoch
}
```

or an equivalent new epoch for the same TicketId.

Operator cannot restore a dead access epoch.

## Operator state actions

Operator may:
- take ticket;
- set IN_PROGRESS;
- set RESOLVED;
- close ticket.

An operator RESOLVED/CLOSED transition also disables its own shell access.

## Admin tombstone interaction

Even if Admin tombstones/hides an old client close event from distributed presentation history, the Client daemon MUST NOT treat the old AccessEpoch as valid again.

Only a new client-signed reopen creates access.
