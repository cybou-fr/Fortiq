use crate::canonical::crypto::keys::MnemonicEntropy;
use crate::canonical::crypto::provider::StandardCryptoProvider;
use crate::canonical::events::reducer::TicketView;
use crate::canonical::events::safety::TicketSafetyState;
use crate::canonical::portable::certificate::{
    CertificateError, OperatorCapabilities, OperatorSessionCertificate, MAX_SESSION_TTL_SECS,
};
use crate::canonical::portable::mnemonic::{
    entropy_to_mnemonic, parse_mnemonic_phrase, MnemonicDeriver, MnemonicError,
};
use crate::canonical::portable::workspace::{MemoryWorkspace, WorkspaceError};
use crate::canonical::signing::{Signer, SigningError, Verifier};
use crate::canonical::types::{
    AccessEpoch, EntityId, KeyId, NetworkId, OwnerId, SegmentId, TicketId,
};

#[derive(Clone)]
struct MockSigner {
    key_id: KeyId,
}

impl Signer for MockSigner {
    fn sign(&self, domain_separated_data: &[u8]) -> Result<Vec<u8>, SigningError> {
        let hash = blake3::hash(domain_separated_data);
        Ok(hash.as_bytes().to_vec())
    }

    fn key_id(&self) -> KeyId {
        self.key_id
    }
}

struct MockVerifier;

impl Verifier for MockVerifier {
    fn verify(&self, domain_separated_data: &[u8], signature: &[u8]) -> Result<(), SigningError> {
        let hash = blake3::hash(domain_separated_data);
        if hash.as_bytes() == signature {
            Ok(())
        } else {
            Err(SigningError::VerificationFailed(
                "signature mismatch".into(),
            ))
        }
    }
}

#[test]
fn test_mnemonic_derivation_deterministic_and_isolated() {
    let entropy = MnemonicEntropy::new([0x42; 32]);
    let deriver = MnemonicDeriver::new(&entropy);

    let root_seed1 = deriver.derive_root_signing_seed().unwrap();
    let root_seed2 = deriver.derive_root_signing_seed().unwrap();
    assert_eq!(root_seed1.as_bytes(), root_seed2.as_bytes());

    let seg_seed = deriver.derive_segment_master_seed().unwrap();
    let session_nonce = [0x55; 16];
    let session_seed = deriver
        .derive_operator_session_seed(&session_nonce)
        .unwrap();

    // Domain separation ensures all three derived seeds are mutually distinct
    assert_ne!(root_seed1.as_bytes(), seg_seed.as_bytes());
    assert_ne!(seg_seed.as_bytes(), session_seed.as_bytes());
    assert_ne!(root_seed1.as_bytes(), session_seed.as_bytes());
}

#[test]
fn test_session_certificate_issuance_and_verification() {
    let root_key = KeyId::from_bytes([0x11; 32]);
    let root_signer = MockSigner { key_id: root_key };
    let verifier = MockVerifier;

    let net_id = NetworkId::from_bytes([0x22; 32]);
    let owner_id = OwnerId::from_bytes([0x33; 32]);
    let host_entity = EntityId::from_bytes([0x66; 32]);
    let op_entity = EntityId::from_bytes([0x55; 32]);
    let op_key = KeyId::from_bytes([0x44; 32]);
    let session_pubkey = [0x77; 32];
    let capabilities = OperatorCapabilities::from_names(["ticket:read", "shell:execute"]);
    let nonce = [0x88; 16];

    let cert = OperatorSessionCertificate::issue(
        net_id,
        owner_id,
        host_entity,
        op_entity,
        op_key,
        session_pubkey,
        capabilities,
        1000,
        5000,
        nonce,
        &root_signer,
    )
    .expect("certificate issuance must succeed");

    // 1. Valid verification before expiry (current_time = 3000)
    cert.verify(&verifier, 3000)
        .expect("verification before expiry must succeed");

    // 2. Expired verification (current_time = 6000)
    let err = cert.verify(&verifier, 6000).unwrap_err();
    assert_eq!(err, CertificateError::Expired(5000, 6000));

    // 3. Not yet valid (current_time = 500 < not_before 1000)
    let err = cert.verify(&verifier, 500).unwrap_err();
    assert_eq!(err, CertificateError::NotYetValid(1000, 500));

    // 4. Tampered signature fails verification
    let mut tampered = cert.clone();
    tampered.owner_signature[0] ^= 0xFF;
    assert!(tampered.verify(&verifier, 3000).is_err());
}

