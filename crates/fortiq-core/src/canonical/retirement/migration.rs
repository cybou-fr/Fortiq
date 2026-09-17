//! Legacy SQLite to Canonical Event Graph Migration.
//!
//! Hard Invariants (Phase 14 / docs/spec/21-implementation-roadmap.md):
//! - Retire mutable canonical SQLite ticket models.
//! - Project legacy mutable tickets and chat messages into immutable canonical
//!   EventPack sequences on the EventGraph.
//! - Produce deterministic TicketSnapshots from migrated event sequences.

use thiserror::Error;

use crate::canonical::events::graph::{EventGraph, EventGraphError, VerifiedEventPack};
use crate::canonical::events::reducer::{reduce_ticket, SimpleRoleResolver};
use crate::canonical::events::snapshot::TicketSnapshot;
use crate::canonical::records::{EventPackPlaintext, LogicalEvent, ObjectTbs, SignedObject};
use crate::canonical::types::{
    AccessEpoch, CryptoProfileId, KeyId, NetworkId, StorageClass, StreamId, TicketId,
};

#[derive(Debug, Error)]
pub enum MigrationError {
    #[error("Event graph error: {0}")]
    Graph(#[from] EventGraphError),
    #[error("Invalid UUID / Ticket ID format: {0}")]
    InvalidId(String),
    #[error("Ticket reduction failed after migration")]
    ReductionFailed,
}

/// Representation of a legacy ticket record prior to Canonical Architecture v3.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LegacyTicketRecord {
    pub id: String,
    pub title: String,
    pub description: String,
    pub state_u8: u8,
    pub client_peer_id: String,
    pub operator_peer_id: String,
    pub remote_access_enabled: bool,
    pub created_at: u64,
}

/// Representation of a legacy chat message record prior to Canonical Architecture v3.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LegacyChatMessageRecord {
    pub id: String,
    pub ticket_id: String,
    pub sender: String,
    pub body: String,
    pub timestamp: u64,
}

/// Migrates legacy mutable SQLite tickets into canonical immutable EventPacks.
pub struct LegacyTicketMigrator;

impl LegacyTicketMigrator {
    /// Parses a legacy ticket ID into a canonical TicketId.
    pub fn parse_ticket_id(legacy_id: &str) -> Result<TicketId, MigrationError> {
        if let Ok(u) = uuid::Uuid::parse_str(legacy_id) {
            Ok(TicketId::from_bytes(u.into_bytes()))
        } else {
            let hash = blake3::hash(legacy_id.as_bytes());
            let mut id = [0u8; 16];
            id.copy_from_slice(&hash.as_bytes()[..16]);
            Ok(TicketId::from_bytes(id))
        }
    }

    /// Converts a legacy ticket and its messages into an ordered sequence of canonical LogicalEvents.
    pub fn migrate_ticket_events(
        ticket: &LegacyTicketRecord,
        messages: &[LegacyChatMessageRecord],
    ) -> Result<(TicketId, Vec<LogicalEvent>), MigrationError> {
        let ticket_id = Self::parse_ticket_id(&ticket.id)?;
        let mut events = Vec::new();

        // 1. TicketCreated event
        // Legacy rows did not contain an access epoch. Derive a stable, full-width
        // migration epoch so repeated imports produce the same event graph without
        // truncating the authority token to the old 64-bit timestamp.
        let mut epoch_hasher = blake3::Hasher::new();
        epoch_hasher.update(b"FORTIQ-LEGACY-ACCESS-EPOCH-v1:");
        epoch_hasher.update(ticket.id.as_bytes());
        epoch_hasher.update(&ticket.created_at.to_le_bytes());
        let mut initial_epoch = [0u8; AccessEpoch::LEN];
        initial_epoch.copy_from_slice(&epoch_hasher.finalize().as_bytes()[..AccessEpoch::LEN]);
        events.push(LogicalEvent::TicketCreated {
            ticket_id,
            title: ticket.title.clone(),
            initial_access_epoch: AccessEpoch::from_bytes(initial_epoch),
        });

        // 2. Chat messages in chronological order
        let mut sorted_messages = messages.to_vec();
        sorted_messages.sort_by_key(|m| m.timestamp);

        for (idx, msg) in sorted_messages.into_iter().enumerate() {
            events.push(LogicalEvent::ChatMessage {
                ticket_id,
                seq: (idx + 1) as u64,
                body: msg.body,
            });
        }

        // 3. State update event if state transitioned beyond initial open
        if ticket.state_u8 != 0 {
            events.push(LogicalEvent::TicketStateChanged {
                ticket_id,
                new_state: ticket.state_u8,
                epoch: ticket.created_at + 1,
            });
        }

        // 4. Remote access epoch event if remote access was explicitly enabled
        if ticket.remote_access_enabled {
            let mut epoch_bytes = [0u8; 16];
            let hash = blake3::hash(format!("{}-epoch", ticket.id).as_bytes());
            epoch_bytes.copy_from_slice(&hash.as_bytes()[..16]);
            events.push(LogicalEvent::AccessEpochGranted {
                ticket_id,
                access_epoch: epoch_bytes,
            });
        }

        Ok((ticket_id, events))
    }

    /// Migrates legacy records into the canonical EventGraph and generates a TicketSnapshot.
    pub fn migrate_into_graph(
        graph: &mut EventGraph,
        network_id: NetworkId,
        writer_stream_id: StreamId,
        writer_key_id: KeyId,
        ticket: &LegacyTicketRecord,
        messages: &[LegacyChatMessageRecord],
    ) -> Result<TicketSnapshot, MigrationError> {
        let (ticket_id, events) = Self::migrate_ticket_events(ticket, messages)?;

        let plaintext = EventPackPlaintext {
            schema_version: 1,
            ticket_id: Some(ticket_id),
            ticket_crypto_epoch: Some(1),
            pack_nonce: [0u8; 16],
            events,
        };

        let tbs = ObjectTbs {
            version: 1,
            network_id,
            segment_id: None,
            storage_class: StorageClass::StatePack,
            writer_key_id,
            writer_stream_id,
            writer_seq: 1,
            prev_pack_id: None,
            crypto_profile: CryptoProfileId::FortiqClassicalDev1,
            envelope_set_digest: [0u8; 32],
            ciphertext_digest: [0u8; 32],
            ciphertext_len: 0,
        };

        let signed_obj = SignedObject {
            tbs,
            signature: vec![0u8; 64],
        };

        let verified_pack = VerifiedEventPack::new_unchecked(signed_obj, plaintext)?;
        let _pack_id = graph.append_pack(verified_pack)?;

        let resolver = SimpleRoleResolver::new().with_client(writer_key_id);
        let view =
            reduce_ticket(ticket_id, graph, &resolver).ok_or(MigrationError::ReductionFailed)?;

        let snapshot = TicketSnapshot::create(&view, ticket.created_at);
        Ok(snapshot)
    }
}
