//! Immutable local Event Graph storage.
//!
//! Stores signed objects, EventPacks, writer stream chains, tombstones,
//! and canonical head sets in an append-only graph structure.

use crate::canonical::events::stream::{StreamAppendEntry, StreamCursor, StreamError};
use crate::canonical::events::tombstone::{CanonicalHeadSet, Tombstone};
use crate::canonical::records::{EventPackPlaintext, SignedObject};
use crate::canonical::signing::{compute_tbs_bytes, construct_signing_payload, derive_object_id};
use crate::canonical::types::{ObjectId, StreamId, TicketId};
use std::collections::HashMap;
use thiserror::Error;

use std::collections::HashSet;

#[derive(Debug, Error)]
pub enum EventGraphError {
    #[error("Object serialization error: {0}")]
    Serialization(String),
    #[error("Stream validation error: {0}")]
    Stream(#[from] StreamError),
    #[error("Signature verification error: {0}")]
    SignatureVerification(String),
    #[error("Object already exists with id {0}")]
    DuplicateObject(ObjectId),
}

/// Typestate representing an EventPack whose cryptographic signature and TBS binding
/// have been strictly verified against the writer's key before ingestion into EventGraph.
#[derive(Debug, Clone)]
pub struct VerifiedEventPack {
    pub signed_obj: SignedObject,
    pub plaintext: EventPackPlaintext,
    pub stream_id: StreamId,
    pub seq: u64,
    pub prev_pack_id: Option<ObjectId>,
    pub object_id: ObjectId,
}

impl VerifiedEventPack {
    /// Cryptographically verifies signed_obj signature and TBS binding before creating a VerifiedEventPack.
    pub fn verify(
        signed_obj: SignedObject,
        plaintext: EventPackPlaintext,
        verifier: &impl crate::canonical::signing::Verifier,
    ) -> Result<Self, EventGraphError> {
        let tbs_bytes = compute_tbs_bytes(&signed_obj.tbs)
            .map_err(|e| EventGraphError::Serialization(e.to_string()))?;
        let payload = construct_signing_payload(&tbs_bytes);
        verifier
            .verify(&payload, &signed_obj.signature)
            .map_err(|e| EventGraphError::SignatureVerification(e.to_string()))?;

        let object_id = derive_object_id(&tbs_bytes, &signed_obj.signature);
        let stream_id = signed_obj.tbs.writer_stream_id;
        let seq = signed_obj.tbs.writer_seq;
        let prev_pack_id = signed_obj.tbs.prev_pack_id;

        Ok(Self {
            signed_obj,
            plaintext,
            stream_id,
            seq,
            prev_pack_id,
            object_id,
        })
    }

    /// For internal migration and trusted testing builders.
    pub fn new_unchecked(
        signed_obj: SignedObject,
        plaintext: EventPackPlaintext,
    ) -> Result<Self, EventGraphError> {
        let tbs_bytes = compute_tbs_bytes(&signed_obj.tbs)
            .map_err(|e| EventGraphError::Serialization(e.to_string()))?;
        let object_id = derive_object_id(&tbs_bytes, &signed_obj.signature);
        let stream_id = signed_obj.tbs.writer_stream_id;
        let seq = signed_obj.tbs.writer_seq;
        let prev_pack_id = signed_obj.tbs.prev_pack_id;

        Ok(Self {
            signed_obj,
            plaintext,
            stream_id,
            seq,
            prev_pack_id,
            object_id,
        })
    }
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
    /// Predecessor parent map for ancestry graph traversal.
    parent_packs: HashMap<ObjectId, Option<ObjectId>>,
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

    /// Appends a verified EventPack into the graph and updates its writer stream cursor.
    pub fn append_pack(&mut self, pack: VerifiedEventPack) -> Result<ObjectId, EventGraphError> {
        let object_id = pack.object_id;

        if self.objects.contains_key(&object_id) {
            return Err(EventGraphError::DuplicateObject(object_id));
        }

        // Validate and update stream cursor
        let cursor = self
            .stream_cursors
            .entry(pack.stream_id)
            .or_insert_with(|| StreamCursor::new(pack.stream_id));

        let entry = cursor.accept_append(pack.seq, pack.prev_pack_id, object_id)?;

        // Record stream append
        self.stream_chains.entry(pack.stream_id).or_default().push(entry);

        // Record parent relationship for ancestry graph traversal
        self.parent_packs.insert(object_id, pack.prev_pack_id);

        // Record ticket indexing
        if let Some(ticket_id) = pack.plaintext.ticket_id {
            self.ticket_packs
                .entry(ticket_id)
                .or_default()
                .push(object_id);
        }

        // Store object and plaintext
        self.objects.insert(object_id, pack.signed_obj);
        self.plaintexts.insert(object_id, pack.plaintext);

        Ok(object_id)
    }

    /// Returns the set containing the given pack id and all its ancestors recursively.
    pub fn get_pack_ancestors_inclusive(&self, head_id: &ObjectId) -> HashSet<ObjectId> {
        let mut ancestors = HashSet::new();
        let mut current = Some(*head_id);
        while let Some(id) = current {
            if !ancestors.insert(id) {
                break; // cycle protection
            }
            current = self.parent_packs.get(&id).copied().flatten();
        }
        ancestors
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
