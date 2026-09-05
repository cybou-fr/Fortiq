# Specification 22: Desktop UI Architecture & Visual Design System

> **Implementation status: Partially implemented.** Both the light and dark Fluent palettes ship in
> `DesignTokens.cs`, and Settings switches between them at runtime. Mica backdrops and several described
> views remain design intent; markers appear inline.


## 1. Purpose & Guiding Principles

This specification defines the user interface architecture, visual design system, interaction workflows, and component hierarchy for the Fortiq desktop client (`Fortiq.Desktop`).

Fortiq's desktop experience differs fundamentally from conventional backup utilities:
- **Calm, Evidence-Driven Confidence**: The UI never produces deceptive "Success" banners or alarms unless backed by cryptographic proof.
- **Zero Startup Friction**: First-run deployments never start with error banners or unhandled exceptions; instead, they present an intuitive zero-state onboarding flow.
- **Modern Windows Integration**: Clean Windows 11 Fluent v2 styling, light surfaces over a soft canvas, and proper multi-resolution window and taskbar icons. (Mica / Acrylic backdrops are design intent, not implemented — see [ADR-015 Revision 1](adr/ADR-015-desktop-ui-architecture-and-design-system.md).)
- **Point-and-Click Ergonomics**: Seamless native folder picker dialogs eliminate error-prone manual typing of directory paths.
- **Dignified Key Ceremony**: Recovery phrases are rendered in an accessible, high-legibility card grid with structured challenge verification.

---

## 2. Visual Design System & Design Tokens

Fortiq utilizes a customized **Fluent v2 Slate Theme** that replaces harsh pitch-black backgrounds (`#000000`) with layered, elevated slate surfaces designed for prolonged visual comfort.

### 2.1 Color Palette & Surface Hierarchy

```text
┌─────────────────────────────────────────────────────────────┐
│ Application Base (#0F1117 - Deep Obsidian Slate)             │
│  ┌───────────────────────────────────────────────────────┐  │
│  │ Navigation Rail / Header (#141720)                    │  │
│  └───────────────────────────────────────────────────────┘  │
│  ┌───────────────────────────────────────────────────────┐  │
│  │ Card Surface (#1A1D26 - Elevated Slate)               │  │
│  │  ┌─────────────────────────────────────────────────┐  │  │
│  │  │ Input / Sub-Card (#222634 - Border: #2A3042)    │  │  │
│  │  └─────────────────────────────────────────────────┘  │  │
│  └───────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

#### Implemented Tokens — Light Fluent (the shipped palette)

Superseded the Dark Slate palette in [ADR-015 Revision 1](adr/ADR-015-desktop-ui-architecture-and-design-system.md). Implemented in `DesignTokens.cs` ([ADR-015 Revision 2](adr/ADR-015-desktop-ui-architecture-and-design-system.md)); the views hold no colour literals. Token names below are the design vocabulary; the class names each token for its role.

| Token Name | Hex Value | Usage |
| :--- | :--- | :--- |
| `Color.Background.Base` | `#F6F8FB` | Window background canvas |
| `Color.Background.Surface` | `#FFFFFF` | Cards, panels, navigation rail |
| `Color.Border.Subtle` | `#E3E8EF` | Card outlines, dividers |
| `Color.Text.Primary` | `#172033` | Headings, titles, primary labels |
| `Color.Text.Secondary` | `#667085` | Subtitles, helper text, timestamps |
| `Color.Brand.Primary` | `#0866D9` | Primary actions, active navigation, informational text |
| `Color.Background.Info` | `#EAF3FF` (border `#B9D7FF`) | Guidance callouts |
| `Color.Verdict.Recoverable` | `#159455` on `#EAF8F0` | Proven recoverable |
| `Color.Verdict.Unproven` | `#B7791F` on `#FFF8E7` (border `#F4CC73`) | Backed up, recovery not proven |
| `Color.Verdict.AtRisk` | `#D92D20` on `#FFF4F2` (border `#FDA29B`) | Recovery would fail today |
| `Color.Text.Caution` | `#8A5A00` | Recovery-phrase sensitivity warnings |
| `Color.Stepper.Inactive` | `#D5DBE5` | Wizard steps not yet reached |

