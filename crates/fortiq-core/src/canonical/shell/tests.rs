use crate::canonical::events::safety::{TicketLifecycle, TicketSafetyState};
use crate::canonical::shell::challenge::{ShellAuthError, ShellAuthResponse, ShellChallenge};
use crate::canonical::shell::session::{EpochRegistry, SessionSafetyGate};
use crate::canonical::signing::{Signer, SigningError, Verifier};
use crate::canonical::types::{AccessEpoch, EntityId, KeyId, TicketId};

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
fn test_shell_challenge_signing_and_verification() {
    let ticket_id = TicketId::from_bytes([0x11; 16]);
    let access_epoch = AccessEpoch::from_bytes([0x22; 16]);
    let challenge = ShellChallenge::new(ticket_id, access_epoch);

    let op_key = KeyId::from_bytes([0x33; 32]);
    let op_entity = EntityId::from_bytes([0x44; 32]);
    let signer = MockSigner { key_id: op_key };
    let verifier = MockVerifier;

    // 1. Valid operator signs challenge
    let response = ShellAuthResponse::create(&signer, op_entity, &challenge)
        .expect("signing challenge should succeed");
    assert_eq!(response.operator_key_id, op_key);
    assert_eq!(response.operator_entity, op_entity);

    // 2. Client verifies valid response
    response
        .verify(&verifier, &challenge)
        .expect("verifying valid response should succeed");

    // 3. Altered challenge fails verification
    let mut altered_challenge = challenge.clone();
    altered_challenge.challenge_nonce[0] ^= 0xFF;
    assert!(response.verify(&verifier, &altered_challenge).is_err());
}

#[test]
fn test_shell_safety_gate_authorization_and_epoch_mismatch() {
    let ticket_id = TicketId::from_bytes([0x55; 16]);
    let epoch1 = AccessEpoch::from_bytes([0x01; 16]);
    let epoch2 = AccessEpoch::from_bytes([0x02; 16]);

    let mut epoch_reg = EpochRegistry::new();
    epoch_reg.register_epoch(ticket_id, epoch1).unwrap();

    let safety_state = TicketSafetyState::new_client_open(ticket_id, epoch1);

    // 1. Authorize with correct epoch succeeds
    let guard = SessionSafetyGate::authorize_session(&safety_state, &epoch_reg, &epoch1)
        .expect("authorization must succeed");
    assert!(!guard.is_revoked());

    // 2. Authorize with mismatched epoch fails
    let err = SessionSafetyGate::authorize_session(&safety_state, &epoch_reg, &epoch2).unwrap_err();
    assert_eq!(err, ShellAuthError::EpochRevoked(epoch2));

    // 3. Closed ticket fails authorization
    let mut closed_state = safety_state.clone();
    closed_state.lifecycle = TicketLifecycle::Closed;
    let err_closed =
        SessionSafetyGate::authorize_session(&closed_state, &epoch_reg, &epoch1).unwrap_err();
    assert_eq!(err_closed, ShellAuthError::TicketStateClosed(ticket_id));
}

#[test]
fn test_immediate_client_revocation_via_guard() {
    let ticket_id = TicketId::from_bytes([0x66; 16]);
    let epoch = AccessEpoch::from_bytes([0x03; 16]);

    let mut epoch_reg = EpochRegistry::new();
    epoch_reg.register_epoch(ticket_id, epoch).unwrap();

    let safety = TicketSafetyState::new_client_open(ticket_id, epoch);
    let guard = SessionSafetyGate::authorize_session(&safety, &epoch_reg, &epoch).unwrap();

    let cancel_token = guard.cancellation_token();
    assert!(!guard.is_revoked());
    assert!(!cancel_token.is_cancelled());

    // Client revokes immediately
    guard.revoke("client emergency button pressed");

    assert!(guard.is_revoked());
    assert!(cancel_token.is_cancelled());
}

#[test]
fn test_anti_resurrection_guarantee() {
    let ticket_id = TicketId::from_bytes([0x77; 16]);
    let epoch1 = AccessEpoch::from_bytes([0x10; 16]);
    let epoch2 = AccessEpoch::from_bytes([0x20; 16]);

    let mut epoch_reg = EpochRegistry::new();
    epoch_reg.register_epoch(ticket_id, epoch1).unwrap();
    assert!(epoch_reg.is_epoch_valid(&ticket_id, &epoch1));

    // Client closes ticket and invalidates epoch1
    epoch_reg.invalidate_epoch(&ticket_id, &epoch1);
    assert!(!epoch_reg.is_epoch_valid(&ticket_id, &epoch1));

    // Attempting to re-register or reuse epoch1 fails permanently
    let err = epoch_reg.register_epoch(ticket_id, epoch1).unwrap_err();
    assert_eq!(err, ShellAuthError::EpochRevoked(epoch1));

    // Client reopens ticket with fresh epoch2 -> succeeds
    epoch_reg.register_epoch(ticket_id, epoch2).unwrap();
    assert!(epoch_reg.is_epoch_valid(&ticket_id, &epoch2));
    assert!(!epoch_reg.is_epoch_valid(&ticket_id, &epoch1));
}
