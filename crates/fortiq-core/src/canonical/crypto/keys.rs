use zeroize::{Zeroize, ZeroizeOnDrop};

/// Symmetric Data Encryption Key (DEK) for ChaCha20Poly1305.
/// Automatically zeroizes memory on drop.
#[derive(Clone, Zeroize, ZeroizeOnDrop)]
pub struct DataEncryptionKey(pub [u8; 32]);

impl DataEncryptionKey {
    pub fn new(bytes: [u8; 32]) -> Self {
        Self(bytes)
    }

    pub fn as_bytes(&self) -> &[u8; 32] {
        &self.0
    }
}

/// Root master seed from which per-client operator HPKE keypairs are derived.
/// Automatically zeroizes memory on drop.
#[derive(Clone, Zeroize, ZeroizeOnDrop)]
pub struct OwnerSegmentMasterSeed(pub [u8; 32]);

impl OwnerSegmentMasterSeed {
    pub fn new(bytes: [u8; 32]) -> Self {
        Self(bytes)
    }

    pub fn as_bytes(&self) -> &[u8; 32] {
        &self.0
    }
}

/// Derived segment-specific secret key material for an operator session.
/// Automatically zeroizes memory on drop.
#[derive(Clone, Zeroize, ZeroizeOnDrop)]
pub struct DerivedSegmentSecret(pub [u8; 32]);

impl DerivedSegmentSecret {
    pub fn new(bytes: [u8; 32]) -> Self {
        Self(bytes)
    }

    pub fn as_bytes(&self) -> &[u8; 32] {
        &self.0
    }
}

/// Raw 256-bit entropy derived from a 24-word BIP-39 mnemonic.
/// Automatically zeroizes memory on drop.
#[derive(Clone, Debug, PartialEq, Eq, Zeroize, ZeroizeOnDrop)]
pub struct MnemonicEntropy(pub [u8; 32]);

impl MnemonicEntropy {
    pub fn new(bytes: [u8; 32]) -> Self {
        Self(bytes)
    }

    pub fn as_bytes(&self) -> &[u8; 32] {
        &self.0
    }
}

/// Seed for owner root signing key.
/// Automatically zeroizes memory on drop.
#[derive(Clone, Zeroize, ZeroizeOnDrop)]
pub struct OwnerRootSigningSeed(pub [u8; 32]);

impl OwnerRootSigningSeed {
    pub fn new(bytes: [u8; 32]) -> Self {
        Self(bytes)
    }

    pub fn as_bytes(&self) -> &[u8; 32] {
        &self.0
    }
}

/// Seed for operator session ephemeral signing key.
/// Automatically zeroizes memory on drop.
#[derive(Clone, Zeroize, ZeroizeOnDrop)]
pub struct OperatorSessionSeed(pub [u8; 32]);

impl OperatorSessionSeed {
    pub fn new(bytes: [u8; 32]) -> Self {
        Self(bytes)
    }

    pub fn as_bytes(&self) -> &[u8; 32] {
        &self.0
    }
}