#### Dark Mode Tokens (*implemented in `DesignTokens.cs`; selected from Settings → Appearance & Theme*)
| Token Name | Hex Value | Usage |
| :--- | :--- | :--- |
| `Color.Background.Base` | `#0F1117` | Window background canvas |
| `Color.Background.Surface` | `#1A1D26` | Repository cards, modal bodies, setting panels |
| `Color.Background.Elevated` | `#222634` | Text inputs, dropdowns, hovered items |
| `Color.Background.Active` | `#2D3346` | Pressed buttons, active navigation item |
| `Color.Border.Subtle` | `#2A3042` | Card outlines, dividers (1px solid, radius 8px) |
| `Color.Border.Focus` | `#3B82F6` | Keyboard focus ring, active field accent |
| `Color.Brand.Primary` | `#2563EB` | Primary action buttons ("Protect a folder") |
| `Color.Brand.PrimaryHover` | `#1D4ED8` | Primary button hover state |
| `Color.Text.Primary` | `#F8FAFC` | Headings, titles, primary labels (100% opacity) |
| `Color.Text.Secondary` | `#94A3B8` | Subtitles, helper text, timestamps (70% opacity) |
| `Color.Text.Muted` | `#64748B` | Disabled controls, footnote caveats (45% opacity) |

#### Light Mode Tokens
| Token Name | Hex Value | Usage |
| :--- | :--- | :--- |
| `Color.Background.Base` | `#F8FAFC` | Window background canvas |
| `Color.Background.Surface` | `#FFFFFF` | Repository cards, dialogs |
| `Color.Background.Elevated` | `#F1F5F9` | Text inputs, hovered rows |
| `Color.Border.Subtle` | `#E2E8F0` | Card borders, dividers |
| `Color.Text.Primary` | `#0F172A` | Primary text and headings |
| `Color.Text.Secondary` | `#475569` | Secondary descriptions |

#### Recovery Assurance Badges
| Verdict | Accent Color | Background Pill | Meaning |
| :--- | :--- | :--- | :--- |
| **`Recoverable`** | `#10B981` (Emerald) | `rgba(16, 185, 129, 0.15)` | Verified by actual live restore proof drill |
| **`Unproven`** | `#F59E0B` (Amber) | `rgba(245, 158, 11, 0.15)` | Backups run, but recovery has never been tested |
| **`AtRisk`** | `#EF4444` (Crimson) | `rgba(239, 68, 68, 0.15)` | Integrity failed, retention expired, or storage offline |

### 2.2 Typography Hierarchy

- **Title / Page Header**: Segoe UI Variable Display, 22pt, SemiBold, LineHeight 28px.
- **Section Heading**: Segoe UI Variable Text, 16pt, SemiBold, LineHeight 22px.
- **Card Title / Metric**: Segoe UI Variable Text, 14pt, SemiBold.
- **Body Text**: Segoe UI Variable Text, 13pt, Regular, LineHeight 18px.
- **Monospace / Cryptographic**: Cascadia Code / Consolas, 13pt, Regular (used for BIP-39 recovery phrases, SHA-256 hashes, repository IDs, and paths).

---

## 3. Window Architecture & Icon Pipeline

### 3.1 Multi-Resolution Icon Embedding

The application bundles an all-in-one Windows icon resource (`assets/icon.ico`) with mipmaps for every display density:
- `16x16`: Windows titlebar and notification tray
- `32x32`: Windows Explorer detailed/list views
- `48x48`: Windows Alt-Tab switcher and taskbar grouping
- `256x256`: Windows Explorer extra large icons and high-DPI scaling (4K/8K)

```xml
<!-- Fortiq.Desktop.csproj -->
<PropertyGroup>
  <ApplicationIcon>..\..\assets\icon.ico</ApplicationIcon>
  <ApplicationManifest>app.manifest</ApplicationManifest>
</PropertyGroup>

<ItemGroup>
  <AvaloniaResource Include="..\..\assets\icon.ico" Link="Assets\icon.ico" />
</ItemGroup>
```

### 3.2 Runtime Window Icon Initialization

In `Program.cs` and window constructors, the icon is resolved and bound directly to the Avalonia `Window.Icon`:

```csharp
var iconStream = AssetLoader.Open(new Uri("avares://Fortiq.Desktop/Assets/icon.ico"));
this.Icon = new WindowIcon(iconStream);
```

*Design intent, not implemented.* On Windows 11 Build 22000+, `TransparencyLevelHint` would be configured to `WindowTransparencyLevel.Mica`, providing subtle desktop tinting behind surface cards. The shipped windows paint an opaque canvas.

