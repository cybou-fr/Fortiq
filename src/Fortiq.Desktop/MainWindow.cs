using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Fortiq.Desktop.ViewModels;
using Fortiq.Monitoring;
using static Fortiq.Desktop.DesignTokens;

namespace Fortiq.Desktop;

public sealed class MainWindow : Window
{
    private readonly RepositoriesViewModel _model;
    private readonly Func<ProtectRepositoryViewModel>? _wizard;
    private readonly Border _page = new() { Background = CanvasBackground };
    private readonly Dictionary<string, Button> _navigation = new(StringComparer.Ordinal);
    private string _activeSection = "Home";
    private bool _historySelected;
    private RepositoryRowViewModel? _recoverySource;
    private RepositoryRowViewModel? _kitSource;

    public MainWindow(RepositoriesViewModel model, Func<ProtectRepositoryViewModel>? wizard = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _wizard = wizard;
        Title = "Fortiq";
        Icon = FortiqBrand.WindowIcon();
        Width = 1000; Height = 650; MinWidth = 850; MinHeight = 560; Background = CanvasBackground;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var shell = new Grid { ColumnDefinitions = new ColumnDefinitions("210,*") };
        shell.Children.Add(Navigation());
        Grid.SetColumn(_page, 1); shell.Children.Add(_page); Content = shell;
        _model.PropertyChanged += (_, _) => RenderActive();
        Opened += async (_, _) => await RefreshAsync();
    }

