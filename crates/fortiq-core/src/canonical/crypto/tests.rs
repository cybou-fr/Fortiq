use crate::canonical::crypto::keys::{DataEncryptionKey, OwnerSegmentMasterSeed};
use crate::canonical::crypto::provider::{CryptoError, CryptoProvider, StandardCryptoProvider};
use crate::canonical::types::{KeyId, NetworkId, SegmentId};

#[test]
fn test_seal_and_open_payload_roundtrip() {
    let provider = StandardCryptoProvider::new();
    let plaintext = b"Confidential customer support diagnosis telemetry";
    let aad = b"Network:123|Segment:456";

    let (ciphertext, dek) = provider
        .seal_payload(plaintext, aad)
        .expect("encryption failed");
    assert_ne!(ciphertext, plaintext);

    let decrypted = provider
        .open_payload(&ciphertext, &dek, aad)
        .expect("decryption failed");
    assert_eq!(plaintext.as_slice(), decrypted.as_slice());
}

#[test]
fn test_tampered_aad_causes_decryption_failure() {
    let provider = StandardCryptoProvider::new();
    let plaintext = b"Sensitive ticket note";
    let valid_aad = b"Network:A|Segment:B";
    let tampered_aad = b"Network:A|Segment:C";

    let (ciphertext, dek) = provider
        .seal_payload(plaintext, valid_aad)
        .expect("encryption failed");

    let err = provider
        .open_payload(&ciphertext, &dek, tampered_aad)
        .expect_err("must reject tampered AAD");
    assert!(matches!(err, CryptoError::DecryptionFailed));
}

#[test]
fn test_one_payload_n_recipients_envelope_model() {
    let provider = StandardCryptoProvider::new();
    let plaintext = b"One payload distributed to three sovereign participants";
    let aad = b"EventPack:v1";
    let info = b"TicketContext:12345";

    // Step 1: Encrypt payload once with random DEK
    let (ciphertext, dek) = provider
        .seal_payload(plaintext, aad)
        .expect("payload seal failed");

    // Step 2: Define 3 distinct recipients with genuine independent asymmetric keypairs
    let (recipient_a_pk, recipient_a_sk) = crate::canonical::crypto::provider::generate_kem_keypair();
    let (recipient_b_pk, recipient_b_sk) = crate::canonical::crypto::provider::generate_kem_keypair();
    let (recipient_c_pk, recipient_c_sk) = crate::canonical::crypto::provider::generate_kem_keypair();

    // Verify asymmetric invariant: public key is NOT the secret key
    assert_ne!(recipient_a_pk, recipient_a_sk, "Public key must not equal secret key");
    assert_ne!(recipient_b_pk, recipient_b_sk, "Public key must not equal secret key");
    assert_ne!(recipient_c_pk, recipient_c_sk, "Public key must not equal secret key");

    let key_id_a = KeyId::from_bytes([0x01; 32]);
    let key_id_b = KeyId::from_bytes([0x02; 32]);
    let key_id_c = KeyId::from_bytes([0x03; 32]);

    // Step 3: Wrap DEK for each recipient
    let envelope_a = provider
        .wrap_dek(&dek, key_id_a, &recipient_a_pk, 1, info)
        .expect("wrap A failed");
    let envelope_b = provider
        .wrap_dek(&dek, key_id_b, &recipient_b_pk, 1, info)
        .expect("wrap B failed");
    let envelope_c = provider
        .wrap_dek(&dek, key_id_c, &recipient_c_pk, 1, info)
        .expect("wrap C failed");

    // Step 4: Each recipient independently unwraps DEK and decrypts the single ciphertext
    let unwrapped_dek_a = provider
        .unwrap_dek(&envelope_a, &recipient_a_sk, info)
        .expect("unwrap A failed");
    let decrypted_a = provider
        .open_payload(&ciphertext, &unwrapped_dek_a, aad)
        .expect("decrypt A failed");
    assert_eq!(plaintext.as_slice(), decrypted_a.as_slice());

    let unwrapped_dek_b = provider
        .unwrap_dek(&envelope_b, &recipient_b_sk, info)
        .expect("unwrap B failed");
    let decrypted_b = provider
        .open_payload(&ciphertext, &unwrapped_dek_b, aad)
        .expect("decrypt B failed");
    assert_eq!(plaintext.as_slice(), decrypted_b.as_slice());

    let unwrapped_dek_c = provider
        .unwrap_dek(&envelope_c, &recipient_c_sk, info)
        .expect("unwrap C failed");
    let decrypted_c = provider
        .open_payload(&ciphertext, &unwrapped_dek_c, aad)
        .expect("decrypt C failed");
    assert_eq!(plaintext.as_slice(), decrypted_c.as_slice());

    // Wrong secret key must fail unwrap
    let wrong_sk = vec![0x99; 32];
    let unwrap_err = provider.unwrap_dek(&envelope_a, &wrong_sk, info);
    assert!(unwrap_err.is_err());
}

#[test]
fn test_deterministic_segment_derivation_and_isolation() {
    let provider = StandardCryptoProvider::new();
    let master_seed = OwnerSegmentMasterSeed::new([0x77; 32]);
    let net_id = NetworkId::from_bytes([0x12; 32]);
    let seg_client_a = SegmentId::from_bytes([0xaa; 32]);
    let seg_client_b = SegmentId::from_bytes([0xbb; 32]);

    // Key derivation is deterministic
    let secret_a1 = provider
        .derive_segment_secret(&master_seed, &net_id, &seg_client_a, 1)
        .expect("derive A1 failed");
    let secret_a2 = provider
        .derive_segment_secret(&master_seed, &net_id, &seg_client_a, 1)
        .expect("derive A2 failed");
    assert_eq!(secret_a1.as_bytes(), secret_a2.as_bytes());

    // Invariant: Cross-client isolation (Client A secret != Client B secret)
    let secret_b = provider
        .derive_segment_secret(&master_seed, &net_id, &seg_client_b, 1)
        .expect("derive B failed");
    assert_ne!(secret_a1.as_bytes(), secret_b.as_bytes());

    // Epoch rotation changes the secret
    let secret_a_epoch2 = provider
        .derive_segment_secret(&master_seed, &net_id, &seg_client_a, 2)
        .expect("derive A epoch 2 failed");
    assert_ne!(secret_a1.as_bytes(), secret_a_epoch2.as_bytes());
}

#[test]
fn test_zeroization_on_drop() {
    use zeroize::Zeroize;

    let mut dek = DataEncryptionKey::new([0x55; 32]);
    assert_eq!(dek.as_bytes(), &[0x55; 32]);
    dek.zeroize();
    assert_eq!(dek.as_bytes(), &[0x00; 32]);
}