---

## 4. Zero-State Lifecycle Architecture

### 4.1 Root Cause of Startup Error

In the prototype, `HealthFileSource` threw `FileNotFoundException` whenever `C:\ProgramData\Fortiq\health\health.json` was missing. On fresh installations before the first scheduled pass, this immediately surfaced as an orange-red failure banner.

### 4.2 Health Store State Machine

The health reader is refactored into a resilient state reader returning a typed `HealthLoadResult`:

```csharp
public enum HealthStoreState
{
    NotInitialized, // health.json does not exist yet (first run)
    Empty,          // health.json exists but contains 0 repositories
    Active,         // Valid health report with 1+ repositories
    Corrupt         // File exists but contains invalid JSON or unsupported schema
}

public sealed record HealthLoadResult(
    HealthStoreState State,
    HealthReport? Report,
    string? DiagnosticMessage);
```

### 4.3 First-Run Onboarding Screen Wireframe

When `State == HealthStoreState.NotInitialized` or `Empty`, `MainWindow` renders the **Welcome & Quick Start View** instead of an error:

```text
┌────────────────────────────────────────────────────────────────────────┐
│ [Fortiq Icon] Fortiq — Verifiable Recovery Assurance                   │
├────────────────────────────────────────────────────────────────────────┤
│                                                                        │
│   ┌────────────────────────────────────────────────────────────────┐   │
│   │  🛡️ Welcome to Fortiq                                          │   │
│   │  No folders are protected yet. Set up your first repository to │   │
│   │  establish cryptographic, ransomware-proof protection.         │   │
│   │                                                                │   │
│   │  [ Protect a folder... (Primary Blue Button) ]                 │   │
│   └────────────────────────────────────────────────────────────────┘   │
│                                                                        │
│   System Readiness Checklist:                                          │
│   ┌───────────────────────┬────────────────────────┬───────────────┐   │
│   │  🔑 TPM 2.0 Provider   │  📦 Pinned Engine      │  ⚙️ Service    │   │
│   │  Ready (Hardware PCR) │  restic 0.17.3 [Valid] │  Active (PID) │   │
│   └───────────────────────┴────────────────────────┴───────────────┘   │
│                                                                        │
│   Recent Activity: Waiting for initial configuration.                  │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 5. Native Path & Storage Picker Architecture

### 5.1 The `IPathPickerService` Contract

To maintain full testability of ViewModels without direct coupling to Avalonia UI threads, all filesystem dialog interactions are abstracted behind `IPathPickerService`:

```csharp
public interface IPathPickerService
{
    Task<string?> PickFolderAsync(string title, string? initialDirectory = null);
    Task<string?> PickSaveFileAsync(string title, string defaultFileName, string extension);
}
```

Implementation in `Fortiq.Desktop`:
```csharp
public sealed class AvaloniaPathPickerService(Window owner) : IPathPickerService
{
    public async Task<string?> PickFolderAsync(string title, string? initialDirectory = null)
    {
        var topLevel = TopLevel.GetTopLevel(owner);
        if (topLevel?.StorageProvider is not { } provider) return null;

        var options = new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        };

        if (initialDirectory is { Length: > 0 } && Directory.Exists(initialDirectory))
        {
            options.SuggestedStartLocation = await provider.TryGetFolderFromPathAsync(initialDirectory);
        }

