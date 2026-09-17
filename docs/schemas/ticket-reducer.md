# Ticket Reducer

Conceptual safety state:

```rust
struct TicketSafetyState {
    ticket_id: TicketId,
    access_epoch: AccessEpoch,
    access_valid: bool,
    lifecycle: TicketLifecycle,
}

enum TicketLifecycle {
    Open,
    InProgress,
    Resolved,
    Closed,
}
```

Rules:

```text
TicketCreated(client)
  -> OPEN, new AccessEpoch, valid=true

TicketInProgress(operator)
  -> IN_PROGRESS if epoch still valid

TicketResolved(operator)
  -> RESOLVED, valid=false

TicketClosed(client|operator)
  -> CLOSED, valid=false

ClientAccessRevoked(client)
  -> valid=false immediately

TicketReopenedByClient(client)
  -> new AccessEpoch, OPEN, valid=true
```

No event authored only by Operator/Admin may change `valid=false` back to `true` for an old AccessEpoch.
