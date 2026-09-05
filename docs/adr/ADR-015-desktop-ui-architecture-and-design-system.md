# ADR-015: Desktop UI Architecture, Visual Design System & Zero-State Lifecycle

- Status: **Accepted Architecture** (palette superseded — see *Revision 1*; token layer decided in *Revision 2*)
- Date: **September 4, 2026**
- Scope: Desktop GUI presentation (`Fortiq.Desktop`), MVVM view models (`Fortiq.Desktop.ViewModels`), visual styling, native path picking, and first-run onboarding

---

## Context

The initial prototype implementation of `Fortiq.Desktop` validated key cryptographic workflows (BIP-39 mnemonic generation, TPM device unlock, restore proof drills), but revealed critical user experience and visual defects during interactive desktop evaluations:

1. **Missing Desktop Iconography**:
   The desktop project (`Fortiq.Desktop.csproj`) lacked an embedded `<ApplicationIcon>` configuration, and the Avalonia application lifetime did not bind runtime `WindowIcon` assets to `MainWindow` or `ProtectRepositoryWindow`. While `Fortiq.PasswordHelper` had an icon reference, the primary desktop executable rendered with a generic fallback icon on the Windows desktop, taskbar, and Alt-Tab switcher.

2. **Harsh Dark/Black Background**:
   Avalonia's unconfigured `FluentTheme()` renders a harsh, pitch-black (`#000000`) canvas. Window controls, text blocks, and raw 1px gray borders sat directly against the black background without surface hierarchy, depth, card elevation, or modern Windows 11 material styling (Mica / Acrylic).

3. **Absence of Native Folder / Path Picker Dialogs**:
   In `ProtectRepositoryWindow`, all paths ("What to back up", "Where the backups go", "Where the recovery kit goes") were represented as manual, plain-text `TextBox` fields. Operators were forced to manually type absolute Windows paths (e.g., `C:\Users\...\Documents`), risking typos, without native browse buttons, volume space detection, or visual separation between local filesystems and remote S3 object storage endpoints.

4. **First-Run Startup Error Banner**:
   On a newly installed machine, before the background service completes its first operational pass, `health.json` does not exist. `HealthFileSource.ReadAsync()` threw a `FileNotFoundException`, which `RepositoriesViewModel` caught and surfaced as an immediate, alarming red/orange error banner:
   `No health report at C:\ProgramData\Fortiq\health\health.json. The Fortiq service writes one after each pass.`
   Starting an enterprise security tool with an apparent crash banner eroded operator confidence before the first backup was even configured.

5. **Rudimentary Mnemonic Verification**:
   The mnemonic challenge screen required typing requested words separated by commas into a single text box. This was error-prone, provided poor tactile feedback, and lacked the visual dignity of a physical paper backup card.

6. **Monolithic Single-Screen Architecture**:
   The desktop UI had only a single screen displaying repository rows. Operators had no dedicated views to inspect component health (Windows service, pinned restic engine, TPM status), review audit receipts, or configure application settings.

---

## Decision

Adopt a **Comprehensive Desktop UI Architecture, Modern Design System, and Zero-State Lifecycle**:

### 1. Multi-Resolution Icon Pipeline & Window Branding
- Pinned multi-resolution icon (`assets/icon.ico`, containing 16x16, 32x32, 48x48, and 256x256 pixel mipmaps) configured in `Fortiq.Desktop.csproj` as `<ApplicationIcon>`.
- The icon is embedded as an Avalonia resource (`avares://Fortiq.Desktop/Assets/icon.ico`) and loaded dynamically into `Application.Current` and `Window.Icon` across all windows (`MainWindow`, `ProtectRepositoryWindow`, `InstallWindow`).
- Unified Windows taskbar grouping via `AppUserModelID` registration (`Fortiq.Backup.Desktop`). *Design intent, not implemented — nothing in `src/` calls `SetCurrentProcessExplicitAppUserModelID`.*

### 2. Fluent v2 Modern Design System & Surface Palette

> **Superseded by Revision 1 (below).** The palette in this section was the decision as originally taken. The shipped client uses a light Fluent palette; the dark values here are retained as the record of what was decided first and as the reference for a future dark theme.

- Replace pitch-black styling with a curated **Dark Slate / Zinc** palette matching Windows 11 Fluent v2 aesthetics:
  - **Base Background**: `#0F1117` (Deep Obsidian Slate)
  - **Card / Surface Background**: `#1A1D26` (Subtle Elevated Slate)
  - **Card Hover / Active**: `#222634`
  - **Borders & Dividers**: `#2A3042` (Subtle 1px border with 8px corner radius)
  - **Primary Brand Accent**: `#2563EB` / `#3B82F6` (Fortiq Royal Blue)
  - **Text Hierarchy**: Primary (`#F8FAFC`, 100%), Secondary (`#94A3B8`, 70%), Disabled/Muted (`#64748B`, 40%)
  - **Assurance Verdict Colors**:
    - `Recoverable`: `#10B981` (Emerald Green)
    - `Unproven`: `#F59E0B` (Vibrant Amber)
    - `AtRisk`: `#EF4444` (Crimson Red)
