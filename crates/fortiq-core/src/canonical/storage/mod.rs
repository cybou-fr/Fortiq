//! Canonical Storage Engine, Reed-Solomon Erasure Coding, and Stripe Pipelines.
//!
//! Implements Phase 6 of FORTIQ Canonical Architecture v3:
//! - Abstract `ErasureCoder` and `ReedSolomonCoder`
//! - Storage class replication vs RS threshold policy
//! - Adaptive profile selection matching eligible storage peers
//! - Shard integrity verification with independent BLAKE3 checksums
//! - Streaming multi-stripe Blob encoder and decoder

pub mod erasure;
pub mod policy;
pub mod stripe;

#[cfg(test)]
mod tests;

pub use erasure::{ErasureCoder, ErasureError, ReedSolomonCoder, Shard, ValidatedRsProfile};
pub use policy::{
    evaluate_shard_health, select_rs_profile, should_erasure_code, ShardHealth,
    DEFAULT_LOGICAL_STRIPE_SIZE_BYTES, STATE_PACK_RS_THRESHOLD_BYTES,
};
pub use stripe::{StripeDecoder, StripeEncoder};
