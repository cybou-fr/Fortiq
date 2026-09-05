using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace Fortiq.Desktop;

/// <summary>
/// Every colour the desktop paints, in one place.
/// Adheres to the Fluent v2 Slate design system specified in Spec 22, Spec 23, and ADR-015.
/// Supports both Light Slate and Dark Slate themes dynamically.
/// </summary>
public static class DesignTokens
{
    private static bool _isDark;

    public static event Action? ThemeChanged;

    public static bool IsDark => _isDark;

    public static void SetTheme(ThemeVariant variant)
    {
        SetTheme(variant == ThemeVariant.Dark);
    }

    public static void SetTheme(bool dark)
    {
        if (_isDark == dark) return;
        _isDark = dark;
        ThemeChanged?.Invoke();
    }

    // --- Dynamic Themed Brushes ---

    /// <summary>Window background canvas.</summary>
    public static IBrush CanvasBackground => _isDark ? DarkCanvasBackground : LightCanvasBackground;

    /// <summary>Sidebar navigation background.</summary>
    public static IBrush SidebarBackground => _isDark ? DarkSidebarBackground : LightSidebarBackground;

    /// <summary>Cards and primary content surfaces (Surface alias).</summary>
    public static IBrush Surface => _isDark ? DarkCardBackground : LightCardBackground;

    /// <summary>Elevated surface (modals, dropdowns, hovered items).</summary>
    public static IBrush CardElevatedBackground => _isDark ? DarkCardElevatedBackground : LightCardElevatedBackground;

    /// <summary>Card hover background.</summary>
    public static IBrush CardHoverBackground => _isDark ? DarkCardHoverBackground : LightCardHoverBackground;

    /// <summary>Card outlines, dividers and subtle lines.</summary>
    public static IBrush Line => _isDark ? DarkBorderSubtle : LightBorderSubtle;

    /// <summary>Medium borders for form controls.</summary>
    public static IBrush BorderMedium => _isDark ? DarkBorderMedium : LightBorderMedium;

    /// <summary>Focus border for interactive inputs.</summary>
    public static IBrush BorderFocus => _isDark ? DarkBorderFocus : LightBorderFocus;

    /// <summary>Headings and primary text (Ink alias).</summary>
    public static IBrush Ink => _isDark ? DarkTextPrimary : LightTextPrimary;

    /// <summary>Helper text, timestamps, secondary labels (Muted alias).</summary>
    public static IBrush Muted => _isDark ? DarkTextSecondary : LightTextSecondary;

    /// <summary>Dimmed or tertiary text.</summary>
    public static IBrush TextMuted => _isDark ? DarkTextMuted : LightTextMuted;

    /// <summary>Primary actions, active navigation, brand accent.</summary>
    public static IBrush Brand => _isDark ? DarkBrandPrimary : LightBrandPrimary;

    /// <summary>Brand hover state.</summary>
    public static IBrush BrandHover => _isDark ? DarkBrandHover : LightBrandHover;

    /// <summary>Informational callout surface.</summary>
    public static IBrush InfoSurface => _isDark ? DarkInfoSurface : LightInfoSurface;

    /// <summary>Informational callout border.</summary>
    public static IBrush InfoLine => _isDark ? DarkInfoLine : LightInfoLine;

    /// <summary>A step of the wizard that has not been reached yet.</summary>
    public static IBrush StepInactive => _isDark ? DarkStepInactive : LightStepInactive;

    /// <summary>Proven recoverable: backed up, checked, and restored from within thresholds.</summary>
    public static IBrush Recoverable => _isDark ? DarkStatusSuccess : LightStatusSuccess;

    /// <summary>Surface tint for proven recoverable status.</summary>
    public static IBrush RecoverableSurface => _isDark ? DarkStatusSuccessBg : LightStatusSuccessBg;

    /// <summary>Unproven verdict: backed up, but not yet restored/proven.</summary>
    public static IBrush Unproven => _isDark ? DarkStatusWarning : LightStatusWarning;

    /// <summary>Surface tint for unproven status.</summary>
    public static IBrush UnprovenSurface => _isDark ? DarkStatusWarningBg : LightStatusWarningBg;

    /// <summary>Border for unproven cards.</summary>
    public static IBrush UnprovenLine => _isDark ? DarkUnprovenLine : LightUnprovenLine;

    /// <summary>At-risk verdict: backup missing, failure or critical warning.</summary>
    public static IBrush AtRisk => _isDark ? DarkStatusDanger : LightStatusDanger;

    /// <summary>Surface tint for at-risk status.</summary>
    public static IBrush AtRiskSurface => _isDark ? DarkStatusDangerBg : LightStatusDangerBg;

