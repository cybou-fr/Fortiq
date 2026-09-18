use serde::{Deserialize, Deserializer, Serialize, Serializer};
use std::fmt;
use std::str::FromStr;

/// A 32-byte cryptographically-secure content identifier.
#[derive(Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Default)]
pub struct ObjectId(pub [u8; 32]);

pub type NetworkId = ObjectId;
pub type EntityId = ObjectId;
pub type OwnerId = ObjectId;
pub type KeyId = ObjectId;
pub type TicketId = String;

impl ObjectId {
    pub const ZERO: Self = Self([0u8; 32]);

    pub const fn from_bytes(bytes: [u8; 32]) -> Self {
        Self(bytes)
    }

    pub const fn as_bytes(&self) -> &[u8; 32] {
        &self.0
    }

    /// Derives an ObjectId using BLAKE3 with domain separation.
    pub fn derive(domain: &[u8], data: &[u8]) -> Self {
        let mut hasher = blake3::Hasher::new();
        hasher.update(domain);
        hasher.update(b":");
        hasher.update(data);
        Self(*hasher.finalize().as_bytes())
    }

    pub fn to_hex(&self) -> String {
        hex::encode(self.0)
    }

    pub fn from_hex(s: &str) -> Result<Self, hex::FromHexError> {
        let mut bytes = [0u8; 32];
        hex::decode_to_slice(s.trim(), &mut bytes)?;
        Ok(Self(bytes))
    }
}

impl fmt::Display for ObjectId {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}", self.to_hex())
    }
}

impl fmt::Debug for ObjectId {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "ObjectId({})", &self.to_hex()[..8])
    }
}

impl FromStr for ObjectId {
    type Err = hex::FromHexError;

    fn from_str(s: &str) -> Result<Self, Self::Err> {
        Self::from_hex(s)
    }
}

impl From<[u8; 32]> for ObjectId {
    fn from(bytes: [u8; 32]) -> Self {
        Self(bytes)
    }
}

impl AsRef<[u8]> for ObjectId {
    fn as_ref(&self) -> &[u8] {
        &self.0
    }
}

impl Serialize for ObjectId {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        if serializer.is_human_readable() {
            serializer.serialize_str(&self.to_hex())
        } else {
            serializer.serialize_bytes(&self.0)
        }
    }
}

impl<'de> Deserialize<'de> for ObjectId {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        if deserializer.is_human_readable() {
            let s = String::deserialize(deserializer)?;
            Self::from_hex(&s).map_err(serde::de::Error::custom)
        } else {
            struct BytesVisitor;
            impl<'de> serde::de::Visitor<'de> for BytesVisitor {
                type Value = ObjectId;

                fn expecting(&self, formatter: &mut fmt::Formatter) -> fmt::Result {
                    formatter.write_str("a 32-byte array")
                }

                fn visit_bytes<E>(self, v: &[u8]) -> Result<Self::Value, E>
                where
                    E: serde::de::Error,
                {
                    if v.len() != 32 {
                        return Err(E::custom(format!("expected 32 bytes, got {}", v.len())));
                    }
                    let mut bytes = [0u8; 32];
                    bytes.copy_from_slice(v);
                    Ok(ObjectId(bytes))
                }

                fn visit_seq<A>(self, mut seq: A) -> Result<Self::Value, A::Error>
                where
                    A: serde::de::SeqAccess<'de>,
                {
                    let mut bytes = [0u8; 32];
                    for (i, item) in bytes.iter_mut().enumerate() {
                        *item = seq
                            .next_element()?
                            .ok_or_else(|| serde::de::Error::invalid_length(i, &self))?;
                    }
                    Ok(ObjectId(bytes))
                }
            }
            deserializer.deserialize_bytes(BytesVisitor)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_object_id_roundtrip() {
        let id = ObjectId::derive(b"TEST", b"hello world");
        let hex = id.to_hex();
        let parsed: ObjectId = hex.parse().unwrap();
        assert_eq!(id, parsed);
    }
}
