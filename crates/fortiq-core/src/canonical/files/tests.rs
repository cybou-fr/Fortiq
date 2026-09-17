use crate::canonical::files::aead::{
    FileStreamDecryptor, FileStreamEncryptor, FileStreamError, DEFAULT_FILE_CHUNK_SIZE,
};
use crate::canonical::files::key::FileKey;
use crate::canonical::files::manifest::AttachmentPlaintextMetadata;
use crate::canonical::files::resume::FileUploadSession;
use crate::canonical::storage::erasure::Shard;
use crate::canonical::storage::stripe::{StripeDecoder, StripeEncoder};
use crate::canonical::types::{
    BlobId, EntityId, NetworkId, ObjectId, RsProfile, SegmentId, TicketId,
};

#[test]
fn test_file_key_generation() {
    let key1 = FileKey::generate();
    let key2 = FileKey::generate();
    assert_ne!(key1.as_bytes(), key2.as_bytes());
    assert_ne!(key1.as_bytes(), &[0u8; 32]);
}

#[test]
fn test_streaming_aead_encrypt_decrypt_roundtrip() {
    let file_key = FileKey::generate();
    let attachment_id = ObjectId::from_bytes([0xaa; 32]);
    let nonce_prefix = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88];

    // Generate 150 KiB test payload (spans across 3 chunks of 64 KiB)
    let mut original_data = Vec::with_capacity(150 * 1024);
    for i in 0..(150 * 1024) {
        original_data.push((i % 251) as u8);
    }

    let mut encryptor = FileStreamEncryptor::new(
        file_key.clone(),
        attachment_id,
        nonce_prefix,
        DEFAULT_FILE_CHUNK_SIZE,
    );

    let chunks = encryptor
        .encrypt_all(&original_data)
        .expect("encryption must succeed");

    assert_eq!(chunks.len(), 3); // 64 KiB + 64 KiB + 22 KiB

    let mut decryptor = FileStreamDecryptor::new(file_key, attachment_id, nonce_prefix);

    let decrypted = decryptor
        .decrypt_all(&chunks)
        .expect("decryption must succeed");

    assert_eq!(decrypted, original_data);
}

#[test]
fn test_streaming_aead_tamper_and_reorder_rejections() {
    let file_key = FileKey::generate();
    let attachment_id = ObjectId::from_bytes([0xbb; 32]);
    let nonce_prefix = [0x01; 8];

    let original_data = vec![0x42u8; 130 * 1024]; // 2 chunks: 64 KiB + 64 KiB + 2 KiB

    let mut encryptor = FileStreamEncryptor::new(
        file_key.clone(),
        attachment_id,
        nonce_prefix,
        DEFAULT_FILE_CHUNK_SIZE,
    );

    let mut chunks = encryptor
        .encrypt_all(&original_data)
        .expect("encryption must succeed");

    // 1. Bit flip in chunk ciphertext triggers DecryptionFailed
    let mut tampered_chunks = chunks.clone();
    tampered_chunks[1][10] ^= 0xFF;
    let mut decryptor1 = FileStreamDecryptor::new(file_key.clone(), attachment_id, nonce_prefix);
    assert_eq!(
        decryptor1.decrypt_all(&tampered_chunks),
        Err(FileStreamError::DecryptionFailed)
    );

    // 2. Reordering chunks fails because sequence number is bound in nonce and AAD
    let reordered = vec![chunks[1].clone(), chunks[0].clone(), chunks[2].clone()];
    let mut decryptor2 = FileStreamDecryptor::new(file_key.clone(), attachment_id, nonce_prefix);
    assert_eq!(
        decryptor2.decrypt_all(&reordered),
        Err(FileStreamError::DecryptionFailed)
    );

    // 3. Omitting the final chunk fails AEAD because is_last flag in AAD does not match
    chunks.pop(); // drop last chunk
    let mut decryptor3 = FileStreamDecryptor::new(file_key, attachment_id, nonce_prefix);
    assert_eq!(
        decryptor3.decrypt_all(&chunks),
        Err(FileStreamError::DecryptionFailed)
    );
}

#[test]
fn test_attachment_plaintext_metadata_seal_and_open() {
    let file_key = FileKey::generate();
    let attachment_id = ObjectId::from_bytes([0xcc; 32]);

    let metadata = AttachmentPlaintextMetadata {
        filename: "customer_crash_dump.dmp".into(),
        mime_type: "application/octet-stream".into(),
        plaintext_size: 1048576,
        plaintext_blake3_hash: [0x55; 32],
        sender: EntityId::from_bytes([0x66; 32]),
        ticket_id: TicketId::from_bytes([0x77; 16]),
        created_at: 1726570000,
    };

    let sealed = metadata
        .seal(&file_key, &attachment_id)
        .expect("sealing metadata should succeed");

    // Decrypt with correct key
    let opened = AttachmentPlaintextMetadata::open(&sealed, &file_key, &attachment_id)
        .expect("opening metadata should succeed");
    assert_eq!(opened, metadata);

    // Decrypt with incorrect key fails
    let wrong_key = FileKey::generate();
    assert!(AttachmentPlaintextMetadata::open(&sealed, &wrong_key, &attachment_id).is_err());
}

