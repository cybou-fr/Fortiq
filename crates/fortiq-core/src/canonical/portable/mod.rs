//! Portable Operator Engine for FORTIQ Canonical Architecture v3.
//!
//! Provides 24-word mnemonic key derivation, Operator Session Certificates,
//! and zero-disk-footprint in-memory decrypted workspaces with lock/wipe guarantees.

pub mod certificate;
pub mod mnemonic;
pub mod workspace;

#[cfg(test)]
mod tests;

pub use certificate::{CertificateError, OperatorSessionCertificate, SESSION_CERT_SIG_DOMAIN};
pub use mnemonic::{
    MnemonicDeriver, MnemonicError, MNEMONIC_KDF_SALT, OPERATOR_SESSION_INFO, ROOT_SIGNING_INFO,
    SEGMENT_MASTER_INFO,
};
pub use workspace::{MemoryWorkspace, WorkspaceError};
