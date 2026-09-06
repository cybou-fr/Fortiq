using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;

namespace Fortiq.Desktop;

/// <summary>
/// The two things this application kept forgetting to give its controls: a name that means something
/// out of context, and the keys anybody expects a dialog to answer.
/// </summary>
/// <remarks>
/// The interface is built in code rather than markup, which is convenient and costs exactly this: no
/// designer ever sees a control without a label, so nothing prompts anybody to add one. A row of
/// buttons reading "Back up now", "Prove" and "Settings" is unambiguous when you can see which row it
/// is in and says nothing at all when you cannot - and there are five identical rows.
/// </remarks>
public static class Accessible
{
    /// <summary>Gives <paramref name="control"/> a name a screen reader can read on its own.</summary>
    public static T Named<T>(this T control, string name) where T : Control
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        AutomationProperties.SetName(control, name);
        return control;
    }

    /// <summary>
    /// Marks something that is decoration rather than information - a coloured dot beside a label that
    /// already says the same thing in words.
    /// </summary>
    /// <remarks>
    /// Announcing it would repeat the label, and repetition in a screen reader is not thoroughness; it
    /// is noise that makes the useful half harder to find.
    /// </remarks>
    public static T Decorative<T>(this T control) where T : Control
    {
        ArgumentNullException.ThrowIfNull(control);
        AutomationProperties.SetAccessibilityView(control, AccessibilityView.Raw);
        return control;
    }

    /// <summary>
    /// Wires the keys a dialog is expected to answer: Escape cancels, Enter takes the primary action.
    /// </summary>
    /// <remarks>
    /// Every dialog in this application had to be dismissed with the mouse. Escape doing nothing is a
    /// small thing until the dialog is the one asking whether to stop protecting a folder, at which
    /// point the only way out of a question somebody opened by accident is to find and click the right
    /// one of two buttons.
    ///
    /// Enter is wired only where a primary action is given, and deliberately not on a dialog whose
    /// primary action destroys something: a key pressed out of habit must not be the thing that
    /// confirms it.
    /// </remarks>
    public static void Keys(Window window, Button? primary = null, Action? cancel = null) =>
        Keys(window, () => primary, cancel);

    /// <summary>
    /// The same, for a window that rebuilds its buttons.
    /// </summary>
    /// <remarks>
    /// The primary button is resolved when the key is pressed rather than when this is called, so a
    /// screen that redraws does not have to re-subscribe - which would leave one handler per redraw
    /// on the window, every one of them holding a button that is no longer on it.
    /// </remarks>
    public static void Keys(Window window, Func<Button?> primary, Action? cancel = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(primary);

        window.KeyDown += (_, key) =>
        {
            if (key.Key == Key.Escape)
            {
                key.Handled = true;
                if (cancel is not null)
                {
                    cancel();
                }
                else
                {
                    window.Close();
                }

                return;
            }

            if (key.Key == Key.Enter && primary() is { IsEnabled: true } button)
            {
                key.Handled = true;
                // The click event rather than the command, because these buttons carry their work in
                // their Click handlers and this has to be the same action, not a second copy of it.
                button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            }
        };
    }
}