#[test]
fn test_file_rs_stripes_end_to_end_pipeline() {
    let file_key = FileKey::generate();
    let attachment_id = ObjectId::from_bytes([0xdd; 32]);
    let blob_id = BlobId::from_bytes([0xee; 32]);
    let network_id = NetworkId::from_bytes([0x11; 32]);
    let segment_id = SegmentId::from_bytes([0x22; 32]);
    let nonce_prefix = [0x99; 8];

    // 80 KiB original file
    let original_bytes: Vec<u8> = (0..(80 * 1024)).map(|i| (i * 7 % 256) as u8).collect();

    // 1. Streaming AEAD Encryption (32 KiB chunks)
    let mut encryptor =
        FileStreamEncryptor::new(file_key.clone(), attachment_id, nonce_prefix, 32 * 1024);
    let chunks = encryptor.encrypt_all(&original_bytes).unwrap();

    // Flatten ciphertext chunks into a contiguous ciphertext blob
    let mut full_ciphertext = Vec::new();
    let mut chunk_lengths = Vec::new();
    for c in &chunks {
        chunk_lengths.push(c.len());
        full_ciphertext.extend_from_slice(c);
    }

    // 2. RS Stripe Encoding (Cauchy 3+2, 32 KiB stripe size)
    let rs_profile = RsProfile::new(3, 2);
    let stripe_encoder = StripeEncoder::with_default();
    let (blob_manifest, stripes_shards) = stripe_encoder
        .encode_blob(
            network_id,
            segment_id,
            blob_id,
            &full_ciphertext,
            rs_profile,
            32 * 1024,
        )
        .expect("stripe encoding should succeed");

    assert_eq!(blob_manifest.stripes.len(), stripes_shards.len());

    // 3. Simulate distributed shard loss: erase 2 shards in each stripe by setting to None
    let mut optional_stripes = Vec::new();
    for shards in stripes_shards {
        let mut opt_stripe: Vec<Option<Shard>> = shards.into_iter().map(Some).collect();
        opt_stripe[3] = None; // remove parity 1
        opt_stripe[1] = None; // remove data 1
        assert_eq!(opt_stripe.iter().filter(|s| s.is_some()).count(), 3); // 3 shards remaining (k=3)
        optional_stripes.push(opt_stripe);
    }

    // 4. Decode / Reconstruct all stripes
    let stripe_decoder = StripeDecoder::with_default();
    let reconstructed_ciphertext = stripe_decoder
        .decode_blob(&blob_manifest, &optional_stripes)
        .expect("stripe decoding should succeed");

    assert_eq!(reconstructed_ciphertext, full_ciphertext);

    // 5. Unpack ciphertext chunks and Decrypt back to original plaintext
    let mut reconstructed_chunks = Vec::new();
    let mut offset = 0;
    for len in chunk_lengths {
        reconstructed_chunks.push(reconstructed_ciphertext[offset..offset + len].to_vec());
        offset += len;
    }

    let mut decryptor = FileStreamDecryptor::new(file_key, attachment_id, nonce_prefix);
    let decrypted_file = decryptor
        .decrypt_all(&reconstructed_chunks)
        .expect("file stream decryption should succeed");

    assert_eq!(decrypted_file, original_bytes);
}

#[test]
fn test_file_upload_resumption_workflow() {
    let attachment_id = ObjectId::from_bytes([0x11; 32]);
    let blob_id = BlobId::from_bytes([0x22; 32]);
    let stripe_size = 64 * 1024;
    let total_bytes = 180 * 1024; // 3 stripes

    let mut session = FileUploadSession::new(attachment_id, blob_id, total_bytes, stripe_size);
    assert_eq!(session.total_stripes, 3);
    assert_eq!(session.pending_stripes(), vec![0, 1, 2]);
    assert!(!session.is_complete());

    // Commit stripe 0
    session.record_stripe_committed(0, vec![]).unwrap();
    assert!(session.is_stripe_committed(0));
    assert_eq!(session.pending_stripes(), vec![1, 2]);

    // Commit stripe 2 (e.g. uploaded out of order)
    session.record_stripe_committed(2, vec![]).unwrap();
    assert!(session.is_stripe_committed(2));
    assert_eq!(session.pending_stripes(), vec![1]);
    assert!(!session.is_complete());

    // Commit final pending stripe 1
    session.record_stripe_committed(1, vec![]).unwrap();
    assert!(session.is_complete());
    assert!(session.pending_stripes().is_empty());
    assert_eq!(session.progress_fraction(), 1.0);
}
