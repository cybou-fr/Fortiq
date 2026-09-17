# ADR-005 — One Payload, N Key Envelopes

**Decision:** Payload bytes are encrypted exactly once with a fresh symmetric key.

That key is wrapped independently for authorized recipients.

FORTIQ MUST NOT store a full ciphertext copy for every recipient.
