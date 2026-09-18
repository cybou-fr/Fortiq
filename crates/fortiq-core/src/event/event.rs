use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};

use crate::object::{Ed25519Signer, ObjectId, PublicKey, SignedObject};
use crate::ticket::TicketEvent;

pub const EVENT_PAYLOAD_DOMAIN: &[u8] = b"FORTIQ-EVENT-V5";

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "type", content = "data")]
pub enum EventPayload {
    Ticket(TicketEvent),
    Custom { name: String, data: Vec<u8> },
}

/// The inner, to-be-signed body of an Event.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct EventBody {
    pub parents: Vec<ObjectId>,
    pub timestamp: u64,
    pub lamport: u64,
    pub payload: EventPayload,
}

/// An immutable, signed Event node in the FORTIQ v5 causal EventGraph.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Event {
    pub id: ObjectId,
    pub parents: Vec<ObjectId>,
    pub author: PublicKey,
    pub timestamp: u64,
    pub lamport: u64,
    pub payload: EventPayload,
}

impl Event {
    /// Creates a new Event, signs it with the provided signer, and returns both the Event and SignedObject.
    pub fn new(
        parents: Vec<ObjectId>,
        timestamp: u64,
        lamport: u64,
        payload: EventPayload,
        signer: &Ed25519Signer,
    ) -> Result<(Self, SignedObject)> {
        let body = EventBody {
            parents: parents.clone(),
            timestamp,
            lamport,
            payload: payload.clone(),
        };

        let mut payload_bytes = Vec::new();
        ciborium::into_writer(&body, &mut payload_bytes)
            .context("failed to serialize event body to canonical CBOR")?;

        let signed_obj = SignedObject::sign(signer, payload_bytes, timestamp);

        let event = Self {
            id: signed_obj.id,
            parents,
            author: signed_obj.author,
            timestamp,
            lamport,
            payload,
        };

        Ok((event, signed_obj))
    }

    /// Deserializes and verifies an Event from a SignedObject.
    pub fn from_signed_object(obj: &SignedObject) -> Result<Self> {
        obj.verify().context("invalid SignedObject envelope")?;

        let body: EventBody = ciborium::from_reader(obj.payload.as_slice())
            .context("failed to decode event body from CBOR payload")?;

        Ok(Self {
            id: obj.id,
            parents: body.parents,
            author: obj.author,
            timestamp: body.timestamp,
            lamport: body.lamport,
            payload: body.payload,
        })
    }

    /// Serializes this Event into a SignedObject using the given Signer.
    pub fn to_signed_object(&self, signer: &Ed25519Signer) -> Result<SignedObject> {
        let body = EventBody {
            parents: self.parents.clone(),
            timestamp: self.timestamp,
            lamport: self.lamport,
            payload: self.payload.clone(),
        };

        let mut payload_bytes = Vec::new();
        ciborium::into_writer(&body, &mut payload_bytes)
            .context("failed to serialize event body to canonical CBOR")?;

        let obj = SignedObject::sign(signer, payload_bytes, self.timestamp);
        Ok(obj)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ticket::TicketPriority;

    #[test]
    fn test_event_sign_and_roundtrip() {
        let signer = Ed25519Signer::generate();
        let payload = EventPayload::Ticket(TicketEvent::Created {
            ticket_id: "t-1".into(),
            title: "Disk failure".into(),
            description: "Sector error".into(),
            priority: TicketPriority::High,
            client_peer_id: "client-peer-1".into(),
        });

        let (event, signed_obj) =
            Event::new(vec![], 1_700_000_000, 1, payload.clone(), &signer).unwrap();

        assert_eq!(event.id, signed_obj.id);
        assert_eq!(event.lamport, 1);

        let recovered = Event::from_signed_object(&signed_obj).unwrap();
        assert_eq!(event, recovered);
    }
}
