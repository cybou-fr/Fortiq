# ADR-007 - Ticket Lifecycle Shell Authority

**Status:** Accepted
**Date:** 2026-09-18
**Supersedes:** ADR-004 - Client Access Epoch

## Decision

A support ticket created by the Client is the sole ticket-level authorization
scope for remote support. There is no independent shell consent token,
`AccessEpoch`, or `remote_access_enabled` permission switch.

Shell admission requires all of the following:

1. The ticket exists on the target Client.
2. The ticket lifecycle is `OPEN` or `IN_PROGRESS`.
3. The operator presents a valid Owner-signed `OperatorSessionCertificate`.
4. The session certificate has `SHELL_EXEC` capability and is not expired.
5. Network, target, and transport bindings match the local Genesis and peer.
6. The fresh shell challenge signature verifies.

## Lifecycle authority

The Client can create tickets, exchange chat and files, and observe ticket
state. The Client cannot resolve, close, reopen, or independently revoke a
remote shell through a second permission layer.

An authorized Operator or Admin session owns lifecycle transitions:

- `OPEN -> IN_PROGRESS`, `RESOLVED`, or `CLOSED`;
- `IN_PROGRESS -> RESOLVED` or `CLOSED`;
- `RESOLVED -> IN_PROGRESS` or `CLOSED`;
- `CLOSED` is terminal.

`RESOLVED` and `CLOSED` immediately terminate active shell sessions for the
ticket. Returning `RESOLVED` to `IN_PROGRESS` re-enables shell admission because
lifecycle is the authority. A new ticket is required after `CLOSED`.

## Consequences

- Ticket creation immediately establishes the support scope.
- Shell protocol and ticket sync versions must be bumped when removing the old
  epoch-bearing wire fields.
- Historical v3 AccessEpoch records remain documented only as superseded
  history; they are not accepted as current shell authority.
- `TicketCryptoEpoch` and other encryption-key epochs remain valid concepts and
  are unrelated to shell authorization.
