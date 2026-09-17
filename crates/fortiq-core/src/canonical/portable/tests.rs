use crate::canonical::crypto::keys::MnemonicEntropy;
use crate::canonical::crypto::provider::StandardCryptoProvider;
use crate::canonical::events::reducer::TicketView;
use crate::canonical::events::safety::TicketSafetyState;
use crate::canonical::portable::certificate::{CertificateError, OperatorSessionCertificate};
use crate::canonical::portable::mnemonic::MnemonicDeriver;
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
    let op_key = KeyId::from_bytes([0x44; 32]);
    let op_entity = EntityId::from_bytes([0x55; 32]);

    let cert = OperatorSessionCertificate::issue(
        net_id,
        owner_id,
        op_key,
        op_entity,
        vec!["ticket:read".into(), "shell:execute".into()],
        1000,
        5000,
        &root_signer,
    )
    .expect("certificate issuance must succeed");

    // 1. Valid verification before expiry (current_time = 3000)
    cert.verify(&verifier, 3000)
        .expect("verification before expiry must succeed");

    // 2. Expired verification (current_time = 6000)
    let err = cert.verify(&verifier, 6000).unwrap_err();
    assert_eq!(err, CertificateError::Expired(5000, 6000));

    // 3. Tampered signature fails verification
    let mut tampered = cert.clone();
    tampered.owner_signature[0] ^= 0xFF;
    assert!(tampered.verify(&verifier, 3000).is_err());
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
