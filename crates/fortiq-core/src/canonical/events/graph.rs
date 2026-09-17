//! Immutable local Event Graph storage.
//!
//! Stores signed objects, EventPacks, writer stream chains, tombstones,
//! and canonical head sets in an append-only graph structure.

use crate::canonical::events::stream::{StreamAppendEntry, StreamCursor, StreamError};
use crate::canonical::events::tombstone::{CanonicalHeadSet, Tombstone};
use crate::canonical::records::{EventPackPlaintext, SignedObject};
use crate::canonical::signing::{compute_tbs_bytes, derive_object_id};
use crate::canonical::types::{ObjectId, StreamId, TicketId};
use std::collections::HashMap;
use thiserror::Error;

#[derive(Debug, Error)]
pub enum EventGraphError {
    #[error("Object serialization error: {0}")]
    Serialization(String),
    #[error("Stream validation error: {0}")]
    Stream(#[from] StreamError),
    #[error("Object already exists with id {0}")]
    DuplicateObject(ObjectId),
}

/// In-memory append-only Event Graph.
#[derive(Default, Debug, Clone)]
pub struct EventGraph {
    /// Stored signed objects by content-addressed ObjectId.
    objects: HashMap<ObjectId, SignedObject>,
    /// Stored plaintexts of decrypted/local EventPacks.
    plaintexts: HashMap<ObjectId, EventPackPlaintext>,
    /// Sequential chain of pack appends per writer stream.
    stream_chains: HashMap<StreamId, Vec<StreamAppendEntry>>,
    /// Active sequence cursors per writer stream.
    stream_cursors: HashMap<StreamId, StreamCursor>,
    /// Index from TicketId to associated pack ObjectIds in append order.
    ticket_packs: HashMap<TicketId, Vec<ObjectId>>,
    /// Active tombstones by target ObjectId.
    tombstones: HashMap<ObjectId, Tombstone>,
    /// Admin canonical head set selections per ticket.
    canonical_heads: HashMap<TicketId, CanonicalHeadSet>,
}

impl EventGraph {
    pub fn new() -> Self {
        Self::default()
    }

    /// Appends a new verified EventPack into the graph and its writer stream.
    pub fn append_pack(
        &mut self,
        signed_obj: SignedObject,
        plaintext: EventPackPlaintext,
        stream_id: StreamId,
        seq: u64,
        prev_pack_id: Option<ObjectId>,
    ) -> Result<ObjectId, EventGraphError> {
        let tbs_bytes = compute_tbs_bytes(&signed_obj.tbs)
            .map_err(|e| EventGraphError::Serialization(e.to_string()))?;
        let object_id = derive_object_id(&tbs_bytes, &signed_obj.signature);

        if self.objects.contains_key(&object_id) {
            return Err(EventGraphError::DuplicateObject(object_id));
        }

        // Validate and update stream cursor
        let cursor = self
            .stream_cursors
            .entry(stream_id)
            .or_insert_with(|| StreamCursor::new(stream_id));

        let entry = cursor.accept_append(seq, prev_pack_id, object_id)?;

        // Record stream append
        self.stream_chains.entry(stream_id).or_default().push(entry);

        // Record ticket indexing
        if let Some(ticket_id) = plaintext.ticket_id {
            self.ticket_packs
                .entry(ticket_id)
                .or_default()
                .push(object_id);
        }

        // Store object and plaintext
        self.objects.insert(object_id, signed_obj);
        self.plaintexts.insert(object_id, plaintext);

        Ok(object_id)
    }

    /// Returns a reference to the signed object if present.
    pub fn get_object(&self, id: &ObjectId) -> Option<&SignedObject> {
        self.objects.get(id)
    }

    /// Returns a reference to the decrypted EventPack plaintext if present.
    pub fn get_plaintext(&self, id: &ObjectId) -> Option<&EventPackPlaintext> {
        self.plaintexts.get(id)
    }

    /// Checks if an object has been logically deleted by a Tombstone.
    pub fn is_tombstoned(&self, id: &ObjectId) -> bool {
        self.tombstones.contains_key(id)
    }

    /// Adds a Tombstone to logically delete an object.
    pub fn add_tombstone(&mut self, tombstone: Tombstone) {
        self.tombstones
            .insert(tombstone.target_object_id, tombstone);
    }

    /// Sets explicit canonical heads for a ticket.
    pub fn set_canonical_heads(&mut self, head_set: CanonicalHeadSet) {
        self.canonical_heads.insert(head_set.ticket_id, head_set);
    }

    /// Returns the canonical head set for a ticket if configured.
    pub fn get_canonical_heads(&self, ticket_id: &TicketId) -> Option<&CanonicalHeadSet> {
        self.canonical_heads.get(ticket_id)
    }

    /// Returns all pack ObjectIds recorded for a ticket in append order.
    pub fn get_ticket_packs(&self, ticket_id: &TicketId) -> &[ObjectId] {
        self.ticket_packs
            .get(ticket_id)
            .map(|v| v.as_slice())
            .unwrap_or(&[])
    }

    /// Returns the cursor for a writer stream.
    pub fn get_stream_cursor(&self, stream_id: &StreamId) -> Option<&StreamCursor> {
        self.stream_cursors.get(stream_id)
    }
}
