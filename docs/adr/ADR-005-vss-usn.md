# ADR-005: VSS for Consistency, USN Journal for Hints

- Status: **Accepted & Implemented for Windows V1**
- Date: **September 3, 2026**
- Scope: Windows filesystem capture, VSS snapshot integration, and incremental discovery

---

## Context

Backing up active systems requires capturing open, locked files without corrupting data mid-write. Two distinct Windows mechanisms exist:
1. **Volume Shadow Copy Service (VSS)**: Coordinates application freeze/thaw and creates point-in-time read-only block-level volume snapshots.
2. **USN Change Journal**: Monotonically records metadata change events on NTFS volumes.

Using the USN Journal as the sole definitive input for backup sets risks silent data loss if journal truncation, resets, or gaps occur. Conversely, creating a VSS snapshot without verifying VSS writer participation cannot truthfully claim application-level consistency.

---

## Decision

1. **VSS is the Primary Source Capture Mechanism**: Restic operates against point-in-time volume shadow device paths (`--use-fs-snapshot`).
2. **Strict Consistency Classification**:
   - `ApplicationConsistent` is recorded only when designated application writers complete freeze/thaw without warnings.
   - General snapshots without active writer involvement are transparently recorded as `CrashConsistent`.
   - Silent live-read fallbacks are prohibited.
3. **USN Journal as Performance Advisory**: USN records are used for rapid pre-backup delta estimations, backup prioritization, and anomaly detection.
4. **Mandatory Fallback on Journal Gaps**: If `FSCTL_QUERY_USN_JOURNAL` detects journal deletion (`ERROR_JOURNAL_ENTRY_DELETED`) or journal ID reset, Fortiq immediately reverts to `FullScanRequired`.