- Light mode support utilizing soft slate surfaces (`#F8FAFC` base, `#FFFFFF` cards, `#E2E8F0` borders).
- Windows 11 Mica / Acrylic backdrop support when hosted on Windows 11 Build 22000+.

### 3. Native Folder & Storage Picker Abstraction
- Introduce `IPathPickerService` and `IStorageProviderAdapter` leveraging Avalonia's `TopLevel.GetTopLevel(window).StorageProvider`:
  - **Source Folder Picker**: Invokes `OpenFolderPickerAsync` with native Windows folder selection dialog.
  - **Local Repository & Kit Picker**: Invokes `OpenFolderPickerAsync` with write-permission and free disk space probing.
- **Dedicated Storage Destination Selector**:
  - **Local / Network Target**: Native folder picker, drive letter display, volume space bar.
  - **Object Storage Target (S3 / Wasabi / B2 / MinIO)**: Structured input form (Endpoint URL, Bucket Name, Region, Access Key, Secret Key) and an interactive **"Test Connection & Immutability"** validation drill.
- **Anti-Co-Location Heuristic**:
  - The recovery kit picker inspects drive volumes. If an operator selects a recovery kit directory on the same physical volume as the backup source or repository, the UI displays an amber advisory warning explaining that losing that drive loses both the backups and the keys.

### 4. Zero-State Lifecycle Architecture (Eliminating Startup Errors)
- Refactor health inspection from exception-driven failure into a typed **Health Store State Machine**:
  - `NotInitialized`: `health.json` does not exist yet (fresh install / first run).
  - `Empty`: `health.json` exists, but contains 0 configured repositories.
  - `Active`: Repositories are configured and reporting health verdicts.
  - `StoreCorrupt`: The file exists but contains invalid JSON schema.
- **Onboarding Zero-State Experience**:
  - When `NotInitialized` or `Empty`, `MainWindow` renders a clean, welcoming **Zero-State Dashboard**:
    - Friendly welcome banner: *"Welcome to Fortiq. Your system is ready to establish verifiable backup protection."*
    - Quick readiness checklist: TPM 2.0 (Active), Pinned Engine (Ready), Windows Service (Running).
    - High-visibility primary action button: **"Protect your first folder"**.
  - System startup never greets the operator with a red failure banner.

