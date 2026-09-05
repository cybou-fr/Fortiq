using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Fortiq.Application;
using Fortiq.Desktop.Controls;
using Fortiq.Desktop.ViewModels;
using Fortiq.Infrastructure.Receipts;
using Fortiq.Monitoring;
using Fortiq.Platform.Windows;
using System.Diagnostics;
using static Fortiq.Desktop.DesignTokens;

namespace Fortiq.Desktop;

public sealed class MainWindow : Window
{
    private readonly Func<FileRecoveryViewModel>? _fileRecovery;
    private readonly bool _installed;
    private readonly RepositoriesViewModel _model;
    private readonly SettingsViewModel _settings;
    private readonly Func<ProtectRepositoryViewModel>? _wizard;
    private readonly Border _page = new();
    private readonly Dictionary<string, Button> _navigation = new(StringComparer.Ordinal);
    private string _activeSection = "Home";

    private readonly Border _statusDot = new()
    {
        Width = 8,
        Height = 8,
        CornerRadius = new CornerRadius(4),
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly TextBlock _statusLabel = Text(string.Empty, 11, FontWeight.Normal, Muted);
    private bool _historySelected;
    private RepositoryRowViewModel? _recoverySource;
    private RepositoryRowViewModel? _kitSource;
    private AuditChainStatus _auditChain = AuditChainStatus.NotChecked;
    private bool _auditLedgerVerifying;
    private readonly Action<string>? _updateTrayStatus;
    private readonly Action? _disposeTray;
    private readonly DesktopPreferencesStore _preferences;
    private bool _isExplicitExit;

    public MainWindow(
        RepositoriesViewModel model,
        Func<ProtectRepositoryViewModel>? wizard = null,
        SettingsViewModel? settings = null, bool installed = false, Func<FileRecoveryViewModel>? fileRecovery = null)
    {
        _installed = installed;
        _fileRecovery = fileRecovery;
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _wizard = wizard;
        _settings = settings ?? new SettingsViewModel(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        _preferences = DesktopPreferencesStore.Resolve(_installed);

        Title = "Fortiq — Data Recovery Assurance";
        Icon = FortiqBrand.WindowIcon();
        Width = 1060;
        Height = 700;
        MinWidth = 880;
        MinHeight = 580;
        Background = CanvasBackground;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _page.Background = CanvasBackground;

        var shell = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("230,*")
        };
        shell.Children.Add(NavigationRail());
        Grid.SetColumn(_page, 1);
        shell.Children.Add(_page);
        Content = shell;

        _model.PropertyChanged += (_, _) => RenderActive();
        DesignTokens.ThemeChanged += () =>
        {
            Background = CanvasBackground;
            _page.Background = CanvasBackground;
            RenderActive();
        };

        Opened += async (_, _) =>
        {
            await RefreshAsync();
            await _settings.RefreshServiceStatusAsync();
        };

        var refreshTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        refreshTimer.Tick += async (_, _) =>
        {
            if (!_model.Busy) await RefreshAsync();
        };
        Opened += (_, _) => refreshTimer.Start();

        var trayIcon = new FortiqTrayIcon(ShowFromTray, ExplicitExit, OpenRecoveryFromTray, () => _ = RefreshAsync());
        _updateTrayStatus = trayIcon.UpdateStatus;
        _disposeTray = trayIcon.Dispose;

        Closed += (_, _) =>
        {
            refreshTimer.Stop();
            _disposeTray();
        };

        Closing += (sender, e) =>
        {
            if (!_isExplicitExit)
            {
                if (!_installed || !_preferences.Current.MinimizeToTrayOnClose)
                {
                    _isExplicitExit = true;
                    return;
                }

                e.Cancel = true;
                Hide();
            }
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            _isExplicitExit = true;
            _disposeTray();
        };

        RenderActive();
    }

    /// <summary>Shrinks the window to fit the display it opened on, if it does not already.</summary>
    /// <remarks>
    /// The default size suits an ordinary screen. On a 1366x768 laptop, or a scaled display where the
    /// same number is most of the height, it puts the bottom of the window under the taskbar and the
    /// controls there out of reach. Asking the screen is the difference between a window that fits
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

        var fitsHigh = Math.Max(MinHeight, screen.WorkingArea.Height / screen.Scaling - 60);
        if (Height > fitsHigh)
        {
            Height = fitsHigh;
        }

        var fitsWide = Math.Max(MinWidth, screen.WorkingArea.Width / screen.Scaling - 60);
        if (Width > fitsWide)
        {
            Width = fitsWide;
        }
    }

    private Border NavigationRail()
    {
        var rail = new Grid
        {
            Background = SidebarBackground,
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(16, 20)
        };

        var brandHeader = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(6, 0, 0, 24),
            Children =
            {
                new Image { Source = FortiqBrand.Logo(), Width = 30, Height = 30 },
                new StackPanel
                {
                    Spacing = 1,
                    Children =
                    {
                        Text("Fortiq", 17, FontWeight.Bold, Ink),
                        Text("Community Edition", 10, FontWeight.SemiBold, Brand),
                        Text("Recovery Assurance", 9, FontWeight.Normal, Muted)
                    }
                }
            }
        };
        rail.Children.Add(brandHeader);

        var menu = new StackPanel { Spacing = 4 };
        // "Protect" is not here any more. It was the only navigation entry that opened a dialog
        // instead of showing a page - so the sidebar meant "go here" four times and "do this" once,
        // and the one exception was the item people clicked first. Protecting a folder is an action,
        // and it is offered as a button on the screens where it makes sense: the welcome card, the
        // dashboard header, and "+ Add source" on Backups, which is also the page that lists what is
        // already protected.
        menu.Children.Add(Nav("Home", RenderHome));
        menu.Children.Add(Nav("Backups", RenderBackups));
        menu.Children.Add(Nav("Recovery", RenderRecovery));
        menu.Children.Add(Nav("Recovery Kit", RenderRecoveryKit));
        Grid.SetRow(menu, 1);
        rail.Children.Add(menu);

        var bottomStack = new StackPanel { Spacing = 6 };
        bottomStack.Children.Add(Nav("Settings", RenderSettings));

