//! Reed-Solomon Erasure Coding Engine and Galois Field GF(2^8) arithmetic.
//!
//! Hard Invariants & Specifications (docs/spec/10-storage-classes-reed-solomon.md):
//! - Systematic Cauchy Reed-Solomon matrix ensures all square submatrices are invertible.
//! - Shard checksums are independently verified via BLAKE3-256 before reconstruction.
//! - Corrupt shards are rejected and treated as missing (erasures).
//! - Deterministic reconstruction of original bytes from any k surviving shards.

use crate::canonical::signing::compute_shard_checksum;
use crate::canonical::types::RsProfile;
use serde::{Deserialize, Serialize};
use thiserror::Error;

#[derive(Debug, Error, PartialEq, Eq)]
pub enum ErasureError {
    #[error("Invalid RS profile: data_shards ({data}) and parity_shards ({parity}) must be > 0 and sum <= 256")]
    InvalidProfile { data: u8, parity: u8 },
    #[error(
        "Not enough valid shards to reconstruct: need {needed}, but only {available} are available"
    )]
    NotEnoughShards { needed: usize, available: usize },
    #[error("Corrupted shard at index {index}: checksum mismatch")]
    CorruptedShard { index: u8 },
    #[error("Matrix inversion failed in Galois Field")]
    SingularMatrix,
    #[error("Invalid shard length: expected {expected}, got {got}")]
    ShardLengthMismatch { expected: usize, got: usize },
}

/// Single erasure coded shard with cryptographic BLAKE3 integrity checksum.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Shard {
    pub index: u8,
    pub data: Vec<u8>,
    pub checksum: [u8; 32],
}

impl Shard {
    /// Creates a new shard and automatically computes its BLAKE3 checksum.
    pub fn new(index: u8, data: Vec<u8>) -> Self {
        let checksum = compute_shard_checksum(&data);
        Self {
            index,
            data,
            checksum,
        }
    }

    /// Validates the internal BLAKE3 integrity checksum.
    pub fn verify_checksum(&self) -> bool {
        compute_shard_checksum(&self.data) == self.checksum
    }
}

// -----------------------------------------------------------------------------
// Galois Field GF(2^8) Arithmetic with irreducible polynomial x^8 + x^4 + x^3 + x^2 + 1 (0x11d)
// -----------------------------------------------------------------------------

struct Gf256 {
    exp: [u8; 512],
    log: [u8; 256],
}

impl Gf256 {
    const fn init() -> Self {
        let mut exp = [0u8; 512];
        let mut log = [0u8; 256];
        let mut x = 1u16;

        let mut i = 0usize;
        while i < 255 {
            let b = x as u8;
            exp[i] = b;
            exp[i + 255] = b;
            log[b as usize] = i as u8;

            x <<= 1;
            if x & 0x100 != 0 {
                x ^= 0x11d;
            }
            i += 1;
        }
        exp[510] = exp[0];
        exp[511] = exp[1];

        Self { exp, log }
    }

    #[inline(always)]
    fn mul(&self, a: u8, b: u8) -> u8 {
        if a == 0 || b == 0 {
            0
        } else {
            let idx = (self.log[a as usize] as usize) + (self.log[b as usize] as usize);
            self.exp[idx]
        }
    }

    #[inline(always)]
    fn inv(&self, a: u8) -> Option<u8> {
        if a == 0 {
            None
        } else {
            let log_a = self.log[a as usize] as usize;
            Some(self.exp[255 - log_a])
        }
    }

    #[allow(dead_code)]
    #[inline(always)]
    fn div(&self, a: u8, b: u8) -> Option<u8> {
        if b == 0 {
            None
        } else if a == 0 {
            Some(0)
        } else {
            let log_a = self.log[a as usize] as usize;
            let log_b = self.log[b as usize] as usize;
            let idx = (log_a + 255 - log_b) % 255;
            Some(self.exp[idx])
        }
    }
}

static GF: Gf256 = Gf256::init();

// -----------------------------------------------------------------------------
// Matrix operations over GF(2^8)
// -----------------------------------------------------------------------------

#[derive(Clone)]
struct Matrix {
    rows: usize,
    cols: usize,
    data: Vec<u8>,
}

impl Matrix {
    fn new(rows: usize, cols: usize) -> Self {
        Self {
            rows,
            cols,
            data: vec![0u8; rows * cols],
        }
    }

    #[inline(always)]
    fn get(&self, r: usize, c: usize) -> u8 {
        self.data[r * self.cols + c]
    }

