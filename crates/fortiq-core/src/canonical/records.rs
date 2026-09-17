use crate::canonical::types::{
    AccessEpoch, BlobId, CryptoProfileId, KeyId, NetworkId, ObjectId, RsProfile, SegmentId,
    StorageClass, StreamId, TicketId,
};
use serde::{Deserialize, Deserializer, Serialize, Serializer};

/// Canonical Object To-Be-Signed (TBS) header.
/// Encoded as a fixed-order 12-element CBOR array:
/// [version, network_id, segment_id, storage_class, writer_key_id, writer_stream_id,
///  writer_seq, prev_pack_id, crypto_profile, envelope_set_digest, ciphertext_digest, ciphertext_len]
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ObjectTbs {
    pub version: u16,
    pub network_id: NetworkId,
    pub segment_id: Option<SegmentId>,
    pub storage_class: StorageClass,
    pub writer_key_id: KeyId,
    pub writer_stream_id: StreamId,
    pub writer_seq: u64,
    pub prev_pack_id: Option<ObjectId>,
    pub crypto_profile: CryptoProfileId,
    pub envelope_set_digest: [u8; 32],
    pub ciphertext_digest: [u8; 32],
    pub ciphertext_len: u64,
}

type ObjectTbsWireTuple = (
    u16,
    NetworkId,
    Option<SegmentId>,
    u8,
    KeyId,
    StreamId,
    u64,
    Option<ObjectId>,
    u16,
    serde_bytes_32::Bytes32,
    serde_bytes_32::Bytes32,
    u64,
);

impl Serialize for ObjectTbs {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        let tuple: ObjectTbsWireTuple = (
            self.version,
            self.network_id,
            self.segment_id,
            self.storage_class.as_u8(),
            self.writer_key_id,
            self.writer_stream_id,
            self.writer_seq,
            self.prev_pack_id,
            self.crypto_profile.as_u16(),
            serde_bytes_32::Bytes32(self.envelope_set_digest),
            serde_bytes_32::Bytes32(self.ciphertext_digest),
            self.ciphertext_len,
        );
        tuple.serialize(serializer)
    }
}

impl<'de> Deserialize<'de> for ObjectTbs {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        let tuple: ObjectTbsWireTuple = Deserialize::deserialize(deserializer)?;
        let storage_class = StorageClass::from_u8(tuple.3).ok_or_else(|| {
            serde::de::Error::custom(format!("invalid storage class: {}", tuple.3))
        })?;
        let crypto_profile = CryptoProfileId::from_u16(tuple.8).ok_or_else(|| {
            serde::de::Error::custom(format!("invalid crypto profile: {}", tuple.8))
        })?;

        Ok(Self {
            version: tuple.0,
            network_id: tuple.1,
            segment_id: tuple.2,
            storage_class,
            writer_key_id: tuple.4,
            writer_stream_id: tuple.5,
            writer_seq: tuple.6,
            prev_pack_id: tuple.7,
            crypto_profile,
            envelope_set_digest: tuple.9 .0,
            ciphertext_digest: tuple.10 .0,
            ciphertext_len: tuple.11,
        })
    }
}

/// Fully authenticated Signed Object container.
/// Encoded as a fixed-order 2-element CBOR array: [tbs, signature].
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SignedObject {
    pub tbs: ObjectTbs,
    #[serde(with = "serde_bytes")]
    pub signature: Vec<u8>,
}

/// Recipient Key Envelope.
/// Encoded as a fixed-order 4-element CBOR array: [key_id, key_epoch, hpke_enc, sealed_key].
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct RecipientEnvelope {
    pub key_id: KeyId,
    pub key_epoch: u64,
    #[serde(with = "serde_bytes")]
    pub hpke_enc: Vec<u8>,
    #[serde(with = "serde_bytes")]
    pub sealed_key: Vec<u8>,
}

/// Logical application-level event.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "type", content = "data")]
pub enum LogicalEvent {
    TicketCreated {
        ticket_id: TicketId,
        title: String,
        initial_access_epoch: AccessEpoch,
    },
    TicketStateChanged {
        ticket_id: TicketId,
        new_state: u8,
        epoch: u64,
    },
    ChatMessage {
        ticket_id: TicketId,
        seq: u64,
        body: String,
    },
    ChatMessageRevised {
        ticket_id: TicketId,
        original_seq: u64,
        replacement_body: String,
    },
    FileAttached {
        ticket_id: TicketId,
        blob_id: BlobId,
        filename: String,
        size_bytes: u64,
    },
    AccessEpochGranted {
        ticket_id: TicketId,
        access_epoch: [u8; 16],
    },
    AccessEpochRevoked {
        ticket_id: TicketId,
        access_epoch: [u8; 16],
    },
}

