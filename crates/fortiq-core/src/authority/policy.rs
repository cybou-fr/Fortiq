use anyhow::{bail, Result};
use serde::{Deserialize, Serialize};

use crate::object::{
    Ed25519Verifier, EntityId, KeyId, NetworkId, ObjectId, OwnerId, PublicKey, Signature, Signer,
};
use crate::ticket::TicketState;

pub const OPERATOR_PROOF_DOMAIN: &[u8] = b"FORTIQ-OPERATOR-PROOF-V5";
pub const OPERATOR_CERT_DOMAIN: &[u8] = b"FORTIQ-OPERATOR-CERT-V5";
pub const GENESIS_SIG_DOMAIN: &[u8] = b"FORTIQ-GENESIS-V5";

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
pub struct OperatorCapabilities(pub u32);

impl OperatorCapabilities {
    pub const NONE: u32 = 0;
    pub const READ: u32 = 1 << 0;
    pub const WRITE: u32 = 1 << 1;
    pub const SHELL_EXEC: u32 = 1 << 2;
    pub const FILE_TRANSFER: u32 = 1 << 3;
    pub const TICKET_MANAGE: u32 = 1 << 4;
    pub const DIAGNOSTICS: u32 = 1 << 5;
    pub const TICKET_READ: u32 = Self::READ;
    pub const ADMIN: u32 = Self::READ
        | Self::WRITE
        | Self::SHELL_EXEC
        | Self::FILE_TRANSFER
        | Self::TICKET_MANAGE
        | Self::DIAGNOSTICS;
    pub const ALL: u32 = Self::ADMIN;

    pub const fn from_bits(bits: u32) -> Self {
        Self(bits)
    }

    pub const fn bits(&self) -> u32 {
        self.0
    }

    pub fn has(&self, required: u32) -> bool {
        (self.0 & required) == required
    }

    pub fn from_names<I, S>(names: I) -> Self
    where
        I: IntoIterator<Item = S>,
        S: AsRef<str>,
    {
        let mut bits = 0u32;
        for name in names {
            match name.as_ref().to_lowercase().as_str() {
                "admin" | "all" => bits |= Self::ADMIN,
                "read" => bits |= Self::READ,
                "write" => bits |= Self::WRITE,
                "shell" | "shell_exec" => bits |= Self::SHELL_EXEC,
                "file" | "file_transfer" => bits |= Self::FILE_TRANSFER,
                "ticket" | "ticket_manage" => bits |= Self::TICKET_MANAGE,
                "diagnostics" => bits |= Self::DIAGNOSTICS,
                _ => {}
            }
        }
        Self(bits)
    }

    pub fn to_names(&self) -> Vec<String> {
        let mut names = Vec::new();
        if self.has(Self::READ) {
            names.push("read".into());
        }
        if self.has(Self::WRITE) {
            names.push("write".into());
        }
        if self.has(Self::SHELL_EXEC) {
            names.push("shell".into());
        }
        if self.has(Self::FILE_TRANSFER) {
            names.push("file".into());
        }
        if self.has(Self::TICKET_MANAGE) {
            names.push("ticket".into());
        }
        if self.has(Self::DIAGNOSTICS) {
            names.push("diagnostics".into());
        }
        names
    }
}

/// Signed operator session certificate proving authority to act on a node.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct OperatorSessionCertificate {
    pub network_id: NetworkId,
    pub owner_id: OwnerId,
    pub host_entity: EntityId,
    pub operator_key_id: KeyId,
    pub session_pubkey: PublicKey,
    pub capabilities: OperatorCapabilities,
    pub created_at: u64,
    pub expires_at: u64,
    pub signature: Signature,
}

impl OperatorSessionCertificate {
    pub fn compute_tbs_bytes(
        network_id: &NetworkId,
        owner_id: &OwnerId,
        host_entity: &EntityId,
        operator_key_id: &KeyId,
        session_pubkey: &PublicKey,
        capabilities: OperatorCapabilities,
        created_at: u64,
        expires_at: u64,
    ) -> Vec<u8> {
        let mut tbs = Vec::new();
        tbs.extend_from_slice(network_id.as_bytes());
        tbs.extend_from_slice(owner_id.as_bytes());
        tbs.extend_from_slice(host_entity.as_bytes());
        tbs.extend_from_slice(operator_key_id.as_bytes());
        tbs.extend_from_slice(session_pubkey.as_bytes());
        tbs.extend_from_slice(&capabilities.0.to_le_bytes());
        tbs.extend_from_slice(&created_at.to_le_bytes());
        tbs.extend_from_slice(&expires_at.to_le_bytes());
        tbs
    }

    pub fn verify(&self, owner_verifier: &Ed25519Verifier, now: u64) -> Result<()> {
        if now >= self.expires_at {
            bail!("operator session certificate has expired");
        }
        let tbs = Self::compute_tbs_bytes(
            &self.network_id,
            &self.owner_id,
            &self.host_entity,
            &self.operator_key_id,
            &self.session_pubkey,
            self.capabilities,
            self.created_at,
            self.expires_at,
        );
        owner_verifier.verify_domain(OPERATOR_CERT_DOMAIN, &tbs, &self.signature)?;
        Ok(())
    }

