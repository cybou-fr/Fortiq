# ADR-004 — Client Access Epoch

**Decision:** Shell authorization is bound to a client-owned Ticket AccessEpoch.

Admin state overrides/tombstones cannot resurrect an invalidated epoch.

Only a new client-signed reopen creates new shell authority.
