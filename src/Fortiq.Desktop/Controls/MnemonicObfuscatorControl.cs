using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using static Fortiq.Desktop.DesignTokens;

namespace Fortiq.Desktop.Controls;

/// <summary>
/// BIP-39 Mnemonic obfuscator and viewer control for Recovery Kit.
/// Adheres to Spec 23 Section 7.5.
/// </summary>
public sealed class MnemonicObfuscatorControl : StackPanel
{
    private readonly string[] _words;
    private readonly Grid _wordGrid;
    private readonly Button _toggleButton;
    private readonly TextBlock _maskedPlaceholder;
    private bool _isRevealed;

    public MnemonicObfuscatorControl(string? mnemonicPhrase)
    {
        Spacing = 14;
        _words = (mnemonicPhrase ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Security Notice
        var warningCard = new Border
        {
            Background = UnprovenSurface,
            BorderBrush = UnprovenLine,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Your recovery mnemonic is extremely sensitive",
                        FontSize = 13,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Caution
                    },
                    new TextBlock
                    {
                        Text = "Anyone who acquires these 24 words can unlock your backups. Never screenshot or store in digital cloud notes.",
                        FontSize = 12,
                        FontWeight = FontWeight.Normal,
                        Foreground = Muted,
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            }
        };
        Children.Add(warningCard);

        // Action Toolbar (Show/Hide, Copy)
        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            VerticalAlignment = VerticalAlignment.Center
        };

        var label = new TextBlock
        {
            Text = "Disaster Recovery Secret (BIP-39)",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = Ink,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 0);
        toolbar.Children.Add(label);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };

        _toggleButton = new Button
        {
            Content = "Show Mnemonic",
            Background = Surface,
            Foreground = Ink,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 6),
            FontWeight = FontWeight.Medium,
            FontSize = 12
        };
        _toggleButton.Click += (_, _) => ToggleVisibility();
        actions.Children.Add(_toggleButton);

        Grid.SetColumn(actions, 1);
        toolbar.Children.Add(actions);
        Children.Add(toolbar);

        // Word Grid vs Masked Placeholder
        _maskedPlaceholder = new TextBlock
        {
            Text = "●●●● ●●●● ●●●● ●●●● ●●●● ●●●● ●●●● ●●●● ●●●● ●●●● ●●●● ●●●●",
            FontSize = 16,
            FontWeight = FontWeight.Medium,
            Foreground = Muted,
            LetterSpacing = 3,
            Margin = new Thickness(12, 20)
        };

        var wordCount = Math.Max(12, _words.Length);
        var columns = 4;
        var rows = (int)Math.Ceiling(wordCount / (double)columns);

        _wordGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*"),
            RowSpacing = 8,
            ColumnSpacing = 8,
            IsVisible = false
        };

        for (var i = 0; i < rows; i++)
        {
            _wordGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        for (var index = 0; index < _words.Length; index++)
        {
            var card = new Border
            {
                Background = Surface,
                BorderBrush = Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8),
                Child = new TextBlock
                {
                    Text = $"{index + 1}.  {_words[index]}",
                    FontSize = 13,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Ink
                }
            };

            Grid.SetColumn(card, index % columns);
            Grid.SetRow(card, index / columns);
            _wordGrid.Children.Add(card);
        }

        var wordsContainer = new Border
        {
            Background = Surface,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Children = { _maskedPlaceholder, _wordGrid }
            }
        };
        Children.Add(wordsContainer);
    }

    private void ToggleVisibility()
    {
        _isRevealed = !_isRevealed;
        _maskedPlaceholder.IsVisible = !_isRevealed;
        _wordGrid.IsVisible = _isRevealed;
        _toggleButton.Content = _isRevealed ? "Hide Mnemonic" : "Show Mnemonic";
    }
}
