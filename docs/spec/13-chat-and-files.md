# 13 — Chat and Files

## Chat logical event

```text
ChatMessage {
  ticket_id,
  message_id,
  sender,
  body,
  reply_to?,
  client_timestamp?
}
```

The logical event is placed into an EventPack.

## Chat edits

No overwrite.

```text
ChatMessageRevision {
  original_message_id,
  replacement_body
}
```

UI reducer shows latest valid revision while history remains.

## File pipeline

```text
File
  ↓
random FileKey
  ↓
fixed-size plaintext chunks
  ↓
AEAD per chunk with unique nonce/AAD
  ↓
ciphertext stripes
  ↓
RS
  ↓
distributed shards
```

## File nonce

FileKey is unique per attachment.

Nonce must be unique under FileKey.

Recommended construction:
- random file nonce prefix;
- monotonically encoded chunk index;
- exact format pinned by crypto profile.

## Attachment manifest

Sensitive metadata is encrypted:

```text
filename
MIME
plaintext size
plaintext integrity hash
ordered blob/stripe IDs
sender
ticket_id
```

Outer metadata contains only what storage needs.

## Resume

Resume is keyed by:
- AttachmentId;
- stripe index;
- shard availability.

Never re-encrypt an already committed chunk with the same FileKey and a different plaintext under the same nonce.

## Recipient key rotation

Large file ciphertext need not be re-encrypted merely to add a new authorized recipient.

A new immutable envelope-set revision may rewrap the same FileKey.

Removing a recipient cannot revoke data they already decrypted.
