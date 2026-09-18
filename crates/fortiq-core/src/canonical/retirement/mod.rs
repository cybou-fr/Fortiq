//! Legacy Retirement and Canonical Transition Layer.
//!
//! Implements Phase 14 of FORTIQ Canonical Architecture v3:
//! - Formal deprecation of permanent `OPERATOR`/`MANAGED` node roles.
//! - Retirement of static `operator_peer_id` authority in favor of cryptographic capabilities.
//! - Migration of mutable SQLite ticket schemas into immutable canonical EventGraph streams.

pub mod authority;
pub mod migration;

// v3 epoch-oriented tests were superseded by security_audit_phase15_v4.

pub use authority::{AuthorityError, CanonicalAuthorityResolver};
pub use migration::{
    LegacyChatMessageRecord, LegacyTicketMigrator, LegacyTicketRecord, MigrationError,
};
