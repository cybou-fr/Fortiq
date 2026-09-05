# Specification 23: GUI Development Guidelines & Component Blueprint

> **Implementation status: Partially implemented.** MVVM separation, testable view models and the design token layer are real, though tokens are C# (`DesignTokens.cs`) rather than XAML — see section 9. The component blueprint in section 7 is design intent: no view classes exist.


## 1. Scope & Engineering Objectives

This document establishes the official frontend development guidelines, architectural patterns, design token definitions, and reusable component blueprints for the Fortiq desktop client (`src/Fortiq.Desktop`).

The implementation is built upon **Avalonia UI** (.NET 10 LTS) adhering strictly to **Model-View-ViewModel (MVVM)** separation:
- **`src/Fortiq.Desktop`**: Avalonia Views (XAML / C# code-behind), window chrome, custom controls, theme styling dictionaries, assets, and platform dialog adapters.
- **`src/Fortiq.Desktop.ViewModels`**: Pure presentation models, observable properties, command bindings, asynchronous operation coordination, and business logic. Zero dependencies on Avalonia UI assemblies; 100% unit-testable via standard xUnit.

---

## 2. Visual Design System & Design Tokens

Fortiq utilizes an accessible, elevated **Fluent v2 Slate Theme** engineered for prolonged operational comfort, high clarity, and seamless contrast across both Dark and Light system modes.

### 2.1 Spacing & Layout Rhythm
Layouts adhere to an 8-point spatial grid with a 4-point half-step:
- `Space.2` = `4px`: Micro spacing (between icon and text label)
- `Space.4` = `8px`: Tight spacing (between related form elements, stack items)
- `Space.6` = `12px`: Standard control gap (padding within input boxes, compact buttons)
- `Space.8` = `16px`: Card inner padding, section row gaps
- `Space.12` = `24px`: Primary layout gutters, container margins, modal padding
- `Space.16` = `32px`: Major section separators

### 2.2 Corner Radii
- `Radius.Small` = `4px`: Badges, status pills, tags
- `Radius.Medium` = `8px`: Text inputs, buttons, dropdowns, sub-cards
- `Radius.Large` = `12px`: Primary surface cards, modal dialogs, hero banners

### 2.3 Color Tokens & Brushes

#### Dark Slate Mode (*design intent — superseded as the default, and not implemented*)

The shipped palette is light Fluent; see [ADR-015 Revision 1](adr/ADR-015-desktop-ui-architecture-and-design-system.md) for the implemented values and [Spec 22](22-desktop-ui-and-visual-system.md) for the token table. The dictionary below is the target shape for the token layer described in the open decision at the end of this document, not a file that exists.

```xml
<!-- Themes/DarkSlateTheme.axaml -->
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <!-- Surface Colors -->
  <Color x:Key="CanvasBackground">#0F1117</Color>
  <Color x:Key="SidebarBackground">#141720</Color>
  <Color x:Key="CardBackground">#1A1D26</Color>
  <Color x:Key="CardElevatedBackground">#222634</Color>
  <Color x:Key="CardHoverBackground">#282D3D</Color>

  <!-- Borders & Dividers -->
  <Color x:Key="BorderSubtle">#2A3042</Color>
  <Color x:Key="BorderMedium">#374151</Color>
  <Color x:Key="BorderFocus">#3B82F6</Color>

  <!-- Typography -->
  <Color x:Key="TextPrimary">#F8FAFC</Color>
  <Color x:Key="TextSecondary">#94A3B8</Color>
  <Color x:Key="TextMuted">#64748B</Color>

  <!-- Accents & Feedback -->
  <Color x:Key="BrandPrimary">#2563EB</Color>
  <Color x:Key="BrandPrimaryHover">#1D4ED8</Color>
  <Color x:Key="StatusSuccess">#10B981</Color>
  <Color x:Key="StatusSuccessBg">#163326</Color>
  <Color x:Key="StatusWarning">#F59E0B</Color>
  <Color x:Key="StatusWarningBg">#332712</Color>
  <Color x:Key="StatusDanger">#EF4444</Color>
  <Color x:Key="StatusDangerBg">#331518</Color>
</ResourceDictionary>
```

#### Light Slate Mode
```xml
<!-- Themes/LightSlateTheme.axaml -->
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Color x:Key="CanvasBackground">#F8FAFC</Color>
  <Color x:Key="SidebarBackground">#F1F5F9</Color>
  <Color x:Key="CardBackground">#FFFFFF</Color>
  <Color x:Key="CardElevatedBackground">#F8FAFC</Color>
  <Color x:Key="CardHoverBackground">#F1F5F9</Color>
  <Color x:Key="BorderSubtle">#E2E8F0</Color>
  <Color x:Key="BorderMedium">#CBD5E1</Color>
  <Color x:Key="BorderFocus">#2563EB</Color>
  <Color x:Key="TextPrimary">#0F172A</Color>
  <Color x:Key="TextSecondary">#475569</Color>
  <Color x:Key="TextMuted">#94A3B8</Color>
  <Color x:Key="BrandPrimary">#2563EB</Color>
  <Color x:Key="BrandPrimaryHover">#1D4ED8</Color>
  <Color x:Key="StatusSuccess">#059669</Color>
  <Color x:Key="StatusSuccessBg">#ECFDF5</Color>
  <Color x:Key="StatusWarning">#D97706</Color>
  <Color x:Key="StatusWarningBg">#FFFBEB</Color>
  <Color x:Key="StatusDanger">#DC2626</Color>
  <Color x:Key="StatusDangerBg">#FEF2F2</Color>
</ResourceDictionary>
```

### 2.4 Typography Standards
- **Page Headings**: `FontSize="20" FontWeight="SemiBold" LineHeight="26"`
- **Section Titles**: `FontSize="16" FontWeight="SemiBold" LineHeight="22"`
- **Card Headings & KPI Values**: `FontSize="15" FontWeight="SemiBold"`
- **Body Text**: `FontSize="13" FontWeight="Regular" LineHeight="18"`
- **Captions & Secondary Text**: `FontSize="12" FontWeight="Regular" Opacity="0.75"`
- **Monospace / Code**: `FontFamily="{StaticResource MonospaceFontFamily}" FontSize="13"` (Cascadia Code / Consolas)

---

## 3. Application Shell & Navigation Layout

The main application window (`MainWindow.axaml`) implements a persistent two-column layout:
1. **Left Navigation Sidebar** (`Width="220"`): Brand header, 5 primary navigation tabs, and bottom status footer.
2. **Main Workspace Canvas** (`LastChildFill="True"`): Active view host (`TransitioningContentControl`).

```text
┌─────────────────────────────────────────────────────────────────────────────┐
│ Fortiq [Icon]                                                       _ □ ✕   │
├──────────────────┬──────────────────────────────────────────────────────────┤
│                  │                                                          │
│  [Logo] Fortiq   │  [ Active Screen View ]                                  │
│                  │  (Dashboard / Protect / Backups / Recovery / Kit / Set)  │
│  ──────────────  │                                                          │
│  🏠 Home         │                                                          │
│  💾 Backups      │                                                          │
│  🔄 Recovery     │                                                          │
│  🔒 Recovery Kit │                                                          │
│  ⚙️ Settings     │                                                          │
│                  │                                                          │
│  ──────────────  │                                                          │
│  Fortiq          │                                                          │
│  Protect What... │                                                          │
│  🟢 Service run. │                                                          │
└──────────────────┴──────────────────────────────────────────────────────────┘
```

### 3.1 Sidebar Component Blueprint
- **Branding Header**: Fortiq shield vector icon + bold "Fortiq" wordmark.
- **Navigation Menu Items**:
  - Each item binds to `AppNavigationItemViewModel`:
    - `IconKey`: Path geometry or icon glyph.
    - `Label`: "Home", "Backups", "Recovery", "Recovery Kit", "Settings".
    - `IsSelected`: Drives active indicator (accent border-left 3px, elevated background `#222634`).
    - `Command`: Switches the active view model in `MainViewModel`.
- **Sidebar Footer**:
  - Secondary wordmark: "Fortiq" / Sub-caption: "Protect What Matters" (`FontSize="11"`).
  - **Live Service Status Pill**:
    - Running: 🟢 `Service running` (`Foreground="{DynamicResource StatusSuccess}"`)
    - Stopped: 🔴 `Service stopped` (`Foreground="{DynamicResource StatusDanger}"`)
    - Clickable: navigates directly to `Settings ➔ Service` tab.

---

## 4. Screen-by-Screen Component Blueprints

### 4.1 Screen 1: Dashboard (`HomeView`)

The primary landing screen provides an immediate, evidence-driven summary of system recoverability.

```text
┌───────────────────────────────────────────────────────────────────────────┐
│ ┌───────────────────────────────────────────────────────────────────────┐ │
│ │  ✔  Your data is recoverable                     [ Run Backup Now ]   │ │
│ │     All critical checks are healthy. Last checked: 2 hours ago        │ │
│ └───────────────────────────────────────────────────────────────────────┘ │
│                                                                           │
│ ┌───────────────┐ ┌───────────────┐ ┌───────────────┐ ┌─────────────────┐ │
│ │ 📅 Last backup│ │ 🔍 Integrity  │ │ 🔄 Proven rest│ │ 🔒 Protection   │ │
│ │ Today, 02:14  │ │ Today, 01:00  │ │ 3 days ago    │ │ S3 (Object Lock)│ │
│ │ Success       │ │ Success       │ │ Success       │ │ Immutable       │ │
│ └───────────────┘ └───────────────┘ └───────────────┘ └─────────────────┘ │
│                                                                           │
│ Protected Sources                                           [ Manage > ]  │
│ ┌───────────────────────────────────────────────────────────────────────┐ │
│ │ 📁 C:\Users                     Daily at 02:00               🟢 Active│ │
│ │ 📁 D:\Projects                  Daily at 03:00               🟢 Active│ │
│ │ 💻 System (C:)                  Weekly (Sun 03:00)           🟢 Active│ │
│ └───────────────────────────────────────────────────────────────────────┘ │
│                                                                           │
│ Recent Activity                                             [ View all >] │
│ ┌───────────────────────────────────────────────────────────────────────┐ │
│ │ ✔ Backup completed                                       Today, 02:14 │ │
│ │ ✔ Integrity check completed                              Today, 01:00 │ │
│ │ 🔄 Proven restore completed                              3 days ago   │ │
│ │ 🔄 Retention (forget/prune)                              6 days ago   │ │
│ └───────────────────────────────────────────────────────────────────────┘ │
└───────────────────────────────────────────────────────────────────────────┘
```

#### Components & Data Contracts:
1. **Hero Health Banner (`HeroHealthBanner.axaml`)**:
   - Background: Soft emerald gradient tint (`#163326` in dark mode, `#ECFDF5` in light mode).
   - Icon: Large 36x36 circular checkmark.
   - Title: `Headline` ("Your data is recoverable" / "Backed up; recovery unproven" / "At risk").
   - Action Button: **"Run Backup Now"** (invokes `RunImmediateBackupCommand`).
2. **4 KPI Stat Cards (`KpiStatCard.axaml`)**:
   - `LastBackup`: Formatted timestamp + status badge (`Success` / `Failed`).
   - `LastIntegrityCheck`: Result of restic check (`Success` / `Warnings`).
   - `LastProvenRestore`: Elapsed time since last verified unwrap & restore drill.
   - `StorageProtection`: Badge (`Immutable` / `Standard`) with target type (`S3 Object Lock` / `Local Directory`).
3. **Protected Sources Summary**: Compact list of configured backup sets with schedule badges.
4. **Recent Activity Feed**: Chronological list of last 4-6 operations with status indicators.

---

### 4.2 Screen 2: Protect / Setup Repository Wizard (`ProtectWizardView`)

A focused 4-step wizard guiding the operator through establishing a new repository.

```text
┌───────────────────────────────────────────────────────────────────────────┐
│ Protect Your Data                                                         │
│ Set up a new backup repository and configure what to protect.             │
│                                                                           │
│   (1) Repository  ───  (2) Sources  ───  (3) Schedule  ───  (4) Review    │
│                                                                           │
│ 1. Choose Repository Location                                             │
│ ┌───────────────────────────────────────────────────────────────────────┐ │
│ │ ○ 💻 Local folder                                                     │ │
│ │      Store backups on this machine or a local drive.                  │ │
│ └───────────────────────────────────────────────────────────────────────┘ │
│ ┌───────────────────────────────────────────────────────────────────────┐ │
│ │ ◉ ☁️ Amazon S3 / Object Storage                                      ➔│ │
│ │      Store backups in S3 with optional Object Lock (immutable).       │ │
│ └───────────────────────────────────────────────────────────────────────┘ │
│                                                                           │
│ 2. Repository Settings                                                    │
│    Bucket name:                           Region:                         │
│    ┌───────────────────────────────┐      ┌───────────────────────────┐   │
│    │ fortiq-backups-prod           │      │ eu-west-1 (Ireland)     ▼ │   │
│    └───────────────────────────────┘      └───────────────────────────┘   │
│                                                                           │
│    [✔] Enable Object Lock (recommended)                                   │
│        Protects backups from deletion for a retention period.             │
│                                                                           │
│    ┌─────────────────────────────────────────────────────────────────┐    │
│    │ ℹ️ Object Lock will help protect your backups from ransomware    │    │
│    │    and accidental deletion.                                     │    │
│    └─────────────────────────────────────────────────────────────────┘    │
│                                                                           │
│                                                               [ Next ➔ ]  │
└───────────────────────────────────────────────────────────────────────────┘
```

#### Step Breakdown & Interaction Rules:
- **Stepper Header**: Visual numbered breadcrumbs with completed (✔), active (accent fill), and upcoming states.
- **Repository Location Selection**:
  - `RadioSelectionCard`: Card-based radio buttons with icon, bold title, and description.
  - Selecting **Local Folder**: Reveals `PathPickerControl` with `[📁 Browse...]` button and volume capacity meter.
  - Selecting **Amazon S3 / Object Storage**: Reveals S3 configuration card (Bucket name, Region dropdown, Access Key, Secret Key, Custom Endpoint option).
- **Object Lock Toggle & Info Callout**:
  - Checkbox triggers validation of bucket WORM parameters.
  - Blue information callout box explains ransomware protection semantics.
- **Validation**: "Next ➔" button remains disabled until required fields pass client-side regex and path validation.

---

### 4.3 Screen 3: Backups & Activity (`BackupsView`)

Central repository and operational management center.

```text
┌───────────────────────────────────────────────────────────────────────────┐
│ Backups                                                    [ + Add Source]│
│ Manage protected sources and view backup history.                         │
│                                                                           │
│  [ Sources ]   [ History ]                                                │
│ ┌───────────────────────────────────────────────────────────────────────┐ │
│ │ Source          Schedule             Last Backup     Status   Actions │ │
│ ├───────────────────────────────────────────────────────────────────────┤ │
│ │ C:\Users        Daily at 02:00       Today, 02:14    🟢 Succ.   ···   │ │
│ │ D:\Projects     Daily at 03:00       Today, 03:12    🟢 Succ.   ···   │ │
│ │ System (C:)     Weekly (Sun 03:00)   6 days ago      🟢 Succ.   ···   │ │
│ └───────────────────────────────────────────────────────────────────────┘ │
│                                                                           │
│ Recent Backup History                                       [ View all >] │
│ ┌───────────────────────────────────────────────────────────────────────┐ │
│ │ Time          Source      Operation        Result      Details        │ │
│ ├───────────────────────────────────────────────────────────────────────┤ │
│ │ Today, 02:14  C:\Users    Backup           🟢 Success  124 GB, 5,432 f│ │
│ │ Today, 01:00  Repository  Integrity check  🟢 Success  All checks pas.│ │
│ │ Yest., 02:13  C:\Users    Backup           🟢 Success  123 GB, 5,421 f│ │
│ │ 3 days ago    Repository  Proven restore   🟢 Success  1.2 GB, 317 fil│ │
│ │ 6 days ago    Repository  Forget & prune   🟢 Success  Removed 12 snp.│ │
│ └───────────────────────────────────────────────────────────────────────┘ │
└───────────────────────────────────────────────────────────────────────────┘
```

#### Technical Specifications:
- **`TabControl` Styling**: Flat pill tabs (`Sources` vs `History`) with subtle active underline.
- **`DataGrid` / `ItemsControl`**:
  - Virtualized rows for large file counts and history events.
  - Alternate row background: subtle `#141720` striping.
  - Status column renders typed indicator: Green pill (`Success`), Amber pill (`Running` / `Warning`), Red pill (`Failed`).
  - Context menu `...`: Run immediate backup, edit schedule, browse snapshots, remove source.

---

### 4.4 Screen 4: Recovery Proof (`RecoveryProofView`)

Implements Fortiq's core brand promise: **verifiable recovery assurance**.

```text
┌───────────────────────────────────────────────────────────────────────────┐
│ Prove Recovery                                                            │
│ Actually restore data and verify that it can be recovered.                │
│                                                                           │
│ ┌───────────────────────────────────────────────────────────────────────┐ │
│ │  🔄   Last proven restore                 317 files         1.2 GB    │ │
│ │       3 days ago                          Restored and verified       │ │
│ │       ✔ Success                                                       │ │
│ │                                                                       │ │
│ │  Fortiq regularly performs real restore tests to ensure your backups  │ │
│ │  are not just present, but actually recoverable.                      │ │
│ │                                                                       │ │
│ │  [ Run Recovery Proof Now ]                                           │ │
│ └───────────────────────────────────────────────────────────────────────┘ │
│                                                                           │
│ Latest Proof Details                                                      │
│ ┌───────────────────────────────────────────────────────────────────────┐ │
│ │ When:           3 days ago (2025-06-15 03:12)                         │ │
│ │ Snapshot:       snapshots/7e319c2...                                  │ │
│ │ Restored to:    C:\ProgramData\Fortiq\Proofs\2025-06-15               │ │
│ │ Files restored: 317                                                   │ │
│ │ Total size:     1.2 GB                                                │ │
│ │ Verification:   ✔ File count and size match engine metadata           │ │
│ │                                                                       │ │
│ │ [📁 Open Proof Location]                                              │ │
│ └───────────────────────────────────────────────────────────────────────┘ │
└───────────────────────────────────────────────────────────────────────────┘
```

#### Technical Specifications:
- **Hero Proof Card**:
  - Displays large circular refresh glyph (`🔄`).
  - Metric blocks: Elapsed time, file count, byte size.
  - Primary Action: **"Run Recovery Proof Now"**:
    - Disables button, renders inline progress bar and status spinner.
    - Spawns background `ProvenRestore` task in temporary isolated sandbox (`%ProgramData%\Fortiq\proofs\<guid>`).
    - Compares byte-for-byte SHA-256 hashes against snapshot manifests.
    - Upon completion, auto-refreshes metrics and updates `health.json`.
- **Proof Details Card**:
  - Displays snapshot hash, restore timestamp, verification verdict, and a quick-open button to explore the restored test folder in Windows Explorer.

---

### 4.5 Screen 5: Recovery Kit (`RecoveryKitView`)

The emergency disaster recovery management center.

```text
┌───────────────────────────────────────────────────────────────────────────┐
│ Recovery Kit                                                              │
│ Your disaster recovery kit. Keep it safe and offline.                     │
│                                                                           │
│ ┌───────────────────────────────────────────────────────────────────────┐ │
│ │  🛡️   Recovery kit is available                          [View Details]│ │
│ │       Created on: 2025-06-10                                          │ │
│ │       Repository ID: 3f2c9e4a-7b1d-4e2f-9812-a8bc12345678             │ │
│ └───────────────────────────────────────────────────────────────────────┘ │
│                                                                           │
│ ┌───────────────────────────────────────────────────────────────────────┐ │
│ │ ⚠️  Your recovery mnemonic is sensitive                               │ │
│ │     Keep it offline. Do not store it on this computer or in the cloud.│ │
│ └───────────────────────────────────────────────────────────────────────┘ │
│                                                                           │
│ Recovery Mnemonic (BIP-39)                                                │
│ ┌───────────────────────────────────────────────────────────────────────┐ │
│ │  •••••••• •••••••• •••••••• ••••••••   [ Show Mnemonic ]         🔒   │ │
│ └───────────────────────────────────────────────────────────────────────┘ │
│                                                                           │
│   [ 🖨️ Print Kit ]        [ 💾 Save to File... ]        [ ✔ Verify Kit ]   │
│                                                                           │
│ What's included                                                           │
│ • Repository information                                                  │
│ • Encrypted unlock material (envelopes)                                   │
│ • Engine compatibility information                                        │
│ • Instructions for disaster recovery                                      │
└───────────────────────────────────────────────────────────────────────────┘
```

#### Security & UX Safeguards:
- **Mnemonic Obfuscation**: The 24-word recovery phrase is hidden behind password-style bullets (`••••`) by default. Clicking `[Show Mnemonic]` reveals the words transiently (with an auto-hide timer of 60 seconds).
- **Clipboard Blocked**: Copying the recovery phrase to the Windows clipboard is strictly disabled in code to prevent clipboard sniffer exposure.
- **Print Kit Action**: Generates a formatted, credit-card sized paper card template with numbered slots for offline physical storage.
- **Verify Kit Action**: Launches the challenge verification drill where the operator confirms words from their physical paper copy.

---

### 4.6 Screen 6: Settings (`SettingsView`)

System preferences, service controls, and directory access.

```text
┌───────────────────────────────────────────────────────────────────────────┐
│ Settings                                                                  │
│ Configure Fortiq and manage system settings.                              │
│                                                                           │
│  [ General ]   [ Schedules ]   [ Storage ]   [ Service ]   [ About ]      │
│                                                                           │
│ Appearance                                                                │
│    Theme:                                                                 │
│    ┌────────────────────────────────────────────────────────────────┐     │
│    │ System default                                               ▼ │     │
│    └────────────────────────────────────────────────────────────────┘     │
│                                                                           │
│ Application                                                               │
│    [✔] Start Fortiq with Windows                                          │
│    [✔] Show notifications                                                 │
│    [✔] Automatically check for updates                                    │
│                                                                           │
│ Data & Logs                                                               │
│    [ 📁 Open data directory ]        [ 📁 Open logs directory ]           │
│                                                                           │
│ Danger Zone                                                               │
│ ┌───────────────────────────────────────────────────────────────────────┐ │
│ │ ⚠️  Stop all scheduled backups                         [ Stop Service]│ │
│ │     This will stop the Fortiq service and all                         │ │
│ │     scheduled operations.                                             │ │
│ └───────────────────────────────────────────────────────────────────────┘ │
└───────────────────────────────────────────────────────────────────────────┘
```

#### Technical Specifications:
- **Theme Selection** (*design intent, not implemented*): Dynamic runtime switching (`System default`, `Dark Slate`, `Light Slate`) via Avalonia's `RequestedThemeVariant`. The client currently ships one light palette and no theme selector.
- **System Integration**:
  - `Start Fortiq with Windows`: Configures `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
  - `Show notifications`: Integrates with Windows 10/11 Toast Notifications.
  - `Check for updates`: Evaluates TUF `manifest.json` against current executable versions.
- **Data & Logs**: Quick shortcuts opening `%ProgramData%\Fortiq` and `%ProgramData%\Fortiq\logs` in Explorer.
- **Danger Zone**:
  - Visual styling: Deep crimson border (`#EF4444`) with subtle red wash.
  - Action: `[Stop Service]` (or `[Start Service]` if stopped) with confirmation dialog.

---

## 5. Reusable Component Implementation Specifications

### 5.1 `PathPickerControl.axaml`
Reusable folder/file selection component encapsulating `IPathPickerService`:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="Fortiq.Desktop.Controls.PathPickerControl">
  <StackPanel Spacing="6">
    <TextBlock Text="{Binding Label}" FontWeight="Medium" />
    <Grid ColumnDefinitions="*, Auto">
      <TextBox Grid.Column="0" Text="{Binding SelectedPath, Mode=TwoWay}"
               Watermark="{Binding Watermark}" VerticalContentAlignment="Center" />
      <Button Grid.Column="1" Margin="8,0,0,0" Content="Browse..."
              Command="{Binding BrowseCommand}" Padding="16,8" />
    </Grid>
    <!-- Live Path Validation & Drive Meter -->
    <StackPanel Orientation="Horizontal" Spacing="6" IsVisible="{Binding HasValidation}">
      <PathIcon Data="{Binding ValidationIcon}" Width="14" Height="14"
                Foreground="{Binding ValidationColor}" />
      <TextBlock Text="{Binding ValidationMessage}" FontSize="12"
                 Foreground="{Binding ValidationColor}" />
    </StackPanel>
  </StackPanel>
</UserControl>
```

### 5.2 `KpiStatCard.axaml`
```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="Fortiq.Desktop.Controls.KpiStatCard">
  <Border Background="{DynamicResource CardBackground}"
          BorderBrush="{DynamicResource BorderSubtle}"
          BorderThickness="1" CornerRadius="8" Padding="14">
    <StackPanel Spacing="6">
      <StackPanel Orientation="Horizontal" Spacing="6">
        <PathIcon Data="{Binding IconGeometry}" Width="14" Height="14"
                  Foreground="{DynamicResource TextSecondary}" />
        <TextBlock Text="{Binding Title}" FontSize="12"
                   Foreground="{DynamicResource TextSecondary}" />
      </StackPanel>
      <TextBlock Text="{Binding PrimaryValue}" FontSize="15" FontWeight="SemiBold"
                 Foreground="{DynamicResource TextPrimary}" />
      <Border Background="{Binding BadgeBackground}" CornerRadius="4"
              Padding="6,2" HorizontalAlignment="Left">
        <TextBlock Text="{Binding BadgeText}" FontSize="11" FontWeight="Medium"
                   Foreground="{Binding BadgeForeground}" />
      </Border>
    </StackPanel>
  </Border>
</UserControl>
```

---

## 6. Asynchronous State, Threading & Error Handling

### 6.1 Dispatcher Safety
All background I/O operations (restic execution, health file reading, S3 connection testing, hash computation) MUST execute on background worker threads via `Task.Run` or async I/O.
Updates to observable view model properties MUST marshal back to the UI thread:

```csharp
await Dispatcher.UIThread.InvokeAsync(() =>
{
    IsBusy = false;
    StatusMessage = "Operation completed successfully.";
});
```

### 6.2 Zero-State Lifecycle Handling
To prevent false-alarm crash banners on startup, `HealthFileSource` never throws unhandled `FileNotFoundException`. ViewModels bind to `HealthLoadResult.State`:
- If `NotInitialized`: The `DashboardViewModel` renders the clean Onboarding Welcome card with readiness checklist and primary "+ Protect a folder" CTA.
- If `Corrupt`: A localized, non-fatal warning card is presented with a "Regenerate Health File" action.

---

## 7. Accessibility & Windows Platform Integration

1. **Keyboard Navigation**:
   - Every interactive control has an explicit `TabIndex`.
   - Focus rings use `Color.Border.Focus` (`#3B82F6`) with a 2px offset.
2. **Screen Reader Support**:
   - `AutomationProperties.Name` and `AutomationProperties.HelpText` are defined for all icon-only buttons and badges.
3. **High Contrast Compliance**:
   - Evaluates `PlatformSettings.ColorValues.HighContrast` to increase card border thickness to 2px and switch to system high-contrast colors.
4. **Mica Backdrop** (*design intent, not implemented*):
   - When running on Windows 11 (`OSVersion.Build >= 22000`), `Window.TransparencyLevelHint` would be set to `Mica`, creating an authentic OS-integrated translucency behind the Slate surface layers.

---

## 8. References

- [ADR-015: Desktop UI Architecture, Visual Design System & Zero-State Lifecycle](adr/ADR-015-desktop-ui-architecture-and-design-system.md)
- [Spec 22: Desktop UI Architecture & Visual Design System](22-desktop-ui-and-visual-system.md)
- [Spec 17: Product UX & Recovery-First Interface](17-product-ux.md)
- [Spec 21: Embedded GUI Installer & Component Lifecycle](21-embedded-installer-and-updater.md)
- [ADR-014: Embedded GUI Installer & Autonomous Component Updater](adr/ADR-014-embedded-gui-installer-and-updater.md)

---

## 9. Decision (DEC-024): The Design Token Layer Is C#, Not XAML

**Resolved: option 2 below was taken.** Design tokens live in `DesignTokens.cs` as a single static class consumed by both windows through `using static`. No `.axaml` or `.xaml` file exists anywhere in the repository, and none is planned until runtime theme switching is actually required.

The XAML samples throughout this document describe the *shape* of a token set — role names and their groupings — not files that exist. Read them as the vocabulary, and `DesignTokens.cs` as the implementation.

### Why this mattered

Before the token layer, every colour was a hex literal declared twice: once in `MainWindow.cs` and again in `ProtectRepositoryWindow.cs`.

This is not a cosmetic gap. It is the reason the palette could change from Dark Slate to light Fluent across the whole client while [ADR-015](adr/ADR-015-desktop-ui-architecture-and-design-system.md), this specification and the README all continued to describe the old one, and nothing flagged it. With a token layer the change is one edit in one reviewable file; without one it is a diff spread across two files of layout code, where a palette decision is indistinguishable from a layout tweak.

The three options considered:

1. **Build the XAML token layer as specified.** Highest cost, and it introduces markup into a client that is currently one language. It makes the guideline true and gives theme switching (Spec 23 §Theme Selection) somewhere to attach.
2. **Keep code-built views, but extract tokens into one static class** (for example `DesignTokens.cs`) referenced by both windows. Cheap, removes the duplication, makes palette changes a reviewable one-file diff. Does not deliver runtime theme switching, which nothing currently requires.
3. **Amend this specification to describe code-built views and drop the XAML prescription.** Cheapest, and honest about what exists — but it leaves the duplication that allowed the drift in place.

Option 2 was taken. It removes the failure mode at the lowest cost and does not foreclose option 1 later: a XAML dictionary, if it is ever needed, has one C# file to be generated from rather than two files of layout code to be picked apart.

### Rules for the token layer

- **Views never name a colour.** A hex literal in a view is a defect; the whole point is that a palette change is one reviewable diff in one file.
- **Tokens are named for their role, not their hue.** `Unproven`, not `Amber`. A role name can be argued with on the merits of what Fortiq is willing to claim; a hue name can only be argued with on taste.
- **`Canvas` is spelled `CanvasBackground`**, because `Canvas` is an Avalonia control and `using static` makes the two ambiguous.
