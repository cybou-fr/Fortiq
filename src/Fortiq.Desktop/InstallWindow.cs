using Avalonia;
using Avalonia.Controls;
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

        Title = "Fortiq Setup & Installation";
        Icon = FortiqBrand.WindowIcon();
        Width = 780;
        Height = 680;
        MinWidth = 700;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = CanvasBackground;

        _pathTextBox.Text = _model.InstallDirectory;
        _pathTextBox.VerticalContentAlignment = VerticalAlignment.Center;
        _pathTextBox.TextChanged += (_, _) => _model.InstallDirectory = _pathTextBox.Text ?? string.Empty;

        _serviceCheckBox.Content = "Install Windows background service (NT SERVICE\\Fortiq) for scheduled protection";
        _serviceCheckBox.IsChecked = _model.InstallService;
        _serviceCheckBox.IsCheckedChanged += (_, _) => _model.InstallService = _serviceCheckBox.IsChecked ?? true;

        _pathCheckBox.Content = "Add Fortiq command-line tools to system PATH";
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

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(34, 28),
                Spacing = 20,
                Children =
                {
                    Header(),
                    ReadinessCard(),
                    OptionsCard(),
                    _progressBar,
                    _statusText,
                    _errorBanner,
                    Footer()
                }
            }
        };

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
                    Text("Fortiq Setup & Discovery", 24, FontWeight.SemiBold, Ink)
                }
            },
            Text("Configure background protection service, storage engines, and security prerequisites.", 13, FontWeight.Normal, Muted)
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
                    Text("Step 1: System Readiness Check", 15, FontWeight.SemiBold, Ink),
                    Text("Evaluates host hardware security, engine binaries, and snapshot prerequisites.", 12, FontWeight.Normal, Muted)
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
                        Text("Step 2: Destination & Options", 15, FontWeight.SemiBold, Ink),
                        Text("Designate where Fortiq components and engine binaries are deployed.", 12, FontWeight.Normal, Muted)
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
            ".NET 10 LTS Desktop Runtime",
            _model.RuntimeDetail));

        // 2. Hardware TPM 2.0
        _readinessContainer.Children.Add(ReadinessRow(
            _model.TpmAvailable ? CheckmarkBadge() : CautionBadge(),
            "Hardware TPM 2.0 Security",
            _model.TpmDetail));

        // 3. Storage Engine
        _readinessContainer.Children.Add(ReadinessRow(
            _model.EngineVerified ? CheckmarkBadge() : FailureBadge(),
            "Storage Engine (restic)",
            _model.EngineDetail));

        // 4. VSS Privileges
        _readinessContainer.Children.Add(ReadinessRow(
            _model.VssAvailable ? CheckmarkBadge() : InfoBadge(),
            "Volume Shadow Copy Service (VSS)",
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