    #[inline(always)]
    fn set(&mut self, r: usize, c: usize, val: u8) {
        self.data[r * self.cols + c] = val;
    }

    /// Inverts a square matrix using Gaussian elimination over GF(2^8).
    fn invert(&self) -> Result<Matrix, ErasureError> {
        if self.rows != self.cols {
            return Err(ErasureError::SingularMatrix);
        }
        let n = self.rows;
        let mut aug = Matrix::new(n, 2 * n);

        for r in 0..n {
            for c in 0..n {
                aug.set(r, c, self.get(r, c));
            }
            aug.set(r, n + r, 1);
        }

        // Gaussian elimination
        for c in 0..n {
            // Find pivot
            let mut pivot = None;
            for r in c..n {
                if aug.get(r, c) != 0 {
                    pivot = Some(r);
                    break;
                }
            }
            let pivot_row = pivot.ok_or(ErasureError::SingularMatrix)?;

            // Swap rows if necessary
            if pivot_row != c {
                for col in 0..(2 * n) {
                    let tmp = aug.get(c, col);
                    aug.set(c, col, aug.get(pivot_row, col));
                    aug.set(pivot_row, col, tmp);
                }
            }

            // Scale pivot row
            let pivot_val = aug.get(c, c);
            let inv_pivot = GF.inv(pivot_val).ok_or(ErasureError::SingularMatrix)?;
            for col in 0..(2 * n) {
                let scaled = GF.mul(aug.get(c, col), inv_pivot);
                aug.set(c, col, scaled);
            }

            // Eliminate column in other rows
            for r in 0..n {
                if r != c {
                    let factor = aug.get(r, c);
                    if factor != 0 {
                        for col in 0..(2 * n) {
                            let sub = GF.mul(factor, aug.get(c, col));
                            let cur = aug.get(r, col);
                            aug.set(r, col, cur ^ sub);
                        }
                    }
                }
            }
        }

        let mut inv = Matrix::new(n, n);
        for r in 0..n {
            for c in 0..n {
                inv.set(r, c, aug.get(r, n + c));
            }
        }
        Ok(inv)
    }
}

// -----------------------------------------------------------------------------
// ErasureCoder Trait and ReedSolomonCoder Implementation
// -----------------------------------------------------------------------------

/// Abstract interface for erasure coding data into shards and reconstructing.
pub trait ErasureCoder {
    /// Encodes source payload into total_shards (data + parity) using the specified profile.
    fn encode(&self, payload: &[u8], profile: RsProfile) -> Result<Vec<Shard>, ErasureError>;

    /// Reconstructs original payload from any >= k valid shards.
    fn reconstruct(
        &self,
        shards: &[Option<Shard>],
        original_len: usize,
        profile: RsProfile,
    ) -> Result<Vec<u8>, ErasureError>;
}

/// Standard Reed-Solomon Coder based on Cauchy systematic generator matrices.
#[derive(Default, Clone, Debug)]
pub struct ReedSolomonCoder;

impl ReedSolomonCoder {
    pub fn new() -> Self {
        Self
    }

    /// Constructs the systematic (k + m) * k Cauchy encoding matrix.
    fn build_cauchy_matrix(k: usize, m: usize) -> Matrix {
        let total = k + m;
        let mut mat = Matrix::new(total, k);

        // Systematic upper identity matrix for data shards: 0..k
        for i in 0..k {
            mat.set(i, i, 1);
        }

        // Cauchy matrix for parity shards: k..k+m
        for p in 0..m {
            let row = k + p;
            let x = (row as u8) ^ 0x80; // disjoint evaluation set
            for d in 0..k {
                let y = d as u8;
                let diff = x ^ y;
                // diff is non-zero because x has bit 7 set and y does not (for k, m <= 128)
                let inv = GF.inv(diff).expect("diff is non-zero");
                mat.set(row, d, inv);
            }
        }

        mat
    }
}

