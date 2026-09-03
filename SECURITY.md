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
- **Least-privilege CI token.** The workflow declares `permissions: contents: read`.
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
- **Signed release artifacts.** Fortiq's own EXE and DLL files are not yet Authenticode-signed, and
  nothing verifies a signature at install or update time. The engine binary is pinned by hash, which
  is a different guarantee: it proves the file did not change, not who published it.
- **Signed and verified engine provenance.** The acquisition script trusts the hashes in the manifest.
  How those hashes were established, and by whom, is not itself verified.
- **Reproducible build and SBOM.** No SBOM is produced, and no build output is compared across
  machines.
- **Dependency review gates.** ADR-013 requires SBOM diff, advisory review and re-run of test vectors
  for every security-critical dependency update. That process is not automated yet.

## Password broker

The password handover uses a `CurrentUserOnly` named pipe and a one-shot challenge-response. It does
**not** yet verify the connecting client's PID and service identity, and it does not apply an
installer-defined SDDL. Until it does, the handover is only as strong as the isolation between
processes running as the same user. This is called out on `PasswordPipeCredentialProvider` itself.

## Reporting

Report a suspected vulnerability privately to the repository owner rather than in a public issue.
