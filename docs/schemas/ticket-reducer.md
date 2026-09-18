# Ticket Reducer

Ticket state:

```rust
enum TicketState {
    Open,
    InProgress,
    Resolved,
    Closed,
}
```

Rules:

```text
TicketCreated(client)
  -> OPEN

TicketInProgress(operator)
  -> IN_PROGRESS

TicketResolved(operator)
  -> RESOLVED, terminate active shells

TicketClosed(operator|admin)
  -> CLOSED, terminate active shells

TicketResolved(operator)
  -> IN_PROGRESS is allowed when the operator resumes work

CLOSED
  -> terminal; create a new ticket for new work
```

Shell admission is exactly `state in {OPEN, IN_PROGRESS}` plus a valid
Owner-signed Operator Session. Authorization is verified before event
ingestion; the reducer only applies accepted events deterministically.
`TicketCryptoEpoch` remains an encryption-key rotation mechanism and has no
relationship to shell authorization.
