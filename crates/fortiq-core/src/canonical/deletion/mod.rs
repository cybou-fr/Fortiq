//! Sovereign Deletion, Physical Purge Authorization, Anti-Resurrection, and GC.
//!
//! Implements Phase 13 of FORTIQ Canonical Architecture v3:
//! - Signed logical deletion via `SignedTombstone`.
//! - Cryptographically signed, delayed physical garbage collection via `PurgeAuthorization`.
//! - Permanent `AntiResurrectionTracker` preventing stale nodes from re-introducing deleted state.
//! - `PhysicalGarbageCollector` executing irreversible ciphertext shard and object reclamation.

pub mod anti_resurrection;
pub mod gc;
pub mod purge_auth;
pub mod tombstone;

#[cfg(test)]
mod tests;

pub use anti_resurrection::{AntiResurrectionError, AntiResurrectionTracker};
pub use gc::{GcError, PhysicalGarbageCollector, PurgeAuditReceipt, PurgeableStorage};
pub use purge_auth::{
    PurgeAuthError, PurgeAuthorization, PurgeAuthorizationTbs, PURGE_AUTH_SIG_DOMAIN,
};
pub use tombstone::{
    SignedTombstone, TombstoneError, TombstoneTbs, TOMBSTONE_ID_DOMAIN, TOMBSTONE_SIG_DOMAIN,
};
