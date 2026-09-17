use crate::canonical::codec::{to_canonical_cbor, CodecError};
use crate::canonical::records::serde_bytes_32::Bytes32;
use crate::canonical::signing::{Ed25519Verifier, SigningError, Verifier};
use crate::canonical::types::{CryptoProfileId, EntityId, KeyId, NetworkId, OwnerId, SegmentId};
use serde::{Deserialize, Deserializer, Serialize, Serializer};
use sha3::{Digest, Sha3_256};
use thiserror::Error;

pub const OWNER_ID_DOMAIN: &[u8] = b"FORTIQ-OWNER-ID-v1:";
pub const GENESIS_ID_DOMAIN: &[u8] = b"FORTIQ-GENESIS-ID-v1:";
pub const GENESIS_SIG_DOMAIN: &[u8] = b"FORTIQ-GENESIS-SIG-v1:";
pub const INVITATION_SIG_DOMAIN: &[u8] = b"FORTIQ-INVITATION-SIG-v1:";
pub const ENROLLMENT_SIG_DOMAIN: &[u8] = b"FORTIQ-ENROLLMENT-SIG-v1:";
pub const DESCRIPTOR_OWNER_SIG_DOMAIN: &[u8] = b"FORTIQ-DESCRIPTOR-OWNER-SIG-v1:";
pub const DESCRIPTOR_CLIENT_SIG_DOMAIN: &[u8] = b"FORTIQ-DESCRIPTOR-CLIENT-SIG-v1:";
pub const REVOCATION_SIG_DOMAIN: &[u8] = b"FORTIQ-REVOCATION-SIG-v1:";
pub const BINDING_SIG_DOMAIN: &[u8] = b"FORTIQ-BINDING-SIG-v1:";

/// Capability bitflags.
pub mod capabilities {
    pub const WRITE_STATE: u32 = 1 << 0;
    pub const WRITE_BLOB: u32 = 1 << 1;
    pub const OPEN_TICKET: u32 = 1 << 2;
    pub const STORE_SHARDS: u32 = 1 << 3;
    pub const ADMIN: u32 = 1 << 4;
}

#[derive(Error, Debug)]
pub enum ControlError {
    #[error("serialization error: {0}")]
    Serialization(#[from] CodecError),
    #[error("mismatched network ID: expected {0}, got {1}")]
    MismatchedNetwork(NetworkId, NetworkId),
    #[error("mismatched genesis ID")]
    MismatchedGenesis,
    #[error("record expired: valid until {0}, current time {1}")]
    Expired(u64, u64),
    #[error("stale revocation epoch: current {0}, received {1}")]
    StaleRevocationEpoch(u64, u64),
    #[error("entity is revoked: {0}")]
    EntityRevoked(EntityId),
    #[error("key is revoked: {0}")]
    KeyRevoked(KeyId),
    #[error("invalid signature")]
    InvalidSignature,
    #[error("owner ID does not match the Genesis root public key")]
    OwnerIdentityMismatch,
    #[error("signature verification error: {0}")]
    SignatureVerification(String),
}

/// Genesis To-Be-Signed body.
/// Fixed-order 8-element CBOR array:
/// [version, network_id, owner_id, owner_root_signing_public_key,
///  recovery_public_key_or_null, initial_crypto_profile, initial_policy_hash, created_at]
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GenesisTbs {
    pub version: u16,
    pub network_id: NetworkId,
    pub owner_id: OwnerId,
    pub owner_root_signing_public_key: Vec<u8>,
    pub recovery_public_key: Option<Vec<u8>>,
    pub initial_crypto_profile: CryptoProfileId,
    pub initial_policy_hash: [u8; 32],
    pub created_at: u64,
}

type GenesisTbsWireTuple = (
    u16,
    NetworkId,
    OwnerId,
    Vec<u8>,
    Option<Vec<u8>>,
    u16,
    Bytes32,
    u64,
);

impl Serialize for GenesisTbs {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        let tuple: GenesisTbsWireTuple = (
            self.version,
            self.network_id,
            self.owner_id,
            self.owner_root_signing_public_key.clone(),
            self.recovery_public_key.clone(),
            self.initial_crypto_profile.as_u16(),
            Bytes32(self.initial_policy_hash),
            self.created_at,
        );
        tuple.serialize(serializer)
    }
}

impl<'de> Deserialize<'de> for GenesisTbs {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        let tuple: GenesisTbsWireTuple = Deserialize::deserialize(deserializer)?;
        let initial_crypto_profile = CryptoProfileId::from_u16(tuple.5).ok_or_else(|| {
            serde::de::Error::custom(format!("invalid crypto profile: {}", tuple.5))
        })?;

        Ok(Self {
            version: tuple.0,
            network_id: tuple.1,
            owner_id: tuple.2,
            owner_root_signing_public_key: tuple.3,
            recovery_public_key: tuple.4,
            initial_crypto_profile,
            initial_policy_hash: tuple.6 .0,
            created_at: tuple.7,
        })
    }
}

/// Fully authenticated Genesis record container.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Genesis {
    pub tbs: GenesisTbs,
    #[serde(with = "serde_vec_bytes")]
    pub signature: Vec<u8>,
}

impl Genesis {
    /// Derive the canonical 32-byte GenesisId.
    pub fn genesis_id(&self) -> Result<[u8; 32], CodecError> {
        let tbs_bytes = to_canonical_cbor(&self.tbs)?;
        Ok(derive_genesis_id(&tbs_bytes, &self.signature))
    }

