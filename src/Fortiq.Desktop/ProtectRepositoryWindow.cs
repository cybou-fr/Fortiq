using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    private readonly ContentControl _actionBar = new();
    private Button? _primaryAction;
    private Func<bool>? _primaryEnabled;
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

        // Same shape as the setup window, and for the same reason: the step's own Next button lives
        // inside _content, at the bottom of a scrolling column, so on this window's default height it
        // was already cut in half. A wizard whose Next button is below the fold is one people think
        // is broken.
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new StackPanel
            {
                Margin = new Thickness(34, 28, 34, 20),
                Spacing = 18,
                Children = { Header(), _stepperHost, _content, _failure }
            }
        };

        var bar = new Border
        {
            Background = Surface,
            BorderBrush = Line,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(34, 16),
            Child = _actionBar
        };

        var layout = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(bar, Dock.Bottom);
        layout.Children.Add(bar);
        layout.Children.Add(scroller);

        Content = layout;

        _model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProtectRepositoryViewModel.Step)
                or nameof(ProtectRepositoryViewModel.Failure)
                or nameof(ProtectRepositoryViewModel.SchedulingFailure)
                or nameof(ProtectRepositoryViewModel.Busy))
            {
                Render();
            }
        };

        Closing += (_, e) =>
        {
            if (_model.CanClose) return;
            e.Cancel = true;
            _failure.Text = _model.Busy
                ? "Creation is still in progress. Keep this window open to receive your recovery phrase."
                : "Write down and confirm your recovery phrase before closing. It cannot be shown again after this window closes.";
            _failure.IsVisible = true;
        };
        Closed += (_, _) => _model.ClearStorageCredentials();
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape && _model.CanClose)
            {
                Close();
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

        // Cleared with the content it belongs to, so a step that sets no actions shows none rather
        // than the previous step's.
        _actionBar.Content = null;
        _primaryAction = null;
        _primaryEnabled = null;
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
            // Empty, with the shape shown as a placeholder. It used to arrive pre-filled with
            // "s3:https://s3.amazonaws.com/my-fortiq-backup", which is a real-looking address for a
            // bucket nobody owns: it satisfies the "is this filled in?" check, so Next lights up and
            // the wizard fails much later, after the recovery phrase has been shown.
            var bucketBox = new TextBox
            {
                Text = _model.RepositoryLocation.StartsWith("s3:", StringComparison.OrdinalIgnoreCase)
                    ? _model.RepositoryLocation
                    : string.Empty,
                PlaceholderText = "s3:https://s3.eu-west-4.example.com/my-bucket",
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8),
                Background = Surface,
                Foreground = Ink,
                BorderBrush = Line
            };
            // Attached after the initial Text is set, and it does not re-render. Both matter: setting
            // Text raises TextChanged, so a handler attached first and calling Render() rebuilds this
            // very box, which sets Text again - the window hung solid the moment S3 was selected.
            // Re-rendering per keystroke would also destroy the box being typed into and take the
            // caret with it.
            bucketBox.TextChanged += (_, _) =>
            {
                _model.RepositoryLocation = bucketBox.Text ?? string.Empty;
                RefreshActions();
            };

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
            kitPicker.PathChanged += path => { _model.KitDirectory = path; RefreshActions(); };

            s3Panel.Children.Add(Field(
                "Access key ID",
                "From your storage provider's account or bucket settings.",
                _model.StorageAccessKeyId,
                value => _model.StorageAccessKeyId = value));

            s3Panel.Children.Add(Field(
                "Secret access key",
                "Stored encrypted on this PC, for this bucket only. It is never written to the backup itself.",
                _model.StorageSecretKey,
                value => _model.StorageSecretKey = value,
                masked: true));

            s3Panel.Children.Add(Field(
                "Region",
                "The region your provider signs requests for. Leave blank if they do not use one.",
                _model.StorageRegion,
                value => _model.StorageRegion = value,
                placeholder: "eu-west-4"));

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
            repoPicker.PathChanged += path => { _model.RepositoryLocation = path; RefreshActions(); };

            var kitPicker = new PathPickerControl(
                this,
                "Offline recovery kit destination",
                "Select a removable drive or safe offline storage location for the disaster recovery kit.",
                _model.KitDirectory);
            kitPicker.PathChanged += path => { _model.KitDirectory = path; RefreshActions(); };

            _content.Children.Add(repoPicker);
            _content.Children.Add(kitPicker);
        }

        _content.Children.Add(Info("The recovery kit contains the cryptographic proof and instructions needed to restore your files if this computer is destroyed."));
        // Object storage without keys cannot be reached at all, so the step is not complete without
        // them. Letting Next through here would move the failure to after the recovery phrase.
        // Kept as a predicate rather than a value, so RefreshActions can ask it again after each
        // keystroke instead of the step being rebuilt to answer the same question.
        _primaryEnabled = () =>
            Has(_model.RepositoryLocation)
            && Has(_model.KitDirectory)
            && (!_isS3Storage || (Has(_model.StorageAccessKeyId) && Has(_model.StorageSecretKey)));

        SetActions(Footer(null, "Next", () => { _setupStep = 1; Render(); }, _primaryEnabled()));
    }

    private void Sources()
    {
        _content.Children.Add(SectionTitle("Choose what to protect", "Select the primary source directory whose files must remain recoverable."));

        var sourcePicker = new PathPickerControl(
            this,
            "Source folder",
            "Fortiq will create verifiable, deduplicated snapshots of this folder on schedule.",
            _model.SourcePath);
        // Without this the Next button stayed grey until something else happened to redraw the step,
        // so choosing a folder appeared to do nothing.
        sourcePicker.PathChanged += path => { _model.SourcePath = path; RefreshActions(); };

        _content.Children.Add(sourcePicker);
        _content.Children.Add(Info("The source folder is strictly read during backup. Fortiq never modifies, renames, or locks your original files."));
        _primaryEnabled = () => Has(_model.SourcePath);
        SetActions(Footer("Back", "Next", () => { _setupStep = 2; Render(); }, _primaryEnabled()));
    }

    private void Schedule()
    {
        if (!_model.AutomaticBackupsAvailable)
        {
            var title = "Automatic backups unavailable";
            var message = _model.AutomaticBackupsUnavailableReason
                ?? "Automatic backups are unavailable in this mode. You can still use Fortiq for manual recovery.";
            _content.Children.Add(SectionTitle(title, "Automatic background backups cannot run on this configuration."));
            _content.Children.Add(Warning(message));
            SetActions(Footer("Back", "Next", () => { _setupStep = 3; Render(); }));
            return;
        }
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
        _content.Children.Add(Info("The default schedule runs at 02:30 in the local time zone. Custom scheduling is not yet available in this interface."));
        SetActions(Footer("Back", "Next", () => { _setupStep = 3; Render(); }));
    }

    private void Review()
    {
        _content.Children.Add(SectionTitle("Review protection configuration", "Verify your repository targets before Fortiq initializes the encrypted repository."));

        var summary = new StackPanel { Spacing = 14 };
        summary.Children.Add(Summary("Storage Destination", _model.RepositoryLocation));
        summary.Children.Add(Summary("Protected Source", _model.SourcePath));
        summary.Children.Add(Summary("Recovery Kit Location", _model.KitDirectory));
        summary.Children.Add(Summary("Backup Recurrence", _model.AutomaticBackupsAvailable ? "Nightly at 02:30 (Automatic)" : "Manual / Recovery kit only"));

        _content.Children.Add(Card(summary));
        _content.Children.Add(Warning("Next, Fortiq will display your 24-word disaster recovery phrase. You must write it down and keep it offline."));
        SetActions(Footer("Back", _model.Busy ? "Creating…" : "Create protection", async () => await _model.CreateAsync(CancellationToken.None), _model.CanCreate));
    }

    private void RenderPhrase()
    {
        _content.Children.Add(SectionTitle("Write down your recovery words", "These words are the only way back to your data if this PC is lost, stolen or destroyed. Write them on paper, in order, and keep them somewhere other than this computer."));
        _content.Children.Add(Warning("Keep this phrase offline. Never photograph it or store it in cloud note services."));
        if (!_model.BackupScheduled)
        {
            _content.Children.Add(Warning(_model.SchedulingFailure ?? "Automatic scheduling needs attention."));
        }

        var words = (_model.RecoveryMnemonic ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        const int columns = 4;
        var rows = (words.Length + columns - 1) / columns;

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*"),
            RowSpacing = 8,
            ColumnSpacing = 8
        };

        // The rows have to exist. A Grid with no RowDefinitions has exactly one implicit row, so
        // Grid.SetRow(1..5) put every word after the fourth into row 0 on top of the words already
        // there - twenty-four words drawn in four cells, and only the last four visible. Somebody
        // would have written down four words, believed they had their recovery phrase, and found out
        // otherwise on the one day it mattered.
        for (var row = 0; row < rows; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        for (var index = 0; index < words.Length; index++)
        {
            var word = Card(Text($"{index + 1}.  {words[index]}", 13, FontWeight.SemiBold, Ink));
            Grid.SetColumn(word, index % columns);
            Grid.SetRow(word, index / columns);
            grid.Children.Add(word);
        }

        _content.Children.Add(grid);
        SetActions(Footer(null, "I wrote it down", _model.WroteItDown));
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

    /// <summary>Puts a step's Back/Next into the bar docked at the bottom of the window.</summary>
    /// <remarks>
    /// These used to be appended to the scrolling column like any other content, so the button that
    /// moves the wizard forward scrolled with it - and at this window's default size it started life
    /// already cut in half. Every step calls this, so no step can forget and put its Next back into
    /// the scroll area.
    /// </remarks>
    private void SetActions(Control footer) => _actionBar.Content = footer;

    /// <summary>
    /// Re-evaluates whether the step can be left, without rebuilding it.
    /// </summary>
    private void RefreshActions()
    {
        if (_primaryAction is not null && _primaryEnabled is not null)
        {
            _primaryAction.IsEnabled = _primaryEnabled();
        }
    }

    private Grid Footer(string? backLabel, string nextLabel, Action next, bool enabled = true)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        if (backLabel is not null)
        {
            var back = Secondary(backLabel);
            back.IsEnabled = !_model.Busy;
            back.Click += (_, _) => { _setupStep = Math.Max(0, _setupStep - 1); Render(); };
            Grid.SetColumn(back, 0);
            grid.Children.Add(back);
        }

        var proceed = Primary(nextLabel);
        proceed.IsEnabled = enabled;
        proceed.Click += (_, _) => next();
        _primaryAction = proceed;
        Grid.SetColumn(proceed, 2);
        grid.Children.Add(proceed);
        return grid;
    }

    /// <summary>A labelled text field, optionally masked for a secret.</summary>
    private StackPanel Field(
        string label,
        string help,
        string value,
        Action<string> onChanged,
        bool masked = false,
        string? placeholder = null)
    {
        var box = new TextBox
        {
            Text = value,
            PlaceholderText = placeholder ?? string.Empty,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8),
            Background = Surface,
            Foreground = Ink,
            BorderBrush = Line
        };

        if (masked)
        {
            box.PasswordChar = '•';
        }

        // Attached after Text is assigned above, so the initial value does not raise this.
        box.TextChanged += (_, _) =>
        {
            onChanged(box.Text ?? string.Empty);

            // Only the button's enabled state is recomputed. Rebuilding the step would destroy the
            // box currently being typed into, and setting its Text again would re-enter here.
            RefreshActions();
        };

        return new StackPanel
        {
            Spacing = 5,
            Children =
            {
                Text(label, 13, FontWeight.SemiBold, Ink),
                Text(help, 11, FontWeight.Normal, Muted, true),
                box
            }
        };
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
