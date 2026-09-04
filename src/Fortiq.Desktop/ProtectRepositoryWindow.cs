using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Fortiq.Desktop.ViewModels;

namespace Fortiq.Desktop;

/// <summary>
/// The wizard that creates a protected repository. Like the main window it only arranges controls:
/// every rule about when the mnemonic may be read, and what finishes the wizard, lives in
/// <see cref="ProtectRepositoryViewModel"/>, where it is tested.
/// </summary>
public sealed class ProtectRepositoryWindow : Window
{
    private readonly ProtectRepositoryViewModel _model;
    private readonly StackPanel _body = new() { Spacing = 12 };
    private Button? _create;
    private readonly TextBlock _failure = new() { Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap, IsVisible = false };

    public ProtectRepositoryWindow(ProtectRepositoryViewModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));

        Title = "Protect something";
        Width = 640;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                Children = { _body, _failure }
            }
        };

        // Only a step or a message changes the layout. Re-rendering on every keystroke would
        // rebuild the text box the person is typing into, and take the caret with it.
        _model.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ProtectRepositoryViewModel.Step):
                case nameof(ProtectRepositoryViewModel.Failure):
                case nameof(ProtectRepositoryViewModel.SchedulingFailure):
                    Render();
                    break;
                case nameof(ProtectRepositoryViewModel.CanCreate):
                    if (_create is not null)
                    {
                        _create.IsEnabled = _model.CanCreate;
                    }

                    break;
            }
        };
        Render();
    }

    /// <summary>Set once the wizard has finished, so the caller knows whether anything was created.</summary>
    public bool Protected { get; private set; }

    private void Render()
    {
        _failure.Text = _model.Failure;
        _failure.IsVisible = _model.Failure is { Length: > 0 };

        _body.Children.Clear();
        switch (_model.Step)
        {
            case ProtectStep.Describe:
                Describe();
                break;
            case ProtectStep.WriteDownRecoveryMaterial:
                WriteDown();
                break;
            case ProtectStep.ConfirmRecoveryMaterial:
                Confirm();
                break;
            default:
                Done();
                break;
        }
    }

    private void Describe()
    {
        var source = Field("What to back up (folder)", _model.SourcePath, value => _model.SourcePath = value);
        var repository = Field("Where the backups go", _model.RepositoryLocation, value => _model.RepositoryLocation = value);
        var kit = Field("Where the recovery kit goes", _model.KitDirectory, value => _model.KitDirectory = value);

        _create = new Button { Content = "Protect it", IsEnabled = _model.CanCreate, HorizontalAlignment = HorizontalAlignment.Left };
        _create.Click += async (_, _) => await _model.CreateAsync(CancellationToken.None);

        Add(Heading("Protect a folder"));
        Add(Note("The recovery kit belongs somewhere other than the machine being backed up: the two are only useful when they cannot be lost together."));
        Add(source);
        Add(repository);
        Add(kit);
        Add(_create);
    }

    private void WriteDown()
    {
        // The one moment the mnemonic exists on screen. It is not copyable by a button on purpose:
        // the clipboard is the place people forget things, and this is the only copy Fortiq has.
        var next = new Button { Content = "I have written it down", HorizontalAlignment = HorizontalAlignment.Left };
        next.Click += (_, _) => _model.WroteItDown();

        Add(Heading("Write these words down, on paper"));
        Add(Note("This is the only way back into the backups. Fortiq cannot show it again and cannot produce it for you later."));
        if (!_model.BackupScheduled)
        {
            Add(Warning(_model.SchedulingFailure ?? "Nightly backup scheduling failed. The repository and recovery kit still exist."));
        }
        Add(new SelectableTextBlock
        {
            Text = _model.RecoveryMnemonic,
            FontFamily = new FontFamily("Consolas, monospace"),
            FontSize = 16,
            TextWrapping = TextWrapping.Wrap
        });
        Add(next);
    }

    private void Confirm()
    {
        var asked = string.Join(", ", _model.RequestedWordNumbers);
        var input = Field($"Words {asked}, in that order", _model.ConfirmationInput, value => _model.ConfirmationInput = value);

        var confirm = new Button { Content = "Check", HorizontalAlignment = HorizontalAlignment.Left };
        confirm.Click += (_, _) =>
        {
            if (_model.Confirm())
            {
                Protected = true;
            }
        };

        var again = new Button { Content = "Show the words again", HorizontalAlignment = HorizontalAlignment.Left };
        again.Click += (_, _) => _model.ShowItAgain();

        Add(Heading("Now type some of them back"));
        Add(Note("This is not a formality. If the words were not really written down, the backups are already unrecoverable and nothing later will say so."));
        Add(input);
        Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { confirm, again } });
    }

    private void Done()
    {
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Left };
        close.Click += (_, _) => Close();

        Add(Heading(_model.BackupScheduled ? "Protected" : "Repository created — scheduling needs attention"));
        if (_model.BackupScheduled)
        {
            Add(Note(_model.DeviceUnlockAvailable
                ? "Backups will run nightly and unlock on this machine on their own. The words you wrote down are the way back from anywhere else."
                : "Backups will run nightly. This machine has no device unlock, so the words you wrote down are the only way in — including for scheduled runs."));
        }
        else
        {
            Add(Warning(_model.SchedulingFailure ?? "Nightly backup scheduling failed. Configure it before relying on this repository."));
        }
        Add(Note("Nothing is proven recoverable until a restore has happened. The main window says so until it has."));
        Add(close);
    }

    private void Add(Control control) => _body.Children.Add(control);

    private static TextBlock Heading(string text) =>
        new() { Text = text, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };

    private static TextBlock Note(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };

    private static TextBlock Warning(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.OrangeRed };

    private static StackPanel Field(string label, string value, Action<string> set)
    {
        var box = new TextBox { Text = value };
        box.TextChanged += (_, _) => set(box.Text ?? string.Empty);

        return new StackPanel
        {
            Spacing = 4,
            Children = { new TextBlock { Text = label }, box }
        };
    }
}