    pub fn issue(
        network_id: NetworkId,
        owner_id: OwnerId,
        host_entity: EntityId,
        _client_entity: EntityId,
        operator_key_id: KeyId,
        session_pubkey: PublicKey,
        capabilities: OperatorCapabilities,
        created_at: u64,
        expires_at: u64,
        _nonce: [u8; 16],
        signer: &impl Signer,
    ) -> Result<Self> {
        let tbs = Self::compute_tbs_bytes(
            &network_id,
            &owner_id,
            &host_entity,
            &operator_key_id,
            &session_pubkey,
            capabilities,
            created_at,
            expires_at,
        );
        let signature = signer
            .sign_domain(OPERATOR_CERT_DOMAIN, &tbs)
            .map_err(|e| anyhow::anyhow!("signing error: {:?}", e))?;
        Ok(Self {
            network_id,
            owner_id,
            host_entity,
            operator_key_id,
            session_pubkey,
            capabilities,
            created_at,
            expires_at,
            signature,
        })
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct GenesisTbs {
    pub network_id: NetworkId,
    pub owner_id: OwnerId,
    pub owner_root_signing_public_key: PublicKey,
    pub created_at: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Genesis {
    pub tbs: GenesisTbs,
    pub signature: Signature,
}

impl Genesis {
    pub fn create(tbs: GenesisTbs, signer: &impl Signer) -> Result<Self> {
        let mut bytes = Vec::new();
        bytes.extend_from_slice(tbs.network_id.as_bytes());
        bytes.extend_from_slice(tbs.owner_id.as_bytes());
        bytes.extend_from_slice(tbs.owner_root_signing_public_key.as_bytes());
        bytes.extend_from_slice(&tbs.created_at.to_le_bytes());
        let signature = signer
            .sign_domain(GENESIS_SIG_DOMAIN, &bytes)
            .map_err(|e| anyhow::anyhow!("signing error: {:?}", e))?;
        Ok(Self { tbs, signature })
    }

    pub fn verify(&self) -> Result<()> {
        let mut tbs = Vec::new();
        tbs.extend_from_slice(self.tbs.network_id.as_bytes());
        tbs.extend_from_slice(self.tbs.owner_id.as_bytes());
        tbs.extend_from_slice(self.tbs.owner_root_signing_public_key.as_bytes());
        tbs.extend_from_slice(&self.tbs.created_at.to_le_bytes());

        let verifier = Ed25519Verifier::from_public_key(&self.tbs.owner_root_signing_public_key)?;
        verifier.verify_domain(GENESIS_SIG_DOMAIN, &tbs, &self.signature)?;
        Ok(())
    }
}

pub fn derive_owner_id(pk: &PublicKey) -> OwnerId {
    pk.derive_id()
}

pub fn derive_genesis_id(network_id: &NetworkId, owner_id: &OwnerId) -> ObjectId {
    let mut data = Vec::new();
    data.extend_from_slice(network_id.as_bytes());
    data.extend_from_slice(owner_id.as_bytes());
    ObjectId::derive(b"FORTIQ-GENESIS-ID-V5", &data)
}

/// Cryptographic session proof granting operator capabilities.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct OperatorSessionProof {
    pub operator_peer_id: String,
    pub session_pubkey: PublicKey,
    pub capabilities: OperatorCapabilities,
    pub created_at: u64,
    pub expires_at: u64,
    pub signature: Signature,
}

impl OperatorSessionProof {
    pub fn compute_tbs_bytes(
        operator_peer_id: &str,
        session_pubkey: &PublicKey,
        capabilities: OperatorCapabilities,
        created_at: u64,
        expires_at: u64,
    ) -> Vec<u8> {
        let mut tbs = Vec::new();
        tbs.extend_from_slice(operator_peer_id.as_bytes());
        tbs.extend_from_slice(session_pubkey.as_bytes());
        tbs.extend_from_slice(&capabilities.0.to_le_bytes());
        tbs.extend_from_slice(&created_at.to_le_bytes());
        tbs.extend_from_slice(&expires_at.to_le_bytes());
        tbs
    }

    pub fn verify(&self, owner_pubkey: &PublicKey, now: u64) -> Result<()> {
        if now >= self.expires_at {
            bail!("operator session proof has expired");
        }
        let tbs = Self::compute_tbs_bytes(
            &self.operator_peer_id,
            &self.session_pubkey,
            self.capabilities,
            self.created_at,
            self.expires_at,
        );
        let verifier = Ed25519Verifier::from_public_key(owner_pubkey)?;
        verifier.verify_domain(OPERATOR_PROOF_DOMAIN, &tbs, &self.signature)?;
        Ok(())
    }
}

/// High-level authorization policy evaluator for tickets and shell operations.
#[derive(Debug, Clone, Default)]
pub struct AuthorityPolicy {
    pub root_owner_pubkey: Option<PublicKey>,
}

impl AuthorityPolicy {
    pub fn new(root_owner_pubkey: Option<PublicKey>) -> Self {
        Self { root_owner_pubkey }
    }

    /// Evaluates whether an actor can transition a ticket to a new state.
    pub fn can_transition_ticket(
        &self,
        current_state: TicketState,
        new_state: TicketState,
        _actor_peer_id: &str,
    ) -> bool {
        current_state.can_transition_to(new_state)
    }

    /// Evaluates whether an operator is authorized to execute a shell on a ticket.
    pub fn can_execute_shell(
        &self,
        ticket_state: TicketState,
        proof: Option<&OperatorSessionProof>,
        now: u64,
    ) -> bool {
        if !ticket_state.permits_work() {
            return false;
        }

        // If no root owner is configured, local sovereign policy permits work on active tickets
        let Some(owner_pk) = &self.root_owner_pubkey else {
            return true;
        };

        // Otherwise proof must be present, valid, and carry SHELL_EXEC capability
        match proof {
            Some(p) => {
                p.capabilities.has(OperatorCapabilities::SHELL_EXEC)
                    && p.verify(owner_pk, now).is_ok()
            }
            None => false,
        }
    }
}
