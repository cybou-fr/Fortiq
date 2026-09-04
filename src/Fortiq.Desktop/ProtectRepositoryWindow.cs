using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Fortiq.Desktop.ViewModels;
using System.Globalization;

namespace Fortiq.Desktop;

public sealed class ProtectRepositoryWindow : Window
{
    private static readonly IBrush Canvas = Paint("#F6F8FB"), Surface = Paint("#FFFFFF"), Line = Paint("#E3E8EF");
    private static readonly IBrush Ink = Paint("#172033"), Muted = Paint("#667085"), Blue = Paint("#0866D9");
    private static readonly IBrush PaleBlue = Paint("#EAF3FF"), Green = Paint("#159455"), Amber = Paint("#B7791F");
    private readonly ProtectRepositoryViewModel _model;
    private readonly ContentControl _stepperHost = new();
    private readonly StackPanel _content = new() { Spacing = 18 };
    private readonly TextBlock _failure = Text(string.Empty, 12, FontWeight.Normal, Paint("#B42318"), true);
    private int _setupStep;
    private Button? _next;

    public ProtectRepositoryWindow(ProtectRepositoryViewModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        Title = "Protect your data"; Icon = FortiqBrand.WindowIcon();
        Width = 800; Height = 660; MinWidth = 700; MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Canvas;

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(34, 28), Spacing = 18,
                Children = { Header(), _stepperHost, _content, _failure }
            }
        };
        _model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProtectRepositoryViewModel.Step) or nameof(ProtectRepositoryViewModel.Failure) or nameof(ProtectRepositoryViewModel.SchedulingFailure)) Render();
        };
        Render();
    }

    public bool Protected { get; private set; }

    private static StackPanel Header() => new()
    {
        Spacing = 4,
        Children = { Text("Protect your data", 25, FontWeight.SemiBold, Ink), Text("Create a recoverable backup without hiding the important decisions.", 13, FontWeight.Normal, Muted) }
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
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(',', Enumerable.Repeat("*", steps.Length))), ColumnSpacing = 8 };
        for (var index = 0; index < steps.Length; index++)
        {
            var selected = index <= active;
            var marker = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12),
                Background = selected ? Blue : Paint("#D5DBE5"),
                Child = Text((index + 1).ToString(CultureInfo.InvariantCulture), 11, FontWeight.SemiBold, Brushes.White),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var item = new StackPanel { Spacing = 6, Children = { marker, Text(steps[index], 11, selected ? FontWeight.SemiBold : FontWeight.Normal, selected ? Ink : Muted) } };
            Grid.SetColumn(item, index); grid.Children.Add(item);
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
        _content.Children.Add(SectionTitle("Choose repository locations", "Keep the backup repository and recovery kit separate from the source whenever possible."));
        _content.Children.Add(PathField("Backup repository", "An empty local folder or mounted storage location.", _model.RepositoryLocation, value => _model.RepositoryLocation = value));
        _content.Children.Add(PathField("Recovery kit", "Prefer removable media kept offline.", _model.KitDirectory, value => _model.KitDirectory = value));
        _content.Children.Add(Info("The recovery kit contains the material needed when this computer is unavailable."));
        _content.Children.Add(Footer(null, "Next", () => { _setupStep = 1; Render(); }, Has(_model.RepositoryLocation) && Has(_model.KitDirectory)));
    }

    private void Sources()
    {
        _content.Children.Add(SectionTitle("Choose what to protect", "Select the folder whose files must remain recoverable."));
        _content.Children.Add(PathField("Source folder", "Fortiq will back up this folder on the configured schedule.", _model.SourcePath, value => _model.SourcePath = value));
        _content.Children.Add(Info("The source is read during backup. Fortiq never modifies files in this folder."));
        _content.Children.Add(Footer("Back", "Next", () => { _setupStep = 2; Render(); }, Has(_model.SourcePath)));
    }

    private void Schedule()
    {
        _content.Children.Add(SectionTitle("Set the backup schedule", "Automatic backups reduce the time between your latest work and a recoverable copy."));
        _content.Children.Add(Card(new StackPanel
        {
            Spacing = 10,
            Children = { Text("Daily backup", 15, FontWeight.SemiBold, Ink), Text("Fortiq will schedule one unattended backup every night at 02:00.", 13, FontWeight.Normal, Muted, true), Badge("Recommended") }
        }));
        _content.Children.Add(Info("Schedule customization will be available from Settings. The initial schedule uses the safest default."));
        _content.Children.Add(Footer("Back", "Next", () => { _setupStep = 3; Render(); }));
    }

    private void Review()
    {
        _content.Children.Add(SectionTitle("Review protection setup", "Confirm these locations before Fortiq creates encryption keys and the recovery kit."));
        var summary = new StackPanel { Spacing = 14 };
        summary.Children.Add(Summary("Repository", _model.RepositoryLocation));
        summary.Children.Add(Summary("Source", _model.SourcePath));
        summary.Children.Add(Summary("Recovery kit", _model.KitDirectory));
        summary.Children.Add(Summary("Schedule", "Daily at 02:00"));
        _content.Children.Add(Card(summary));
        _content.Children.Add(Warning("Next, Fortiq will show the recovery phrase once. Write it down and keep it offline."));
        _content.Children.Add(Footer("Back", _model.Busy ? "Creating…" : "Create protection", async () => await _model.CreateAsync(CancellationToken.None), _model.CanCreate));
    }

    private void RenderPhrase()
    {
        _content.Children.Add(SectionTitle("Write down your recovery phrase", "This is the only way to recover the repository when this computer is lost."));
        _content.Children.Add(Warning("Keep this phrase offline. Do not save it on this computer or in cloud notes."));
        if (!_model.BackupScheduled) _content.Children.Add(Warning(_model.SchedulingFailure ?? "Automatic scheduling needs attention."));

        var words = (_model.RecoveryMnemonic ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), RowSpacing = 8, ColumnSpacing = 8 };
        for (var index = 0; index < words.Length; index++)
        {
            var word = Card(Text($"{index + 1}.  {words[index]}", 13, FontWeight.SemiBold, Ink));
            Grid.SetColumn(word, index % 4); Grid.SetRow(word, index / 4); grid.Children.Add(word);
        }
        _content.Children.Add(grid);
        _content.Children.Add(Footer(null, "I wrote it down", _model.WroteItDown));
    }

    private void RenderVerify()
    {
        var requested = string.Join(", ", _model.RequestedWordNumbers.Select(number => $"#{number}"));
        _content.Children.Add(SectionTitle("Verify your recovery phrase", "Type the requested words in order to prove that your offline copy is usable."));
        _content.Children.Add(Field($"Words {requested}", _model.ConfirmationInput, value => _model.ConfirmationInput = value));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        var again = Secondary("Show phrase again"); again.Click += (_, _) => _model.ShowItAgain();
        var verify = Primary("Verify recovery kit"); verify.Click += (_, _) => { if (_model.Confirm()) { Protected = true; Render(); } };
        actions.Children.Add(again); actions.Children.Add(verify); _content.Children.Add(actions);
    }

    private void RenderDone()
    {
        _content.Children.Add(Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Text(_model.BackupScheduled ? "Protection is ready" : "Repository created", 22, FontWeight.SemiBold, Green),
                Text(_model.BackupScheduled ? "The first backup is scheduled. Recovery will remain unproven until Fortiq completes a real restore test." : _model.SchedulingFailure ?? "Automatic scheduling needs attention.", 13, FontWeight.Normal, Muted, true)
            }
        }, Paint("#EAF8F0"), Green));
        var close = Primary("Done"); close.Click += (_, _) => Close();
        _content.Children.Add(new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Children = { close } });
    }

    private StackPanel PathField(string label, string hint, string value, Action<string> set)
    {
        var box = new TextBox { Text = value, PlaceholderText = "Choose or enter a folder path" };
        box.TextChanged += (_, _) => { set(box.Text ?? string.Empty); UpdateNext(); };
        var browse = Secondary("Browse…");
        browse.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = label, AllowMultiple = false });
            if (folders.Count > 0) box.Text = folders[0].TryGetLocalPath() ?? folders[0].Path.LocalPath;
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Children = { box } };
        Grid.SetColumn(browse, 1); row.Children.Add(browse);
        return new StackPanel { Spacing = 5, Children = { Text(label, 13, FontWeight.SemiBold, Ink), Text(hint, 11, FontWeight.Normal, Muted), row } };
    }

    private Grid Footer(string? backLabel, string nextLabel, Action next, bool enabled = true)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        if (backLabel is not null) { var back = Secondary(backLabel); back.Click += (_, _) => { _setupStep--; Render(); }; row.Children.Add(back); }
        var forward = Primary(nextLabel); forward.IsEnabled = enabled; forward.Click += (_, _) => next(); Grid.SetColumn(forward, 1); row.Children.Add(forward); _next = forward; return row;
    }

    private void UpdateNext()
    {
        if (_next is null || _model.Step != ProtectStep.Describe) return;
        _next.IsEnabled = _setupStep switch
        {
            0 => Has(_model.RepositoryLocation) && Has(_model.KitDirectory),
            1 => Has(_model.SourcePath),
            _ => true
        };
    }

    private static StackPanel SectionTitle(string title, string subtitle) => new() { Spacing = 4, Children = { Text(title, 19, FontWeight.SemiBold, Ink), Text(subtitle, 12, FontWeight.Normal, Muted, true) } };
    private static StackPanel Summary(string label, string value) => new() { Spacing = 3, Children = { Text(label, 11, FontWeight.SemiBold, Muted), Text(value, 13, FontWeight.Normal, Ink, true) } };
    private static Border Info(string value) => Card(Text(value, 12, FontWeight.Normal, Blue, true), PaleBlue, Paint("#B9D7FF"));
    private static Border Warning(string value) => Card(Text(value, 12, FontWeight.Normal, Paint("#8A5A00"), true), Paint("#FFF8E7"), Paint("#F4CC73"));
    private static Border Badge(string value) => new() { Background = PaleBlue, CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 3), HorizontalAlignment = HorizontalAlignment.Left, Child = Text(value, 10, FontWeight.SemiBold, Blue) };
    private static StackPanel Field(string label, string value, Action<string> set) { var box = new TextBox { Text = value }; box.TextChanged += (_, _) => set(box.Text ?? string.Empty); return new StackPanel { Spacing = 5, Children = { Text(label, 13, FontWeight.SemiBold, Ink), box } }; }
    private static Border Card(Control child, IBrush? background = null, IBrush? border = null) => new() { Child = child, Background = background ?? Surface, BorderBrush = border ?? Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(16) };
    private static Button Primary(string label) => new() { Content = label, Background = Blue, Foreground = Brushes.White, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(5), Padding = new Thickness(16, 9), FontWeight = FontWeight.SemiBold };
    private static Button Secondary(string label) => new() { Content = label, Background = Surface, Foreground = Ink, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(14, 8) };
    private static TextBlock Text(string value, double size, FontWeight weight, IBrush color, bool wrap = false) => new() { Text = value, FontSize = size, FontWeight = weight, Foreground = color, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
    private static bool Has(string value) => !string.IsNullOrWhiteSpace(value);
    private static SolidColorBrush Paint(string value) => new(Color.Parse(value));
}
