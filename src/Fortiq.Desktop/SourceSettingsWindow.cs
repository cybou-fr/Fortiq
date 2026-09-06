using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Fortiq.Desktop.ViewModels;
using static Fortiq.Desktop.DesignTokens;

namespace Fortiq.Desktop;

/// <summary>
/// One protected source's own settings: when it is backed up, how often recovery is proven, what may
/// be forgotten, and how to stop protecting it.
/// </summary>
/// <remarks>
/// None of this was reachable before. Provisioning wrote 02:30 and a weekly drill because those were
/// the constants in the code that wrote the file, and changing either meant finding a JSON file under
/// %ProgramData% and editing it by hand - on a directory a standard account cannot write to. Retention
/// was not written at all, so every repository kept every snapshot forever and the only way to stop
/// that was the same hand edit.
///
/// A dialog rather than a sixth item in the navigation rail: it is about one source, opened from the
/// row for that source, and the rail already answers "where am I" four times without also having to
/// answer "which one".
/// </remarks>
public sealed class SourceSettingsWindow : Window
{
    private readonly SourceSettingsViewModel _model;
    private readonly StackPanel _body = new() { Spacing = 18, Margin = new Thickness(28, 24) };

    /// <summary>The Save button as it currently exists. Rebuilt by every render, so it is not readonly.</summary>
    private Button? _save;

    /// <summary>True when this source is no longer protected, so the caller can refresh rather than guess.</summary>
    public bool Changed { get; private set; }

