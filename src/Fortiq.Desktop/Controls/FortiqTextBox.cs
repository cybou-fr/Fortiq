using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using static Fortiq.Desktop.DesignTokens;

namespace Fortiq.Desktop.Controls;

/// <summary>
/// Text fields that stay readable when they are pointed at, focused or typed into.
/// </summary>
/// <remarks>
/// The same defect the buttons had, in the control where it matters more. Setting Background and
/// Foreground on a TextBox paints the control; the Fluent theme paints the fill on
/// <c>Border#PART_BorderElement</c> inside the template, and its <c>:pointerover</c> and
/// <c>:focus</c> setters target that border. A template setter beats a local value, so the moment a
/// field was clicked it took the theme's focused fill - dark on this machine - while the text stayed
/// dark ink. A black box with black text, at the exact moment somebody is typing their storage
/// endpoint or their secret key into it.
/// </remarks>
public static class FortiqTextBox
{
    /// <summary>Applies the product's own colours to every state of <paramref name="box"/>.</summary>
    public static TextBox Style(TextBox box)
    {
        ArgumentNullException.ThrowIfNull(box);

        box.Background = Surface;
        box.Foreground = Ink;
        box.BorderBrush = Line;
        box.CornerRadius = new CornerRadius(6);
        box.Padding = new Thickness(10, 8);

        // Rest is set above; these three are the states the theme would otherwise repaint.
        box.Styles.Add(BorderFill(":pointerover", Surface, BorderMedium));
        box.Styles.Add(BorderFill(":focus", Surface, BorderFocus));
        box.Styles.Add(BorderFill(":focus-within", Surface, BorderFocus));

        // The text itself, for the same reason: a foreground set on the control is not what the
        // template's presenter uses once the theme has an opinion about the state.
        box.Styles.Add(TextFill(":pointerover", Ink));
        box.Styles.Add(TextFill(":focus", Ink));
        box.Styles.Add(TextFill(":focus-within", Ink));

        return box;
    }

    /// <summary>A field carrying the product's styling from the start.</summary>
    public static TextBox Create(string? placeholder = null, bool masked = false)
    {
        var box = Style(new TextBox { PlaceholderText = placeholder ?? string.Empty });

        if (masked)
        {
            box.PasswordChar = '•';
        }

        return box;
    }

    private static Style BorderFill(string pseudoClass, IBrush background, IBrush border)
    {
        var style = new Style(selector => selector
            .OfType<TextBox>()
            .Class(pseudoClass)
            .Template()
            .OfType<Border>()
            .Name("PART_BorderElement"));

        style.Add(new Setter(Border.BackgroundProperty, background));
        style.Add(new Setter(Border.BorderBrushProperty, border));
        return style;
    }

    private static Style TextFill(string pseudoClass, IBrush foreground)
    {
        var style = new Style(selector => selector.OfType<TextBox>().Class(pseudoClass));
        style.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, foreground));
        return style;
    }
}
