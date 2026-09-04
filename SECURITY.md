# Supply chain and repository security

This file records what the repository enforces in code, and what has to be enabled in GitHub or in
the release process before the corresponding claim can be made. Everything under **Not yet enforced**
is an open gate, not a description of the current state.

## Enforced by code in this repository

- **Pinned engine.** `engines/manifest.json` fixes the restic version, length, binary SHA-256,
  archive SHA-256 and source URL. `scripts/Get-Engine.ps1` verifies the archive hash before
  extracting anything and the binary length and hash afterwards; a mismatch leaves nothing on disk.
- **Verified binary stays the verified binary.** `EngineBinaryVerifier` hashes the file through a
  handle it keeps open with `FileShare.Read`, so the file cannot be written, deleted or renamed while
  the engine is in use. Immediately before each execution the path is re-opened and its file identity
  (volume serial and file index) is compared with the verified one, which rejects the remaining case
  where a directory above the binary is repointed so the same path leads to a different file.
- **No ambient engine.** The adapter is internal and reachable only through a factory that requires a
  `VerifiedEngine`; a globally installed restic is never used, and tests skip rather than fall back.
- **Pinned dependencies.** `Directory.Packages.props` defines every package version centrally.
  Version ranges and floating versions are not used.
- **Pinned actions.** Every GitHub Action is referenced by commit SHA with the human-readable tag in a
  comment. A tag can be moved to different code at any time; a SHA cannot.
- **Least-privilege CI token.** The build workflow declares `permissions: contents: read`; the
  release workflow adds only what attestation needs.
- **Release evidence.** `scripts/New-ReleaseArtifacts.ps1` publishes the recovery tool with its
  runtime, generates a CycloneDX bill of materials with a pinned tool version, and records the
  SHA-256 of every published file. The release workflow attests provenance over the published
  binaries, the bill of materials and the hash list, so a consumer can tell which workflow and which
  commit produced exactly those files.
- **The password goes to one approved process.** Before the engine password is written, the broker
  resolves the connecting client's process, requires its image to be the very helper file the broker
  pinned open, and requires it to run as the expected account. The process check runs at connection
  time, before the challenge is offered; the account check runs after the first read, because Windows
  only permits impersonation once data has been read, and still before any part of the password is
  written. An installer-defined SDDL can be supplied to control who may open the pipe at all;
  without one the operating system restricts it to the current user.
- **Secrets never travel as arguments.** The engine password reaches restic over a one-shot named
  pipe; the command line carries only the pinned helper path and a non-secret operation ID. The
  recovery tool reads the mnemonic from standard input and rejects every option that would carry a
  secret.

## Not yet enforced

These require GitHub configuration or release tooling that this repository cannot set by itself.

- **Protected `main`.** Require pull requests, require the CI check to pass, require review from Code
  Owners, dismiss stale approvals, block force pushes and deletions, and apply the rules to
  administrators.
- **Signed commits and tags.** Require signed commits on `main` and sign every release tag. Until
  this is on, the authorship of a commit is a claim, not evidence.
- **CODEOWNERS.** `.github/CODEOWNERS` lists the security-critical paths, but it has no effect until
  branch protection requires Code Owner review, and the owner it names must exist.
- **Signed release artifacts.** Fortiq's own EXE and DLL files are not yet Authenticode-signed. The
  verification side exists: `AuthenticodeSignature` asks Windows itself, and the password broker can
  be told to require a trusted signature on the helper (`RequireSignedHelper`). It is off by default
  precisely because the binaries are unsigned, and the release workflow warns rather than pretending
  otherwise. Until a certificate exists, provenance says which workflow built a file, not who
  vouches for it.
- **Signed and verified engine provenance.** The acquisition script trusts the hashes in the manifest.
  How those hashes were established, and by whom, is not itself verified.
- **Reproducible build.** No build output is compared across machines, so nothing yet proves the
  published files could be produced again from the same source.
- **Dependency review gates.** ADR-013 requires SBOM diff, advisory review and re-run of test vectors
  for every security-critical dependency update. That process is not automated yet.

## What object locking does and does not do

Verified against a real S3 server rather than assumed:

- A locked bucket refuses to delete a **version**. An attempt that names one is rejected outright.
- It does **not** refuse a delete that names only a key: that adds a delete marker, and the object
  stops being visible while every version of it survives. Pruning a repository in a locked bucket
  therefore succeeds and hides data rather than destroying it.
- So immutability protects against irreversible loss, not against a repository being made to look
  empty. Fortiq recovers from that: `S3HiddenObjectRecovery` finds keys whose newest version is a
  delete marker with data surviving beneath, removes those markers, and leaves every version holding
  data untouched. It deliberately does not resurrect the engine's own locks - a healthy repository
  always carries markers over locks it removed on purpose, and restoring one would block it.

## Password broker

The handover is a one-shot challenge-response over a pipe that exists for a single operation, and it
is now bound to a specific client: the pinned helper image, running as the expected account.

Residual limitations, stated plainly:

- The client is identified by its process ID, which the broker resolves into a held process handle
  before reading the image path. That prevents the ID being reused underneath the check, but the
  identification still starts from an ID rather than from a handle the operating system hands over.
- The helper image is compared by file identity against a file the broker holds open, so it cannot be
  replaced. Nothing yet verifies who published that file: Authenticode signing of Fortiq binaries
  remains an open gate above.
- The SDDL is honoured but not authored here. An installer that supplies a permissive descriptor
  weakens the pipe, and nothing in this repository validates the descriptor it is given.

## Reporting

Report a suspected vulnerability privately to the repository owner rather than in a public issue.