        // Was a green dot and the words "Local protection active", both hard-coded. It said that on a
        // machine protecting nothing, and it would have said it while every repository was at risk.
        // In a product whose entire claim is that it does not tell you your data is safe when it
        // cannot show that it is, a permanently green light in the corner is the worst thing on the
        // screen.
        var serviceBadge = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            Margin = new Thickness(12, 10, 0, 0),
            Children =
            {
                _statusDot,
                _statusLabel
            }
        };
        bottomStack.Children.Add(serviceBadge);
        UpdateSidebarStatus();

        var versionTag = Text($"v{_settings.AppVersion}", 10, FontWeight.Normal, TextMuted);
        versionTag.Margin = new Thickness(12, 2, 0, 0);
        bottomStack.Children.Add(versionTag);

        Grid.SetRow(bottomStack, 2);
        rail.Children.Add(bottomStack);

        return new Border
        {
            Background = SidebarBackground,
            BorderBrush = Line,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = rail
        };
    }

    private Button Nav(string label, Action action)
    {
        var button = new Button
        {
            Content = label,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(14, 9),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            FontSize = 13
        };
        _navigation[label] = button;
        button.Click += (_, _) => { Select(label); action(); };
        ApplyNavigationStyle(button, label == _activeSection);
        return button;
    }

    private void Select(string section)
    {
        _activeSection = section;
        foreach (var item in _navigation)
        {
            ApplyNavigationStyle(item.Value, item.Key == section);
        }
    }

    private static void ApplyNavigationStyle(Button button, bool selected)
    {
        button.Background = selected ? InfoSurface : Brushes.Transparent;
        button.Foreground = selected ? Brand : Ink;
        button.FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal;
    }

    /// <summary>Puts the machine's actual protection state in the corner of every screen.</summary>
    private void UpdateSidebarStatus()
    {
        var (brush, label) = _model.State switch
        {
            HealthStoreState.NotInitialized or HealthStoreState.Empty => (Muted, "Nothing protected yet"),
            HealthStoreState.Corrupt => (AtRisk, "Protection status unreadable"),
            HealthStoreState.Stale => (Unproven, "Status out of date"),
            _ when _model.Repositories.Count == 0 => (Muted, "Nothing protected yet"),
            _ when _model.Repositories.Any(r => r.Health.Verdict == HealthVerdict.AtRisk) => (AtRisk, "Needs attention"),
            _ when _model.Repositories.Any(r => r.Health.Verdict == HealthVerdict.Unproven) => (Unproven, "Backed up, not proven"),
            _ => (Recoverable, "Recovery proven")
        };

        _statusDot.Background = brush;
        _statusLabel.Text = label;
        _updateTrayStatus?.Invoke(label);
    }

    private void RenderActive()
    {
        UpdateSidebarStatus();

        if (_activeSection == "Backups") RenderBackups();
        else if (_activeSection == "Recovery") RenderRecovery();
        else if (_activeSection == "Recovery Kit") RenderRecoveryKit();
        else if (_activeSection == "Settings") RenderSettings();
        else RenderHome();
    }

    // --- Screen 1: Dashboard (Home) ---
    private void RenderHome()
    {
        Select("Home");
        var body = new StackPanel { Spacing = 20, Margin = new Thickness(32, 26) };

        var nothingProtectedYet =
            _model.State is HealthStoreState.NotInitialized or HealthStoreState.Empty
            || _model.Repositories.Count == 0;

        if (nothingProtectedYet)
        {
            // One screen, one thing to do. This used to show a "Protect a folder" button in the
            // header, the same button again in a banner, and "Protect your first folder" in the card
            // below it - three buttons for one action, which reads as three different actions and
            // makes a person stop to work out which is the real one.
            //
            // The four measurement tiles are gone from this state too. Before anything is protected
            // they all read "Never", which measures nothing and looks like four problems.
            body.Children.Add(Header(
                "Welcome to Fortiq",
                "Nothing is being backed up yet. Start with one folder - you can add more later."));

            body.Children.Add(ZeroStateWelcomeCard());
            _page.Child = new ScrollViewer { Content = body };
            return;
        }

        body.Children.Add(Header(
            "Dashboard",
            "Your backups, and whether they have been proven to come back.",
            "Protect a folder",
            ProtectAsync));

        var (mode, headline, desc, actionText) = ResolveHeroState();
        body.Children.Add(new HeroHealthBanner(mode, headline, desc, actionText, RefreshAsync));

        body.Children.Add(MetricsGrid());
        body.Children.Add(RepositoriesSummaryCard());
        body.Children.Add(RecentActivityCard());

        _page.Child = new ScrollViewer { Content = body };
    }

    private (HeroStatusMode Mode, string Headline, string Desc, string Action) ResolveHeroState()
    {
        if (_model.State is HealthStoreState.NotInitialized or HealthStoreState.Empty || _model.Repositories.Count == 0)
        {
            return (HeroStatusMode.ZeroState,
                "Protect what matters before you need it",
                "No protected sources are configured yet. Add your first folder to start automated verifiable backups.",
                "Protect a folder");
        }

        if (_model.State == HealthStoreState.Corrupt)
        {
            return (HeroStatusMode.AtRisk,
                "Protection status temporarily unavailable",
                _model.Failure ?? "Could not read health evidence. Check local service.",
                "Refresh");
        }

        if (_model.State == HealthStoreState.Stale)
        {
            return (HeroStatusMode.Unproven,
                "Protection status is out of date",
                "The health report has not been refreshed recently. Verify that the Fortiq service is running.",
                "Refresh");
        }

        if (_model.Repositories.Any(r => r.Health.Verdict == HealthVerdict.AtRisk))
        {
            return (HeroStatusMode.AtRisk,
                "Something may not be recoverable today",
                "One or more protected sources report integrity or verification issues. Review findings below.",
                "Review");
        }

        if (_model.Repositories.Any(r => r.Health.Verdict == HealthVerdict.Unproven))
        {
            return (HeroStatusMode.Unproven,
                "Recovery has not been proven for all sources",
                "Backups exist and are healthy, but at least one source needs a verified restore test.",
                "Prove recovery");
        }

        return (HeroStatusMode.Recoverable,
            "Your data is recoverable",
            "All critical checks are healthy. Fortiq has recently restored and verified your protected sources.",
            "Refresh");
    }

    private Grid MetricsGrid()
    {
        var total = _model.Repositories.Count;
        var backedUpRecently = _model.Repositories.Count(r =>
            r.Health.Facts.LastBackupAt is { } dt && (DateTimeOffset.UtcNow - dt) <= TimeSpan.FromDays(2));
        // "Recovery proven" is a fact about this repository - something was restored from it - not a
        // synonym for the overall verdict. A repository whose integrity check has gone stale is
        // Unproven overall while its restore drill still happened, and counting the verdict here told
        // the person their restore had somehow un-happened.
        var recoveryProven = _model.Repositories.Count(r => r.Health.Facts.LastProvenRestoreAt is not null);
        var atRisk = _model.Repositories.Count(r => r.Health.Verdict == HealthVerdict.AtRisk);
        var unproven = _model.Repositories.Count(r => r.Health.Verdict == HealthVerdict.Unproven);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*"),
            ColumnSpacing = 12
        };

        Add(grid, new KpiStatCard(
            "Protected sources",
            total.ToString(System.Globalization.CultureInfo.InvariantCulture),
            total == 0 ? "None configured" : "Configured",
            total == 0 ? Muted : Ink,
            total == 0 ? InfoSurface : Surface), 0);

        var backupAllGood = total > 0 && backedUpRecently == total;
        Add(grid, new KpiStatCard(
            "Backed up recently",
            total == 0 ? "0" : $"{backedUpRecently} of {total}",
            total == 0 ? "No sources" : backupAllGood ? "All up to date" : $"{total - backedUpRecently} pending",
            total == 0 ? Muted : backupAllGood ? Recoverable : Unproven,
            total == 0 ? InfoSurface : backupAllGood ? RecoverableSurface : UnprovenSurface), 1);

        var provenAllGood = total > 0 && recoveryProven == total;
        Add(grid, new KpiStatCard(
            "Recovery proven",
            total == 0 ? "0" : $"{recoveryProven} of {total}",
            total == 0 ? "No sources" : provenAllGood ? "All verified" : $"{total - recoveryProven} unproven",
            total == 0 ? Muted : provenAllGood ? Recoverable : Unproven,
            total == 0 ? InfoSurface : provenAllGood ? RecoverableSurface : UnprovenSurface), 2);

        // At risk and unproven are different situations and must not be added into one red number.
        // "At risk" means a recovery would fail today. "Unproven" means nothing has demonstrated that
        // it would succeed - a repository waiting for its first restore drill has done nothing wrong,
        // and calling that at risk teaches people to ignore the word by the time it is true.
        var needsAttention = atRisk + unproven;
        Add(grid, new KpiStatCard(
            "Needs attention",
            total == 0 ? "0" : needsAttention.ToString(System.Globalization.CultureInfo.InvariantCulture),
            total == 0
                ? "Nothing protected yet"
                : atRisk > 0 && unproven > 0 ? $"{atRisk} at risk, {unproven} unproven"
                : atRisk > 0 ? $"{atRisk} source(s) at risk"
                : unproven > 0 ? $"{unproven} awaiting a restore drill"
                : "All proven recoverable",
            total == 0 ? Muted : atRisk > 0 ? Failure : unproven > 0 ? Unproven : Recoverable,
            total == 0 ? InfoSurface : atRisk > 0 ? AtRiskSurface : unproven > 0 ? UnprovenSurface : RecoverableSurface), 3);

        return grid;
    }

    private Border ZeroStateWelcomeCard()
    {
        var protectBtn = Primary("Get Started — Protect a folder");
        protectBtn.Click += async (_, _) => await ProtectAsync();

        return Card(new StackPanel
        {
            Spacing = 16,
            Children =
            {
                Text("Welcome to Fortiq Community Edition", 19, FontWeight.SemiBold, Ink),
                Text("Unlike traditional tools that only tell you a backup ran, Fortiq continuously proves that your data can actually be restored before the day you need it.", 13, FontWeight.Normal, Muted, true),
                Check("1. Select a folder — personal documents, projects, photos, or databases"),
                Check("2. Choose destination — external USB drive, secondary disk, or S3 cloud storage"),
                Check("3. Secure your 24-word recovery phrase — guaranteed offline recovery on any computer"),
                Check("4. Automated drills periodically prove data integrity and byte-level recoverability"),
                protectBtn
            }
        }, Surface, Line, new Thickness(24));
    }

    private Border RepositoriesSummaryCard()
    {
        var list = new StackPanel { Spacing = 10 };
        foreach (var repository in _model.Repositories)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("1.5*,2*,Auto"), ColumnSpacing = 12 };
            row.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    Text(repository.Title, 14, FontWeight.SemiBold, Ink),
                    Text(repository.Health.RepositoryId, 11, FontWeight.Normal, Muted)
                }
            });
            row.Children.Add(At(Text(repository.Summary, 12, FontWeight.Normal, Muted, true), 1));

            var prove = Secondary(repository.Health.Verdict == HealthVerdict.Recoverable ? "Proven" : "Prove recovery");
            prove.IsEnabled = repository.CanProveRecovery && !_model.Busy;
            prove.Click += async (_, _) => await ProveAsync(repository);
            row.Children.Add(At(prove, 2));

            list.Children.Add(row);
        }

        return Card(new StackPanel
        {
            Spacing = 16,
            Children =
            {
                Text("Protected sources", 16, FontWeight.SemiBold, Ink),
                list
            }
        }, Surface, Line, new Thickness(20));
    }

    private Border RecentActivityCard()
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
            .Take(5)
            .ToArray();

        var list = new StackPanel { Spacing = 4 };
        list.Children.Add(TableRow("Time", "Source", "Operation", "Result", true));
        foreach (var item in events)
        {
            list.Children.Add(TableRow(Relative(item.At), item.Source, item.Operation, item.Result));
        }

        return Card(new StackPanel
        {
            Spacing = 14,
            Children =
            {
                Text("Recent activity", 16, FontWeight.SemiBold, Ink),
                list
            }
        }, Surface, Line, new Thickness(20));
    }

    // --- Screen 3: Backups & Activity ---
    private void RenderBackups()
    {
        Select("Backups");
        var body = new StackPanel { Spacing = 18, Margin = new Thickness(32, 26) };
        body.Children.Add(Header("Backups", "The folders Fortiq is protecting, and what it has done with them.", "Protect a folder", ProtectAsync));

        body.Children.Add(AuditLedgerCard());

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        tabs.Children.Add(Tab("Sources", !_historySelected, () => { _historySelected = false; RenderBackups(); }));
        tabs.Children.Add(Tab("History", _historySelected, () => { _historySelected = true; RenderBackups(); }));
        body.Children.Add(tabs);

        body.Children.Add(_historySelected ? BackupHistoryView() : BackupSourcesView());
        _page.Child = new ScrollViewer { Content = body };
    }

    private Border AuditLedgerCard()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };

        var leftStack = new StackPanel { Spacing = 6 };

        var badgeStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };

        var colors = AuditBadgeColors();
        var badge = new Border
        {
            Background = colors.Background,
            BorderBrush = colors.Foreground,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2),
            Child = Text(AuditBadgeText(), 11, FontWeight.SemiBold, colors.Foreground)
        };
        badgeStack.Children.Add(badge);

        var shaTag = new Border
        {
            Background = InfoSurface,
            BorderBrush = Brand,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2),
            Child = Text("SHA-256 Chained (ADR-007)", 11, FontWeight.SemiBold, Brand)
        };
        badgeStack.Children.Add(shaTag);

        leftStack.Children.Add(badgeStack);

        var descText = Text(AuditStatus().Detail, 12, FontWeight.Normal, Muted, wrap: true);
        leftStack.Children.Add(descText);

        grid.Children.Add(leftStack);

        var verifyBtn = Secondary(_auditLedgerVerifying ? "Verifying…" : "Verify chain");
        verifyBtn.IsEnabled = !_auditLedgerVerifying;
        verifyBtn.Click += async (_, _) =>
        {
            _auditLedgerVerifying = true;
            RenderBackups();
            try
            {
                var dir = FortiqStatePaths.Resolve().Receipts;
                _auditChain = AuditChainStatus.From(await AuditLedgerVerifier.VerifyLedgerAsync(dir));
            }
            catch (Exception ex)
            {
                _auditChain = AuditChainStatus.Failed(ex.Message);
            }
            finally
            {
                _auditLedgerVerifying = false;
                RenderBackups();
            }
        };

        Grid.SetColumn(verifyBtn, 1);
        verifyBtn.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(verifyBtn);

        return Card(grid, Surface, Line, new Thickness(18, 14));
    }

    private Border BackupSourcesView()
    {
        if (_model.Repositories.Count == 0)
        {
            var add = Primary("Protect a folder");
            add.Click += async (_, _) => await ProtectAsync();
            return Card(new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    Text("No protected sources configured", 18, FontWeight.SemiBold, Ink),
                    Text("Add a source folder to begin continuous, deduplicated backups.", 13, FontWeight.Normal, Muted),
                    add
                }
            }, Surface, Line, new Thickness(24));
        }

        var list = new StackPanel { Spacing = 6 };
        var headerRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1.5*,1.1*,1.1*,Auto,Auto"),
            ColumnSpacing = 12,
            Margin = new Thickness(0, 4),
            MinHeight = 24
        };
        headerRow.Children.Add(Text("Source", 11, FontWeight.SemiBold, Muted));
        headerRow.Children.Add(At(Text("Last backup", 11, FontWeight.SemiBold, Muted), 1));
        headerRow.Children.Add(At(Text("Recovery", 11, FontWeight.SemiBold, Muted), 2));
        headerRow.Children.Add(At(Text("Status", 11, FontWeight.SemiBold, Muted), 3));
        list.Children.Add(headerRow);

        foreach (var repository in _model.Repositories)
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("1.5*,1.1*,1.1*,Auto,Auto"),
                ColumnSpacing = 12,
                Margin = new Thickness(0, 6),
                MinHeight = 32
            };
            row.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    Text(repository.Title, 13, FontWeight.SemiBold, Ink),
                    Text(repository.Health.RepositoryId, 10, FontWeight.Normal, Muted)
                }
            });
            row.Children.Add(At(Text(Relative(repository.Health.Facts.LastBackupAt), 12, FontWeight.Normal, Muted), 1));
            var recovery = repository.Health.Facts.LastProvenRestoreAt is null ? "Not proven" : Relative(repository.Health.Facts.LastProvenRestoreAt);
            row.Children.Add(At(Text(recovery, 12, FontWeight.Normal, Muted), 2));

            // The label and the colour have to agree. An at-risk row used to be painted the same
            // amber as an unproven one, so the only row that meant "act now" looked like the rest.
            var (statusLabel, statusColor) = repository.Health.Verdict switch
            {
                HealthVerdict.Recoverable => ("Recoverable", Recoverable),
                HealthVerdict.Unproven => ("Unproven", Unproven),
                _ => ("At risk", AtRisk)
            };
            row.Children.Add(At(Text(statusLabel, 12, FontWeight.SemiBold, statusColor), 3));

            if (repository.CanProveRecovery && repository.Health.Verdict != HealthVerdict.Recoverable)
            {
                var proveBtn = Secondary("Prove");
                proveBtn.Padding = new Thickness(10, 4);
                proveBtn.FontSize = 11;
                proveBtn.IsEnabled = !_model.Busy;
                proveBtn.Click += async (_, _) => await ProveAsync(repository);
                row.Children.Add(At(proveBtn, 4));
            }

            list.Children.Add(row);
        }
        return Card(list, Surface, Line, new Thickness(20));
    }

    private Border BackupHistoryView()
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
        {
            return Card(new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    Text("No backup history yet", 18, FontWeight.SemiBold, Ink),
                    Text("Audit trail and proof evidence will appear here after the first backup cycle.", 13, FontWeight.Normal, Muted)
                }
            }, Surface, Line, new Thickness(24));
        }

        var list = new StackPanel { Spacing = 4 };
        list.Children.Add(TableRow("Time", "Source", "Operation", "Result", true));
        foreach (var item in events)
        {
            list.Children.Add(TableRow(Relative(item.At), item.Source, item.Operation, item.Result));
        }
        return Card(list, Surface, Line, new Thickness(20));
    }

    // --- Screen 4: Recovery Proof ---
    private void RenderRecovery()
    {
        Select("Recovery");
        _recoverySource = _model.Repositories.FirstOrDefault(item => item.Health.RepositoryId == _recoverySource?.Health.RepositoryId)
            ?? _model.Repositories.FirstOrDefault();

        var body = new StackPanel { Spacing = 18, Margin = new Thickness(32, 26) };
        body.Children.Add(Header("Recovery", "Restore files from a recovery kit or run a recovery proof for a protected source."));
        if (_fileRecovery is not null)
        {
            var restoreFiles = Primary("Restore files from a recovery kit");
            restoreFiles.Click += async (_, _) =>
            {
                await new FileRecoveryWindow(_fileRecovery()).ShowDialog(this);
            };
            body.Children.Add(restoreFiles);
        }

        if (_recoverySource is null)
        {
            var protect = Primary("Protect a folder");
            protect.Click += async (_, _) => await ProtectAsync();
            body.Children.Add(Card(new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    Text("No repositories available to prove", 18, FontWeight.SemiBold, Ink),
                    Text("Protect a source first before running recovery drills.", 13, FontWeight.Normal, Muted),
                    protect
                }
            }, Surface, Line, new Thickness(24)));
            _page.Child = new ScrollViewer { Content = body };
            return;
        }

        if (_model.Repositories.Count > 1)
        {
            var selector = new ComboBox
            {
                ItemsSource = _model.Repositories.Select(item => item.Title).ToArray(),
                SelectedIndex = Math.Max(0, _model.Repositories.IndexOf(_recoverySource)),
                MinWidth = 260,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            selector.SelectionChanged += (_, _) =>
            {
                if (selector.SelectedIndex >= 0)
                {
                    _recoverySource = _model.Repositories[selector.SelectedIndex];
                    RenderRecovery();
                }
            };
            body.Children.Add(new StackPanel { Spacing = 6, Children = { Text("Protected source", 12, FontWeight.SemiBold, Ink), selector } });
        }

        body.Children.Add(RecoveryHero(_recoverySource));
        body.Children.Add(RecoveryDetails(_recoverySource));
        _page.Child = new ScrollViewer { Content = body };
    }

    private Border RecoveryHero(RepositoryRowViewModel repository)
    {
        var proven = repository.Health.Facts.LastProvenRestoreAt;
        var current = repository.Health.Verdict == HealthVerdict.Recoverable;
        var tone = current ? RecoverableSurface : UnprovenSurface;
        var accent = current ? Recoverable : Unproven;

        var run = Primary(_model.Busy ? "Running restore drill…" : "Run recovery proof now");
        run.IsEnabled = repository.CanProveRecovery && !_model.Busy;
        run.Click += async (_, _) => await ProveAsync(repository);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 0, 18, 0),
            Children =
            {
                Text(current ? "Recovery is proven by current evidence" : "Recovery requires verification drill", 20, FontWeight.SemiBold, Ink, true),
                Text(current ? $"A real restore completed {Relative(proven)} and byte integrity was verified." : repository.Detail, 13, FontWeight.Normal, Muted, true)
            }
        });
        Grid.SetColumn(run, 1);
        run.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(run);

        return Card(grid, tone, accent, new Thickness(22));
    }

    private static Border RecoveryDetails(RepositoryRowViewModel repository)
    {
        var facts = repository.Health.Facts;
        var details = new StackPanel { Spacing = 12 };
        details.Children.Add(Text("Latest proof evidence", 16, FontWeight.SemiBold, Ink));
        details.Children.Add(DetailRow("Repository ID", repository.Health.RepositoryId));
        details.Children.Add(DetailRow("Source Path", repository.Title));
        details.Children.Add(DetailRow("Last Backup", Absolute(facts.LastBackupAt)));
        details.Children.Add(DetailRow("Integrity Check", Absolute(facts.LastHealthyCheckAt)));
        details.Children.Add(DetailRow("Proven Restore", Absolute(facts.LastProvenRestoreAt)));
        details.Children.Add(DetailRow("Evidence Match", facts.LastProvenRestoreAt is null ? "Pending drill" : "Restore completed and restored bytes were reconciled on disk."));

        return Card(details, Surface, Line, new Thickness(20));
    }

    // --- Screen 5: Recovery Kit ---
    private void RenderRecoveryKit()
    {
        Select("Recovery Kit");
        _kitSource = _model.Repositories.FirstOrDefault(item => item.Health.RepositoryId == _kitSource?.Health.RepositoryId)
            ?? _model.Repositories.FirstOrDefault();

        var body = new StackPanel { Spacing = 18, Margin = new Thickness(32, 26) };
        body.Children.Add(Header("Recovery Kit", "Emergency offline material required to restore files on an independent machine."));

        if (_kitSource is null)
        {
            var protect = Primary("Protect a folder");
            protect.Click += async (_, _) => await ProtectAsync();
            body.Children.Add(Card(new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    Text("No recovery kit configured yet", 18, FontWeight.SemiBold, Ink),
                    Text("The protection wizard initializes an offline recovery kit alongside the encrypted repository.", 13, FontWeight.Normal, Muted, true),
                    protect
                }
            }, Surface, Line, new Thickness(24)));
            _page.Child = new ScrollViewer { Content = body };
            return;
        }

        var present = _kitSource.Health.Facts.KitPresent;
        body.Children.Add(Card(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Text(present ? "Recovery kit is available and verified" : "Recovery kit is missing or damaged", 20, FontWeight.SemiBold, present ? Recoverable : Failure),
                Text(present ? "Fortiq found valid recovery material for this repository." : "This repository cannot be restored if this machine fails until recovery material is restored.", 13, FontWeight.Normal, Muted, true),
                Text($"Repository ID: {_kitSource.Health.RepositoryId}", 11, FontWeight.Normal, Muted, true)
            }
        }, present ? RecoverableSurface : AtRiskSurface, present ? Recoverable : AtRisk, new Thickness(22)));

        // Masked BIP-39 mnemonic component
        body.Children.Add(new MnemonicObfuscatorControl(null));

        var verifyBtn = Primary("Verify recovery with a drill");
        verifyBtn.IsEnabled = present && _kitSource.CanProveRecovery;
        verifyBtn.Click += (_, _) => { _recoverySource = _kitSource; RenderRecovery(); };

        body.Children.Add(Card(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Text("What the recovery kit enables", 16, FontWeight.SemiBold, Ink),
                Check("Open the encrypted repository on another computer without Fortiq"),
                Check("Restore data if the local disk is destroyed or wiped"),
                Check("Prove that recovery keys remain usable offline"),
                verifyBtn
            }
        }, Surface, Line, new Thickness(20)));

        _page.Child = new ScrollViewer { Content = body };
    }

    // --- Screen 6: Settings ---
    private void RenderSettings()
    {
        Select("Settings");
        var body = new StackPanel { Spacing = 20, Margin = new Thickness(32, 26) };
        body.Children.Add(Header("Settings", "Preferences, theme, Windows Service lifecycle, and paths."));

        // 1. Theme Configuration
        var themeGroup = new StackPanel { Spacing = 10 };
        themeGroup.Children.Add(Text("Appearance & Theme", 16, FontWeight.SemiBold, Ink));
        themeGroup.Children.Add(Text("Switch between elevated Dark Slate and Light Slate design palettes.", 12, FontWeight.Normal, Muted));

        var themeSelector = new ComboBox
        {
            ItemsSource = new[] { "System Default", "Light Slate", "Dark Slate" },
            SelectedIndex = _preferences.Current.Theme switch
            {
                AppThemePreference.Dark => 2,
                AppThemePreference.Light => 1,
                _ => 0
            },
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        themeSelector.SelectionChanged += (_, _) =>
        {
            var selectedTheme = themeSelector.SelectedIndex switch
            {
                2 => AppThemePreference.Dark,
                1 => AppThemePreference.Light,
                _ => AppThemePreference.System
            };
            _preferences.UpdateTheme(selectedTheme);
            if (selectedTheme == AppThemePreference.Dark)
            {
                DesignTokens.SetTheme(true);
                if (Avalonia.Application.Current != null)
                {
                    Avalonia.Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
                }
            }
            else if (selectedTheme == AppThemePreference.Light)
            {
                DesignTokens.SetTheme(false);
                if (Avalonia.Application.Current != null)
                {
                    Avalonia.Application.Current.RequestedThemeVariant = ThemeVariant.Light;
                }
            }
            else
            {
                if (Avalonia.Application.Current != null)
                {
                    Avalonia.Application.Current.RequestedThemeVariant = ThemeVariant.Default;
                    var isSysDark = Avalonia.Application.Current.ActualThemeVariant == ThemeVariant.Dark;
                    DesignTokens.SetTheme(isSysDark);
                }
            }
        };
        themeGroup.Children.Add(themeSelector);
        body.Children.Add(Card(themeGroup, Surface, Line, new Thickness(20)));

        // 2. Windows Background Protection
        var serviceGroup = new StackPanel { Spacing = 12 };
        serviceGroup.Children.Add(Text("Background Protection", 16, FontWeight.SemiBold, Ink));
        serviceGroup.Children.Add(Text("The Windows background service executes scheduled backups and periodic integrity verifications.", 12, FontWeight.Normal, Muted));

        var serviceRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), VerticalAlignment = VerticalAlignment.Center };
        var isRunning = _settings.IsServiceRunning;
        var statusHeadline = isRunning
            ? "● Running — Backups continue even when Fortiq is closed."
            : "○ Stopped — Scheduled backups are paused until the service is running.";

        serviceRow.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Text(statusHeadline, 14, FontWeight.SemiBold, isRunning ? Recoverable : Failure),
                Text(_installed ? "Managed by the Windows background service (NT SERVICE\\Fortiq)" : "Portable mode: no background scheduler", 12, FontWeight.Normal, Muted)
            }
        });

        if (!_settings.IsServiceRunning && _installed)
        {
            var repairServiceBtn = Primary("Repair Service");
            repairServiceBtn.IsEnabled = !_settings.IsBusy;
            repairServiceBtn.Click += async (_, _) =>
            {
                if (!await EnsurePrivilegesAsync()) return;
                await _settings.ToggleServiceAsync();
                RenderSettings();
            };
            Grid.SetColumn(repairServiceBtn, 1);
            serviceRow.Children.Add(repairServiceBtn);
        }

        serviceGroup.Children.Add(serviceRow);
        if (_settings.StatusMessage is { } statusMessage)
            serviceGroup.Children.Add(Text(statusMessage, 12, FontWeight.Normal, Failure, true));
        body.Children.Add(Card(serviceGroup, Surface, Line, new Thickness(20)));

        // 3. Storage & Folders
        var storageGroup = new StackPanel { Spacing = 12 };
        storageGroup.Children.Add(Text("Storage & Diagnostics", 16, FontWeight.SemiBold, Ink));
        storageGroup.Children.Add(DetailRow("Data Directory", _settings.DataDirectory));
        storageGroup.Children.Add(DetailRow("Logs Directory", _settings.LogsDirectory));

        var folderButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 8, 0, 0) };
        var openData = Secondary("Open Data Folder");
        openData.Click += (_, _) => _settings.OpenDataFolder();
        var openLogs = Secondary("Open Logs Folder");
        openLogs.Click += (_, _) => _settings.OpenLogsFolder();
        folderButtons.Children.Add(openData);
        folderButtons.Children.Add(openLogs);
        storageGroup.Children.Add(folderButtons);
        body.Children.Add(Card(storageGroup, Surface, Line, new Thickness(20)));

        // 4. Startup & System Tray
        var trayGroup = new StackPanel { Spacing = 12 };
        trayGroup.Children.Add(Text("Startup & System Tray", 16, FontWeight.SemiBold, Ink));
        trayGroup.Children.Add(Text("Control background monitoring and automatic launch behavior.", 12, FontWeight.Normal, Muted));

        var autostartCheck = new CheckBox
        {
            Content = "Start Fortiq automatically when signing in to Windows (starts in tray)",
            IsChecked = _settings.StartWithWindows,
            IsEnabled = OperatingSystem.IsWindows()
        };
        autostartCheck.IsCheckedChanged += (_, _) =>
        {
            var isChecked = autostartCheck.IsChecked ?? false;
            _settings.StartWithWindows = isChecked;
            _preferences.UpdateStartWithWindows(isChecked);
        };
        trayGroup.Children.Add(autostartCheck);
        trayGroup.Children.Add(Text(
            _installed
                ? "Closing this window hides the Fortiq status app. The Windows background service continues backups independently."
                : "In portable mode, closing this window exits Fortiq. Scheduled background backups require an installed service.",
            11, FontWeight.Normal, Muted, true));

        var exitAppBtn = Secondary("Exit Fortiq");
        exitAppBtn.Click += (_, _) => ExplicitExit();
        trayGroup.Children.Add(new StackPanel { Margin = new Thickness(0, 4, 0, 0), Children = { exitAppBtn } });

        body.Children.Add(Card(trayGroup, Surface, Line, new Thickness(20)));

        // 5. About Fortiq Community Edition
        var aboutGroup = new StackPanel { Spacing = 10 };
        aboutGroup.Children.Add(Text("About Fortiq Community Edition", 16, FontWeight.SemiBold, Ink));
        aboutGroup.Children.Add(Text("Open-source personal and enterprise data recovery assurance platform.", 12, FontWeight.Normal, Muted, true));
        aboutGroup.Children.Add(DetailRow("Edition", "Community Edition"));
        aboutGroup.Children.Add(DetailRow("Version", $"v{_settings.AppVersion}"));
        aboutGroup.Children.Add(DetailRow(".NET Runtime", _settings.RuntimeVersion));
        aboutGroup.Children.Add(DetailRow("License", "Open Source (Apache-2.0 / MIT)"));
        aboutGroup.Children.Add(DetailRow("Zero-Secret Architecture", "Keys & passphrases never touch disk unencrypted or leak in arguments"));

        var linksRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 8, 0, 0) };
        var openGuideBtn = Secondary("Disaster Recovery Guide");
        openGuideBtn.Click += (_, _) =>
        {
            var devGuide = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "docs", "desktop-recovery-guide.md"));
            var prodGuide = Path.Combine(AppContext.BaseDirectory, "docs", "desktop-recovery-guide.md");
            var guide = File.Exists(prodGuide) ? prodGuide : File.Exists(devGuide) ? devGuide : null;
            if (guide is not null)
            {
                try { Process.Start(new ProcessStartInfo { FileName = guide, UseShellExecute = true }); } catch { }
            }
            else
            {
                try { Process.Start(new ProcessStartInfo { FileName = "https://github.com/cybou-fr/Fortiq#readme", UseShellExecute = true }); } catch { }
            }
        };
        linksRow.Children.Add(openGuideBtn);

        var openRepoBtn = Secondary("GitHub Repository");
        openRepoBtn.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo { FileName = "https://github.com/cybou-fr/Fortiq", UseShellExecute = true }); } catch { }
        };
        linksRow.Children.Add(openRepoBtn);
        aboutGroup.Children.Add(linksRow);

        body.Children.Add(Card(aboutGroup, Surface, Line, new Thickness(20)));

        _page.Child = new ScrollViewer { Content = body };
    }

    // --- Helpers ---
    /// <summary>One sentence about the background service, answering what a person came here to ask.</summary>
    private string ServiceStatusHeadline() => _settings.ServiceStatus switch
    {
        "Running" => "Backups run on schedule, even when Fortiq is closed",
        "Stopped" => "The background service is installed but stopped",
        "Not installed" => "Automatic backups are unavailable without the Fortiq service",
        _ => "Checking the background service…"
    };

    private string ServiceStatusDetail() => _settings.ServiceStatus switch
    {
        "Running" => @"Running as NT SERVICE\Fortiq, an account with only the permissions Fortiq needs.",
        "Stopped" => "Start it to let scheduled backups run again. Until then, backups happen only while Fortiq is open.",
        "Not installed" => "No background service is installed on this PC. Fortiq can still back up and restore while it is open; scheduled backups need the service, which the installer sets up.",
        _ => string.Empty
    };

    /// <summary>What the card is showing, with no repository standing in for no check.</summary>
    private AuditChainStatus AuditStatus() =>
        _model.Repositories.Count == 0 ? AuditChainStatus.NothingRecorded : _auditChain;

    private string AuditBadgeText() => AuditStatus().Badge;

    private (IBrush Background, IBrush Foreground) AuditBadgeColors() => AuditStatus().State switch
    {
        AuditChainState.Verified => (RecoverableSurface, Recoverable),
        AuditChainState.Anomaly => (AtRiskSurface, Failure),
        AuditChainState.LegacyUnverified => (UnprovenSurface, Unproven),
        AuditChainState.Error => (UnprovenSurface, Caution),
        AuditChainState.NotChecked => (UnprovenSurface, Unproven),
        _ => (InfoSurface, Muted)
    };

    private static Grid Header(string title, string subtitle, string? action = null, Func<Task>? handler = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new StackPanel
        {
            Spacing = 3,
            Children =
            {
                Text(title, 26, FontWeight.SemiBold, Ink),
                Text(subtitle, 13, FontWeight.Normal, Muted)
            }
        });
        if (action is not null && handler is not null)
        {
            grid.Children.Add(Action(action, handler, 1, true));
        }
        return grid;
    }

    private async Task ProveAsync(RepositoryRowViewModel repository)
    {
        if (await EnsurePrivilegesAsync())
            await _model.ProveRecoveryAsync(repository, CancellationToken.None);
    }

    private async Task<bool> EnsurePrivilegesAsync()
    {
        if (!_installed || !OperatingSystem.IsWindows() || Fortiq.Platform.Windows.WindowsPrivilegeChecker.IsElevated())
            return true;

        var explanation = Text("This action requires administrator permission. Reopen Fortiq as administrator, then repeat the action. Windows will ask for your approval.", 14, FontWeight.Normal, Ink, true);
        var reopen = Primary("Reopen as administrator");
        var cancel = Secondary("Cancel");
        var dialog = new Window
        {
            Title = "Administrator permission required", Width = 480, Height = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(24), Spacing = 16,
                Children = { explanation, reopen, cancel }
            }
        };
        var launched = false;
        cancel.Click += (_, _) => dialog.Close();
        reopen.Click += (_, _) =>
        {
            try
            {
                var executable = Path.Combine(AppContext.BaseDirectory, "Fortiq.Desktop.exe");
                if (!File.Exists(executable)) throw new FileNotFoundException("The Fortiq desktop executable was not found.");
                using var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" });
                if (process is null) throw new InvalidOperationException("Windows did not start Fortiq.");
                launched = true;
                dialog.Close();
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                explanation.Text = "Fortiq was not reopened. " + error.Message;
            }
        };
        await dialog.ShowDialog(this);
        if (launched) ExplicitExit();
        return false;
    }

    private async Task ProtectAsync()
    {
        if (_installed && OperatingSystem.IsWindows() && !Fortiq.Infrastructure.Keys.WindowsTpmEnvelope.IsAvailable)
        {
            var explanation = Text(
                "Automatic backups aren't available on this PC.\n\n" +
                "Automatic Community Protection requires Windows with a TPM 2.0 security chip for device-bound unlock. " +
                "You can still use Fortiq for manual file recovery from existing recovery kits.",
                14, FontWeight.Normal, Ink, true);
            var okBtn = Primary("OK");
            var dlg = new Window
            {
                Title = "Automatic protection unavailable", Width = 480, Height = 240,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel
                {
                    Margin = new Thickness(24), Spacing = 16,
                    Children = { explanation, okBtn }
                }
            };
            okBtn.Click += (_, _) => dlg.Close();
            await dlg.ShowDialog(this);
            return;
        }

        if (_wizard is null) return;

        // Installed mode hands provisioning to the service, and the service refuses a caller who does
        // not hold its privileges. Rather than tell the person to reopen the whole application as an
        // administrator and start again - which leaves a backup client running with full rights for
        // as long as it stays open - Fortiq elevates this one operation: Windows prompts, a second
        // instance shows only the wizard, and it exits when the wizard is done.
        if (_installed && OperatingSystem.IsWindows() && !WindowsPrivilegeChecker.IsElevated())
        {
            await ProtectElevatedAsync();
            return;
        }

        await new ProtectRepositoryWindow(_wizard()).ShowDialog(this);
        await RefreshAsync();
    }

    /// <summary>Runs the protection wizard in an elevated instance and waits for it to finish.</summary>
    private async Task ProtectElevatedAsync()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "Fortiq.Desktop.exe");
        if (!File.Exists(executable))
        {
            await ShowNoticeAsync(
                "Fortiq could not be started",
                $"'{executable}' is missing, so the protection wizard cannot be opened with the " +
                "permissions it needs. Reinstall Fortiq.");
            return;
        }

        try
        {
            using var elevated = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                ArgumentList = { "--protect" }
            });

            if (elevated is null)
            {
                return;
            }

            await elevated.WaitForExitAsync();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The person declined the prompt, which is an answer and not a fault. Saying so is
            // better than a silent no-op that looks like a broken button.
            await ShowNoticeAsync(
                "Permission not granted",
                "Protecting a folder needs administrator approval, because the background service " +
                "reads the folder and creates a key bound to this machine. Nothing was changed.");
            return;
        }

        // Whatever the elevated pass did, this window's picture of the machine is now out of date.
        await RefreshAsync();
    }

    private async Task ShowNoticeAsync(string title, string message)
    {
        var ok = Primary("OK");
        var dialog = new Window
        {
            Title = title,
            Width = 480,
            Height = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = CanvasBackground,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 16,
                Children = { Text(message, 14, FontWeight.Normal, Ink, true), ok }
            }
        };

        ok.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    /// <summary>The wizard this window would open, for the elevated pass to show on its own.</summary>
    internal ProtectRepositoryViewModel? CreateWizard() => _wizard?.Invoke();

    private async Task RefreshAsync() => await _model.RefreshAsync(CancellationToken.None);

    private static Button Action(string label, Func<Task> action, int column, bool primary = false)
    {
        var button = primary ? Primary(label) : Secondary(label);
        button.Click += async (_, _) => await action();
        Grid.SetColumn(button, column);
        return button;
    }

    // Shared with the wizard and the installer, so the hover behaviour is decided once. Painting a
    // colour onto the button alone left the Fluent theme free to repaint the presenter underneath on
    // hover, and the button disappeared under the cursor.
    private static Button Primary(string label)
    {
        var button = FortiqButton.Primary(label);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        return button;
    }

    private static Button Secondary(string label)
    {
        var button = FortiqButton.Secondary(label);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        return button;
    }

    private static Button Tab(string label, bool selected, Action action)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(16, 8),
            Background = selected ? InfoSurface : Brushes.Transparent,
            Foreground = selected ? Brand : Muted,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static Border Card(Control child, IBrush? background = null, IBrush? border = null, Thickness? padding = null) => new()
    {
        Child = child,
        Background = background ?? Surface,
        BorderBrush = border ?? Line,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = padding ?? new Thickness(16)
    };

    private static StackPanel Check(string label) => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 9,
        Children =
        {
            new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = Recoverable,
                VerticalAlignment = VerticalAlignment.Center
            },
            Text(label, 13, FontWeight.Normal, Ink)
        }
    };

    private static Grid DetailRow(string label, string value)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*"), ColumnSpacing = 12 };
        row.Children.Add(Text(label, 12, FontWeight.SemiBold, Muted));
        row.Children.Add(At(Text(value, 12, FontWeight.Normal, Ink, true), 1));
        return row;
    }

    private static Grid TableRow(string first, string second, string third, string fourth, bool heading = false)
    {
        var weight = heading ? FontWeight.SemiBold : FontWeight.Normal;
        var color = heading ? Muted : Ink;
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1.4*,1.1*,1.2*,Auto"),
            ColumnSpacing = 12,
            Margin = new Thickness(0, 8),
            MinHeight = 30
        };
        row.Children.Add(Text(first, heading ? 11 : 13, weight, color, true));
        row.Children.Add(At(Text(second, heading ? 11 : 12, weight, color, true), 1));
        row.Children.Add(At(Text(third, heading ? 11 : 12, weight, color, true), 2));
        var statusColor = !heading && fourth is "Recoverable" or "Completed" or "Healthy" or "Verified" ? Recoverable : !heading ? Unproven : color;
        row.Children.Add(At(Text(fourth, heading ? 11 : 12, heading ? weight : FontWeight.SemiBold, statusColor), 3));
        return row;
    }

    private static TextBlock Text(string value, double size, FontWeight weight, IBrush color, bool wrap = false) => new()
    {
        Text = value,
        FontSize = size,
        FontWeight = weight,
        Foreground = color,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static T At<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
    private static void Add(Grid grid, Control control, int column) { Grid.SetColumn(control, column); grid.Children.Add(control); }

    private DateTimeOffset? Latest(Func<RepositoryFacts, DateTimeOffset?> selector) =>
        _model.Repositories.Select(x => selector(x.Health.Facts)).Where(x => x is not null).OrderByDescending(x => x).FirstOrDefault();

    private static string Relative(DateTimeOffset? value)
    {
        if (value is null) return "Never";
        var age = DateTimeOffset.UtcNow - value.Value;
        if (age < TimeSpan.FromMinutes(1)) return "Just now";
        if (age < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)age.TotalMinutes)} min ago";
        if (age < TimeSpan.FromDays(1)) return $"{Math.Max(1, (int)age.TotalHours)} hr ago";
        return $"{Math.Max(1, (int)age.TotalDays)} days ago";
    }

    private static string Absolute(DateTimeOffset? value) =>
        value is null ? "Not available" : value.Value.LocalDateTime.ToString("g", System.Globalization.CultureInfo.CurrentCulture);

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void OpenRecoveryFromTray()
    {
        ShowFromTray();
        if (_fileRecovery is not null)
        {
            _ = new FileRecoveryWindow(_fileRecovery()).ShowDialog(this);
        }
    }

    public void ExplicitExit()
    {
        _isExplicitExit = true;
        _disposeTray?.Invoke();
        Close();
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}
