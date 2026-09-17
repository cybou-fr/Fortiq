# ADR-004 — Client Access Epoch

**Status:** Superseded by [ADR-007 — Ticket Lifecycle Shell Authority](ADR-007-ticket-lifecycle-shell-authority.md).

**Decision:** Shell authorization is bound to a client-owned Ticket AccessEpoch.

Admin state overrides/tombstones cannot resurrect an invalidated epoch.

Only a new client-signed reopen creates new shell authority.
