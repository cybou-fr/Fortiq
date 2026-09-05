using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Fortiq.Desktop.ViewModels;
using static Fortiq.Desktop.DesignTokens;

namespace Fortiq.Desktop;

/// <summary>
/// First-Run Installation & Discovery Wizard window matching Fluent v2 Slate guidelines.
/// Presents system prerequisite discovery, destination path selection, and one-click
/// UAC-elevated installation or portable execution.
/// </summary>
public sealed class InstallWindow : Window
{
    private readonly InstallViewModel _model;
    private readonly StackPanel _readinessContainer = new() { Spacing = 10 };
    private readonly TextBox _pathTextBox = new();
    private readonly CheckBox _serviceCheckBox = new();
    private readonly CheckBox _autostartCheckBox = new();
    private readonly CheckBox _pathCheckBox = new();
    private readonly ProgressBar _progressBar = new() { Height = 4, Minimum = 0, Maximum = 100, IsVisible = false };
    private readonly TextBlock _statusText = Text(string.Empty, 12, FontWeight.Normal, Brand);
    private readonly Border _errorBanner;
    private readonly TextBlock _errorText = Text(string.Empty, 12, FontWeight.Normal, Failure, true);
    private readonly Button _installButton;
    private readonly Button _portableButton;

    public InstallWindow(InstallViewModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));

        Title = "Fortiq Setup";
        Icon = FortiqBrand.WindowIcon();
        Width = 780;
        Height = 800;
        MinWidth = 700;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = CanvasBackground;

        _pathTextBox.Text = _model.InstallDirectory;
        _pathTextBox.VerticalContentAlignment = VerticalAlignment.Center;
        _pathTextBox.TextChanged += (_, _) => _model.InstallDirectory = _pathTextBox.Text ?? string.Empty;

        // Says what it does for the person, with the mechanism left out of the label. The old text -
        // "Install Windows background service (NT SERVICE\Fortiq) for scheduled protection" - named a
        // Windows account and left the reader to work out that unticking it means backups stop
        // happening on their own, which is the only thing the choice actually decides.
        _serviceCheckBox.Content = "Back up automatically, even when Fortiq is closed";
        _serviceCheckBox.IsChecked = _model.InstallService;
        _serviceCheckBox.IsCheckedChanged += (_, _) => _model.InstallService = _serviceCheckBox.IsChecked ?? true;

        _autostartCheckBox.Content = "Start Fortiq automatically when I log on to Windows (in tray)";
        _autostartCheckBox.IsChecked = _model.AutoStartOnLogon;
        _autostartCheckBox.IsCheckedChanged += (_, _) => _model.AutoStartOnLogon = _autostartCheckBox.IsChecked ?? true;

        _pathCheckBox.Content = "Let me run Fortiq commands from a terminal";
        _pathCheckBox.IsChecked = _model.AddToPath;
        _pathCheckBox.IsCheckedChanged += (_, _) => _model.AddToPath = _pathCheckBox.IsChecked ?? true;

        _errorBanner = new Border
        {
            Background = AtRiskSurface,
            BorderBrush = AtRiskLine,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10),
            IsVisible = false,
            Child = _errorText
        };

        _portableButton = new Button
        {
            Content = "Run as Portable",
            Padding = new Thickness(18, 10),
            Background = Surface,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Foreground = Ink,
            FontWeight = FontWeight.Medium
        };
        _portableButton.Click += (_, _) => _model.RunPortable();

        _installButton = new Button
        {
            Content = "Install Fortiq ➔",
            Padding = new Thickness(24, 10),
            Background = Brand,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Foreground = Brushes.White,
            FontWeight = FontWeight.SemiBold
        };
        _installButton.Click += async (_, _) => await _model.InstallAsync();

        // The action bar is docked, not scrolled. It used to sit at the bottom of the same
        // ScrollViewer as everything else, so on a smaller window - or a higher DPI, which is the
        // common case - "Install Fortiq" was below the fold and half cut off. A primary action a
        // person has to go looking for is one they cannot be sure they have found.
        var body = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new StackPanel
            {
                Margin = new Thickness(34, 28, 34, 20),
                Spacing = 20,
                Children =
                {
                    Header(),
                    ReadinessCard(),
                    OptionsCard(),
                    _progressBar,
                    _statusText,
                    _errorBanner
                }
            }
        };

        var actionBar = new Border
        {
            Background = Surface,
            BorderBrush = Line,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(34, 18),
            Child = Footer()
        };

        var layout = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(actionBar, Dock.Bottom);
        layout.Children.Add(actionBar);
        layout.Children.Add(body);

        Content = layout;

        _model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(InstallViewModel.Status)
                or nameof(InstallViewModel.IsInstalling)
                or nameof(InstallViewModel.ErrorMessage)
                or nameof(InstallViewModel.ProgressMessage)
                or nameof(InstallViewModel.ProgressPercent)
                or nameof(InstallViewModel.CanInstall))
            {
                UpdateView();
            }
        };

        UpdateView();
    }

    /// <summary>
    /// Shrinks the window to fit the display it opened on, if it does not already.
    /// </summary>
    /// <remarks>
    /// The default height is chosen so the whole wizard is visible without scrolling on an ordinary
    /// screen. On a smaller one - a 1366x768 laptop, or a scaled display where 800 logical pixels is
    /// most of the height - that same number puts the action bar under the taskbar, and the person
    /// cannot reach the button at all. Asking the screen is the difference between a window that fits
    /// everywhere and one that fits where it was designed.
    /// </remarks>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        var screen = Screens.ScreenFromWindow(this);
        if (screen is null)
        {
            return;
        }

        var available = screen.WorkingArea.Height / screen.Scaling;
        var fits = Math.Max(MinHeight, available - 60);

        if (Height > fits)
        {
            Height = fits;
        }
    }

    private static StackPanel Header() => new()
    {
        Spacing = 6,
        Children =
        {
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new Image { Source = FortiqBrand.Logo(), Width = 32, Height = 32 },
                    Text("Set up Fortiq", 24, FontWeight.SemiBold, Ink)
                }
            },
            Text("A few checks, then choose where to put it. This takes about a minute.", 13, FontWeight.Normal, Muted)
        }
    };

    private Border ReadinessCard() => Card(new StackPanel
    {
        Spacing = 14,
        Children =
        {
            new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    Text("Step 1 - What this PC can do", 15, FontWeight.SemiBold, Ink),
                    Text("Checked automatically. Everything below is either ready or explained.", 12, FontWeight.Normal, Muted)
                }
            },
            new Border { Height = 1, Background = Line },
            _readinessContainer
        }
    });

    private Border OptionsCard()
    {
        var browseButton = new Button
        {
            Content = "Browse…",
            Padding = new Thickness(14, 6),
            Background = Surface,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Foreground = Ink
        };
        browseButton.Click += async (_, _) =>
        {
            var storage = StorageProvider;
            if (storage is null) return;
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Installation Directory",
                AllowMultiple = false
            });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { Length: > 0 } path)
            {
                _pathTextBox.Text = path;
            }
        };

        var pathGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
            Children =
            {
                _pathTextBox,
                browseButton
            }
        };
        Grid.SetColumn(_pathTextBox, 0);
        Grid.SetColumn(browseButton, 1);

        return Card(new StackPanel
        {
            Spacing = 14,
            Children =
            {
                new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        Text("Step 2 - Where it goes", 15, FontWeight.SemiBold, Ink),
                        Text("The defaults are right for most people. Change them only if you have a reason.", 12, FontWeight.Normal, Muted)
                    }
                },
                new Border { Height = 1, Background = Line },
                new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        Text("Install Directory", 12, FontWeight.SemiBold, Ink),
                        pathGrid
                    }
                },
                _serviceCheckBox,
                _autostartCheckBox,
                _pathCheckBox
            }
        });
    }

    private Grid Footer()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Children =
            {
                _portableButton,
                _installButton
            }
        };
        Grid.SetColumn(_portableButton, 0);
        Grid.SetColumn(_installButton, 2);
        return grid;
    }

    private void UpdateView()
    {
        _readinessContainer.Children.Clear();

        // 1. Operating System & .NET Runtime
        _readinessContainer.Children.Add(ReadinessRow(
            _model.RuntimeValid ? CheckmarkBadge() : FailureBadge(),
            "Windows components",
            _model.RuntimeDetail));

        // 2. Hardware TPM 2.0
        _readinessContainer.Children.Add(ReadinessRow(
            _model.TpmAvailable ? CheckmarkBadge() : CautionBadge(),
            "Unlocking on this PC",
            _model.TpmDetail));

        // 3. Storage Engine
        _readinessContainer.Children.Add(ReadinessRow(
            _model.EngineVerified ? CheckmarkBadge() : FailureBadge(),
            "Backup program",
            _model.EngineDetail));

        // 4. VSS Privileges
        _readinessContainer.Children.Add(ReadinessRow(
            _model.VssAvailable ? CheckmarkBadge() : InfoBadge(),
            "Files that are in use",
            _model.VssDetail));

        // Progress and Error State
        _progressBar.IsVisible = _model.IsInstalling;
        _progressBar.Value = _model.ProgressPercent;
        _statusText.Text = _model.ProgressMessage;
        _statusText.IsVisible = _model.IsInstalling;

        if (!string.IsNullOrWhiteSpace(_model.ErrorMessage))
        {
            _errorText.Text = _model.ErrorMessage;
            _errorBanner.IsVisible = true;
        }
        else
        {
            _errorBanner.IsVisible = false;
        }

        _installButton.IsEnabled = _model.CanInstall;
        _portableButton.IsEnabled = _model.CanRunPortable;
        _pathTextBox.IsEnabled = !_model.IsInstalling;
        _serviceCheckBox.IsEnabled = !_model.IsInstalling;
        _autostartCheckBox.IsEnabled = !_model.IsInstalling;
        _pathCheckBox.IsEnabled = !_model.IsInstalling;
    }

    private static Grid ReadinessRow(Control badge, string title, string description)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 12,
            VerticalAlignment = VerticalAlignment.Center
        };

        var details = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                Text(title, 13, FontWeight.SemiBold, Ink),
                Text(description, 11, FontWeight.Normal, Muted, true)
            }
        };

        Grid.SetColumn(badge, 0);
        Grid.SetColumn(details, 1);
        grid.Children.Add(badge);
        grid.Children.Add(details);
        return grid;
    }

    private static Border CheckmarkBadge() => StatusBadge("✓", RecoverableSurface, Recoverable);
    private static Border CautionBadge() => StatusBadge("!", UnprovenSurface, Unproven);
    private static Border FailureBadge() => StatusBadge("✕", AtRiskSurface, AtRisk);
    private static Border InfoBadge() => StatusBadge("ℹ", InfoSurface, Brand);

    private static Border StatusBadge(string symbol, IBrush bg, IBrush fg) => new()
    {
        Width = 24,
        Height = 24,
        CornerRadius = new CornerRadius(12),
        Background = bg,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = symbol,
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = fg,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private static Border Card(Control child) => new()
    {
        Background = Surface,
        BorderBrush = Line,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(18),
        Child = child
    };

    private static TextBlock Text(
        string value,
        double size,
        FontWeight weight,
        IBrush brush,
        bool wrap = false) => new()
    {
        Text = value,
        FontSize = size,
        FontWeight = weight,
        Foreground = brush,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap
    };
}
