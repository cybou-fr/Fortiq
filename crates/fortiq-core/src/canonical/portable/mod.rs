//! Portable Operator Engine for FORTIQ Canonical Architecture v3.
//!
//! Provides 24-word mnemonic key derivation, Operator Session Certificates,
//! and zero-disk-footprint in-memory decrypted workspaces with lock/wipe guarantees.

pub mod certificate;
pub mod mnemonic;
pub mod workspace;

#[cfg(test)]
mod tests;

pub use certificate::{
    CertificateError, OperatorCapabilities, OperatorSessionCertificate, MAX_SESSION_TTL_SECS,
    SESSION_CERT_SIG_DOMAIN,
};
pub use mnemonic::{
    entropy_to_mnemonic, get_wordlist, parse_mnemonic_phrase, word_to_index, MnemonicDeriver,
    MnemonicError, MNEMONIC_KDF_SALT, OPERATOR_SESSION_INFO, ROOT_SIGNING_INFO,
    SEGMENT_MASTER_INFO,
};
pub use workspace::{MemoryWorkspace, WorkspaceError};
