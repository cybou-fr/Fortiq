use anyhow::{Context, Result};
use serde::{de::DeserializeOwned, Serialize};

#[derive(Debug, Clone, Copy, Default)]
pub struct DecoderLimits;

impl DecoderLimits {
    pub const CONTROL: Self = Self;
    pub const RECORD: Self = Self;
}

pub fn to_canonical_cbor<T: Serialize>(value: &T) -> Result<Vec<u8>> {
    let mut bytes = Vec::new();
    ciborium::into_writer(value, &mut bytes).context("failed to encode canonical CBOR")?;
    Ok(bytes)
}

pub fn from_canonical_cbor<T: DeserializeOwned>(bytes: &[u8], _limits: DecoderLimits) -> Result<T> {
    ciborium::from_reader(bytes).context("failed to decode canonical CBOR")
}
