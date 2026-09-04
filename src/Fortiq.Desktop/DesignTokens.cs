using Avalonia.Media;

namespace Fortiq.Desktop;

/// <summary>
/// Every colour the desktop paints, in one place.
/// </summary>
/// <remarks>
/// This exists because of a specific failure. The palette was changed from a dark scheme to this
/// light one across the whole client while the ADR, the UI specification and the README went on
/// describing the old one, and nothing caught it for several commits. The colours were hex literals
/// duplicated across two files of layout code, where a palette decision is indistinguishable from a
/// layout tweak, so there was no diff a reviewer could look at and call a palette change.
/// <para>
/// Changing a colour is now one edit in one file. That is the whole point; it is not an abstraction
/// for its own sake. See DEC-024 and ADR-015 Revision 1.
/// </para>
/// <para>
/// Names describe the role, not the colour. A token called <see cref="Unproven"/> can be argued with
/// on the merits; one called "Amber" can only be argued with on taste, and the argument that matters
/// here is about what Fortiq is willing to claim.
/// </para>
/// </remarks>
public static class DesignTokens
{
    /// <summary>Window background. Named in full because <c>Canvas</c> is an Avalonia control.</summary>
    public static readonly IBrush CanvasBackground = Of("#F6F8FB");

    /// <summary>Cards, panels and the navigation rail.</summary>
    public static readonly IBrush Surface = Of("#FFFFFF");

    /// <summary>Card outlines and dividers.</summary>
    public static readonly IBrush Line = Of("#E3E8EF");

    /// <summary>Headings and primary text.</summary>
    public static readonly IBrush Ink = Of("#172033");

    /// <summary>Helper text, timestamps, secondary labels.</summary>
    public static readonly IBrush Muted = Of("#667085");

    /// <summary>Primary actions, active navigation, informational text.</summary>
    public static readonly IBrush Brand = Of("#0866D9");

    /// <summary>Surface behind guidance callouts, with <see cref="InfoLine"/> as its border.</summary>
    public static readonly IBrush InfoSurface = Of("#EAF3FF");

    /// <inheritdoc cref="InfoSurface"/>
    public static readonly IBrush InfoLine = Of("#B9D7FF");

    /// <summary>A step of the wizard that has not been reached yet.</summary>
    public static readonly IBrush StepInactive = Of("#D5DBE5");

    /// <summary>
    /// Proven recoverable: backed up, checked, and restored from within the thresholds.
    /// </summary>
    public static readonly IBrush Recoverable = Of("#159455");

    /// <inheritdoc cref="Recoverable"/>
    public static readonly IBrush RecoverableSurface = Of("#EAF8F0");

    /// <summary>
    /// Backed up, but nobody has restored from it. Deliberately not green: a repository nobody has
    /// restored from looks finished to a person, and removing that impression is why the verdict
    /// exists at all.
    /// </summary>
    public static readonly IBrush Unproven = Of("#B7791F");

    /// <inheritdoc cref="Unproven"/>
    public static readonly IBrush UnprovenSurface = Of("#FFF8E7");

    /// <inheritdoc cref="Unproven"/>
    public static readonly IBrush UnprovenLine = Of("#F4CC73");

    /// <summary>A recovery that would fail today.</summary>
    public static readonly IBrush AtRisk = Of("#D92D20");

    /// <inheritdoc cref="AtRisk"/>
    public static readonly IBrush AtRiskSurface = Of("#FFF4F2");

    /// <inheritdoc cref="AtRisk"/>
    public static readonly IBrush AtRiskLine = Of("#FDA29B");

    /// <summary>Text of a failure the person needs to read, rather than a verdict badge.</summary>
    public static readonly IBrush Failure = Of("#B42318");

    /// <summary>Text of a caution that is not a failure, such as the recovery-phrase warning.</summary>
    public static readonly IBrush Caution = Of("#8A5A00");

    private static SolidColorBrush Of(string value) => new(Color.Parse(value));
}
