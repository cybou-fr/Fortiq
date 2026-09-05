using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Fortiq.Desktop.Controls;
using Fortiq.Desktop.ViewModels;
using System.Globalization;
using static Fortiq.Desktop.DesignTokens;

namespace Fortiq.Desktop;

public sealed class ProtectRepositoryWindow : Window
{
    private readonly ProtectRepositoryViewModel _model;
    private readonly ContentControl _stepperHost = new();
    private readonly StackPanel _content = new() { Spacing = 18 };
    private readonly TextBlock _failure = Text(string.Empty, 12, FontWeight.Normal, Failure, true);
    private int _setupStep;
    private bool _isS3Storage;

    public ProtectRepositoryWindow(ProtectRepositoryViewModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        Title = "Protect your data — Fortiq";
        Icon = FortiqBrand.WindowIcon();
        Width = 820;
        Height = 680;
        MinWidth = 720;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = CanvasBackground;

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(34, 28),
                Spacing = 18,
                Children = { Header(), _stepperHost, _content, _failure }
            }
        };

        _model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProtectRepositoryViewModel.Step)
                or nameof(ProtectRepositoryViewModel.Failure)
                or nameof(ProtectRepositoryViewModel.SchedulingFailure))
            {
                Render();
            }
        };

        Render();
    }

    public bool Protected { get; private set; }

    private static StackPanel Header() => new()
    {
        Spacing = 4,
        Children =
        {
            Text("Protect your data", 25, FontWeight.SemiBold, Ink),
            Text("Create a verifiable, encrypted backup without hiding important recovery decisions.", 13, FontWeight.Normal, Muted)
        }
    };

    private Grid Stepper()
    {
        var steps = _model.Step switch
        {
            ProtectStep.WriteDownRecoveryMaterial => new[] { "Repository", "Sources", "Schedule", "Review", "Recovery phrase" },
            ProtectStep.ConfirmRecoveryMaterial or ProtectStep.Done => new[] { "Repository", "Sources", "Schedule", "Review", "Verify" },
            _ => new[] { "Repository", "Sources", "Schedule", "Review" }
        };

        var active = _model.Step == ProtectStep.Describe ? _setupStep : steps.Length - 1;
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(string.Join(',', Enumerable.Repeat("*", steps.Length))),
            ColumnSpacing = 8
        };

        for (var index = 0; index < steps.Length; index++)
        {
            var selected = index <= active;
            var marker = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = selected ? Brand : StepInactive,
                Child = Text((index + 1).ToString(CultureInfo.InvariantCulture), 11, FontWeight.SemiBold, Brushes.White),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var item = new StackPanel
            {
                Spacing = 6,
                Children = { marker, Text(steps[index], 11, selected ? FontWeight.SemiBold : FontWeight.Normal, selected ? Ink : Muted) }
            };
            Grid.SetColumn(item, index);
            grid.Children.Add(item);
        }
        return grid;
    }

    private void Render()
    {
        _stepperHost.Content = Stepper();
        _content.Children.Clear();
        _failure.Text = _model.Failure ?? string.Empty;
        _failure.IsVisible = _model.Failure is { Length: > 0 };

        if (_model.Step == ProtectStep.Describe) RenderSetup();
        else if (_model.Step == ProtectStep.WriteDownRecoveryMaterial) RenderPhrase();
        else if (_model.Step == ProtectStep.ConfirmRecoveryMaterial) RenderVerify();
        else RenderDone();
    }

    private void RenderSetup()
    {
        switch (_setupStep)
        {
            case 0: Repository(); break;
            case 1: Sources(); break;
            case 2: Schedule(); break;
            default: Review(); break;
        }
    }

    private void Repository()
    {
        _content.Children.Add(SectionTitle("Choose repository destination", "Select where encrypted backup archives and disaster recovery material will be stored."));

        var localCard = new RadioSelectionCard(
            "Local folder or external drive",
            "Store backups on a fast local disk, external USB drive, or attached network share.",
            !_isS3Storage,
            "Default");

        var s3Card = new RadioSelectionCard(
            "Amazon S3 or S3-compatible cloud storage",
            "Store offsite in object storage with optional Object Lock immutability against ransomware.",
            _isS3Storage,
            "Offsite");

        localCard.SelectionChanged += selected =>
        {
            if (selected)
            {
                _isS3Storage = false;
                s3Card.IsSelected = false;
                Render();
            }
        };

        s3Card.SelectionChanged += selected =>
        {
            if (selected)
            {
                _isS3Storage = true;
                localCard.IsSelected = false;
                Render();
            }
        };

        var storageOptions = new StackPanel { Spacing = 10, Children = { localCard, s3Card } };
        _content.Children.Add(storageOptions);

        if (_isS3Storage)
        {
            var s3Panel = new StackPanel { Spacing = 12 };
            var bucketBox = new TextBox
            {
                Text = _model.RepositoryLocation.StartsWith("s3:", StringComparison.OrdinalIgnoreCase) ? _model.RepositoryLocation : "s3:https://s3.amazonaws.com/my-fortiq-backup",
                PlaceholderText = "s3:https://s3.region.amazonaws.com/bucket-name",
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8),
                Background = Surface,
                Foreground = Ink,
                BorderBrush = Line
            };
            bucketBox.TextChanged += (_, _) => _model.RepositoryLocation = bucketBox.Text ?? string.Empty;
            _model.RepositoryLocation = bucketBox.Text ?? string.Empty;

            s3Panel.Children.Add(new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    Text("S3 Target URL or Bucket", 13, FontWeight.SemiBold, Ink),
                    Text("Enter full restic S3 endpoint URI with bucket name.", 11, FontWeight.Normal, Muted),
                    bucketBox
                }
            });

            var kitPicker = new PathPickerControl(
                this,
                "Offline recovery kit destination",
                "Select a local folder or removable drive to receive the encrypted disaster recovery kit.",
                _model.KitDirectory);
            kitPicker.PathChanged += path => _model.KitDirectory = path;

            s3Panel.Children.Add(kitPicker);
            _content.Children.Add(s3Panel);
        }
        else
        {
            var repoPicker = new PathPickerControl(
                this,
                "Backup repository folder",
                "Select an empty local folder, external drive root, or network share path.",
                _model.RepositoryLocation);
            repoPicker.PathChanged += path => _model.RepositoryLocation = path;

            var kitPicker = new PathPickerControl(
                this,
                "Offline recovery kit destination",
                "Select a removable drive or safe offline storage location for the disaster recovery kit.",
                _model.KitDirectory);
            kitPicker.PathChanged += path => _model.KitDirectory = path;

            _content.Children.Add(repoPicker);
            _content.Children.Add(kitPicker);
        }

        _content.Children.Add(Info("The recovery kit contains the cryptographic proof and instructions needed to restore your files if this computer is destroyed."));
        _content.Children.Add(Footer(null, "Next", () => { _setupStep = 1; Render(); }, Has(_model.RepositoryLocation) && Has(_model.KitDirectory)));
    }

    private void Sources()
    {
        _content.Children.Add(SectionTitle("Choose what to protect", "Select the primary source directory whose files must remain recoverable."));

        var sourcePicker = new PathPickerControl(
            this,
            "Source folder",
            "Fortiq will create verifiable, deduplicated snapshots of this folder on schedule.",
            _model.SourcePath);
        sourcePicker.PathChanged += path => _model.SourcePath = path;

        _content.Children.Add(sourcePicker);
        _content.Children.Add(Info("The source folder is strictly read during backup. Fortiq never modifies, renames, or locks your original files."));
        _content.Children.Add(Footer("Back", "Next", () => { _setupStep = 2; Render(); }, Has(_model.SourcePath)));
    }

    private void Schedule()
    {
        _content.Children.Add(SectionTitle("Set the backup schedule", "Automatic background backups minimize data loss between your work sessions."));

        var scheduleCard = Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { Text("Daily Automated Backup", 15, FontWeight.SemiBold, Ink), Badge("Recommended") }
                },
                Text("Fortiq will run an unattended backup every night at 02:30 when system activity is low.", 13, FontWeight.Normal, Muted, true)
            }
        });

        _content.Children.Add(scheduleCard);
        _content.Children.Add(Info("Custom schedules and interval drills can be adjusted at any time in Settings."));
        _content.Children.Add(Footer("Back", "Next", () => { _setupStep = 3; Render(); }));
    }

    private void Review()
    {
        _content.Children.Add(SectionTitle("Review protection configuration", "Verify your repository targets before Fortiq initializes the encrypted repository."));

        var summary = new StackPanel { Spacing = 14 };
        summary.Children.Add(Summary("Storage Destination", _model.RepositoryLocation));
        summary.Children.Add(Summary("Protected Source", _model.SourcePath));
        summary.Children.Add(Summary("Recovery Kit Location", _model.KitDirectory));
        summary.Children.Add(Summary("Backup Recurrence", "Nightly at 02:30 (Automatic)"));

        _content.Children.Add(Card(summary));
        _content.Children.Add(Warning("Next, Fortiq will display your 24-word disaster recovery phrase. You must write it down and keep it offline."));
        _content.Children.Add(Footer("Back", _model.Busy ? "Creating…" : "Create protection", async () => await _model.CreateAsync(CancellationToken.None), _model.CanCreate));
    }

    private void RenderPhrase()
    {
        _content.Children.Add(SectionTitle("Write down your disaster recovery phrase", "This 24-word cryptographic key is the only way to recover your data if this computer is lost or destroyed."));
        _content.Children.Add(Warning("Keep this phrase offline. Never photograph it or store it in cloud note services."));
        if (!_model.BackupScheduled)
        {
            _content.Children.Add(Warning(_model.SchedulingFailure ?? "Automatic scheduling needs attention."));
        }

        var words = (_model.RecoveryMnemonic ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*"),
            RowSpacing = 8,
            ColumnSpacing = 8
        };

        for (var index = 0; index < words.Length; index++)
        {
            var word = Card(Text($"{index + 1}.  {words[index]}", 13, FontWeight.SemiBold, Ink));
            Grid.SetColumn(word, index % 4);
            Grid.SetRow(word, index / 4);
            grid.Children.Add(word);
        }

        _content.Children.Add(grid);
        _content.Children.Add(Footer(null, "I wrote it down", _model.WroteItDown));
    }

    private void RenderVerify()
    {
        var requested = string.Join(", ", _model.RequestedWordNumbers.Select(number => $"#{number}"));
        _content.Children.Add(SectionTitle("Verify your recovery phrase", "Type the requested words in order to prove that your offline paper backup is accurate."));

        var verifyBox = new TextBox
        {
            Text = _model.ConfirmationInput,
            PlaceholderText = $"Enter words {requested} separated by spaces...",
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8),
            Background = Surface,
            Foreground = Ink,
            BorderBrush = Line
        };
        verifyBox.TextChanged += (_, _) => _model.ConfirmationInput = verifyBox.Text ?? string.Empty;

        _content.Children.Add(new StackPanel
        {
            Spacing = 5,
            Children = { Text($"Verification words ({requested})", 13, FontWeight.SemiBold, Ink), verifyBox }
        });

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var again = Secondary("Show phrase again");
        again.Click += (_, _) => _model.ShowItAgain();

        var verify = Primary("Verify recovery kit");
        verify.Click += (_, _) =>
        {
            if (_model.Confirm())
            {
                Protected = true;
                Render();
            }
        };

        actions.Children.Add(again);
        actions.Children.Add(verify);
        _content.Children.Add(actions);
    }

    private void RenderDone()
    {
        _content.Children.Add(Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Text(_model.BackupScheduled ? "Protection is ready" : "Repository created", 22, FontWeight.SemiBold, Recoverable),
                Text(_model.BackupScheduled
                    ? "Your repository is initialized and the first backup is scheduled. Recovery will remain unproven until Fortiq completes a real restore test."
                    : _model.SchedulingFailure ?? "Automatic scheduling needs attention.", 13, FontWeight.Normal, Muted, true)
            }
        }, RecoverableSurface, Recoverable));

        var close = Primary("Done");
        close.Click += (_, _) => Close();
        _content.Children.Add(new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Children = { close } });
    }

    private Grid Footer(string? backLabel, string nextLabel, Action next, bool enabled = true)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 16, 0, 0) };
        if (backLabel is not null)
        {
            var back = Secondary(backLabel);
            back.Click += (_, _) => { _setupStep = Math.Max(0, _setupStep - 1); Render(); };
            Grid.SetColumn(back, 0);
            grid.Children.Add(back);
        }

        var proceed = Primary(nextLabel);
        proceed.IsEnabled = enabled;
        proceed.Click += (_, _) => next();
        Grid.SetColumn(proceed, 2);
        grid.Children.Add(proceed);
        return grid;
    }

    private static StackPanel SectionTitle(string title, string subtitle) => new()
    {
        Spacing = 4,
        Children = { Text(title, 18, FontWeight.SemiBold, Ink), Text(subtitle, 12, FontWeight.Normal, Muted, true) }
    };

    private static Grid Summary(string label, string value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("180,*"), ColumnSpacing = 12 };
        grid.Children.Add(Text(label, 13, FontWeight.SemiBold, Muted));
        var valueText = Text(string.IsNullOrWhiteSpace(value) ? "(Not set)" : value, 13, FontWeight.Normal, Ink, true);
        Grid.SetColumn(valueText, 1);
        grid.Children.Add(valueText);
        return grid;
    }

    private static Border Info(string message) => Card(Text(message, 12, FontWeight.Normal, Muted, true), InfoSurface, InfoLine, new Thickness(14));
    private static Border Warning(string message) => Card(Text(message, 12, FontWeight.SemiBold, Caution, true), UnprovenSurface, UnprovenLine, new Thickness(14));
    private static Border Badge(string text) => new()
    {
        Background = InfoSurface,
        BorderBrush = InfoLine,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(8, 2),
        Child = Text(text, 11, FontWeight.SemiBold, Brand)
    };

    private static Button Primary(string label) => new()
    {
        Content = label,
        Background = Brand,
        Foreground = Brushes.White,
        BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(18, 9),
        FontWeight = FontWeight.SemiBold
    };

    private static Button Secondary(string label) => new()
    {
        Content = label,
        Background = Surface,
        Foreground = Ink,
        BorderBrush = Line,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(14, 8)
    };

    private static Border Card(Control child, IBrush? background = null, IBrush? border = null, Thickness? padding = null) => new()
    {
        Child = child,
        Background = background ?? Surface,
        BorderBrush = border ?? Line,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = padding ?? new Thickness(16)
    };

    private static TextBlock Text(string value, double size, FontWeight weight, IBrush color, bool wrap = false) => new()
    {
        Text = value,
        FontSize = size,
        FontWeight = weight,
        Foreground = color,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap
    };

    private static bool Has(string? text) => !string.IsNullOrWhiteSpace(text);
}
