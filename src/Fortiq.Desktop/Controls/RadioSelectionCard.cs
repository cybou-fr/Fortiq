using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using static Fortiq.Desktop.DesignTokens;

namespace Fortiq.Desktop.Controls;

/// <summary>
/// Selectable card control with radio button appearance for wizards and option forms.
/// Adheres to Spec 23 Section 7.4.
/// </summary>
public sealed class RadioSelectionCard : Border
{
    private readonly Border _radioCircle;
    private readonly Border _radioDot;
    private bool _isSelected;

    public event Action<bool>? SelectionChanged;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            UpdateVisualState();
            SelectionChanged?.Invoke(_isSelected);
        }
    }

    public RadioSelectionCard(
        string title,
        string description,
        bool isInitialSelected = false,
        string? badgeText = null)
    {
        _isSelected = isInitialSelected;
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(16, 14);
        Cursor = new Cursor(StandardCursorType.Hand);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 14,
            VerticalAlignment = VerticalAlignment.Center
        };

        _radioCircle = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(2),
            VerticalAlignment = VerticalAlignment.Center
        };

        _radioDot = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = Brand,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _radioCircle.Child = _radioDot;
        Grid.SetColumn(_radioCircle, 0);
        grid.Children.Add(_radioCircle);

        var textStack = new StackPanel
        {
            Spacing = 4
        };

        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = Ink
        };
        titleRow.Children.Add(titleBlock);

        if (!string.IsNullOrEmpty(badgeText))
        {
            var badge = new Border
            {
                Background = InfoSurface,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1),
                Child = new TextBlock
                {
                    Text = badgeText,
                    FontSize = 10,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Brand
                },
                VerticalAlignment = VerticalAlignment.Center
            };
            titleRow.Children.Add(badge);
        }

        var descBlock = new TextBlock
        {
            Text = description,
            FontSize = 12,
            FontWeight = FontWeight.Normal,
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap
        };

        textStack.Children.Add(titleRow);
        textStack.Children.Add(descBlock);
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        Child = grid;

        PointerPressed += (_, _) => IsSelected = true;

        UpdateVisualState();
    }

    private void UpdateVisualState()
    {
        if (_isSelected)
        {
            Background = InfoSurface;
            BorderBrush = Brand;
            BorderThickness = new Thickness(1.5);
            _radioCircle.BorderBrush = Brand;
            _radioDot.IsVisible = true;
        }
        else
        {
            Background = Surface;
            BorderBrush = Line;
            BorderThickness = new Thickness(1);
            _radioCircle.BorderBrush = Line;
            _radioDot.IsVisible = false;
        }
    }
}