### 5. Redesigned 5-Step Protection Wizard & Mnemonic Grid
- Reorganize `ProtectRepositoryWindow` into a step-by-step guided wizard with visual progress breadcrumbs:
  1. **Source Selection**: Folder picker with exclusion preset toggles (Temp files, Caches, Node modules).
  2. **Storage Target**: Local disk browse or S3 cloud credentials with live bucket check.
  3. **Schedule & Device Key**: Automated nightly run time and TPM 2.0 device seal status.
  4. **Recovery Mnemonic Grid**: 24 BIP-39 words rendered in an elegant 4x6 grid of numbered cards (`01: abandon`, `02: ability`...) with high legibility, clipboard copy disabled, and a "Print Physical Card" option.
  5. **Interactive Slot Challenge**: Displays 3 random slot positions (e.g. Word #4, Word #11, Word #19) with dedicated individual text boxes or interactive chip selection, providing instant visual feedback.

### 6. Multi-View App Shell & Navigation Rail
- Expand `MainWindow` into an App Shell featuring a left navigation rail:
  - 🛡️ **Repositories & Health**: Primary inventory, health verdict badges, "Prove Recovery" drills, and snapshot restore explorer.
  - 📦 **Component Hub**: Live dashboard showing status of `Fortiq.Service`, pinned `restic` engine version & SHA-256 hash match, TPM crypto provider, and one-click in-app updater.
  - 📜 **Audit Receipts**: Chronological timeline of backup runs and cryptographic audit receipts (`receipts/*.json`).
  - ⚙️ **Settings**: Path locations, diagnostic bundle export, and zero-telemetry attestation.

---

## Consequences

### Positive
- **Professional Operator Confidence**: No false-alarm startup errors; clear, calming zero-state onboarding for new deployments.
- **Seamless Ergonomics**: Native Windows folder pickers eliminate manual typing of directory paths and eliminate typos.
- **Visual Distinction & Accessibility**: Modern dark/light slate theme provides clear surface hierarchy, high-contrast text, and distinct status badges for recovery verdicts.
- **Dignified Key Ceremony**: The 24-word grid and slot challenge reinforce the gravity of paper backup material without frustrating the operator.
- **Operational Transparency**: The Component Hub and Receipt Viewer give operators direct visual insight into background services, engine versions, and cryptographic proof logs.

### Negative / Trade-offs
- Requires migrating from pure code-behind control building to structured Avalonia XAML or cohesive UI component builders.
- Additional abstraction layer (`IPathPickerService`) required to maintain unit testability in `Fortiq.Desktop.ViewModels` without coupling to Avalonia UI threading.

---

## References

- [ADR-014: Embedded GUI Installer, System Discovery & Autonomous Component Updater](ADR-014-embedded-gui-installer-and-updater.md)
- [Spec 17: Product UX & Recovery-First Interface](../17-product-ux.md)
- [Spec 21: Embedded GUI Installer & Component Lifecycle](../21-embedded-installer-and-updater.md)
- [Spec 22: Desktop UI Specification & Visual Design System](../22-desktop-ui-and-visual-system.md)
- [Spec 23: GUI Development Guidelines & Component Blueprint](../23-gui-development-guidelines.md)

---

## Revision 1 — Light Fluent Palette Supersedes Dark Slate

- Date: **September 4, 2026**
- Supersedes: the Dark Slate / Zinc palette in *Decision §2*
- Evidence: [`design-qa.md`](../../design-qa.md) (visual QA against the supplied reference, recorded verdict: passed)

### Context

The Dark Slate palette was chosen to fix a specific defect: Avalonia's unconfigured `FluentTheme()` renders a pitch-black canvas with no surface hierarchy. Moving away from black was the decision that mattered; whether the destination was dark or light was a second, weaker choice bundled into the same record.

The subsequent interactive visual QA pass compared the implementation against the supplied reference direction and settled on a light canvas. The implementation followed the QA outcome, and this ADR did not, leaving the two in contradiction for several commits.

### Decision

The shipped palette is **light Fluent**, defined as follows and implemented in `MainWindow.cs` and `ProtectRepositoryWindow.cs`:

| Role | Hex | Usage |
| :--- | :--- | :--- |
| Canvas | `#F6F8FB` | Window background |
| Surface | `#FFFFFF` | Cards, panels, navigation rail |
| Border | `#E3E8EF` | Card outlines, dividers |
| Ink | `#172033` | Headings and primary text |
| Muted | `#667085` | Helper text, timestamps, secondary labels |
| Brand accent | `#0866D9` | Primary actions, active navigation, informational text |
| Informational surface | `#EAF3FF` / border `#B9D7FF` | Guidance callouts |
| `Recoverable` | `#159455` on `#EAF8F0` | Proven-recoverable verdict |
| `Unproven` | `#B7791F` on `#FFF8E7`, border `#F4CC73` | Backed up, recovery not proven |
| `AtRisk` | `#D92D20` on `#FFF4F2`, border `#FDA29B` | Recovery would fail today |
| Caution text | `#8A5A00` | Recovery-phrase sensitivity warnings |
| Inactive stepper marker | `#D5DBE5` | Wizard steps not yet reached |

`Unproven` remains deliberately amber rather than green. A repository nobody has restored from looks finished to a person, and removing that impression is the reason the verdict exists.

### Consequences

- Windows 11 Mica / Acrylic backdrop support (*Decision §2*, last bullet) is **not implemented**; the windows paint an opaque canvas.
- Runtime theme switching is **not implemented**. There is one palette, not a selectable set (cf. Spec 23 §Theme Selection).
- Taskbar grouping via `AppUserModelID` (*Decision §1*) is **not implemented**.
- These colours were hex literals duplicated across two source files, which is what allowed the contradiction between this ADR and the implementation to go unnoticed. Resolved in *Revision 2*.

---

## Revision 2 — Design Tokens Live in C#

- Date: **September 4, 2026**
- Resolves: [DEC-024](../08-open-decisions.md); supersedes the XAML token prescription in [Spec 23](../23-gui-development-guidelines.md)

### Context

Revision 1 recorded a palette change that the documentation had missed for several commits. The reason it could be missed is structural: the colours were hex literals duplicated across `MainWindow.cs` and `ProtectRepositoryWindow.cs`, so a palette decision produced a diff indistinguishable from a layout tweak, spread over two files. There was nothing for a reviewer to look at and call a palette change.

Spec 23 prescribed XAML resource dictionaries as the answer. That would work, but it introduces markup into a client that is currently one language, and it is a large change to buy one property.

### Decision

Design tokens are a single C# static class, `src/Fortiq.Desktop/DesignTokens.cs`, consumed by both windows through `using static`. Views hold no colour literals.

Tokens are named for the role they play, not the colour they are: `Unproven`, `AtRisk`, `Recoverable`, `Failure`, `Caution`. A reviewer can then disagree with `Unproven` being what it is on the grounds of what Fortiq is willing to claim, which is the argument worth having.

### Consequences

- A palette change is one edit in one file, and reads as a palette change in review.
- Runtime theme switching remains unimplemented and unscheduled. Nothing requires it, and this decision does not block it: a XAML dictionary would be generated from one C# file rather than reverse-engineered from two files of layout code.
- `Canvas` is spelled `CanvasBackground`, because `using static` makes a token of that name ambiguous with Avalonia's `Canvas` control.
