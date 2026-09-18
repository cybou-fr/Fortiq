use serde::{Deserialize, Serialize};
use std::fmt;

macro_rules! define_id {
    ($name:ident, $len:expr, $doc:expr) => {
        #[doc = $doc]
        #[derive(Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
        #[serde(transparent)]
        pub struct $name(#[serde(with = "serde_bytes")] pub [u8; $len]);

        impl $name {
            pub const LEN: usize = $len;

            pub const fn from_bytes(bytes: [u8; $len]) -> Self {
                Self(bytes)
            }

            pub fn as_bytes(&self) -> &[u8; $len] {
                &self.0
            }

            pub fn to_hex(&self) -> String {
                hex::encode(&self.0)
            }

            pub fn from_hex(s: &str) -> Result<Self, hex::FromHexError> {
                let bytes = hex::decode(s)?;
                if bytes.len() != $len {
                    return Err(hex::FromHexError::InvalidStringLength);
                }
                let mut arr = [0u8; $len];
                arr.copy_from_slice(&bytes);
                Ok(Self(arr))
            }
        }

        impl fmt::Debug for $name {
            fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
                write!(f, "{}({})", stringify!($name), self.to_hex())
            }
        }

        impl fmt::Display for $name {
            fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
                write!(f, "{}", self.to_hex())
            }
        }

        impl AsRef<[u8]> for $name {
            fn as_ref(&self) -> &[u8] {
                &self.0
            }
        }
    };
}

// Byte serde helper for fixed array serialization
mod serde_bytes {
    use serde::{de::Visitor, Deserializer, Serializer};
    use std::fmt;

    pub fn serialize<S, const N: usize>(bytes: &[u8; N], serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        serializer.serialize_bytes(bytes)
    }

    struct ByteArrayVisitor<const N: usize>;

    impl<'de, const N: usize> Visitor<'de> for ByteArrayVisitor<N> {
        type Value = [u8; N];

        fn expecting(&self, formatter: &mut fmt::Formatter) -> fmt::Result {
            write!(formatter, "a byte array of length {}", N)
        }

        fn visit_bytes<E>(self, v: &[u8]) -> Result<Self::Value, E>
        where
            E: serde::de::Error,
        {
            if v.len() == N {
                let mut arr = [0u8; N];
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
            let mut arr = [0u8; N];
            for (i, slot) in arr.iter_mut().enumerate() {
                *slot = seq
                    .next_element()?
                    .ok_or_else(|| serde::de::Error::invalid_length(i, &self))?;
            }
            Ok(arr)
        }
    }

    pub fn deserialize<'de, D, const N: usize>(deserializer: D) -> Result<[u8; N], D::Error>
    where
        D: Deserializer<'de>,
    {
        deserializer.deserialize_bytes(ByteArrayVisitor::<N>)
    }
}

define_id!(
    NetworkId,
    32,
    "Unique 256-bit identifier of a FORTIQ sovereign network instance."
);
define_id!(
    OwnerId,
    32,
    "Cryptographic hash of the Owner Root verification key."
);
define_id!(
    EntityId,
    32,
    "Application-level identity of an actor (person, device, bot)."
);
define_id!(
    SegmentId,
    32,
    "Cryptographic boundary identifier for tenant data."
);
define_id!(
    ObjectId,
    32,
    "Content-addressed canonical identifier: SHA3-256('FORTIQ-OBJECT-ID-v1' || TBS || Sig)."
);
define_id!(
    KeyId,
    32,
    "Segment-scoped identifier for an HPKE recipient or signing key."
);
define_id!(
    BlobId,
    32,
    "Unique content identifier for an encrypted file blob."
);
define_id!(
    StreamId,
    16,
    "Writer stream identifier within an entity/segment."
);
define_id!(TicketId, 16, "Support ticket identifier.");

/// Canonical storage class defining durability and replication strategy.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[repr(u8)]
pub enum StorageClass {
    /// Genesis, membership, revocation, and policies. High direct replication, no RS.
    Control = 0,
    /// EventPacks and snapshots. Replicated to bounded peers (<64 KiB) or RS (>=64 KiB).
    StatePack = 1,
    /// Encrypted file stripes and attachments. Streaming Reed-Solomon erasure-coded.
    Blob = 2,
}

impl StorageClass {
    pub const fn as_u8(&self) -> u8 {
        *self as u8
    }

    pub const fn from_u8(v: u8) -> Option<Self> {
        match v {
            0 => Some(Self::Control),
            1 => Some(Self::StatePack),
            2 => Some(Self::Blob),
            _ => None,
        }
    }
}

/// Versioned cryptographic suite identifier.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[repr(u16)]
pub enum CryptoProfileId {
    /// Reserved historical PQ-capable profile identifier.
    FortiqPq1 = 1,
    /// Classical development profile currently used by the runtime.
    ///
    /// This profile uses Ed25519 signing, X25519 key agreement, HKDF-SHA256, and
    /// ChaCha20-Poly1305 AEAD. Real PQ-capable profiles remain reserved for a later
    /// implementation and must not claim to be active while the runtime does not
    /// provide the corresponding primitives.
    FortiqClassicalDev1 = 2,
}

impl CryptoProfileId {
    pub const fn as_u16(&self) -> u16 {
        *self as u16
    }

    pub const fn from_u16(v: u16) -> Option<Self> {
        match v {
            1 => Some(Self::FortiqPq1),
            2 => Some(Self::FortiqClassicalDev1),
            _ => None,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::CryptoProfileId;

    #[test]
    fn crypto_profile_ids_never_change_meaning() {
        assert_eq!(CryptoProfileId::FortiqPq1.as_u16(), 1);
        assert_eq!(CryptoProfileId::FortiqClassicalDev1.as_u16(), 2);
        assert_eq!(
            CryptoProfileId::from_u16(1),
            Some(CryptoProfileId::FortiqPq1)
        );
        assert_eq!(
            CryptoProfileId::from_u16(2),
            Some(CryptoProfileId::FortiqClassicalDev1)
        );
    }
}

/// Reed-Solomon profile parameters for erasure coding.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct RsProfile {
    pub data_shards: u8,
    pub parity_shards: u8,
}

impl RsProfile {
    pub const DEFAULT: Self = Self {
        data_shards: 4,
        parity_shards: 2,
    };

    pub const fn new(data_shards: u8, parity_shards: u8) -> Self {
        Self {
            data_shards,
            parity_shards,
        }
    }

    pub fn total_shards(&self) -> usize {
        (self.data_shards as usize) + (self.parity_shards as usize)
    }
}