    /// Verifies the OwnerId binding and the Owner Root signature over Genesis TBS.
    pub fn verify(&self) -> Result<(), ControlError> {
        if derive_owner_id(&self.tbs.owner_root_signing_public_key) != self.tbs.owner_id {
            return Err(ControlError::OwnerIdentityMismatch);
        }
        let tbs = to_canonical_cbor(&self.tbs)?;
        let mut payload = Vec::with_capacity(GENESIS_SIG_DOMAIN.len() + tbs.len());
        payload.extend_from_slice(GENESIS_SIG_DOMAIN);
        payload.extend_from_slice(&tbs);
        let verifier = Ed25519Verifier::from_public_key(&self.tbs.owner_root_signing_public_key)
            .map_err(map_signing_error)?;
        verifier
            .verify(&payload, &self.signature)
            .map_err(map_signing_error)
    }
}

fn map_signing_error(error: SigningError) -> ControlError {
    ControlError::SignatureVerification(error.to_string())
}

/// Derive OwnerId: SHA3-256("FORTIQ-OWNER-ID-v1:" || owner_root_signing_public_key)
pub fn derive_owner_id(public_key: &[u8]) -> OwnerId {
    let mut hasher = Sha3_256::new();
    hasher.update(OWNER_ID_DOMAIN);
    hasher.update(public_key);
    let digest: [u8; 32] = hasher.finalize().into();
    OwnerId::from_bytes(digest)
}

/// Derive GenesisId: SHA3-256("FORTIQ-GENESIS-ID-v1:" || canonical_cbor(TBS) || signature)
pub fn derive_genesis_id(tbs_bytes: &[u8], signature: &[u8]) -> [u8; 32] {
    let mut hasher = Sha3_256::new();
    hasher.update(GENESIS_ID_DOMAIN);
    hasher.update(tbs_bytes);
    hasher.update(signature);
    hasher.finalize().into()
}

/// One-time or scoped client bootstrap invitation signed by Owner.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct JoinInvitation {
    pub network_id: NetworkId,
    pub genesis_id: [u8; 32],
    pub bootstrap_peers: Vec<String>,
    #[serde(with = "crate::canonical::records::serde_bytes_16")]
    pub join_nonce: [u8; 16],
    pub expires_at: u64,
    pub capability_template: u32,
    #[serde(with = "serde_vec_bytes")]
    pub owner_signature: Vec<u8>,
}

/// Application entity enrollment certificate signed by Owner Root.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct EnrollmentCertificate {
    pub network_id: NetworkId,
    pub entity_id: EntityId,
    #[serde(with = "serde_vec_bytes")]
    pub signing_public_key: Vec<u8>,
    pub capabilities: u32,
    pub valid_until: u64,
    #[serde(with = "serde_vec_bytes")]
    pub owner_signature: Vec<u8>,
}

/// Client Segment boundary descriptor co-signed by Owner and accepted by Client.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SegmentDescriptor {
    pub version: u16,
    pub network_id: NetworkId,
    pub segment_id: SegmentId,
    pub owner_id: OwnerId,
    #[serde(with = "serde_vec_bytes")]
    pub operator_segment_hpke_public_key: Vec<u8>,
    pub client_entity_id: EntityId,
    #[serde(with = "serde_vec_bytes")]
    pub client_signing_public_key: Vec<u8>,
    #[serde(with = "serde_vec_bytes")]
    pub client_hpke_public_key: Vec<u8>,
    pub key_epoch: u64,
    pub quota_profile: u32,
    pub created_at: u64,
    #[serde(with = "serde_vec_bytes")]
    pub owner_signature: Vec<u8>,
    #[serde(with = "serde_vec_bytes")]
    pub client_acceptance_signature: Vec<u8>,
}

/// Authoritative revocation list signed by Owner Root with monotonic epoch.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct RevocationList {
    pub network_id: NetworkId,
    pub revocation_epoch: u64,
    pub revoked_entities: Vec<EntityId>,
    pub revoked_keys: Vec<KeyId>,
    pub reason: String,
    pub created_at: u64,
    #[serde(with = "serde_vec_bytes")]
    pub owner_signature: Vec<u8>,
}

/// Ephemeral device-to-entity binding linking application EntityId to transport PeerId.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DeviceBinding {
    pub network_id: NetworkId,
    pub entity_id: EntityId,
    pub peer_id: String,
    pub valid_until: u64,
    #[serde(with = "serde_vec_bytes")]
    pub entity_signature: Vec<u8>,
}

mod serde_vec_bytes {
    use serde::{de::Visitor, Deserializer, Serializer};
    use std::fmt;

    pub fn serialize<S>(bytes: &[u8], serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        serializer.serialize_bytes(bytes)
    }

    struct ByteBufVisitor;

    impl<'de> Visitor<'de> for ByteBufVisitor {
        type Value = Vec<u8>;

        fn expecting(&self, formatter: &mut fmt::Formatter) -> fmt::Result {
            formatter.write_str("a byte array")
        }

        fn visit_bytes<E>(self, v: &[u8]) -> Result<Self::Value, E>
        where
            E: serde::de::Error,
        {
            Ok(v.to_vec())
        }

        fn visit_byte_buf<E>(self, v: Vec<u8>) -> Result<Self::Value, E>
        where
            E: serde::de::Error,
        {
            Ok(v)
        }

        fn visit_seq<A>(self, mut seq: A) -> Result<Self::Value, A::Error>
        where
            A: serde::de::SeqAccess<'de>,
        {
            let mut vec = Vec::new();
            while let Some(b) = seq.next_element()? {
                vec.push(b);
            }
            Ok(vec)
        }
    }

    pub fn deserialize<'de, D>(deserializer: D) -> Result<Vec<u8>, D::Error>
    where
        D: Deserializer<'de>,
    {
        deserializer.deserialize_bytes(ByteBufVisitor)
    }
}
