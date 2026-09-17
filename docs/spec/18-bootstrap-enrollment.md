# 18 — Bootstrap and Enrollment

## Client bootstrap

A new Client should not scan arbitrary public peers.

Use an Owner-signed one-time Join Invitation.

Conceptual invitation:

```text
network_id
genesis_id
bootstrap peer addresses
join_nonce
expires_at
allowed capability template
owner signature
```

## Enrollment

1. client verifies invitation and Genesis;
2. client generates signing and Segment HPKE keys;
3. client proves possession of signing key;
4. operator/admin approves;
5. random SegmentId is created;
6. operator derives Segment-specific HPKE public key;
7. Owner signs SegmentDescriptor/Enrollment;
8. client signs acceptance;
9. control object is replicated.

## Revocation

Admin revocation stops new writes after a monotonic membership epoch.

Client may still possess already decrypted historical data.

## Operator recovery on a new machine

Mnemonic -> OwnerId.

Discovery locates signed Genesis advertisements associated with OwnerId.

Operator verifies Genesis signature before trusting NetworkId/bootstrap peers.
