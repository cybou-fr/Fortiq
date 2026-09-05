using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using static Fortiq.Desktop.DesignTokens;

namespace Fortiq.Desktop.Controls;

public enum HeroStatusMode
{
    Recoverable,
    Unproven,
    AtRisk,
    Info,
    ZeroState
}

/// <summary>
/// Prominent hero banner showing current data assurance verdict and primary call to action.
/// Adheres to Spec 23 Section 7.1.
/// </summary>
public sealed class HeroHealthBanner : Border
{
    public HeroHealthBanner(
        HeroStatusMode mode,
        string headline,
        string description,
        string? actionLabel = null,
        Func<Task>? onAction = null)
    {
        var (background, borderBrush, accent) = mode switch
        {
            HeroStatusMode.Recoverable => (RecoverableSurface, Recoverable, Recoverable),
            HeroStatusMode.Unproven => (UnprovenSurface, UnprovenLine, Unproven),
            HeroStatusMode.AtRisk => (AtRiskSurface, AtRiskLine, AtRisk),
            HeroStatusMode.Info => (InfoSurface, InfoLine, Brand),
            HeroStatusMode.ZeroState or _ => (InfoSurface, InfoLine, Brand)
        };

        Background = background;
        BorderBrush = borderBrush;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(12);
        Padding = new Thickness(24, 20);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            VerticalAlignment = VerticalAlignment.Center
        };

        var textStack = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 0, 16, 0)
        };

        var statusRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        var dot = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(5),
            Background = accent,
            VerticalAlignment = VerticalAlignment.Center
        };
        statusRow.Children.Add(dot);

        var title = new TextBlock
        {
            Text = headline,
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            Foreground = Ink,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        statusRow.Children.Add(title);

        var subtitle = new TextBlock
        {
            Text = description,
            FontSize = 13,
            FontWeight = FontWeight.Normal,
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap
        };

        textStack.Children.Add(statusRow);
        textStack.Children.Add(subtitle);

        Grid.SetColumn(textStack, 0);
        grid.Children.Add(textStack);

        if (!string.IsNullOrEmpty(actionLabel) && onAction != null)
        {
            // Through FortiqButton, like every other action in the product. Painted locally it would
            // vanish under the pointer: the Fluent theme repaints the presenter on hover and a local
            // background does not survive that.
            var button = FortiqButton.Primary(actionLabel);
            button.VerticalAlignment = VerticalAlignment.Center;
            button.Click += async (_, _) => await onAction();

            Grid.SetColumn(button, 1);
            grid.Children.Add(button);
        }

        Child = grid;
    }
}
