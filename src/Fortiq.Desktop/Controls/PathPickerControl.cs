using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using static Fortiq.Desktop.DesignTokens;

namespace Fortiq.Desktop.Controls;

/// <summary>
/// Reusable Path Picker Control for selecting local folders or file destinations.
/// Integrates seamlessly with Avalonia StorageProvider dialogs.
/// </summary>
public sealed class PathPickerControl : StackPanel
{
    private readonly TextBox _textBox;
    private readonly Button _browseButton;
    private readonly TextBlock _labelBlock;
    private readonly TextBlock _hintBlock;
    private readonly Window _parentWindow;
    private readonly bool _isFolderPicker;

    public event Action<string>? PathChanged;

    public string SelectedPath
    {
        get => _textBox.Text ?? string.Empty;
        set => _textBox.Text = value;
    }

    public PathPickerControl(
        Window parentWindow,
        string label,
        string hint,
        string initialPath = "",
        bool isFolderPicker = true,
        string placeholder = "Choose or enter a folder path...")
    {
        _parentWindow = parentWindow ?? throw new ArgumentNullException(nameof(parentWindow));
        _isFolderPicker = isFolderPicker;

        Spacing = 6;

        _labelBlock = new TextBlock
        {
            Text = label,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Ink
        };

        _hintBlock = new TextBlock
        {
            Text = hint,
            FontSize = 11,
            FontWeight = FontWeight.Normal,
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap
        };

        _textBox = new TextBox
        {
            Text = initialPath,
            PlaceholderText = placeholder,
            CornerRadius = new CornerRadius(6),
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            Background = Surface,
            Foreground = Ink,
            Padding = new Thickness(10, 8),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _textBox.TextChanged += (_, _) => PathChanged?.Invoke(_textBox.Text ?? string.Empty);

        _browseButton = new Button
        {
            Content = "Browse…",
            Padding = new Thickness(14, 8),
            CornerRadius = new CornerRadius(6),
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            Background = Surface,
            Foreground = Ink,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _browseButton.Click += async (_, _) => await BrowseAsync();

        var inputRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8
        };
        Grid.SetColumn(_textBox, 0);
        Grid.SetColumn(_browseButton, 1);
        inputRow.Children.Add(_textBox);
        inputRow.Children.Add(_browseButton);

        Children.Add(_labelBlock);
        if (!string.IsNullOrWhiteSpace(hint))
        {
            Children.Add(_hintBlock);
        }
        Children.Add(inputRow);
    }

    private async Task BrowseAsync()
    {
        if (_isFolderPicker)
        {
            var folders = await _parentWindow.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = _labelBlock.Text,
                AllowMultiple = false
            });

            if (folders.Count > 0)
            {
                var selected = folders[0].TryGetLocalPath() ?? folders[0].Path.LocalPath;
                _textBox.Text = selected;
            }
        }
        else
        {
            var files = await _parentWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = _labelBlock.Text,
                AllowMultiple = false
            });

            if (files.Count > 0)
            {
                var selected = files[0].TryGetLocalPath() ?? files[0].Path.LocalPath;
                _textBox.Text = selected;
            }
        }
    }
}
