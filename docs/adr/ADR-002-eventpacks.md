# ADR-002 — EventPacks

**Decision:** High-volume logical events are packed by Segment/author/ticket epoch before expensive PQ signing/encryption.

**Reason:** PQ KEM/signature overhead dominates tiny chat messages.

Safety-critical revocation events flush immediately.
