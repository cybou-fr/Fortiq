# Community beta: implementation and local validation

Date: 2026-09-07. Scope: the Community Desktop release plan in `docs/community-beta-release-plan.md`.

## Implemented

- Setup validates cached files against its embedded ZIP, including the manifest, instead of trusting a completion marker. The marker lives outside the payload. Invalid caches get a new generation; previous copies are not deleted. Setup portable state lives outside the verified payload.
- Community provisioning requires a device key and proves that the provisioning identity can unwrap it. Failure does not report successful protection. Recovery-only provisioning remains available to the core recovery tests; kit-and-phrase recovery is unchanged.
- Portable Source Settings composes local stale-lock recovery with its own schedules, engine, credentials, run registry and receipts. The existing exclusive-run guard and warning about another computer remain in place.
- Source Settings carries explicit update intent for backup time, drills and retention. Unchanged fields survive a save. Custom recurrence is read-only in the GUI; editing a daily time preserves its timezone and weekday restrictions.
- Save, Stop protection and Clear lock can use a short-lived elevated service client. Operators use direct IPC. The worker accepts only these three operations; it cannot provision or change source paths. UAC refusal does not issue the mutation.
- Tagged release runs verify the canonical version before packaging/publishing. After packaging, CI exercises ZIP extraction through the Setup implementation and `InstallAsync` into a temporary target, with service/ACL/PATH/shortcut changes disabled.
- HKCU autostart is configured by the original installation caller after the elevated worker succeeds. The worker no longer writes HKCU. CLI uninstall clears the original caller's autostart; it does not attempt to edit other users' hives. An autostart failure is distinguished from an installation failure.
- README file restore claims and README-FIRST instructions were updated. Packaging now resolves the output directory to an absolute path so MSBuild can find the embedded ZIP; an existing output directory is refused instead of deleted.

## Baseline and provenance

Work began at `5dc2981` with existing uncommitted work. During this session another operation created `c812cf8`, including some of the early fixes from this work. No existing changes were rolled back. The candidate is built from the working tree above `c812cf8`, not from an immutable release commit. No tag, push or GitHub Release was created by this task.

Final local artifacts: `artifacts/beta-candidate-20260907-final/`.

| Artifact | SHA-256 |
| --- | --- |
| Fortiq-Community-0.1.0-beta.1-win-x64-Setup.exe | ae8cbd21add198bf62bbbfbfc127f9c2182cc9b9b305215edce91eff1e56dd4b |
| Fortiq-Community-0.1.0-beta.1-win-x64.zip | f2d5f1ce6dc79e2b9d872fdd8c8dfb0428692c1da26c3c9c6fc3d3c654f7c849 |

The canonical file hashes are also recorded in the candidate's `SHA256SUMS`. The candidate includes an SBOM and is unsigned. Build log: `artifacts/beta-candidate-final-build.log`.

## Validation

- Initial Desktop suite: 172 passed.
- Final Desktop suite: 186 passed, no skips (`artifacts/beta-desktop-final`).
- Scheduling: 64 passed, no skips (`artifacts/beta-scheduling`).
- Final packaged ZIP: Setup extraction → external marker → `InstallAsync` → installed Desktop hash comparison passed (`artifacts/beta-setup-final`). This exercises the actual release ZIP, without launching the GUI or installing a service.
- Documentation claims: 44 documents checked successfully, including this report.
- Actual workflow version guard: matching tag accepted; mismatched tag rejected.
- Full Release run: 682 passed, zero failed, six skipped (`artifacts/beta-validation/summary.json`). This run used `PrivilegeMode!=ElevatedVss`; the elevated VSS lane was excluded, not passed. The six runtime skips were the five installed-pilot tests and `WindowsTpmEnvelopeTests.AMachineScopedKeyIsRecordedAndOpensFromTheMachineStore`, all requiring elevation. The Desktop suite was then rerun with the final additional tests, producing the 186-pass result above.
- Focused final device/lock checks: three passed, no skips (`artifacts/beta-device-lock-final`). The real-engine fixtures now require device unlock during provisioning; they exercise successful local unlock and refusal while another local operation holds the repository. The third test refuses a phrase-only fallback when device unlock is required.

## Remaining release acceptance

This is a local candidate, not evidence that the public-beta acceptance matrix is complete.

1. `Test-InstalledPilot.ps1` refused this non-elevated session. Real service installation, ACLs and Operators group creation still need an elevated Windows lane; skips are not passes.
2. Run the actual Setup EXE GUI path on clean Windows, including a reboot and a second machine restoring with only kit, phrase and release tools.
3. Exercise Operator, unelevated admin, standard user with another administrator's UAC credentials, UAC refusal and per-user autostart. Local routing tests do not establish the cross-account result.
4. Exercise the complete portable Stop → remaining restic lock → Clear lock → Backup flow through the UI. Local adapter and real-engine lock tests cover parts of this path, not the full UI sequence.
5. Review and commit the final tree, then rebuild a candidate from that exact commit before tagging. Keep the clean-machine evidence associated with its artifact hashes.

No legacy cache is deleted automatically. A previously used pre-beta cache may still contain its old portable-state directory; retain it when cleaning up development caches. Automatic migration of that legacy state is not implemented.