    /// <summary>Border for at-risk cards.</summary>
    public static IBrush AtRiskLine => _isDark ? DarkAtRiskLine : LightAtRiskLine;

    /// <summary>Text of a failure that the operator needs to read.</summary>
    public static IBrush Failure => _isDark ? DarkFailure : LightFailure;

    /// <summary>Text of a caution that is not a failure.</summary>
    public static IBrush Caution => _isDark ? DarkCaution : LightCaution;

    // --- Static Palette Definitions ---

    // Light Slate Palette
    private static readonly IBrush LightCanvasBackground = Of("#F8FAFC");
    private static readonly IBrush LightSidebarBackground = Of("#F1F5F9");
    private static readonly IBrush LightCardBackground = Of("#FFFFFF");
    private static readonly IBrush LightCardElevatedBackground = Of("#F8FAFC");
    private static readonly IBrush LightCardHoverBackground = Of("#F1F5F9");
    private static readonly IBrush LightBorderSubtle = Of("#E2E8F0");
    private static readonly IBrush LightBorderMedium = Of("#CBD5E1");
    private static readonly IBrush LightBorderFocus = Of("#2563EB");
    private static readonly IBrush LightTextPrimary = Of("#0F172A");
    private static readonly IBrush LightTextSecondary = Of("#475569");
    private static readonly IBrush LightTextMuted = Of("#94A3B8");
    private static readonly IBrush LightBrandPrimary = Of("#2563EB");
    private static readonly IBrush LightBrandHover = Of("#1D4ED8");
    private static readonly IBrush LightInfoSurface = Of("#EFF6FF");
    private static readonly IBrush LightInfoLine = Of("#BFDBFE");
    private static readonly IBrush LightStepInactive = Of("#CBD5E1");
    private static readonly IBrush LightStatusSuccess = Of("#059669");
    private static readonly IBrush LightStatusSuccessBg = Of("#ECFDF5");
    private static readonly IBrush LightStatusWarning = Of("#D97706");
    private static readonly IBrush LightStatusWarningBg = Of("#FFFBEB");
    private static readonly IBrush LightUnprovenLine = Of("#FDE68A");
    private static readonly IBrush LightStatusDanger = Of("#DC2626");
    private static readonly IBrush LightStatusDangerBg = Of("#FEF2F2");
    private static readonly IBrush LightAtRiskLine = Of("#FECACA");
    private static readonly IBrush LightFailure = Of("#B91C1C");
    private static readonly IBrush LightCaution = Of("#B45309");

    // Dark Slate Palette
    private static readonly IBrush DarkCanvasBackground = Of("#0F1117");
    private static readonly IBrush DarkSidebarBackground = Of("#141720");
    private static readonly IBrush DarkCardBackground = Of("#1A1D26");
    private static readonly IBrush DarkCardElevatedBackground = Of("#222634");
    private static readonly IBrush DarkCardHoverBackground = Of("#282D3D");
    private static readonly IBrush DarkBorderSubtle = Of("#2A3042");
    private static readonly IBrush DarkBorderMedium = Of("#374151");
    private static readonly IBrush DarkBorderFocus = Of("#3B82F6");
    private static readonly IBrush DarkTextPrimary = Of("#F8FAFC");
    private static readonly IBrush DarkTextSecondary = Of("#94A3B8");
    private static readonly IBrush DarkTextMuted = Of("#64748B");
    private static readonly IBrush DarkBrandPrimary = Of("#3B82F6");
    private static readonly IBrush DarkBrandHover = Of("#60A5FA");
    private static readonly IBrush DarkInfoSurface = Of("#1E293B");
    private static readonly IBrush DarkInfoLine = Of("#334155");
    private static readonly IBrush DarkStepInactive = Of("#334155");
    private static readonly IBrush DarkStatusSuccess = Of("#10B981");
    private static readonly IBrush DarkStatusSuccessBg = Of("#163326");
    private static readonly IBrush DarkStatusWarning = Of("#F59E0B");
    private static readonly IBrush DarkStatusWarningBg = Of("#332712");
    private static readonly IBrush DarkUnprovenLine = Of("#78350F");
    private static readonly IBrush DarkStatusDanger = Of("#EF4444");
    private static readonly IBrush DarkStatusDangerBg = Of("#331518");
    private static readonly IBrush DarkAtRiskLine = Of("#7F1D1D");
    private static readonly IBrush DarkFailure = Of("#F87171");
    private static readonly IBrush DarkCaution = Of("#FBBF24");

    private static SolidColorBrush Of(string value) => new(Color.Parse(value));
}
