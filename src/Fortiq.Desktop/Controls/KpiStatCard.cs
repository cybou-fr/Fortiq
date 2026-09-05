using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using static Fortiq.Desktop.DesignTokens;

namespace Fortiq.Desktop.Controls;

/// <summary>
/// Metric display card for Dashboard overview metrics.
/// Adheres to Spec 23 Section 7.2.
/// </summary>
public sealed class KpiStatCard : Border
{
    public KpiStatCard(
        string title,
        string primaryValue,
        string statusText,
        IBrush statusBrush,
        IBrush? statusBgBrush = null)
    {
        Background = Surface;
        BorderBrush = Line;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(16, 14);

        var stack = new StackPanel
        {
            Spacing = 8
        };

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = Muted
        };

        var valueBlock = new TextBlock
        {
            Text = primaryValue,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = Ink,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var pillBorder = new Border
        {
            Background = statusBgBrush ?? InfoSurface,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var pillText = new TextBlock
        {
            Text = statusText,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = statusBrush
        };
        pillBorder.Child = pillText;

        stack.Children.Add(titleBlock);
        stack.Children.Add(valueBlock);
        stack.Children.Add(pillBorder);

        Child = stack;
    }
}