        var results = await provider.OpenFolderPickerAsync(options);
        return results.Count > 0 ? results[0].Path.LocalPath : null;
    }
}
```

### 5.2 Composite `PathPickerControl`

Replaces plain `TextBox` controls with a rich composite control:

```text
┌───────────────────────────────────────────────────────────────────────┐
│ Source folder to protect                                              │
│ ┌───────────────────────────────────────────────┬───────────────────┐ │
│ │ C:\Users\Alice\Documents                      │ [📁 Browse...]    │ │
│ └───────────────────────────────────────────────┴───────────────────┘ │
│ ✔ 12,450 files found (4.2 GB) • Drive C: (184 GB free)                │
└───────────────────────────────────────────────────────────────────────┘
```

- **Browse Button**: Launches native Windows folder selection dialog.
- **Live Path Validation**:
  - Green Checkmark: Directory exists, readable, and non-empty.
  - Amber Warning: Directory does not exist yet (will be created).
  - Red Error: Access denied, invalid path characters, or path points to a file.
- **Drive Free Space Bar**: Live indicator of available space on the target volume.

### 5.3 Storage Destination Switcher: Local vs Cloud S3

The wizard provides a tabbed storage destination selector:

```text
┌───────────────────────────────────────────────────────────────────────┐
│ Destination Storage Type                                              │
│ [ (•) Local Drive / External SSD / NAS ]   [ ( ) Cloud Object Storage ]│
├───────────────────────────────────────────────────────────────────────┤
│ [Local Mode Selected]                                                 │
│ Backup Repository Location:                                           │
│ ┌───────────────────────────────────────────────┬───────────────────┐ │
│ │ D:\FortiqBackups\AliceDocuments               │ [📁 Browse...]    │ │
│ └───────────────────────────────────────────────┴───────────────────┘ │
│ Volume: External SSD (D:) • 840 GB free space                         │
└───────────────────────────────────────────────────────────────────────┘
```

When **Cloud Object Storage** is selected:
```text
┌───────────────────────────────────────────────────────────────────────┐
│ S3 / S3-Compatible Endpoint:                                          │
│ ┌───────────────────────────────────────────────────────────────────┐ │
│ │ https://s3.eu-central-1.amazonaws.com                             │ │
│ └───────────────────────────────────────────────────────────────────┘ │
│ Bucket Name:                     Region:                              │
│ ┌──────────────────────────────┐ ┌──────────────────────────────────┐ │
│ │ my-company-secure-backups    │ │ eu-central-1                     │ │
│ └──────────────────────────────┘ └──────────────────────────────────┘ │
│ AWS Access Key ID:               AWS Secret Access Key:               │
│ ┌──────────────────────────────┐ ┌──────────────────────────────────┐ │
│ │ AKIAIOSFODNN7EXAMPLE         │ │ ******************************** │ │
│ └──────────────────────────────┘ └──────────────────────────────────┘ │
│ Immutability Requirement:                                             │
│ [✔] Require S3 Object Lock (Compliance Mode, 30-day retention)        │
│                                                                        │
│ [ ⚡ Test Connection & Object Lock ] -> Result: Connected [OK]         │
└───────────────────────────────────────────────────────────────────────┘
```

### 5.4 Recovery Kit Anti-Co-Location Heuristic

When selecting the directory for the Recovery Kit (`kit.json`):
1. The UI inspects the volume root of the **Source Folder**, the **Repository Location**, and the **Recovery Kit Location**.
2. If the Recovery Kit resides on the same drive volume as the Source or Repository, a prominent amber advisory is rendered:
   > ⚠️ **Anti-Co-Location Advisory**: You have selected `C:\Users\Alice\Desktop\RecoveryKit`. This is on the same drive as your source files (`C:`). If this disk suffers physical hardware failure, both your original files and the recovery keys will be lost simultaneously. Store your recovery kit on a separate USB flash drive, external drive, or secure secondary media.

---

## 6. Redesigned 5-Step Protection Setup Wizard

The setup wizard (`ProtectRepositoryWindow`) is structured into a guided workflow with a persistent progress stepper:

```text
[ 1. Folders ] ── [ 2. Destination ] ── [ 3. Schedule ] ── [ 4. Recovery Phrase ] ── [ 5. Verify ]
```

### 6.1 Step 4: 24-Word Recovery Phrase Grid

The 24-word BIP-39 mnemonic is displayed in an accessible **4x6 grid of numbered cards** on an elevated slate surface:

```text
┌───────────────────────────────────────────────────────────────────────┐
│ 🔑 Write these 24 words down on paper, in exact order                  │
│ This phrase is your SOVEREIGN RECOVERY KEY. Fortiq does not hold a    │
│ copy in the cloud. If you lose your computer, this is the only way in. │
├───────────────────────────────────────────────────────────────────────┤
│ ┌──────────────┐ ┌──────────────┐ ┌──────────────┐ ┌──────────────┐   │
│ │ 01. abandon  │ │ 02. amount   │ │ 03. anchor   │ │ 04. ancient  │   │
│ └──────────────┘ └──────────────┘ └──────────────┘ └──────────────┘   │
│ ┌──────────────┐ ┌──────────────┐ ┌──────────────┐ ┌──────────────┐   │
│ │ 05. apology  │ │ 06. athlete  │ │ 07. balance  │ │ 08. beauty   │   │
│ └──────────────┘ └──────────────┘ └──────────────┘ └──────────────┘   │
│ ┌──────────────┐ ┌──────────────┐ ┌──────────────┐ ┌──────────────┐   │
│ │ 09. border   │ │ 10. cabinet  │ │ 11. carbon   │ │ 12. caution  │   │
│ └──────────────┘ └──────────────┘ └──────────────┘ └──────────────┘   │
│ ┌──────────────┐ ┌──────────────┐ ┌──────────────┐ ┌──────────────┐   │
│ │ 13. cement   │ │ 14. cereal   │ │ 15. clarify  │ │ 16. climate  │   │
│ └──────────────┘ └──────────────┘ └──────────────┘ └──────────────┘   │
│ ┌──────────────┐ ┌──────────────┐ ┌──────────────┐ ┌──────────────┐   │
│ │ 17. column   │ │ 18. coyote   │ │ 19. cruise   │ │ 20. crystal  │   │
│ └──────────────┘ └──────────────┘ └──────────────┘ └──────────────┘   │
│ ┌──────────────┐ ┌──────────────┐ ┌──────────────┐ ┌──────────────┐   │
│ │ 21. desert   │ │ 22. digital  │ │ 23. display  │ │ 24. donate   │   │
│ └──────────────┘ └──────────────┘ └──────────────┘ └──────────────┘   │
├───────────────────────────────────────────────────────────────────────┤
│ [ 🖨️ Print Recovery Card ]               [ I have written it down ➔ ]  │
└───────────────────────────────────────────────────────────────────────┘
```

- **Security Enforcement**: Clipboard copy buttons and shortcuts (`Ctrl+C`) are disabled on the mnemonic grid.
- **Physical Print Template**: Generates a clean, credit-card-sized printable template with numbered slots for pencil recording.

### 6.2 Step 5: Interactive Slot Challenge Verification

Instead of typing words into a raw comma-separated textbox, the operator is presented with **3 discrete word slot cards**:

```text
┌───────────────────────────────────────────────────────────────────────┐
│ 📝 Verify Recovery Phrase Recording                                   │
│ Enter the requested words to prove your paper record is accurate:     │
├───────────────────────────────────────────────────────────────────────┤
│                                                                       │
│   Word #04                   Word #11                  Word #19       │
│   ┌───────────────────┐      ┌───────────────────┐     ┌────────────┐ │
│   │ ancient           │      │ carbon            │     │ cruise     │ │
│   └───────────────────┘      └───────────────────┘     └────────────┘ │
│   ✔ Verified                 ✔ Verified                ✔ Verified     │
│                                                                       │
├───────────────────────────────────────────────────────────────────────┤
│ [ ⬅️ Back to words ]                     [ Finalize & Prove Backup ➔ ]│
└───────────────────────────────────────────────────────────────────────┘
```

Upon submitting correct words:
1. `RepositoryProvisioner` seals the repository;
2. Generates the hardware TPM device envelope for background scheduled runs;
3. Saves `kit.json` to the chosen external destination;
4. Performs an immediate test unwrap drill;
5. Registers the nightly backup schedule;
6. Returns to `MainWindow` and immediately shows the repository in the list.

---

## 7. Multi-View App Shell & Navigation Rail

`MainWindow` implements a persistent two-column layout with a left navigation rail mirroring the 5 primary functional sections:

```text
┌──────────────┬────────────────────────────────────────────────────────┐
│ [Logo]       │ Dashboard (Home)                                       │
│ Fortiq       │                                                        │
├──────────────┼────────────────────────────────────────────────────────┤
│ 🏠 Home      │ ┌────────────────────────────────────────────────────┐ │
│              │ │ ✔  Your data is recoverable    [ Run Backup Now ]  │ │
│ 💾 Backups   │ └────────────────────────────────────────────────────┘ │
│              │ ┌──────────────┬──────────────┬──────────────┬────────┐│
│ 🔄 Recovery  │ │ 📅 Backup    │ 🔍 Integrity │ 🔄 Restore   │ 🔒 WORM││
│              │ │ Today, 02:14 │ Today, 01:00 │ 3 days ago   │ S3 Lock││
│ 🔒 Recov. Kit│ └──────────────┴──────────────┴──────────────┴────────┘│
│              │ Protected Sources                         [ Manage > ] │
│ ⚙️ Settings  │ ┌────────────────────────────────────────────────────┐ │
│              │ │ 📁 C:\Users                  Daily at 02:00 🟢 Act.│ │
│              │ │ 📁 D:\Projects               Daily at 03:00 🟢 Act.│ │
│ ──────────── │ └────────────────────────────────────────────────────┘ │
│ Fortiq       │ Recent Activity                          [ View all >] │
│ Protect What.│ ┌────────────────────────────────────────────────────┐ │
│ 🟢 Service...│ │ ✔ Backup completed                      Today, 02:14│ │
│              │ └────────────────────────────────────────────────────┘ │
└──────────────┴────────────────────────────────────────────────────────┘
```

### 7.1 Screen Mapping

1. **🏠 Home (Dashboard)**: High-level recoverability hero banner, 4 KPI stat cards (Last backup, Integrity check, Proven restore, Storage protection), Protected Sources summary, and Recent Activity feed.
2. **➕ Protect (Setup Repository Wizard)**: 4-step wizard: `1. Repository` (Local vs S3 with Object Lock toggle) ➔ `2. Sources` ➔ `3. Schedule` ➔ `4. Review`.
3. **💾 Backups (Sources & History)**: Tabbed dual view (`Sources` and `History`), `[+ Add Source]` action, detailed source table, and historical activity table (Time, Source, Operation, Result, File count, Byte size).
4. **🔄 Recovery (Prove Recovery Drill)**: Evidence-based hero card (Last proven restore, 317 files, 1.2 GB), **"Run Recovery Proof Now"** execution CTA, and detailed proof drill inspection with Explorer link.
5. **🔒 Recovery Kit (Disaster Recovery)**: Disaster recovery kit status, amber sensitivity advisory, obfuscated BIP-39 mnemonic with `[Show Mnemonic]` toggle, actions (`Print Kit`, `Save to File...`, `Verify Kit`), and included material checklist.
6. **⚙️ Settings**: Tabbed system preferences (`General`, `Schedules`, `Storage`, `Service`, `About`), theme selection (System default, Light, Dark), startup & notification options, directory shortcuts, and Danger Zone service shutdown.

Detailed implementation blueprints, XAML markup, and MVVM contracts are specified in [Spec 23: GUI Development Guidelines & Component Blueprint](23-gui-development-guidelines.md).

---

## 8. Summary of Interface Improvements

| Feature Area | Prototype Behavior | Redesigned Specification |
| :--- | :--- | :--- |
| **Window Icon** | Blank / generic default window chrome | Multi-resolution `assets/icon.ico` embedded in `.csproj` and bound to `WindowIcon` |
| **Canvas Background** | Pitch-black (`#000000`) with raw 1px gray borders | Light Fluent palette (`#F6F8FB` canvas, `#FFFFFF` cards, `#E3E8EF` borders) — see [ADR-015 Revision 1](adr/ADR-015-desktop-ui-architecture-and-design-system.md) |
| **Folder Selection** | Manual text typing in raw `TextBox` | `PathPickerControl` with native Windows folder dialog (`OpenFolderPickerAsync`) |
| **Storage Choice** | Plain string parsing for S3 URLs | Dedicated Local folder vs Cloud S3 cards with Object Lock toggle & info callout |
| **First-Run Behavior**| `FileNotFoundException` error banner | Welcoming Zero-State Onboarding card with system readiness indicators |
| **Mnemonic Phrase** | Unformatted text block, manual typing | Obfuscated card with `Show Mnemonic` toggle, physical print template, clipboard protection |
| **Challenge Input** | Comma-separated single text line | 3 discrete word slot cards with real-time verification |
| **Navigation** | Single monolithic screen | 5-tab navigation rail (Home, Backups, Recovery, Recovery Kit, Settings) + live service pill |

---

## 9. References

- [Spec 23: GUI Development Guidelines & Component Blueprint](23-gui-development-guidelines.md)
- [ADR-015: Desktop UI Architecture, Visual Design System & Zero-State Lifecycle](adr/ADR-015-desktop-ui-architecture-and-design-system.md)
- [Spec 17: Product UX & Recovery-First Interface](17-product-ux.md)
- [Spec 21: Embedded GUI Installer & Component Lifecycle](21-embedded-installer-and-updater.md)
- [ADR-014: Embedded GUI Installer, System Discovery & Autonomous Component Updater](adr/ADR-014-embedded-gui-installer-and-updater.md)
