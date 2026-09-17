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

    // Reject indefinite-length CBOR containers (0x1f, 0x3f, 0x5f, 0x7f, 0x9f, 0xbf, 0xff)
    verify_definite_length_cbor(bytes)?;

    // Inspect CBOR value structure for strict adherence to depth, bounds, and definite lengths
    let raw_val: ciborium::Value = ciborium::from_reader(Cursor::new(bytes))
        .map_err(|e| CodecError::DecodingError(e.to_string()))?;

    validate_cbor_value(&raw_val, 0, &limits)?;

    // Deserialize into target type
    ciborium::from_reader(Cursor::new(bytes)).map_err(|e| CodecError::DecodingError(e.to_string()))
}

/// Strict stream scanner rejecting indefinite-length CBOR items (RFC 8949 canonical rule).
pub fn verify_definite_length_cbor(bytes: &[u8]) -> Result<(), CodecError> {
    let mut cursor = 0;
    while cursor < bytes.len() {
        scan_single_cbor_item(bytes, &mut cursor, 0)?;
    }
    Ok(())
}

fn scan_single_cbor_item(bytes: &[u8], cursor: &mut usize, depth: usize) -> Result<(), CodecError> {
    if depth > 32 {
        return Err(CodecError::MaxDepthExceeded(depth, 32));
    }
    if *cursor >= bytes.len() {
        return Err(CodecError::DecodingError(
            "unexpected end of CBOR stream".into(),
        ));
    }

    let initial_byte = bytes[*cursor];
    *cursor += 1;

    let major = initial_byte >> 5;
    let info = initial_byte & 0x1f;

    if info == 31 {
        return Err(CodecError::IndefiniteLengthRejected);
    }

    let val: u64 = if info < 24 {
        info as u64
    } else if info == 24 {
        if *cursor + 1 > bytes.len() {
            return Err(CodecError::DecodingError("truncated CBOR int8".into()));
        }
        let b = bytes[*cursor];
        *cursor += 1;
        b as u64
    } else if info == 25 {
        if *cursor + 2 > bytes.len() {
            return Err(CodecError::DecodingError("truncated CBOR int16".into()));
        }
        let b = u16::from_be_bytes([bytes[*cursor], bytes[*cursor + 1]]);
        *cursor += 2;
        b as u64
    } else if info == 26 {
        if *cursor + 4 > bytes.len() {
            return Err(CodecError::DecodingError("truncated CBOR int32".into()));
        }
        let b = u32::from_be_bytes([
            bytes[*cursor],
            bytes[*cursor + 1],
            bytes[*cursor + 2],
            bytes[*cursor + 3],
        ]);
        *cursor += 4;
        b as u64
    } else if info == 27 {
        if *cursor + 8 > bytes.len() {
            return Err(CodecError::DecodingError("truncated CBOR int64".into()));
        }
        let mut buf = [0u8; 8];
        buf.copy_from_slice(&bytes[*cursor..*cursor + 8]);
        *cursor += 8;
        u64::from_be_bytes(buf)
    } else {
        return Err(CodecError::DecodingError(
            "reserved CBOR additional info".into(),
        ));
    };

    match major {
        0 | 1 => {
            // Integer: value is contained in header
        }
        2 | 3 => {
            // Byte or text string: `val` bytes of payload follow
            let len = usize::try_from(val)
                .map_err(|_| CodecError::DecodingError("string length overflow".into()))?;
            let remaining = bytes.len().saturating_sub(*cursor);
            if len > remaining {
                return Err(CodecError::DecodingError(
                    "truncated CBOR string/bytes".into(),
                ));
            }
            *cursor += len;
        }
        4 => {
            // Array: `val` items follow. Each item must be at least 1 byte.
            let count = usize::try_from(val)
                .map_err(|_| CodecError::DecodingError("array length overflow".into()))?;
            let remaining = bytes.len().saturating_sub(*cursor);
            if count > remaining {
                return Err(CodecError::DecodingError(
                    "array length exceeds remaining stream bytes".into(),
                ));
            }
            for _ in 0..count {
                scan_single_cbor_item(bytes, cursor, depth + 1)?;
            }
        }
        5 => {
            // Map: `2 * val` items follow (key, value pairs). Each item must be at least 1 byte.
            let count = usize::try_from(val)
                .map_err(|_| CodecError::DecodingError("map length overflow".into()))?;
            let items = count
                .checked_mul(2)
                .ok_or_else(|| CodecError::DecodingError("map item count overflow".into()))?;
            let remaining = bytes.len().saturating_sub(*cursor);
            if items > remaining {
                return Err(CodecError::DecodingError(
                    "map length exceeds remaining stream bytes".into(),
                ));
            }
            for _ in 0..items {
                scan_single_cbor_item(bytes, cursor, depth + 1)?;
            }
        }
        6 => {
            // Tag: 1 item follows
            scan_single_cbor_item(bytes, cursor, depth + 1)?;
        }
        7 => {
            // Simple value / float: header has already consumed value
        }
        _ => unreachable!(),
    }

    Ok(())
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_canonical_cbor_definite_length_roundtrip() {
        #[derive(Debug, PartialEq, Eq, Serialize, serde::Deserialize)]
        struct TestRecord {
            id: u64,
            payload: Vec<u8>,
            name: String,
        }

        // Payload with byte patterns that could collide with CBOR control bytes (0xff, 0x9f, 0xbf, etc.)
        let record = TestRecord {
            id: 42,
            payload: vec![0x1f, 0x3f, 0x5f, 0x7f, 0x9f, 0xbf, 0xff, 0x00, 0xaa],
            name: "test-definite".into(),
        };

        let bytes = to_canonical_cbor(&record).expect("serialization succeeds");
        let decoded: TestRecord =
            from_canonical_cbor(&bytes, DecoderLimits::DEFAULT).expect("deserialization succeeds");
        assert_eq!(record, decoded);
    }

    #[test]
    fn test_canonical_cbor_rejects_indefinite_length() {
        // Indefinite array: 0x9f, 0x01, 0xff
        let indef_array = vec![0x9f, 0x01, 0xff];
        let res: Result<Vec<u64>, CodecError> =
            from_canonical_cbor(&indef_array, DecoderLimits::DEFAULT);
        assert!(matches!(res, Err(CodecError::IndefiniteLengthRejected)));

        // Indefinite map: 0xbf, 0xff
        let indef_map = vec![0xbf, 0xff];
        let res: Result<std::collections::HashMap<String, u64>, CodecError> =
            from_canonical_cbor(&indef_map, DecoderLimits::DEFAULT);
        assert!(matches!(res, Err(CodecError::IndefiniteLengthRejected)));

        // Indefinite byte string: 0x5f, 0xff
        let indef_bytes = vec![0x5f, 0xff];
        let res: Result<Vec<u8>, CodecError> =
            from_canonical_cbor(&indef_bytes, DecoderLimits::DEFAULT);
        assert!(matches!(res, Err(CodecError::IndefiniteLengthRejected)));

        // Lone break stop code: 0xff
        let lone_break = vec![0xff];
        let res: Result<u64, CodecError> = from_canonical_cbor(&lone_break, DecoderLimits::DEFAULT);
        assert!(matches!(res, Err(CodecError::IndefiniteLengthRejected)));
    }
}
