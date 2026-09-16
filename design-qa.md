# FORTIQ Desktop — design QA

## Scope

- Reference: operator-console screenshot supplied by the user on 2026-09-16.
- Prototype: `http://127.0.0.1:4173/` built from the current workspace.
- Focus: desktop window sizing, responsive layout, navigation, overflow, and keyboard accessibility.

## Findings and resolutions

| Severity | Finding | Resolution |
| --- | --- | --- |
| P1 | The default 1440×960 logical window becomes almost full-screen on Windows with display scaling. | Reduced the default to 1280×800 and the supported minimum to 900×620. |
| P1 | The fixed three-column grid overflows on narrower screens. | Added responsive grid breakpoints and a compact two-row layout for the detail and terminal panels. |
| P1 | Long peer identifiers force the details column wider and create a horizontal scrollbar. | Added wrapping, selectable text, and `min-width: 0` containment to every grid panel. |
| P1 | The sidebar tabs looked interactive but did not expose consistent selected state. | Unified tab switching, visible active state, and `aria-selected` updates. |
| P2 | Peer cards were mouse-only. | Added keyboard focus plus Enter/Space activation. |
| P2 | The sidebar consumed too much width on compact windows. | Added an icon-only compact state below 1050 px. |
| P2 | Short displays left controls cramped vertically. | Added height-aware spacing below 720 px. |

## Verification

- Production web build: required to pass before delivery.
- Live preview: both navigation tabs were exercised successfully.
- Compact viewport: no page-level horizontal overflow was observed.
- Tauri IPC is unavailable in the browser preview, so service connectivity remains a native-app verification item.

Final result: passed for the implemented UI scope.
