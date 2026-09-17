//! Streaming Blob Stripe Pipeline and Manifest Management.
//!
//! Encodes large blob payloads into independent stripes with Reed-Solomon shards
//! and generates canonical BlobManifest and StripeManifest records.

use crate::canonical::records::serde_bytes_32::Bytes32;
use crate::canonical::records::{BlobManifest, StripeManifest};
use crate::canonical::storage::erasure::{ErasureCoder, ErasureError, ReedSolomonCoder, Shard};
use crate::canonical::types::{BlobId, NetworkId, RsProfile, SegmentId};

/// Encodes an arbitrary blob payload into stripes and generates its BlobManifest.
pub struct StripeEncoder<'a> {
    coder: &'a dyn ErasureCoder,
}

impl<'a> StripeEncoder<'a> {
    pub fn new(coder: &'a dyn ErasureCoder) -> Self {
        Self { coder }
    }

    pub fn with_default() -> StripeEncoder<'static> {
        static DEFAULT_CODER: ReedSolomonCoder = ReedSolomonCoder;
        StripeEncoder {
            coder: &DEFAULT_CODER,
        }
    }

    /// Splits `payload` into stripes of `stripe_size`, encodes each stripe with `profile`,
    /// and returns the complete `BlobManifest` alongside all generated `Shard`s by stripe.
    pub fn encode_blob(
        &self,
        network_id: NetworkId,
        segment_id: SegmentId,
        blob_id: BlobId,
        payload: &[u8],
        profile: RsProfile,
        stripe_size: u32,
    ) -> Result<(BlobManifest, Vec<Vec<Shard>>), ErasureError> {
        let stripe_sz = stripe_size.max(1) as usize;
        let mut stripes_manifest = Vec::new();
        let mut stripes_shards = Vec::new();

        let mut offset = 0usize;
        let mut stripe_index = 0u32;

        while offset < payload.len() || (offset == 0 && payload.is_empty()) {
            let end = (offset + stripe_sz).min(payload.len());
            let stripe_data = &payload[offset..end];

            let shards = self.coder.encode(stripe_data, profile)?;
            let shard_hashes: Vec<Bytes32> = shards.iter().map(|s| Bytes32(s.checksum)).collect();

            stripes_manifest.push(StripeManifest {
                stripe_index,
                plain_len: stripe_data.len() as u32,
                cipher_len: stripe_data.len() as u32,
                shard_hashes,
            });

            stripes_shards.push(shards);

            offset += stripe_sz;
            stripe_index += 1;

            if offset >= payload.len() {
                break;
            }
        }

        let manifest = BlobManifest {
            version: 1,
            network_id,
            segment_id,
            blob_id,
            ciphertext_len: payload.len() as u64,
            stripe_size,
            rs_profile: profile,
            stripes: stripes_manifest,
        };

        Ok((manifest, stripes_shards))
    }
}

/// Reconstructs a blob from its BlobManifest and available shards per stripe.
pub struct StripeDecoder<'a> {
    coder: &'a dyn ErasureCoder,
}

impl<'a> StripeDecoder<'a> {
    pub fn new(coder: &'a dyn ErasureCoder) -> Self {
        Self { coder }
    }

    pub fn with_default() -> StripeDecoder<'static> {
        static DEFAULT_CODER: ReedSolomonCoder = ReedSolomonCoder;
        StripeDecoder {
            coder: &DEFAULT_CODER,
        }
    }

    /// Reassembles the full blob data using the manifest and provided shards per stripe.
    pub fn decode_blob(
        &self,
        manifest: &BlobManifest,
        stripes_shards: &[Vec<Option<Shard>>],
    ) -> Result<Vec<u8>, ErasureError> {
        if stripes_shards.len() != manifest.stripes.len() {
            return Err(ErasureError::NotEnoughShards {
                needed: manifest.stripes.len(),
                available: stripes_shards.len(),
            });
        }

        let mut full_payload = Vec::with_capacity(manifest.ciphertext_len as usize);

        for (stripe_meta, shards) in manifest.stripes.iter().zip(stripes_shards.iter()) {
            let stripe_len = stripe_meta.plain_len as usize;
            let reconstructed_stripe =
                self.coder
                    .reconstruct(shards, stripe_len, manifest.rs_profile)?;
            full_payload.extend_from_slice(&reconstructed_stripe);
        }

        Ok(full_payload)
    }
}