/// Plaintext body of an EventPack before symmetric encryption.
/// Encoded as a fixed-order 5-element CBOR array:
/// [schema_version, ticket_id, ticket_crypto_epoch, pack_nonce, events]
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct EventPackPlaintext {
    pub schema_version: u16,
    pub ticket_id: Option<TicketId>,
    pub ticket_crypto_epoch: Option<u64>,
    #[serde(with = "serde_bytes_16")]
    pub pack_nonce: [u8; 16],
    pub events: Vec<LogicalEvent>,
}

/// Stripe manifest within a BlobManifest.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct StripeManifest {
    pub stripe_index: u32,
    pub plain_len: u32,
    pub cipher_len: u32,
    pub shard_hashes: Vec<serde_bytes_32::Bytes32>,
}

/// Manifest detailing a Reed-Solomon encoded Blob.
/// Encoded as a fixed-order 8-element CBOR array:
/// [version, network_id, segment_id, blob_id, ciphertext_len, stripe_size, rs_profile, stripes]
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct BlobManifest {
    pub version: u16,
    pub network_id: NetworkId,
    pub segment_id: SegmentId,
    pub blob_id: BlobId,
    pub ciphertext_len: u64,
    pub stripe_size: u32,
    pub rs_profile: RsProfile,
    pub stripes: Vec<StripeManifest>,
}

pub mod serde_bytes_32 {
    use serde::{de::Visitor, Deserializer, Serializer};
    use std::fmt;

    #[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
    pub struct Bytes32(pub [u8; 32]);

    impl serde::Serialize for Bytes32 {
        fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
        where
            S: Serializer,
        {
            serializer.serialize_bytes(&self.0)
        }
    }

    struct Bytes32Visitor;

    impl<'de> Visitor<'de> for Bytes32Visitor {
        type Value = Bytes32;

        fn expecting(&self, formatter: &mut fmt::Formatter) -> fmt::Result {
            formatter.write_str("a byte array of length 32")
        }

        fn visit_bytes<E>(self, v: &[u8]) -> Result<Self::Value, E>
        where
            E: serde::de::Error,
        {
            if v.len() == 32 {
                let mut arr = [0u8; 32];
                arr.copy_from_slice(v);
                Ok(Bytes32(arr))
            } else {
                Err(E::invalid_length(v.len(), &self))
            }
        }

        fn visit_byte_buf<E>(self, v: Vec<u8>) -> Result<Self::Value, E>
        where
            E: serde::de::Error,
        {
            self.visit_bytes(&v)
        }

        fn visit_seq<A>(self, mut seq: A) -> Result<Self::Value, A::Error>
        where
            A: serde::de::SeqAccess<'de>,
        {
            let mut arr = [0u8; 32];
            for (i, slot) in arr.iter_mut().enumerate() {
                *slot = seq
                    .next_element()?
                    .ok_or_else(|| serde::de::Error::invalid_length(i, &self))?;
            }
            Ok(Bytes32(arr))
        }
    }

    impl<'de> serde::Deserialize<'de> for Bytes32 {
        fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
        where
            D: Deserializer<'de>,
        {
            deserializer.deserialize_bytes(Bytes32Visitor)
        }
    }
}

pub mod serde_bytes_16 {
    use serde::{de::Visitor, Deserializer, Serializer};
    use std::fmt;

    pub fn serialize<S>(bytes: &[u8; 16], serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        serializer.serialize_bytes(bytes)
    }

    struct Bytes16Visitor;

    impl<'de> Visitor<'de> for Bytes16Visitor {
        type Value = [u8; 16];

        fn expecting(&self, formatter: &mut fmt::Formatter) -> fmt::Result {
            formatter.write_str("a byte array of length 16")
        }

        fn visit_bytes<E>(self, v: &[u8]) -> Result<Self::Value, E>
        where
            E: serde::de::Error,
        {
            if v.len() == 16 {
                let mut arr = [0u8; 16];
                arr.copy_from_slice(v);
                Ok(arr)
            } else {
                Err(E::invalid_length(v.len(), &self))
            }
        }

        fn visit_byte_buf<E>(self, v: Vec<u8>) -> Result<Self::Value, E>
        where
            E: serde::de::Error,
        {
            self.visit_bytes(&v)
        }

        fn visit_seq<A>(self, mut seq: A) -> Result<Self::Value, A::Error>
        where
            A: serde::de::SeqAccess<'de>,
        {
            let mut arr = [0u8; 16];
            for (i, slot) in arr.iter_mut().enumerate() {
                *slot = seq
                    .next_element()?
                    .ok_or_else(|| serde::de::Error::invalid_length(i, &self))?;
            }
            Ok(arr)
        }
    }

    pub fn deserialize<'de, D>(deserializer: D) -> Result<[u8; 16], D::Error>
    where
        D: Deserializer<'de>,
    {
        deserializer.deserialize_bytes(Bytes16Visitor)
    }
}

mod serde_bytes {
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