    public SourceSettingsWindow(SourceSettingsViewModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));

        Title = $"{model.Title} — settings";
        Icon = FortiqBrand.WindowIcon();
        Width = 620;
        Height = 700;
        MinWidth = 520;
        MinHeight = 480;
        Background = CanvasBackground;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new ScrollViewer { Content = _body };

        // Only the properties that change the shape of the screen redraw it. Redrawing on every
        // change destroyed and rebuilt the controls while somebody was typing in one of them: a
        // NumericUpDown reports each parsed value, so entering "12" redrew after the "1" and took the
        // focus with it, and the second digit went nowhere. The counts keep their own labels up to
        // date instead.
        _model.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName is nameof(SourceSettingsViewModel.Details)
                or nameof(SourceSettingsViewModel.Busy)
                or nameof(SourceSettingsViewModel.Failure)
                or nameof(SourceSettingsViewModel.Saved))
            {
                Render();
            }
        };
        Accessible.Keys(this, () => _save);
        Opened += async (_, _) => await _model.LoadAsync(CancellationToken.None);
        Render();
    }

    private void Render()
    {
        _body.Children.Clear();

        _body.Children.Add(Text(_model.Title, 20, FontWeight.SemiBold, Ink, true));

        if (_model.Details is { } details)
        {
            _body.Children.Add(Text(details.SourcePath, 12, FontWeight.Normal, Muted, true));
        }

        if (_model.Failure is { Length: > 0 } failure)
        {
            _body.Children.Add(Card(Text(failure, 13, FontWeight.Normal, Ink, true), AtRiskSurface, AtRiskLine));
        }

        if (_model.Saved is { Length: > 0 } saved)
        {
            _body.Children.Add(Card(Text(saved, 13, FontWeight.SemiBold, Recoverable), RecoverableSurface, Recoverable));
        }

        if (_model.Details is null)
        {
            _save = null;
            var close = Secondary("Close");
            close.Click += (_, _) => Close();
            _body.Children.Add(close);
            return;
        }

        _body.Children.Add(SchedulingCard());
        _body.Children.Add(DrillCard());
        _body.Children.Add(RetentionCard());
        _body.Children.Add(LockCard());
        _body.Children.Add(StopCard());
        _body.Children.Add(Buttons());
    }

    private Border SchedulingCard()
    {
        var running = new CheckBox
        {
            Content = "Back this folder up automatically",
            IsChecked = _model.Enabled,
            IsEnabled = !_model.Busy
        };
        AutomationProperties.SetName(running, "Back this folder up automatically");

        var summary = Text(string.Empty, 12, FontWeight.SemiBold, Muted, true);
        void Describe()
        {
            summary.Text = _model.Enabled
                ? $"Backs up daily at {_model.BackupHour:00}:{_model.BackupMinute:00}."
                // Pausing stops the drills and the retention runs as well as the backups - they are all
                // occurrences of this one schedule - and the label above says only "back up", so the
                // rest of what the switch does is said here rather than discovered later.
                : "Paused. No backups, recovery drills or retention runs happen on their own for this "
                    + "folder. \"Back up now\" and the recovery drill button still work.";
            summary.Foreground = _model.Enabled ? Muted : Caution;
        }

        running.IsCheckedChanged += (_, _) =>
        {
            _model.Enabled = running.IsChecked == true;
            Describe();
        };
        var hour = Number("Hour", _model.BackupHour, 0, 23, value => { _model.BackupHour = value; Describe(); });
        var minute = Number("Minute", _model.BackupMinute, 0, 59, value => { _model.BackupMinute = value; Describe(); });
        Describe();

        return Card(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Text("When", 16, FontWeight.SemiBold, Ink),
                Text("The daily backup runs at this time, in this computer's own time zone. "
                    + "A backup that was missed because the machine was off runs once when it comes back, not once per day missed.",
                    12, FontWeight.Normal, Muted, true),
                running,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children = { hour, minute }
                },
                summary
            }
        }, Surface, Line);
    }

    private Border DrillCard()
    {
        var on = new CheckBox
        {
            Content = "Prove recovery automatically",
            IsChecked = _model.DrillEveryDays is not null,
            IsEnabled = !_model.Busy
        };
        AutomationProperties.SetName(on, "Prove recovery automatically");
        on.IsCheckedChanged += (_, _) =>
        {
            // A shape change rather than a value change: the field below appears or goes, so this one
            // does redraw, and it is a click rather than something being typed into.
            _model.DrillEveryDays = on.IsChecked == true ? _model.DrillEveryDays ?? 7 : null;
            Render();
        };

        var body = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Text("Proving recovery", 16, FontWeight.SemiBold, Ink),
                Text("A drill restores the newest backup into a scratch folder and checks what came out. "
                    + "It is the only thing that turns \"backed up\" into \"known to come back\", and it is what "
                    + "keeps this source out of the Unproven column.",
                    12, FontWeight.Normal, Muted, true),
                on
            }
        };

        if (_model.DrillEveryDays is { } days)
        {
            body.Children.Add(Number("Every (days)", days, 1, 365, value => _model.DrillEveryDays = value));
        }
        else
        {
            body.Children.Add(Text(
                "Turned off. This source will stay Unproven until somebody runs a drill by hand.",
                12, FontWeight.SemiBold, Caution, true));
        }

        return Card(body, Surface, Line);
    }

    private Border RetentionCard()
    {
        var on = new CheckBox
        {
            Content = "Delete old backups on a policy",
            IsChecked = _model.RetentionEnabled,
            IsEnabled = !_model.Busy
        };
        AutomationProperties.SetName(on, "Delete old backups on a policy");
        on.IsCheckedChanged += (_, _) =>
        {
            _model.SetRetentionEnabled(on.IsChecked == true);
            Render();
        };

        var body = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Text("What to keep", 16, FontWeight.SemiBold, Ink),
                // The default is stated plainly because it is the one that surprises people: nothing
                // is ever deleted unless somebody here says it may be.
                Text("Off means every backup is kept forever, which is what Fortiq does unless you say otherwise. "
                    + "There is no safe default for deleting somebody's backups.",
                    12, FontWeight.Normal, Muted, true),
                on
            }
        };

        if (_model.RetentionEnabled)
        {
            var kept = Text(string.Empty, 12, FontWeight.Normal, Muted, true);
            void Describe() => kept.Text =
                $"Keeps the newest backup of each of the last {_model.KeepDaily ?? 7} days, "
                + $"{_model.KeepWeekly ?? 4} weeks and {_model.KeepMonthly ?? 12} months. Everything else is forgotten.";

            body.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    Number("Daily", _model.KeepDaily ?? 7, 1, 365, value => { _model.KeepDaily = value; Describe(); }),
                    Number("Weekly", _model.KeepWeekly ?? 4, 1, 520, value => { _model.KeepWeekly = value; Describe(); }),
                    Number("Monthly", _model.KeepMonthly ?? 12, 1, 240, value => { _model.KeepMonthly = value; Describe(); })
                }
            });

            Describe();
            body.Children.Add(kept);

            var prune = new CheckBox
            {
                Content = "Also remove the data of forgotten backups",
                IsChecked = _model.Prune,
                IsEnabled = !_model.Busy
            };
            AutomationProperties.SetName(prune, "Also remove the data of forgotten backups");

            var pruneEffect = Text(string.Empty, 12, FontWeight.Normal, Muted, true);
            void DescribePrune()
            {
                pruneEffect.Text = _model.Prune
                    ? "Space is reclaimed. This rewrites the repository, so it takes longer and cannot be undone."
                    : "Forgotten backups stop being listed, but their data stays in the repository and no space is reclaimed.";
                pruneEffect.Foreground = _model.Prune ? Caution : Muted;
            }

            prune.IsCheckedChanged += (_, _) => { _model.Prune = prune.IsChecked == true; DescribePrune(); };
            DescribePrune();

            body.Children.Add(prune);
            body.Children.Add(pruneEffect);
        }

        return Card(body, Surface, Line);
    }

    /// <summary>
    /// The way out of a repository an interrupted run left locked.
    /// </summary>
    /// <remarks>
    /// Offered with what it assumes stated next to it rather than performed automatically. Fortiq
    /// cannot tell a lock left by a killed run on this machine from one held by a second computer
    /// backing up to the same repository right now, and clearing the second kind interrupts a backup
    /// that is working.
    /// </remarks>
    private Border LockCard()
    {
        var clear = Secondary("Clear the lock").Named("Clear the lock left by an interrupted run");
        clear.IsEnabled = !_model.Busy;
        clear.Click += async (_, _) => await _model.ClearLockAsync(CancellationToken.None);

        return Card(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Text("If backups say the repository is locked", 16, FontWeight.SemiBold, Ink),
                Text("A run that was interrupted - cancelled, or stopped by a power cut - leaves its lock behind, "
                    + "and every later backup fails until it is cleared. Fortiq takes the repository to itself while "
                    + "it clears, so nothing of its own is running underneath.",
                    12, FontWeight.Normal, Muted, true),
                Text("Do not do this while another computer is backing up to the same repository: that lock looks "
                    + "identical from here, and clearing it interrupts a backup that is working.",
                    12, FontWeight.SemiBold, Caution, true),
                clear
            }
        }, Surface, Line);
    }

    private Border StopCard()
    {
        var remove = Secondary("Stop protecting this folder");
        remove.Foreground = Failure;
        remove.IsEnabled = !_model.Busy;
        remove.Click += async (_, _) => await ConfirmRemoveAsync();

        return Card(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Text("Stop", 16, FontWeight.SemiBold, Ink),
                // Said before the button rather than in the dialog behind it, because this is the
                // question somebody is actually asking when they hover over it.
                Text("Fortiq stops backing this folder up. The backups it has already taken are not deleted, "
                    + "and the recovery kit and your 24 words still open them on any machine.",
                    12, FontWeight.Normal, Muted, true),
                remove
            }
        }, Surface, Line);
    }

    private StackPanel Buttons()
    {
        var save = Primary(_model.Busy ? "Saving…" : "Save changes");
        save.IsEnabled = !_model.Busy;
        save.Click += async (_, _) =>
        {
            await _model.SaveAsync(CancellationToken.None);
            if (_model.Failure is null)
            {
                Changed = true;
            }
        };

        var close = Secondary("Close");
        close.IsEnabled = !_model.Busy;
        close.Click += (_, _) => Close();

        // The window's key handler reads this field rather than being subscribed again here: one
        // subscription per render would leave a handler behind for every redraw, each holding a
        // button that has since been thrown away.
        _save = save;

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { save, close }
        };
    }

    private async Task ConfirmRemoveAsync()
    {
        var confirm = Primary("Stop protecting it");
        confirm.Background = AtRisk;
        var cancel = Secondary("Keep protecting it");

        var dialog = new Window
        {
            Title = "Stop protecting this folder?",
            Width = 480,
            Height = 280,
            Background = CanvasBackground,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 14,
                Children =
                {
                    Text($"Stop protecting {_model.Title}?", 16, FontWeight.SemiBold, Ink, true),
                    Text("No more backups will be taken and no more drills will run. "
                        + "Nothing that has already been backed up is deleted: the repository, the recovery kit "
                        + "and your 24 words keep working exactly as they do now.",
                        13, FontWeight.Normal, Muted, true),
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children = { cancel, confirm }
                    }
                }
            }
        };

        var agreed = false;
        // Escape only. Enter is deliberately not wired here: this dialog's primary action stops
        // protecting somebody's data, and a key pressed out of habit must not be what confirms it.
        Accessible.Keys(dialog);
        cancel.Click += (_, _) => dialog.Close();
        confirm.Click += (_, _) => { agreed = true; dialog.Close(); };
        await dialog.ShowDialog(this);

        if (!agreed)
        {
            return;
        }

        await _model.RemoveAsync(CancellationToken.None);
        if (_model.Removed)
        {
            Changed = true;
            Close();
        }
    }

    /// <summary>A labelled whole number, clamped, because these are counts of days and backups.</summary>
    private StackPanel Number(string label, int value, int minimum, int maximum, Action<int> assign)
    {
        var box = new NumericUpDown
        {
            Value = value,
            Minimum = minimum,
            Maximum = maximum,
            Increment = 1,
            FormatString = "0",
            Width = 110,
            IsEnabled = !_model.Busy
        };
        AutomationProperties.SetName(box, label);
        box.ValueChanged += (_, _) =>
        {
            if (box.Value is { } current)
            {
                assign((int)current);
            }
        };

        return new StackPanel
        {
            Spacing = 4,
            Children = { Text(label, 11, FontWeight.SemiBold, Muted), box }
        };
    }

    private static TextBlock Text(string value, double size, FontWeight weight, IBrush brush, bool wrap = false) => new()
    {
        Text = value,
        FontSize = size,
        FontWeight = weight,
        Foreground = brush,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap
    };

    private static Border Card(Control child, IBrush background, IBrush border) => new()
    {
        Background = background,
        BorderBrush = border,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(18, 16),
        Child = child
    };

    private static Button Primary(string label) => new()
    {
        Content = label,
        Background = Brand,
        Foreground = Brushes.White,
        BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(18, 9)
    };

    private static Button Secondary(string label) => new()
    {
        Content = label,
        Background = Surface,
        Foreground = Ink,
        BorderBrush = BorderMedium,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(16, 9)
    };
}
