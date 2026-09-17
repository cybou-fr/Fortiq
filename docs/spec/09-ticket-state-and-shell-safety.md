# 09 - Ticket State and Shell Authority

**Canonical v4 status:** This specification supersedes the v3 AccessEpoch shell
model. See [ADR-007](../adr/ADR-007-ticket-lifecycle-shell-authority.md).

## Ticket creation

Only the Client creates a support ticket. Creation establishes the support
authorization scope and starts the ticket in `OPEN`.

## Shell admission

Shell access is allowed exactly when:

```text
Ticket exists on target Client
AND TicketState is OPEN or IN_PROGRESS
AND Owner-signed OperatorSessionCertificate is valid
AND certificate has SHELL_EXEC capability
AND target/network/transport bindings verify
AND fresh challenge signature verifies
=> shell may be opened
```

There is no separate `AccessEpoch`, `remote_access_enabled`, consent token, or
client-side revoke command.

## Lifecycle transitions

Operator/Admin sessions own ticket lifecycle transitions:

```text
OPEN         -> IN_PROGRESS | RESOLVED | CLOSED
IN_PROGRESS  -> RESOLVED | CLOSED
RESOLVED     -> IN_PROGRESS | CLOSED
CLOSED       -> terminal
```

`RESOLVED` and `CLOSED` terminate active shell sessions. Returning `RESOLVED`
to `IN_PROGRESS` permits a new shell because lifecycle is the authority. A new
ticket is required after `CLOSED`.

## Session termination

Active sessions terminate when:

- the ticket transitions to `RESOLVED` or `CLOSED`;
- the operator locks or replaces the local session;
- the session certificate expires;
- the P2P stream or daemon closes.

The managed node re-reads ticket lifecycle immediately before handshake
admission and watches it throughout the active session.

## Related epochs

`TicketCryptoEpoch`, `KeyEpoch`, and other encryption-key epochs remain valid
cryptographic rotation mechanisms. They have no relationship to shell
authorization.