impl ErasureCoder for ReedSolomonCoder {
    fn encode(&self, payload: &[u8], profile: RsProfile) -> Result<Vec<Shard>, ErasureError> {
        let k = profile.data_shards as usize;
        let m = profile.parity_shards as usize;

        if k == 0 || (k + m) > 256 {
            return Err(ErasureError::InvalidProfile {
                data: profile.data_shards,
                parity: profile.parity_shards,
            });
        }

        // Shard size rounded up to multiple of k
        let shard_len = payload.len().div_ceil(k).max(1);

        // Prepare data shards with zero padding
        let mut data_shards: Vec<Vec<u8>> = Vec::with_capacity(k);
        for i in 0..k {
            let start = i * shard_len;
            let mut shard = vec![0u8; shard_len];
            if start < payload.len() {
                let end = (start + shard_len).min(payload.len());
                let slice = &payload[start..end];
                shard[..slice.len()].copy_from_slice(slice);
            }
            data_shards.push(shard);
        }

        // Compute parity shards
        let matrix = Self::build_cauchy_matrix(k, m);
        let mut parity_shards: Vec<Vec<u8>> = Vec::with_capacity(m);

        for p in 0..m {
            let row = k + p;
            let mut parity = vec![0u8; shard_len];
            for (d, d_shard) in data_shards.iter().enumerate().take(k) {
                let coeff = matrix.get(row, d);
                if coeff != 0 {
                    for (b, val) in parity.iter_mut().enumerate().take(shard_len) {
                        *val ^= GF.mul(coeff, d_shard[b]);
                    }
                }
            }
            parity_shards.push(parity);
        }

        // Package all shards with BLAKE3 checksums
        let mut shards = Vec::with_capacity(k + m);
        for (i, shard_buf) in data_shards.into_iter().enumerate() {
            shards.push(Shard::new(i as u8, shard_buf));
        }
        for (p, shard_buf) in parity_shards.into_iter().enumerate() {
            shards.push(Shard::new((k + p) as u8, shard_buf));
        }

        Ok(shards)
    }

    fn reconstruct(
        &self,
        shards: &[Option<Shard>],
        original_len: usize,
        profile: RsProfile,
    ) -> Result<Vec<u8>, ErasureError> {
        let k = profile.data_shards as usize;
        let m = profile.parity_shards as usize;

        if k == 0 || (k + m) > 256 {
            return Err(ErasureError::InvalidProfile {
                data: profile.data_shards,
                parity: profile.parity_shards,
            });
        }

        // Filter and verify shards, strictly discarding corrupt ones
        let mut valid_shards: Vec<&Shard> = Vec::new();
        for s in shards.iter().flatten() {
            if s.verify_checksum() {
                valid_shards.push(s);
            }
        }

        if valid_shards.len() < k {
            return Err(ErasureError::NotEnoughShards {
                needed: k,
                available: valid_shards.len(),
            });
        }

        // Fast path: if all first k data shards are present and valid, concatenate directly!
        let has_all_data = (0..k).all(|idx| valid_shards.iter().any(|s| s.index as usize == idx));
        if has_all_data {
            let mut recovered = Vec::with_capacity(original_len);
            for idx in 0..k {
                let s = valid_shards
                    .iter()
                    .find(|s| s.index as usize == idx)
                    .unwrap();
                recovered.extend_from_slice(&s.data);
            }
            recovered.truncate(original_len);
            return Ok(recovered);
        }

        // Erasure recovery: select exactly k surviving shards
        let selected: Vec<&Shard> = valid_shards.into_iter().take(k).collect();
        let shard_len = selected[0].data.len();

        for s in &selected {
            if s.data.len() != shard_len {
                return Err(ErasureError::ShardLengthMismatch {
                    expected: shard_len,
                    got: s.data.len(),
                });
            }
        }

        // Construct submatrix for the selected shard rows
        let full_matrix = Self::build_cauchy_matrix(k, m);
        let mut submatrix = Matrix::new(k, k);
        for (row_idx, s) in selected.iter().enumerate() {
            let orig_row = s.index as usize;
            for col in 0..k {
                submatrix.set(row_idx, col, full_matrix.get(orig_row, col));
            }
        }

        // Invert submatrix
        let inv_matrix = submatrix.invert()?;

        // Multiply inverted matrix by selected shards to reconstruct original data shards
        let mut recovered_data_shards: Vec<Vec<u8>> = Vec::with_capacity(k);
        for d in 0..k {
            let mut restored = vec![0u8; shard_len];
            for (col_idx, s) in selected.iter().enumerate() {
                let coeff = inv_matrix.get(d, col_idx);
                if coeff != 0 {
                    for (b, val) in restored.iter_mut().enumerate().take(shard_len) {
                        *val ^= GF.mul(coeff, s.data[b]);
                    }
                }
            }
            recovered_data_shards.push(restored);
        }

        // Concatenate and truncate to original length
        let mut result = Vec::with_capacity(original_len);
        for shard in recovered_data_shards {
            result.extend_from_slice(&shard);
        }
        result.truncate(original_len);
        Ok(result)
    }
}
