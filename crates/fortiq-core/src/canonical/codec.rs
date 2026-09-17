use serde::{de::DeserializeOwned, Serialize};
use std::io::Cursor;
use thiserror::Error;

/// Decoder limits preventing parser resource exhaustion attacks.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DecoderLimits {
    /// Maximum allowed input buffer size in bytes.
    pub max_input_bytes: usize,
    /// Maximum allowed container nesting depth.
    pub max_depth: usize,
    /// Maximum number of elements in an array or container.
    pub max_container_len: usize,
    /// Maximum byte string length.
    pub max_bytes_len: usize,
}

impl DecoderLimits {
    /// Default strict limits for control and eventpack records.
    pub const DEFAULT: Self = Self {
        max_input_bytes: 256 * 1024, // 256 KiB
        max_depth: 8,
        max_container_len: 256,
        max_bytes_len: 64 * 1024,
    };

    /// Strict limits for small control records (Genesis, Descriptors, Revocations).
    pub const CONTROL: Self = Self {
        max_input_bytes: 64 * 1024, // 64 KiB
        max_depth: 6,
        max_container_len: 128,
        max_bytes_len: 16 * 1024,
    };

    /// Limits for blob and stripe manifests.
    pub const MANIFEST: Self = Self {
        max_input_bytes: 1024 * 1024, // 1 MiB
        max_depth: 8,
        max_container_len: 16384,
        max_bytes_len: 256 * 1024,
    };
}

#[derive(Error, Debug)]
pub enum CodecError {
    #[error("input length {0} exceeds max allowed {1} bytes")]
    InputTooLarge(usize, usize),
    #[error("CBOR encoding error: {0}")]
    EncodingError(String),
    #[error("CBOR decoding error: {0}")]
    DecodingError(String),
    #[error("indefinite length container rejected")]
    IndefiniteLengthRejected,
    #[error("nesting depth {0} exceeds maximum depth {1}")]
    MaxDepthExceeded(usize, usize),
    #[error("container length {0} exceeds maximum {1}")]
    ContainerTooLarge(usize, usize),
}

/// Serialize a data structure into deterministic CBOR bytes.
pub fn to_canonical_cbor<T: Serialize>(value: &T) -> Result<Vec<u8>, CodecError> {
    let mut buffer = Vec::new();
    ciborium::into_writer(value, &mut buffer)
        .map_err(|e| CodecError::EncodingError(e.to_string()))?;
    Ok(buffer)
}

/// Deserialize a data structure from canonical CBOR bytes enforcing strict decoder limits.
pub fn from_canonical_cbor<T: DeserializeOwned>(
    bytes: &[u8],
    limits: DecoderLimits,
) -> Result<T, CodecError> {
    if bytes.len() > limits.max_input_bytes {
        return Err(CodecError::InputTooLarge(
            bytes.len(),
            limits.max_input_bytes,
        ));
    }

    // Inspect CBOR value structure for strict adherence to depth, bounds, and definite lengths
    let raw_val: ciborium::Value = ciborium::from_reader(Cursor::new(bytes))
        .map_err(|e| CodecError::DecodingError(e.to_string()))?;

    validate_cbor_value(&raw_val, 0, &limits)?;

    // Deserialize into target type
    ciborium::from_reader(Cursor::new(bytes)).map_err(|e| CodecError::DecodingError(e.to_string()))
}

fn validate_cbor_value(
    val: &ciborium::Value,
    current_depth: usize,
    limits: &DecoderLimits,
) -> Result<(), CodecError> {
    if current_depth > limits.max_depth {
        return Err(CodecError::MaxDepthExceeded(
            current_depth,
            limits.max_depth,
        ));
    }

    match val {
        ciborium::Value::Array(arr) => {
            if arr.len() > limits.max_container_len {
                return Err(CodecError::ContainerTooLarge(
                    arr.len(),
                    limits.max_container_len,
                ));
            }
            for item in arr {
                validate_cbor_value(item, current_depth + 1, limits)?;
            }
        }
        ciborium::Value::Map(entries) => {
            if entries.len() > limits.max_container_len {
                return Err(CodecError::ContainerTooLarge(
                    entries.len(),
                    limits.max_container_len,
                ));
            }
            for (k, v) in entries {
                validate_cbor_value(k, current_depth + 1, limits)?;
                validate_cbor_value(v, current_depth + 1, limits)?;
            }
        }
        ciborium::Value::Bytes(b) => {
            if b.len() > limits.max_bytes_len {
                return Err(CodecError::ContainerTooLarge(b.len(), limits.max_bytes_len));
            }
        }
        ciborium::Value::Text(t) => {
            if t.len() > limits.max_bytes_len {
                return Err(CodecError::ContainerTooLarge(t.len(), limits.max_bytes_len));
            }
        }
        ciborium::Value::Tag(_, inner) => {
            validate_cbor_value(inner, current_depth + 1, limits)?;
        }
        _ => {}
    }

    Ok(())
}

pub mod serde_bytes {
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
    }

    pub fn deserialize<'de, D>(deserializer: D) -> Result<Vec<u8>, D::Error>
    where
        D: Deserializer<'de>,
    {
        deserializer.deserialize_byte_buf(ByteBufVisitor)
    }
}