    private Border Navigation()
    {
        var rail = new Grid
        {
            Background = Surface, RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(16, 18)
        };
        rail.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(4, 0, 0, 22),
            Children = { new Image { Source = FortiqBrand.Logo(), Width = 28, Height = 28 }, Text("Fortiq", 18, FontWeight.SemiBold, Ink) }
        });
        var menu = new StackPanel { Spacing = 5 };
        menu.Children.Add(Nav("Home", RenderHome));
        menu.Children.Add(Nav("Protect", async () => await ProtectAsync()));
        menu.Children.Add(Nav("Backups", RenderBackups));
        menu.Children.Add(Nav("Recovery", RenderRecovery));
        menu.Children.Add(Nav("Recovery Kit", RenderRecoveryKit));
        menu.Children.Add(Nav("Settings", () => ShowSection("Settings", "Application, schedule, storage and service settings.")));
        Grid.SetRow(menu, 1); rail.Children.Add(menu);
        var service = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = Recoverable, VerticalAlignment = VerticalAlignment.Center }, Text("Service running", 11, FontWeight.Normal, Recoverable) } };
        var footer = new StackPanel { Spacing = 4, Margin = new Thickness(4, 0), Children = { Text("Fortiq", 13, FontWeight.SemiBold, Ink), Text("Protect What Matters", 11, FontWeight.Normal, Muted), service } };
        Grid.SetRow(footer, 2); rail.Children.Add(footer);
        return new Border { Background = Surface, BorderBrush = Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = rail };
    }

    private Button Nav(string label, Action action)
    {
        var button = new Button
        {
            Content = label, HorizontalContentAlignment = HorizontalAlignment.Left, HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(13, 10), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(6)
        };
        _navigation[label] = button;
        button.Click += (_, _) => { Select(label); action(); };
        ApplyNavigationStyle(button, label == _activeSection);
        return button;
    }

    private void Select(string section)
    {
        _activeSection = section;
        foreach (var item in _navigation) ApplyNavigationStyle(item.Value, item.Key == section);
    }

    private void RenderActive()
    {
        if (_activeSection == "Backups") RenderBackups();
        else if (_activeSection == "Recovery") RenderRecovery();
        else if (_activeSection == "Recovery Kit") RenderRecoveryKit();
        else if (_activeSection == "Home") RenderHome();
    }

    private static void ApplyNavigationStyle(Button button, bool selected)
    {
        button.Background = selected ? InfoSurface : Brushes.Transparent;
        button.Foreground = selected ? Brand : Ink;
        button.FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal;
    }

    private void RenderHome()
    {
        Select("Home");
        var body = new StackPanel { Spacing = 16, Margin = new Thickness(30, 26) };
        body.Children.Add(Header("Recovery assurance", "See whether your protected data can actually be recovered.", "Protect a folder", ProtectAsync));
        if (_model.State is HealthStoreState.NotInitialized or HealthStoreState.Empty) body.Children.Add(Welcome());
        else { body.Children.Add(Hero()); body.Children.Add(Metrics()); body.Children.Add(Repositories()); }
        if (_model.Failure is { Length: > 0 })
            body.Children.Add(Card(new StackPanel { Spacing = 5, Children = { Text("We could not read the latest protection status.", 14, FontWeight.SemiBold, Failure), Text(_model.Failure, 12, FontWeight.Normal, Muted, true) } }, AtRiskSurface, AtRiskLine));
        _page.Child = new ScrollViewer { Content = body };
    }

    private Border Welcome()
    {
        var protect = Primary("Protect a folder"); protect.Click += async (_, _) => await ProtectAsync();
        return Card(new StackPanel
        {
            Spacing = 15, MaxWidth = 650, HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                Text("Set up your first protected source", 23, FontWeight.SemiBold, Ink),
                Text("Fortiq will create an encrypted backup, check its integrity, and prove recovery with a real restore.", 14, FontWeight.Normal, Muted, true),
                Check("Choose the files that matter"), Check("Store an encrypted copy separately"),
                Check("Create and verify an offline recovery kit"), protect
            }
        }, Surface, Line, new Thickness(30));
    }

    private Border Hero()
    {
        var risk = _model.Repositories.Any(x => x.Health.Verdict == HealthVerdict.AtRisk);
        var unproven = _model.Repositories.Any(x => x.Health.Verdict == HealthVerdict.Unproven);
        var tone = risk ? AtRiskSurface : unproven ? UnprovenSurface : RecoverableSurface;
        var accent = risk ? AtRisk : unproven ? Unproven : Recoverable;
        var detail = risk ? "One or more sources need attention. Review the findings below."
            : unproven ? "Backups exist, but at least one source still needs a proven restore."
            : "All critical checks are healthy. Fortiq has recently restored and verified your data.";
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new StackPanel { Spacing = 5, Margin = new Thickness(0, 0, 18, 0), Children = { Text(_model.Headline, 22, FontWeight.SemiBold, Ink, true), Text(detail, 13, FontWeight.Normal, Muted, true) } });
        grid.Children.Add(Action("Refresh", RefreshAsync, 1));
        return Card(grid, tone, accent, new Thickness(22));
    }

    private Grid Metrics()
    {
        var backup = Latest(x => x.LastBackupAt);
        var check = Latest(x => x.LastHealthyCheckAt);
        var restore = Latest(x => x.LastProvenRestoreAt);
        var immutable = _model.Repositories.Count > 0 && _model.Repositories.All(x => x.Health.Facts.StorageImmutable);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), ColumnSpacing = 12 };
        Add(grid, Metric("Last backup", Relative(backup), backup is null ? "Not available" : "Completed"), 0);
        Add(grid, Metric("Integrity check", Relative(check), check is null ? "Not checked" : "Healthy"), 1);
        Add(grid, Metric("Proven restore", Relative(restore), restore is null ? "Not proven" : "Verified"), 2);
        Add(grid, Metric("Storage protection", immutable ? "Immutable" : "Review", immutable ? "Protected" : "Needs attention"), 3);
        return grid;
    }

    private DateTimeOffset? Latest(Func<RepositoryFacts, DateTimeOffset?> selector) =>
        _model.Repositories.Select(x => selector(x.Health.Facts)).Where(x => x is not null).OrderByDescending(x => x).FirstOrDefault();

    private Border Repositories()
    {
        var list = new StackPanel { Spacing = 12 };
        foreach (var repository in _model.Repositories)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("1.2*,2*,Auto") };
            row.Children.Add(new StackPanel { Spacing = 3, Children = { Text(repository.Title, 14, FontWeight.SemiBold, Ink), Text(repository.Health.RepositoryId, 11, FontWeight.Normal, Muted) } });
            row.Children.Add(At(Text(repository.Summary, 12, FontWeight.Normal, Muted, true), 1));
            var prove = Secondary(repository.Health.Verdict == HealthVerdict.Recoverable ? "Proven" : "Prove recovery");
            prove.IsEnabled = repository.CanProveRecovery && !_model.Busy;
            prove.Click += async (_, _) => await _model.ProveRecoveryAsync(repository, CancellationToken.None);
            row.Children.Add(At(prove, 2)); list.Children.Add(row);
        }
        return Card(new StackPanel { Spacing = 16, Children = { Text("Protected sources", 17, FontWeight.SemiBold, Ink), list } }, Surface, Line, new Thickness(20));
    }

    private static Border Metric(string title, string value, string status) => Card(new StackPanel
    {
        Spacing = 7, Children = { Text(title, 12, FontWeight.Normal, Muted), Text(value, 17, FontWeight.SemiBold, Ink), Text(status, 11, FontWeight.SemiBold, status is "Healthy" or "Verified" or "Protected" or "Completed" ? Recoverable : Unproven) }
    }, Surface, Line);

    private void RenderBackups()
    {
        Select("Backups");
        var body = new StackPanel { Spacing = 16, Margin = new Thickness(30, 26) };
        body.Children.Add(Header("Backups", "Manage protected sources and inspect backup history.", "Add source", ProtectAsync));

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        tabs.Children.Add(Tab("Sources", !_historySelected, () => { _historySelected = false; RenderBackups(); }));
        tabs.Children.Add(Tab("History", _historySelected, () => { _historySelected = true; RenderBackups(); }));
        body.Children.Add(tabs);
        body.Children.Add(_historySelected ? BackupHistory() : BackupSources());
        _page.Child = new ScrollViewer { Content = body };
    }

    private Border BackupSources()
    {
        if (_model.Repositories.Count == 0)
        {
            var add = Primary("Add your first source"); add.Click += async (_, _) => await ProtectAsync();
            return Card(new StackPanel { Spacing = 10, Children = { Text("No protected sources yet", 18, FontWeight.SemiBold, Ink), Text("Add a source to begin creating encrypted, verifiable backups.", 13, FontWeight.Normal, Muted), add } }, Surface, Line, new Thickness(22));
        }

        var list = new StackPanel { Spacing = 0 };
        list.Children.Add(TableRow("Source", "Last backup", "Recovery", "Status", true));
        foreach (var repository in _model.Repositories)
        {
            var status = repository.Health.Verdict switch { HealthVerdict.Recoverable => "Recoverable", HealthVerdict.Unproven => "Unproven", _ => "At risk" };
            var recovery = repository.Health.Facts.LastProvenRestoreAt is null ? "Not proven" : Relative(repository.Health.Facts.LastProvenRestoreAt);
            list.Children.Add(TableRow(repository.Title, Relative(repository.Health.Facts.LastBackupAt), recovery, status));
        }
        return Card(list, Surface, Line, new Thickness(20));
    }

    private Border BackupHistory()
    {
        var events = _model.Repositories
            .SelectMany(repository => new (DateTimeOffset? At, string Source, string Operation, string Result)[]
            {
                (repository.Health.Facts.LastBackupAt, repository.Title, "Backup", "Completed"),
                (repository.Health.Facts.LastHealthyCheckAt, repository.Title, "Integrity check", "Healthy"),
                (repository.Health.Facts.LastProvenRestoreAt, repository.Title, "Proven restore", "Verified")
            })
            .Where(item => item.At is not null)
            .OrderByDescending(item => item.At)
            .ToArray();

        if (events.Length == 0)
            return Card(new StackPanel { Spacing = 6, Children = { Text("No backup history yet", 18, FontWeight.SemiBold, Ink), Text("Completed backup, check and restore evidence will appear here.", 13, FontWeight.Normal, Muted) } }, Surface, Line, new Thickness(22));

        var list = new StackPanel { Spacing = 0 };
        list.Children.Add(TableRow("Time", "Source", "Operation", "Result", true));
        foreach (var item in events) list.Children.Add(TableRow(Relative(item.At), item.Source, item.Operation, item.Result));
        return Card(list, Surface, Line, new Thickness(20));
    }

    private static Grid TableRow(string first, string second, string third, string fourth, bool heading = false)
    {
        var weight = heading ? FontWeight.SemiBold : FontWeight.Normal;
        var color = heading ? Muted : Ink;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("1.4*,1.1*,1.2*,Auto"), ColumnSpacing = 12, Margin = new Thickness(0, 8), MinHeight = 30 };
        row.Children.Add(Text(first, heading ? 11 : 13, weight, color, true));
        row.Children.Add(At(Text(second, heading ? 11 : 12, weight, color, true), 1));
        row.Children.Add(At(Text(third, heading ? 11 : 12, weight, color, true), 2));
        var statusColor = !heading && fourth is "Recoverable" or "Completed" or "Healthy" or "Verified" ? Recoverable : !heading ? Unproven : color;
        row.Children.Add(At(Text(fourth, heading ? 11 : 12, heading ? weight : FontWeight.SemiBold, statusColor), 3));
        return row;
    }

    private static Button Tab(string label, bool selected, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(14, 7), Background = selected ? InfoSurface : Brushes.Transparent, Foreground = selected ? Brand : Muted, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(5), FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal };
        button.Click += (_, _) => action(); return button;
    }

    private void RenderRecovery()
    {
        Select("Recovery");
        if (_recoverySource is null || !_model.Repositories.Contains(_recoverySource)) _recoverySource = _model.Repositories.FirstOrDefault();
        var body = new StackPanel { Spacing = 16, Margin = new Thickness(30, 26) };
        body.Children.Add(Header("Prove recovery", "Actually restore data and verify that it can be recovered."));

        if (_recoverySource is null)
        {
            var protect = Primary("Protect a folder"); protect.Click += async (_, _) => await ProtectAsync();
            body.Children.Add(Card(new StackPanel { Spacing = 10, Children = { Text("There is nothing to recover yet", 18, FontWeight.SemiBold, Ink), Text("Create a protected source before running a recovery proof.", 13, FontWeight.Normal, Muted), protect } }, Surface, Line, new Thickness(22)));
            _page.Child = new ScrollViewer { Content = body }; return;
        }

        var selector = new ComboBox { ItemsSource = _model.Repositories.Select(item => item.Title).ToArray(), SelectedIndex = Math.Max(0, _model.Repositories.IndexOf(_recoverySource)), MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Left };
        selector.SelectionChanged += (_, _) => { if (selector.SelectedIndex >= 0) { _recoverySource = _model.Repositories[selector.SelectedIndex]; RenderRecovery(); } };
        body.Children.Add(new StackPanel { Spacing = 6, Children = { Text("Protected source", 12, FontWeight.SemiBold, Ink), selector } });
        body.Children.Add(RecoveryHero(_recoverySource));
        body.Children.Add(RecoveryDetails(_recoverySource));
        _page.Child = new ScrollViewer { Content = body };
    }

    private Border RecoveryHero(RepositoryRowViewModel repository)
    {
        var proven = repository.Health.Facts.LastProvenRestoreAt;
        var tone = proven is null ? UnprovenSurface : RecoverableSurface;
        var accent = proven is null ? Unproven : Recoverable;
        var run = Primary(_model.Busy ? "Running recovery proof…" : "Run recovery proof now");
        run.IsEnabled = repository.CanProveRecovery && !_model.Busy;
        run.Click += async (_, _) => await _model.ProveRecoveryAsync(repository, CancellationToken.None);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new StackPanel
        {
            Spacing = 6, Margin = new Thickness(0, 0, 18, 0),
            Children =
            {
                Text(proven is null ? "Recovery has not been proven" : "Latest recovery proof succeeded", 21, FontWeight.SemiBold, Ink, true),
                Text(proven is null ? "A backup exists, but Fortiq has not yet demonstrated that its files come back." : $"A real restore completed {Relative(proven)} and its output was verified.", 13, FontWeight.Normal, Muted, true)
            }
        });
        Grid.SetColumn(run, 1); run.VerticalAlignment = VerticalAlignment.Center; grid.Children.Add(run);
        return Card(grid, tone, accent, new Thickness(22));
    }

    private static Border RecoveryDetails(RepositoryRowViewModel repository)
    {
        var facts = repository.Health.Facts;
        var details = new StackPanel { Spacing = 14 };
        details.Children.Add(Text("Latest proof details", 17, FontWeight.SemiBold, Ink));
        details.Children.Add(DetailRow("Repository", repository.Title));
        details.Children.Add(DetailRow("Last backup", Absolute(facts.LastBackupAt)));
        details.Children.Add(DetailRow("Integrity check", Absolute(facts.LastHealthyCheckAt)));
        details.Children.Add(DetailRow("Proven restore", Absolute(facts.LastProvenRestoreAt)));
        details.Children.Add(DetailRow("Verification", facts.LastProvenRestoreAt is null ? "Not yet verified" : "Restore completed and output matched recorded evidence"));
        return Card(details, Surface, Line, new Thickness(20));
    }

    private static Grid DetailRow(string label, string value)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), ColumnSpacing = 12 };
        row.Children.Add(Text(label, 12, FontWeight.SemiBold, Muted));
        row.Children.Add(At(Text(value, 12, FontWeight.Normal, Ink, true), 1)); return row;
    }

    private void RenderRecoveryKit()
    {
        Select("Recovery Kit");
        if (_kitSource is null || !_model.Repositories.Contains(_kitSource)) _kitSource = _model.Repositories.FirstOrDefault();
        var body = new StackPanel { Spacing = 16, Margin = new Thickness(30, 26) };
        body.Children.Add(Header("Recovery Kit", "Keep the material required for disaster recovery safe and offline."));
        if (_kitSource is null)
        {
            var protect = Primary("Create protection and a kit"); protect.Click += async (_, _) => await ProtectAsync();
            body.Children.Add(Card(new StackPanel { Spacing = 10, Children = { Text("No recovery kit is configured", 18, FontWeight.SemiBold, Ink), Text("The protection wizard creates a recovery kit together with the encrypted repository.", 13, FontWeight.Normal, Muted, true), protect } }, Surface, Line, new Thickness(22)));
            _page.Child = new ScrollViewer { Content = body }; return;
        }

        var selector = new ComboBox { ItemsSource = _model.Repositories.Select(item => item.Title).ToArray(), SelectedIndex = Math.Max(0, _model.Repositories.IndexOf(_kitSource)), MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Left };
        selector.SelectionChanged += (_, _) => { if (selector.SelectedIndex >= 0) { _kitSource = _model.Repositories[selector.SelectedIndex]; RenderRecoveryKit(); } };
        body.Children.Add(new StackPanel { Spacing = 6, Children = { Text("Repository", 12, FontWeight.SemiBold, Ink), selector } });

        var present = _kitSource.Health.Facts.KitPresent;
        body.Children.Add(Card(new StackPanel
        {
            Spacing = 7,
            Children =
            {
                Text(present ? "Recovery kit is available" : "Recovery kit is missing", 21, FontWeight.SemiBold, present ? Recoverable : Failure),
                Text(present ? "Fortiq found recovery material for this repository." : "This repository cannot be opened on another machine until its recovery material is restored.", 13, FontWeight.Normal, Muted, true),
                Text($"Repository ID: {_kitSource.Health.RepositoryId}", 11, FontWeight.Normal, Muted, true)
            }
        }, present ? RecoverableSurface : AtRiskSurface, present ? Recoverable : AtRisk, new Thickness(22)));
        body.Children.Add(Card(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Text("Your recovery phrase is sensitive", 15, FontWeight.SemiBold, Caution),
                Text("Fortiq does not retain a displayable copy after setup. Use the offline copy you verified in the protection wizard; never store it on this computer or in cloud notes.", 12, FontWeight.Normal, Muted, true)
            }
        }, UnprovenSurface, UnprovenLine, new Thickness(18)));

        var verify = Primary("Verify recovery with a restore");
        verify.IsEnabled = present && _kitSource.CanProveRecovery;
        verify.Click += (_, _) => { _recoverySource = _kitSource; RenderRecovery(); };
        body.Children.Add(Card(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Text("What the kit enables", 17, FontWeight.SemiBold, Ink),
                Check("Identify and open the encrypted repository"),
                Check("Unlock repository encryption from an independent copy"),
                Check("Recover on another machine when the original is unavailable"),
                verify
            }
        }, Surface, Line, new Thickness(20)));
        _page.Child = new ScrollViewer { Content = body };
    }

    private void ShowSection(string title, string subtitle)
    {
        Select(title);
        var home = new Button { Content = "Back to Home", Padding = new Thickness(14, 8) }; home.Click += (_, _) => RenderHome();
        _page.Child = new StackPanel { Margin = new Thickness(30, 26), Spacing = 18, Children = { Header(title, subtitle), Card(new StackPanel { Spacing = 10, Children = { Text("This section is ready for the next implementation pass.", 17, FontWeight.SemiBold, Ink), Text("The new shell and navigation are working; detailed workflows will reuse the same evidence-first components.", 13, FontWeight.Normal, Muted, true), home } }, Surface, Line, new Thickness(24)) } };
    }

    private static Grid Header(string title, string subtitle, string? action = null, Func<Task>? handler = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new StackPanel { Spacing = 3, Children = { Text(title, 27, FontWeight.SemiBold, Ink), Text(subtitle, 13, FontWeight.Normal, Muted) } });
        if (action is not null && handler is not null) grid.Children.Add(Action(action, handler, 1, true));
        return grid;
    }

    private async Task ProtectAsync() { if (_wizard is null) return; await new ProtectRepositoryWindow(_wizard()).ShowDialog(this); await RefreshAsync(); }
    private async Task RefreshAsync() => await _model.RefreshAsync(CancellationToken.None);

    private static Button Action(string label, Func<Task> action, int column, bool primary = false)
    {
        var button = primary ? Primary(label) : Secondary(label);
        button.Click += async (_, _) => await action(); Grid.SetColumn(button, column); return button;
    }
    private static Button Primary(string label) => new() { Content = label, Background = Brand, Foreground = Brushes.White, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(5), Padding = new Thickness(17, 9), HorizontalAlignment = HorizontalAlignment.Left, FontWeight = FontWeight.SemiBold };
    private static Button Secondary(string label) => new() { Content = label, Background = Surface, Foreground = Ink, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(14, 7), HorizontalAlignment = HorizontalAlignment.Left };
    private static Border Card(Control child, IBrush background, IBrush border, Thickness? padding = null) => new() { Child = child, Background = background, BorderBrush = border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = padding ?? new Thickness(16) };
    private static StackPanel Check(string label) => new() { Orientation = Orientation.Horizontal, Spacing = 9, Children = { new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Recoverable, VerticalAlignment = VerticalAlignment.Center }, Text(label, 13, FontWeight.Normal, Ink) } };
    private static TextBlock Text(string value, double size, FontWeight weight, IBrush color, bool wrap = false) => new() { Text = value, FontSize = size, FontWeight = weight, Foreground = color, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
    private static T At<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
    private static void Add(Grid grid, Control control, int column) { Grid.SetColumn(control, column); grid.Children.Add(control); }
    private static string Relative(DateTimeOffset? value)
    {
        if (value is null) return "Not yet"; var age = DateTimeOffset.UtcNow - value.Value;
        if (age < TimeSpan.FromMinutes(1)) return "Just now";
        if (age < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)age.TotalMinutes)} min ago";
        if (age < TimeSpan.FromDays(1)) return $"{Math.Max(1, (int)age.TotalHours)} hr ago";
        return $"{Math.Max(1, (int)age.TotalDays)} days ago";
    }
    private static string Absolute(DateTimeOffset? value) => value is null ? "Not available" : value.Value.LocalDateTime.ToString("g", System.Globalization.CultureInfo.CurrentCulture);
}
