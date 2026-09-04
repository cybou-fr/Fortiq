# Fortiq desktop design QA

## Sources

- Reference: `C:\Users\cybou\Downloads\fortiq_gui.png`
- Implemented Home capture: `artifacts\fortiq-home-qa.png`
- Implemented Protect capture: `artifacts\fortiq-protect-qa.png`
- Implemented Backups capture: `artifacts\fortiq-backups-qa.png`
- Implemented Recovery capture: `artifacts\fortiq-recovery-qa.png`
- Viewport: Windows desktop at 175% DPI

## Comparison

- App identity uses the supplied Fortiq icon in both Windows chrome and the product header.
- The light Fluent canvas, white surfaces, blue primary actions, compact typography, navigation rail, and content density match the selected reference direction.
- Protect preserves the reference hierarchy: title, progress stepper, location controls, guidance panel, and bottom-right primary action.
- First-run Home uses an intentional onboarding state instead of presenting a missing health report as a failure.
- No clipped content, broken layout, overlapping controls, or unreadable contrast was observed in the captured states.
- Primary CTA invocation was exercised through Windows UI Automation; both repository path fields accepted values and the wizard advanced to Sources.
- Backups was verified with two repository states. The active navigation follows the current page, Sources renders real health evidence, and History switches to timestamp-sorted backup/check/restore events.
- Recovery was verified with proven and unproven sources. Source selection, evidence details, responsive headline wrapping, and the real recovery-proof action are present and legible.

## Remaining polish

- P3: add a consistent licensed icon set to navigation and informational callouts when an icon dependency is selected for the native client.
- P3: capture active-repository and final recovery-phrase screens with production-like fixture data in a later visual regression suite.

final result: passed
