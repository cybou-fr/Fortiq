using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Styling;
using static Fortiq.Desktop.DesignTokens;

namespace Fortiq.Desktop.Controls;

/// <summary>
/// The buttons this application uses, styled so they survive being pointed at.
/// </summary>
/// <remarks>
/// Every button here used to be built by setting <c>Background</c> and <c>Foreground</c> directly on
/// the control. That looks right and is not: the Fluent theme paints a button's fill on its inner
/// <c>ContentPresenter</c>, and its <c>:pointerover</c> setter targets that presenter. A template
/// setter beats a local value on the outer control, so hovering replaced the brand fill with the
/// theme's default light grey while the foreground stayed white - white text on a near-white
/// background. The primary action of every screen vanished under the cursor, which is the one moment
/// a person is certain to be looking at it.
///
/// The fix is to style the presenter for each state rather than assign a colour once, and to do it in
/// one place: the same mistake was independently made in five.
/// </remarks>
public static class FortiqButton
{
    /// <summary>The main action of a screen: solid brand fill, white label.</summary>
    public static Button Primary(string label) => Build(
        label,
        Brand,
        BrandHover,
        Brushes.White,
        border: null,
        padding: new Thickness(18, 9),
        weight: FontWeight.SemiBold);

    /// <summary>A secondary action: outlined, and readable against the surface it sits on.</summary>
    public static Button Secondary(string label) => Build(
        label,
        Surface,
        CardHoverBackground,
        Ink,
        border: Line,
        padding: new Thickness(14, 8),
        weight: FontWeight.Medium);

    private static Button Build(
        string label,
        IBrush rest,
        IBrush hover,
        IBrush foreground,
        IBrush? border,
        Thickness padding,
        FontWeight weight)
    {
        var button = new Button
        {
            Content = label,
            Foreground = foreground,
            Background = rest,
            BorderBrush = border ?? Brushes.Transparent,
            BorderThickness = new Thickness(border is null ? 0 : 1),
            CornerRadius = new CornerRadius(6),
            Padding = padding,
            FontWeight = weight
        };

        // Each state names the presenter explicitly. Setting Background on the button alone leaves the
        // theme free to repaint the presenter underneath it on hover, which is the whole defect.
        button.Styles.Add(PresenterFill(":pointerover", hover, foreground));
        button.Styles.Add(PresenterFill(":pressed", hover, foreground));

        // Disabled has to stay legible too. A primary action that is off should look off and still be
        // readable - a person needs to see the thing they cannot use yet in order to work out why.
        button.Styles.Add(PresenterFill(":disabled", StepInactive, TextMuted));

        return button;
    }

    private static Style PresenterFill(string pseudoClass, IBrush background, IBrush foreground)
    {
        // The colon stays. Avalonia's Class() takes a pseudo-class with its ':' prefix; stripping it
        // asks for a real style class of that name, which nothing has - so the style silently matched
        // nothing and the hover bug survived a commit claiming to fix it.
        var style = new Style(selector => selector
            .OfType<Button>()
            .Class(pseudoClass)
            .Template()
            .OfType<ContentPresenter>());

        style.Add(new Setter(ContentPresenter.BackgroundProperty, background));
        style.Add(new Setter(ContentPresenter.ForegroundProperty, foreground));
        return style;
    }
}