#[test]
fn test_session_certificate_max_ttl_and_window_validation() {
    let root_signer = MockSigner {
        key_id: KeyId::from_bytes([0x11; 32]),
    };
    let net_id = NetworkId::from_bytes([0x22; 32]);
    let owner_id = OwnerId::from_bytes([0x33; 32]);
    let host_entity = EntityId::from_bytes([0x66; 32]);
    let op_entity = EntityId::from_bytes([0x55; 32]);
    let op_key = KeyId::from_bytes([0x44; 32]);

    // TTL exceeding 86,400s must be rejected
    let too_long_ttl = OperatorSessionCertificate::issue(
        net_id,
        owner_id,
        host_entity,
        op_entity,
        op_key,
        [0x77; 32],
        OperatorCapabilities::from_bits(OperatorCapabilities::ADMIN),
        1000,
        1000 + MAX_SESSION_TTL_SECS + 1, // 86401 seconds
        [0x88; 16],
        &root_signer,
    );
    assert_eq!(
        too_long_ttl,
        Err(CertificateError::TtlExceeded(
            MAX_SESSION_TTL_SECS + 1,
            MAX_SESSION_TTL_SECS
        ))
    );

    // Inverted validity window (expires_at < not_before)
    let inverted = OperatorSessionCertificate::issue(
        net_id,
        owner_id,
        host_entity,
        op_entity,
        op_key,
        [0x77; 32],
        OperatorCapabilities::default(),
        5000,
        4000,
        [0x88; 16],
        &root_signer,
    );
    assert_eq!(
        inverted,
        Err(CertificateError::InvalidValidityWindow(4000, 5000))
    );
}

#[test]
fn test_bip39_24_word_mnemonic_validation_and_checksum() {
    let original_entropy = MnemonicEntropy::new([0x42; 32]);
    let phrase = entropy_to_mnemonic(&original_entropy);

    // Must be exactly 24 words
    let words: Vec<&str> = phrase.split_whitespace().collect();
    assert_eq!(words.len(), 24);

    // Roundtrip parsing must recover identical 256-bit entropy
    let parsed_entropy = parse_mnemonic_phrase(&phrase).expect("valid mnemonic phrase");
    assert_eq!(original_entropy.as_bytes(), parsed_entropy.as_bytes());

    // Error case 1: Invalid word count (23 words)
    let bad_count_phrase = words[..23].join(" ");
    assert_eq!(
        parse_mnemonic_phrase(&bad_count_phrase),
        Err(MnemonicError::InvalidWordCount(23))
    );

    // Error case 2: Unknown word outside BIP-39 dictionary
    let mut bad_words = words.clone();
    bad_words[0] = "foobarxyznonexistent";
    let unknown_phrase = bad_words.join(" ");
    assert_eq!(
        parse_mnemonic_phrase(&unknown_phrase),
        Err(MnemonicError::UnknownWord("foobarxyznonexistent".into()))
    );

    // Error case 3: Tampered word (corrupted checksum)
    let mut corrupted_words = words.clone();
    // Swap last word (which carries the 8-bit checksum) to a different valid dictionary word
    corrupted_words[23] = if corrupted_words[23] == "abandon" {
        "ability"
    } else {
        "abandon"
    };
    let corrupted_phrase = corrupted_words.join(" ");
    assert_eq!(
        parse_mnemonic_phrase(&corrupted_phrase),
        Err(MnemonicError::InvalidChecksum)
    );
}

#[test]
fn test_memory_workspace_lifecycle_lock_and_wipe() {
    let entropy = MnemonicEntropy::new([0x99; 32]);
    let deriver = MnemonicDeriver::new(&entropy);
    let master_seed = deriver.derive_segment_master_seed().unwrap();

    let net_id = NetworkId::from_bytes([0x11; 32]);
    let owner_id = OwnerId::from_bytes([0x22; 32]);
    let seg_id = SegmentId::from_bytes([0x33; 32]);
    let ticket_id = TicketId::from_bytes([0x44; 16]);

    let mut workspace = MemoryWorkspace::new();
    assert!(!workspace.is_unlocked());

    // Operations on locked workspace fail
    let crypto_provider = StandardCryptoProvider::new();
    assert!(matches!(
        workspace.derive_segment_secret(&seg_id, 1, &crypto_provider),
        Err(WorkspaceError::WorkspaceLocked)
    ));

    // 1. Unlock with master seed
    workspace.unlock(net_id, owner_id, master_seed.clone());
    assert!(workspace.is_unlocked());

    // 2. Derive segment secret succeeds
    let secret = workspace
        .derive_segment_secret(&seg_id, 1, &crypto_provider)
        .expect("deriving segment secret must succeed");
    assert_ne!(secret.as_bytes(), &[0u8; 32]);

    // 3. Cache and retrieve decrypted ticket
    let epoch = AccessEpoch::from_bytes([0x01; 16]);
    let safety = TicketSafetyState::new_client_open(ticket_id, epoch);
    let view = TicketView {
        ticket_id,
        title: "Customer Support Issue".into(),
        safety,
        messages: Vec::new(),
        attachments: Vec::new(),
        incorporated_packs: Vec::new(),
    };

    workspace.cache_ticket(view.clone()).unwrap();
    let retrieved = workspace.get_ticket(&ticket_id).unwrap();
    assert_eq!(retrieved, Some(&view));

    // 4. Operator Lock securely clears all cached data and master seeds
    workspace.lock();
    assert!(!workspace.is_unlocked());
    assert!(matches!(
        workspace.get_ticket(&ticket_id),
        Err(WorkspaceError::WorkspaceLocked)
    ));
    assert!(matches!(
        workspace.derive_segment_secret(&seg_id, 1, &crypto_provider),
        Err(WorkspaceError::WorkspaceLocked)
    ));

    // 5. Re-unlocking restores capability
    workspace.unlock(net_id, owner_id, master_seed);
    assert!(workspace.is_unlocked());
    // Previous cache was wiped!
    assert_eq!(workspace.get_ticket(&ticket_id).unwrap(), None);
}
