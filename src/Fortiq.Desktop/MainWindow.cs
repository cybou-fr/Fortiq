using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Fortiq.Application;
using Fortiq.Desktop.Controls;
using Fortiq.Assistant;
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
    private readonly Func<string, string, SourceSettingsViewModel>? _sourceSettings;
    private readonly Func<Task<IReadOnlyList<ReceiptEvent>>>? _history;
    private IReadOnlyList<ReceiptEvent>? _historyEvents;
    private bool _historyLoading;
    private readonly bool _installed;
    private readonly RepositoriesViewModel _model;
    private readonly SettingsViewModel _settings;
    private readonly Func<ProtectRepositoryViewModel>? _wizard;
    private readonly Func<AssistantViewModel>? _assistantFactory;

    /// <summary>
    /// Kept for the life of the window, not built per visit.
    /// </summary>
    /// <remarks>
    /// It owns a child process holding a gigabyte of weights. Rebuilding it every time somebody
    /// clicks Assistant would start a second model, and abandoning the first would leave a process
    /// nobody can see and nobody would think to look for.
    /// </remarks>
    private AssistantViewModel? _assistant;
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
    private RepositoryRowViewModel? _recoverySource;
    private RepositoryRowViewModel? _kitSource;
    private AuditChainStatus _auditChain = AuditChainStatus.NotChecked;
    private bool _auditLedgerVerifying;
    private readonly Action<string>? _updateTrayStatus;
    private readonly Action? _disposeTray;
    private readonly DesktopPreferencesStore _preferences;
    private bool _isExplicitExit;

    /// <summary>
    /// True while a raised instance is doing the work. Busy on the view model does not cover it: the
    /// operation is happening in another process, so this window stays idle and its buttons stay live
    /// unless something says otherwise.
    /// </summary>
    private bool _elevating;

    public MainWindow(
        RepositoriesViewModel model,
        Func<ProtectRepositoryViewModel>? wizard = null,
        SettingsViewModel? settings = null, bool installed = false, Func<FileRecoveryViewModel>? fileRecovery = null,
        Func<string, string, SourceSettingsViewModel>? sourceSettings = null,
        Func<Task<IReadOnlyList<ReceiptEvent>>>? history = null,
        Func<AssistantViewModel>? assistant = null)
    {
        _assistantFactory = assistant;
        _sourceSettings = sourceSettings;
        _history = history;
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
            // Not while something is running. A refresh clears the last failure, and a poll that
            // landed between somebody reading a failed backup and acting on it would take the message
            // off the screen for them.
            if (!_model.Busy && !_elevating) await RefreshAsync();
        };
        Opened += (_, _) => refreshTimer.Start();

        var trayIcon = new FortiqTrayIcon(ShowFromTray, ExplicitExit, OpenRecoveryFromTray, () => _ = RefreshAsync());
        _updateTrayStatus = trayIcon.UpdateStatus;
        _disposeTray = trayIcon.Dispose;

        Closed += (_, _) =>
        {
            refreshTimer.Stop();
            StopAssistant();
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
            StopAssistant();

            // ProcessExit does not run on the UI thread, and a TrayIcon may only be touched from it.
            // Disposing directly threw every time the application closed - at the very end, where
            // nobody was looking, but thrown all the same. Closed already disposes it, so this only
            // has to catch the case where the process ends without the window closing, and it has to
            // ask the UI thread to do it.
            try
            {
                Avalonia.Threading.Dispatcher.UIThread.Invoke(() => _disposeTray());
            }
            catch (Exception error) when (error is InvalidOperationException or TaskCanceledException or ObjectDisposedException)
            {
                // The dispatcher has already stopped, which means the tray icon went with it.
            }
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
        // Four destinations named after what somebody wants, not after how Fortiq is built. The old
        // rail asked people to know the difference between Recovery, Recovery Kit and restoring
        // files before they could pick one - three entries for one intention, at the moment they are
        // least able to study an architecture. "Backups" went the same way: the noun says what Fortiq
        // stores, and "Protected folders" says what they gave it.
        menu.Children.Add(Nav("Home", RenderHome, "\uE80F"));
        menu.Children.Add(Nav("Protected folders", RenderFolders, "\uE8B7"));
        menu.Children.Add(Nav("Restore", RenderRestore, "\uE777"));
        menu.Children.Add(Nav("Activity", RenderActivity, "\uE81C"));
        menu.Children.Add(Nav("Assistant", RenderAssistant, "\uE8BD"));
        Grid.SetRow(menu, 1);
        rail.Children.Add(menu);

        var bottomStack = new StackPanel { Spacing = 6 };
        bottomStack.Children.Add(Nav("Settings", RenderSettings, "\uE713"));

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

    /// <summary>
    /// One rail entry: a glyph, then the word.
    /// </summary>
    /// <remarks>
    /// The glyphs come from Segoe Fluent Icons, which every supported Windows has, and each one is
    /// paired with its word rather than replacing it - an icon nobody can name is a puzzle, and this
    /// rail is read by people who are already having a bad day. They are decoration to a screen
    /// reader, which announces the label and the current-screen state as before.
    /// </remarks>
    private Button Nav(string label, Action action, string glyph)
    {
        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = glyph,
                        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                        FontSize = 15,
                        VerticalAlignment = VerticalAlignment.Center
                    }.Decorative(),
                    new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }
                }
            },
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

    private void ApplyNavigationStyle(Button button, bool selected)
    {
        button.Background = selected ? InfoSurface : Brushes.Transparent;
        button.Foreground = selected ? Brand : Ink;
        button.FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal;

        // Which item is current was said in blue and semibold and in no other way, so somebody using a
        // screen reader - or looking at this in the greys some people see it in - heard five identical
        // buttons and no answer to "where am I". The label is read from the rail's own record rather
        // than from the button's content, which is a panel now.
        var label = _navigation.FirstOrDefault(entry => ReferenceEquals(entry.Value, button)).Key;
        if (label is not null)
        {
            button.Named(selected ? $"{label}, current screen" : label);
        }
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

        _statusDot.Decorative();
        _statusDot.Background = brush;
        _statusLabel.Text = label;
        _updateTrayStatus?.Invoke(label);
    }

    /// <summary>
    /// The last failure, where the person who caused it is looking.
    /// </summary>
    /// <remarks>
    /// The view model keeps a failed operation's reason deliberately, putting it back after the
    /// refresh so it survives. Nothing displayed it: the dashboard showed the failure text only while
    /// the health report itself was unreadable, so a backup or a drill that failed reported the
    /// failure to a screen that never drew it, and the button looked as though it had worked.
    /// </remarks>
    private Border? NoticeBanner()
    {
        if (_model.Failure is not { Length: > 0 } failure)
        {
            return null;
        }

        var dismiss = Secondary("Dismiss").Named("Dismiss this failure message");
        dismiss.Padding = new Thickness(10, 4);
        dismiss.FontSize = 11;
        dismiss.VerticalAlignment = VerticalAlignment.Top;
        dismiss.Click += (_, _) => { _model.ClearFailure(); RenderActive(); };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        grid.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Text("That did not work", 14, FontWeight.SemiBold, Failure),
                Text(failure, 12, FontWeight.Normal, Ink, true)
            }
        });
        Grid.SetColumn(dismiss, 1);
        grid.Children.Add(dismiss);

        return Card(grid, AtRiskSurface, AtRiskLine, new Thickness(18, 14));
    }

    /// <summary>What is running right now, said out loud, so a disabled screen is not a hung one.</summary>
    private Border? ActivityBanner()
    {
        // The elevated case first: the work is happening in another process, so nothing on this view
        // model is busy and only this window knows anything is going on at all.
        var running = _elevating
            ? "Running with administrator permission…"
            : _model.Activity;

        if (running is not { Length: > 0 })
        {
            return null;
        }

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new ProgressBar { IsIndeterminate = true, Width = 120, VerticalAlignment = VerticalAlignment.Center }.Named(running),
                Text(running, 13, FontWeight.SemiBold, Ink)
            }
        };

        // Only what this window is running can be stopped from here. The elevated pass has its own
        // window with its own button, and offering a dead one beside it would be worse than none.
        if (_model.CanCancel)
        {
            var stop = Secondary("Stop").Named("Stop the operation that is running");
            stop.Padding = new Thickness(12, 4);
            stop.FontSize = 11;
            stop.Click += (_, _) => { _model.CancelRunning(); RenderActive(); };
            bar.Children.Add(stop);
        }

        return Card(bar, InfoSurface, InfoLine, new Thickness(18, 12));
    }

    /// <summary>Puts the running and failed banners at the top of a screen, when there are any.</summary>
    private void AddBanners(StackPanel body)
    {
        if (ActivityBanner() is { } activity)
        {
            body.Children.Add(activity);
        }

        if (NoticeBanner() is { } notice)
        {
            body.Children.Add(notice);
        }
    }

    private void RenderActive()
    {
        UpdateSidebarStatus();

        if (_activeSection == "Protected folders") RenderFolders();
        else if (_activeSection == "Restore") RenderRestore();
        else if (_activeSection == "Activity") RenderActivity();
        else if (_activeSection == "Assistant") RenderAssistant();
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
        AddBanners(body);

        var (mode, headline, desc, actionText) = ResolveHeroState();
        body.Children.Add(new HeroHealthBanner(mode, headline, desc, actionText, HeroAction(mode)));

        body.Children.Add(MetricsGrid());
        body.Children.Add(RepositoriesSummaryCard());

        _page.Child = new ScrollViewer { Content = body };
    }

    /// <summary>
    /// What the banner's button does, chosen with the words written on it.
    /// </summary>
    /// <remarks>
    /// Every state used to hand the banner the same callback, so a button reading "Prove recovery" or
    /// "Review" refreshed the screen and nothing else. On a product whose subject is whether it can be
    /// believed, a control that does not do what it says is worse than one that is missing: somebody
    /// clicks "Prove recovery", sees the page redraw, and concludes the drill ran.
    /// </remarks>
    private Func<Task> HeroAction(HeroStatusMode mode) => mode switch
    {
        HeroStatusMode.ZeroState => ProtectAsync,

        // To the list, where the source with the problem is named and its own buttons are beside it.
        HeroStatusMode.AtRisk when _model.State is not (HealthStoreState.Corrupt or HealthStoreState.Stale)
            => () => { RenderFolders(); return Task.CompletedTask; },

        // To the drill, on the source that needs one - not to the first source in the list.
        HeroStatusMode.Unproven => () =>
        {
            _recoverySource = _model.Repositories.FirstOrDefault(row => row.Health.Verdict == HealthVerdict.Unproven)
                ?? _model.Repositories.FirstOrDefault();
            RenderFolders();
            return Task.CompletedTask;
        },

        // Corrupt and stale both say the report itself is the problem, and refreshing is the answer.
        _ => RefreshAsync
    };

    private (HeroStatusMode Mode, string Headline, string Desc, string Action) ResolveHeroState()
    {
        if (_model.State is HealthStoreState.NotInitialized or HealthStoreState.Empty || _model.Repositories.Count == 0)
        {
            return (HeroStatusMode.ZeroState,
                "Protect what matters before you need it",
                "Nothing on this PC is backed up yet. Choose a folder, and Fortiq will keep it and prove it can bring it back.",
                "Protect a folder");
        }

        if (_model.State == HealthStoreState.Corrupt)
        {
            return (HeroStatusMode.AtRisk,
                "Protection status temporarily unavailable",
                _model.Failure ?? "Could not read health evidence. Check local service.",
                "Refresh status");
        }

        if (_model.State == HealthStoreState.Stale)
        {
            return (HeroStatusMode.Unproven,
                "Protection status is out of date",
                "The health report has not been refreshed recently. Verify that the Fortiq service is running.",
                "Refresh status");
        }

        if (_model.Repositories.Any(r => r.Health.Verdict == HealthVerdict.AtRisk))
        {
            return (HeroStatusMode.AtRisk,
                "Something may not be recoverable today",
                "One or more protected sources report integrity or verification issues.",
                "Review the sources");
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
            "Refresh status");
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
            // A fixed action column, not Auto. Each row is its own Grid, so an Auto column is sized by
            // that row alone: the moment a healthy source stopped showing a second button, its first
            // one slid right and no longer lined up with the rows beneath it. A reserved width keeps
            // every "Back up now" in the same place whether or not anything follows it.
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("1.5*,2*,300"), ColumnSpacing = 12 };
            row.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    Text(repository.Title, 14, FontWeight.SemiBold, Ink),
                    // The path, not the identifier. A row whose two lines say the same UUID tells
                    // somebody nothing twice.
                    Text(repository.SourcePath ?? repository.Health.RepositoryId, 11, FontWeight.Normal, Muted)
                }
            });
            // Painted only where it means something. The dashboard used the same grey for the row that
            // reports a repository nobody could recover from today as for the ones that are fine, so
            // the hero and the tiles carried the colour and the line naming the actual problem did
            // not. A list where every row is painted is a list where the colour has stopped meaning
            // anything, which is why the healthy ones stay quiet.
            var tone = repository.Health.Verdict switch
            {
                HealthVerdict.AtRisk => Failure,
                HealthVerdict.Unproven => Caution,
                _ => Muted
            };
            var weight = repository.Health.Verdict == HealthVerdict.Recoverable ? FontWeight.Normal : FontWeight.SemiBold;
            row.Children.Add(At(Text(repository.Summary, 12, weight, tone, true), 1));

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            if (_model.CanBackupNow)
            {
                var backup = Secondary("Back up now").Named($"Back up {repository.Title} now");
                backup.IsEnabled = !_model.Busy && !_elevating;
                backup.Click += async (_, _) => await BackupNowAsync(repository);
                actions.Children.Add(backup);
            }

            // A button reading "Proven" was a status wearing a button's clothes - and an enabled one,
            // so pressing what looked like a badge started a restore drill. The state is already in
            // the line to the left; what belongs here is an action, and only where there is one to
            // take. This is also what the Backups list has always done.
            if (repository.Health.Verdict != HealthVerdict.Recoverable)
            {
                var prove = Secondary("Prove recovery").Named($"Prove recovery for {repository.Title}");
                prove.IsEnabled = repository.CanProveRecovery && !_model.Busy && !_elevating;
                prove.Click += async (_, _) => await ProveAsync(repository);
                actions.Children.Add(prove);
            }

            row.Children.Add(At(actions, 2));

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

    /// <summary>The folders somebody gave Fortiq, and what can be done to each of them.</summary>
    private void RenderFolders()
    {
        Select("Protected folders");
        var body = new StackPanel { Spacing = 18, Margin = new Thickness(32, 26) };
        body.Children.Add(Header(
            "Protected folders",
            "What Fortiq is keeping, and whether each one has been shown to come back.",
            "Protect a folder",
            ProtectAsync));
        AddBanners(body);

        body.Children.Add(BackupSourcesView());

        // Proving recovery is something done to a source, so it lives with the sources rather than on
        // a screen of its own that people had to know to look for.
        _recoverySource = _model.Repositories.FirstOrDefault(item => item.Health.RepositoryId == _recoverySource?.Health.RepositoryId)
            ?? _model.Repositories.FirstOrDefault();

        if (_recoverySource is { } source)
        {
            if (_model.Repositories.Count > 1)
            {
                body.Children.Add(SourceSelector(
                    "Recovery evidence for",
                    "Protected source to prove recovery for",
                    source,
                    picked => { _recoverySource = picked; RenderFolders(); }));
            }

            body.Children.Add(RecoveryHero(source));
            body.Children.Add(RecoveryDetails(source));
        }

        _page.Child = new ScrollViewer { Content = body };
    }

    /// <summary>Everything Fortiq has done, and whether that record can be trusted.</summary>
    private void RenderActivity()
    {
        Select("Activity");
        var body = new StackPanel { Spacing = 18, Margin = new Thickness(32, 26) };
        body.Children.Add(Header("Activity", "Every backup, check, recovery drill and clean-up this PC has run."));
        AddBanners(body);

        // The ledger card belongs beside the history rather than above the source list: it says
        // whether the history below it can be believed, which is a statement about these rows.
        body.Children.Add(AuditLedgerCard());
        body.Children.Add(BackupHistoryView());
        _page.Child = new ScrollViewer { Content = body };
    }

    /// <summary>A labelled picker over the protected sources.</summary>
    private StackPanel SourceSelector(string label, string automationName, RepositoryRowViewModel current, Action<RepositoryRowViewModel> pick)
    {
        var selector = new ComboBox
        {
            ItemsSource = _model.Repositories.Select(item => item.Title).ToArray(),
            SelectedIndex = Math.Max(0, _model.Repositories.IndexOf(current)),
            MinWidth = 260,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        selector.Named(automationName);
        selector.SelectionChanged += (_, _) =>
        {
            if (selector.SelectedIndex >= 0)
            {
                pick(_model.Repositories[selector.SelectedIndex]);
            }
        };

        return new StackPanel { Spacing = 6, Children = { Text(label, 12, FontWeight.SemiBold, Ink), selector } };
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
            // Was "SHA-256 Chained (ADR-007)". The decision record number means nothing to anybody
            // outside this repository, and naming the hash function does not tell a person what the
            // badge is claiming. What it is claiming is that the history cannot be edited quietly.
            Child = Text("Tamper-evident history", 11, FontWeight.SemiBold, Brand)
        };
        badgeStack.Children.Add(shaTag);

        leftStack.Children.Add(badgeStack);

        var descText = Text(AuditStatus().Detail, 12, FontWeight.Normal, Muted, wrap: true);
        leftStack.Children.Add(descText);

        grid.Children.Add(leftStack);

        var verifyBtn = Secondary(_auditLedgerVerifying ? "Verifying…" : "Verify chain")
            .Named("Verify that the recorded history has not been edited");
        verifyBtn.IsEnabled = !_auditLedgerVerifying;
        verifyBtn.Click += async (_, _) =>
        {
            _auditLedgerVerifying = true;
            RenderActivity();
            try
            {
                var dir = FortiqStatePaths.Resolve().Receipts;
                _auditChain = AuditChainStatus.From(await AuditLedgerVerifier.VerifyLedgerAsync(dir));
            }
            catch (Exception ex)
            {
                _auditChain = AuditChainStatus.Failed(PlainFailure.Describe(ex));
            }
            finally
            {
                _auditLedgerVerifying = false;
                RenderActivity();
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
                    Text("Nothing is backed up yet", 18, FontWeight.SemiBold, Ink),
                    Text("Pick a folder that would hurt to lose. Fortiq keeps it, and checks that it comes back.", 13, FontWeight.Normal, Muted),
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
                    Text(repository.SourcePath ?? repository.Health.RepositoryId, 10, FontWeight.Normal, Muted)
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

            // Both actions live in one cell. A column per button left the row's shape depending on
            // which buttons that row happened to show, so the columns stopped lining up down the list.
            var rowActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

            if (_model.CanBackupNow)
            {
                var backupBtn = Secondary("Back up now").Named($"Back up {repository.Title} now");
                backupBtn.Padding = new Thickness(10, 4);
                backupBtn.FontSize = 11;
                backupBtn.IsEnabled = !_model.Busy && !_elevating;
                backupBtn.Click += async (_, _) => await BackupNowAsync(repository);
                rowActions.Children.Add(backupBtn);
            }

            if (repository.CanProveRecovery && repository.Health.Verdict != HealthVerdict.Recoverable)
            {
                var proveBtn = Secondary("Prove").Named($"Prove recovery for {repository.Title}");
                proveBtn.Padding = new Thickness(10, 4);
                proveBtn.FontSize = 11;
                proveBtn.IsEnabled = !_model.Busy && !_elevating;
                proveBtn.Click += async (_, _) => await ProveAsync(repository);
                rowActions.Children.Add(proveBtn);
            }

            if (_sourceSettings is not null)
            {
                var settingsBtn = Secondary("Settings").Named($"Settings for {repository.Title}");
                settingsBtn.Padding = new Thickness(10, 4);
                settingsBtn.FontSize = 11;
                settingsBtn.IsEnabled = !_model.Busy && !_elevating;
                settingsBtn.Click += async (_, _) => await OpenSourceSettingsAsync(repository);
                rowActions.Children.Add(settingsBtn);
            }

            if (rowActions.Children.Count > 0)
            {
                row.Children.Add(At(rowActions, 4));
            }

            list.Children.Add(row);
        }
        return Card(list, Surface, Line, new Thickness(20));
    }

    /// <summary>
    /// The history the receipts actually record, rather than a summary of the newest of each kind.
    /// </summary>
    /// <remarks>
    /// What stood here was three rows per repository - last backup, last check, last proven restore -
    /// built from the same three timestamps shown on the dashboard above it, each one labelled with a
    /// result that was true by construction ("Completed", "Healthy", "Verified"). A failed drill, a
    /// backup that could not reach its repository, a retention run that deleted snapshots: none of it
    /// appeared, although every one of those events had been written to disk and chained.
    ///
    /// This reads the receipts. A failure is a row, and the row says so.
    /// </remarks>
    private Border BackupHistoryView()
    {
        if (_history is null)
        {
            return Card(Text("This build has no receipt reader wired to the history view.", 13, FontWeight.Normal, Muted, true),
                Surface, Line, new Thickness(24));
        }

        if (_historyEvents is null)
        {
            if (!_historyLoading)
            {
                _historyLoading = true;
                _ = LoadHistoryAsync();
            }

            return Card(Text("Reading the receipts…", 13, FontWeight.Normal, Muted), Surface, Line, new Thickness(24));
        }

        if (_historyEvents.Count == 0)
        {
            return Card(new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    Text("Nothing has run yet", 18, FontWeight.SemiBold, Ink),
                    Text("Every backup, integrity check, recovery drill and retention run is recorded here once the first one has run.", 13, FontWeight.Normal, Muted, true)
                }
            }, Surface, Line, new Thickness(24));
        }

        var titles = _model.Repositories.ToDictionary(
            repository => repository.Health.RepositoryId,
            repository => repository.Title,
            StringComparer.OrdinalIgnoreCase);

        var list = new StackPanel { Spacing = 4 };
        list.Children.Add(TableRow("Time", "Source", "Operation", "Result", true));

        foreach (var entry in _historyEvents)
        {
            var source = titles.TryGetValue(entry.RepositoryId, out var title) ? title : entry.RepositoryId;
            list.Children.Add(TableRow(
                Relative(entry.CompletedAt),
                source,
                OperationName(entry.Operation),
                entry.Succeeded ? "Succeeded" : "Failed"));

            if (!entry.Succeeded && entry.Detail is { Length: > 0 } detail)
            {
                var reason = Text(detail, 11, FontWeight.Normal, Muted, true);
                reason.Margin = new Thickness(0, 0, 0, 6);
                list.Children.Add(reason);
            }
        }

        var unverifiable = _historyEvents.Count(entry => !entry.Verifiable);
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(list);

        if (unverifiable > 0)
        {
            // Shown rather than hidden, and never counted as evidence: a receipt written before the
            // chained schema carries no hash, so a file claiming a restore succeeded cannot be told
            // apart from one somebody wrote.
            body.Children.Add(Text(
                $"{unverifiable} of these entries were written before Fortiq chained its receipts. They are history, "
                + "not evidence: nothing about them can be checked. A fresh backup and drill replace them.",
                11, FontWeight.Normal, Caution, true));
        }

        return Card(body, Surface, Line, new Thickness(20));
    }

    private async Task LoadHistoryAsync()
    {
        try
        {
            _historyEvents = await _history!();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // An unreadable receipt directory is worth saying out loud on the screen whose subject is
            // the receipts.
            _historyEvents = [];
            _model.ReportReadFailure(PlainFailure.Describe(error));
        }
        finally
        {
            _historyLoading = false;
            if (_activeSection == "Activity")
            {
                RenderActivity();
            }
        }
    }

    /// <summary>The operation as somebody who did not write the receipt would name it.</summary>
    private static string OperationName(string operation) => operation switch
    {
        "backup" => "Backup",
        "check" => "Integrity check",
        "restoreProof" => "Recovery drill",
        "restore" => "Restore",
        "retention" => "Retention",
        _ => operation
    };

    // --- Screen 4: Recovery Proof ---
    private Border RecoveryHero(RepositoryRowViewModel repository)
    {
        var proven = repository.Health.Facts.LastProvenRestoreAt;
        var current = repository.Health.Verdict == HealthVerdict.Recoverable;
        var tone = current ? RecoverableSurface : UnprovenSurface;
        var accent = current ? Recoverable : Unproven;

        var run = Primary(_model.Busy ? "Running restore drill…" : "Run recovery proof now");
        run.IsEnabled = repository.CanProveRecovery && !_model.Busy && !_elevating;
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
        // It showed repository.Title, which was the schedule id - so a field labelled Source Path
        // displayed a UUID and called it a path.
        details.Children.Add(DetailRow("Source Path", repository.SourcePath ?? "Not recorded in this report"));
        details.Children.Add(DetailRow("Last Backup", Absolute(facts.LastBackupAt)));
        details.Children.Add(DetailRow("Integrity Check", Absolute(facts.LastHealthyCheckAt)));
        details.Children.Add(DetailRow("Proven Restore", Absolute(facts.LastProvenRestoreAt)));
        details.Children.Add(DetailRow("Evidence Match", facts.LastProvenRestoreAt is null ? "Pending drill" : "Restore completed and restored bytes were reconciled on disk."));

        return Card(details, Surface, Line, new Thickness(20));
    }

    /// <summary>
    /// One place to get files back, whichever way somebody has to do it.
    /// </summary>
    /// <remarks>
    /// This was three destinations - Recovery, Recovery Kit, and a button inside the first of them -
    /// and telling them apart needed the product's own architecture in your head. Somebody who has
    /// just lost a file is the last person who should have to learn it. Both ways of getting data
    /// back are here, in the order they are needed: from this PC, where Fortiq already knows where
    /// everything is, and from a kit and 24 words, which is what works on a machine that has never
    /// had Fortiq on it.
    ///
    /// Proving recovery is not here. It restores nothing anybody keeps; it is a test of a source, and
    /// it lives with the sources.
    /// </remarks>
    private void RenderRestore()
    {
        Select("Restore");
        _kitSource = _model.Repositories.FirstOrDefault(item => item.Health.RepositoryId == _kitSource?.Health.RepositoryId)
            ?? _model.Repositories.FirstOrDefault();

        var body = new StackPanel { Spacing = 18, Margin = new Thickness(32, 26) };
        body.Children.Add(Header("Restore", "Getting your files back, on this PC or on a machine that has never had Fortiq."));
        AddBanners(body);

        body.Children.Add(RestoreFilesCard());
        body.Children.Add(EmergencyRecoveryCard());

        _page.Child = new ScrollViewer { Content = body };
    }

    /// <summary>Getting files back with the kit, which is what the file recovery window does.</summary>
    private Border RestoreFilesCard()
    {
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(Text("Restore files", 16, FontWeight.SemiBold, Ink));
        body.Children.Add(Text(
            "Pick a backup by the date it was taken and write its files into a new folder. Nothing you "
            + "already have is overwritten: Fortiq refuses a destination that is not empty.",
            12, FontWeight.Normal, Muted, true));

        if (_fileRecovery is not null)
        {
            var restoreFiles = Primary("Restore files").Named("Restore files from a backup");
            restoreFiles.Click += async (_, _) => await new FileRecoveryWindow(_fileRecovery()).ShowDialog(this);
            body.Children.Add(restoreFiles);
        }
        else
        {
            body.Children.Add(Text("This build has no file recovery window wired up.", 12, FontWeight.Normal, Caution, true));
        }

        return Card(body, Surface, Line, new Thickness(20));
    }

    /// <summary>
    /// What it takes to open a repository somewhere else, and the one thing Fortiq cannot give back.
    /// </summary>
    /// <remarks>
    /// The sentence about Fortiq not holding the 24 words is the most important thing this product
    /// says about itself, so it stays on a screen somebody reaches in one click rather than being
    /// filed under a source.
    /// </remarks>
    private Border EmergencyRecoveryCard()
    {
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(Text("Recovery on another computer", 16, FontWeight.SemiBold, Ink));
        body.Children.Add(Text(
            "If this PC is gone, your backups are still openable - by anybody holding these three things, "
            + "and by nobody else.", 12, FontWeight.Normal, Muted, true));

        body.Children.Add(Check("The repository - the folder, drive or bucket holding the backups"));
        body.Children.Add(Check("The recovery kit folder, copied onto that machine"));
        body.Children.Add(Check("Your 24 words, typed in from paper"));

        body.Children.Add(Text(
            "Fortiq does not have your 24 words. They were shown once, while the folder was being "
            + "protected, and were never stored - not on this PC, not by Fortiq. That is what makes the "
            + "backups yours: nobody else can open them. It also means nobody can give them back to you.",
            12, FontWeight.SemiBold, Caution, true));

        body.Children.Add(Text(
            "The recover folder in the Fortiq package does this without installing anything, and "
            + "RECOVERY-GUIDE.md walks through it.", 12, FontWeight.Normal, Muted, true));

        if (_kitSource is { } source)
        {
            if (_model.Repositories.Count > 1)
            {
                body.Children.Add(SourceSelector(
                    "Recovery kit for",
                    "Protected source to show the recovery kit for",
                    source,
                    picked => { _kitSource = picked; RenderRestore(); }));
            }

            var present = source.Health.Facts.KitPresent;
            body.Children.Add(Text(
                present
                    // "Found", not "verified". Fortiq has seen the files; whether they open is what a
                    // recovery drill answers, and that lives with the sources.
                    ? $"Recovery kit found for {source.Title}. Keep a copy of it somewhere other than this PC."
                    : $"The recovery kit for {source.Title} is missing. Without it this repository cannot be "
                        + "opened on another machine. Protect the folder again to write a new one.",
                12, FontWeight.SemiBold, present ? Recoverable : Failure, true));
        }

        return Card(body, Surface, Line, new Thickness(20));
    }

    /// <summary>
    /// The assistant: a question, and what a local model said about it.
    /// </summary>
    /// <remarks>
    /// It opens with the questions this machine actually raises rather than an empty box. A blank
    /// prompt asks somebody to work out what an assistant is for while they are already worried
    /// about their data; a button saying "Why is Documents at risk?" is the question they came with.
    ///
    /// What it can and cannot do is stated on the screen, not in a document. The claim that nothing
    /// leaves the PC is the main reason anybody would use this at all, and the fact that it cannot
    /// change anything is what makes it safe to ignore.
    /// </remarks>
    private void RenderAssistant()
    {
        Select("Assistant");
        var body = new StackPanel { Spacing = 18, Margin = new Thickness(32, 26) };
        body.Children.Add(Header("Assistant", "Explains what Fortiq did, in plain words."));

        if (_assistantFactory is null)
        {
            body.Children.Add(Card(Text(
                "This copy of Fortiq was built without an assistant.",
                13, FontWeight.Normal, Muted, wrap: true)));
            _page.Child = new ScrollViewer { Content = body };
            return;
        }

        var model = _assistant ??= _assistantFactory();
        model.Suggestions = AssistantSuggestions();

        body.Children.Add(Card(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Text("Runs on this PC.", 13, FontWeight.SemiBold, Ink),
                Text(
                    "Nothing you type and nothing it reads is sent anywhere. It is never given your "
                    + "recovery phrase or your passwords, and it cannot start, stop or delete anything - "
                    + "it only explains.",
                    12, FontWeight.Normal, Muted, wrap: true)
            }
        }, InfoSurface, Brand));

        if (model.Suggestions.Count > 0)
        {
            var chips = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
            foreach (var suggestion in model.Suggestions)
            {
                var chip = Secondary(suggestion.Question).Named(suggestion.Question);
                chip.IsEnabled = !model.Busy;
                var captured = suggestion;
                chip.Click += async (_, _) =>
                {
                    await model.AskAsync(captured, CancellationToken.None);
                    RenderActive();
                };
                chips.Children.Add(chip);
            }

            body.Children.Add(new StackPanel
            {
                Spacing = 8,
                Children = { Text("Ask about this PC", 12, FontWeight.SemiBold, Ink), chips }
            });
        }

        var box = FortiqTextBox.Create("Ask a question about your backups");
        box.Text = model.Question;
        box.AcceptsReturn = false;
        box.IsEnabled = !model.Busy;
        box.Named("Your question for the assistant");
        box.TextChanged += (_, _) => model.Question = box.Text ?? string.Empty;

        var ask = Primary(model.Busy ? "Asking\u2026" : "Ask").Named("Ask the assistant");
        ask.IsEnabled = !model.Busy && !string.IsNullOrWhiteSpace(box.Text);
        ask.Click += async (_, _) =>
        {
            model.Question = box.Text ?? string.Empty;
            await model.AskAsync(CancellationToken.None);
            RenderActive();
        };

        // Enter asks. A single-line box where the only way to submit is the mouse is a box people
        // type into and then wait at.
        box.KeyDown += (_, args) =>
        {
            if (args.Key == Avalonia.Input.Key.Enter && ask.IsEnabled)
            {
                ask.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            }
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        row.Children.Add(box);
        row.Children.Add(At(ask, 1));
        body.Children.Add(row);

        if (model.Busy)
        {
            var busy = new StackPanel
            {
                Spacing = 10,
                Children = { Text(model.Status ?? "Thinking.", 13, FontWeight.Normal, Muted, wrap: true) }
            };

            // Cancellable, because the first question loads a model and some machines are slow.
            var stop = Secondary("Stop").Named("Stop the assistant answering");
            stop.HorizontalAlignment = HorizontalAlignment.Left;
            stop.Click += (_, _) => model.Cancel();
            busy.Children.Add(stop);
            body.Children.Add(Card(busy));
        }

        if (model.Failure is { } failure)
        {
            body.Children.Add(Card(new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    Text("The assistant could not answer.", 13, FontWeight.SemiBold, Failure),
                    Text(failure, 12, FontWeight.Normal, Muted, wrap: true)
                }
            }, AtRiskSurface, Failure));
        }

        if (model.Answer is { } answer)
        {
            var card = new StackPanel { Spacing = 8 };

            // The question is repeated above the answer. Answers arrive after a wait, and a
            // paragraph on its own is one somebody has to remember the question for.
            card.Children.Add(Text(model.AnsweredQuestion ?? string.Empty, 12, FontWeight.SemiBold, Muted, wrap: true));
            card.Children.Add(new SelectableTextBlock
            {
                Text = answer,
                FontSize = 14,
                Foreground = Ink,
                TextWrapping = TextWrapping.Wrap
            });

            if (model.AnswerTruncated)
            {
                card.Children.Add(Text("The answer stopped at its length limit.", 11, FontWeight.Normal, Unproven, wrap: true));
            }

            // Last, and always. This is the only screen in Fortiq whose text was not read off a
            // receipt, and it must not be mistaken for one.
            card.Children.Add(Text(
                "Written by a model, and it can be wrong. What Fortiq actually recorded is on Activity.",
                11, FontWeight.Normal, Muted, wrap: true));

            body.Children.Add(Card(card));
        }

        _page.Child = new ScrollViewer { Content = body };
    }

    /// <summary>
    /// The questions this PC raises, in the state it is in now.
    /// </summary>
    /// <remarks>
    /// Each one carries the facts that answer it, so the model is never asked to guess at machine
    /// state it was not given. A source that is not recoverable is offered first, because that is
    /// what somebody opening this screen is most likely to be here about.
    /// </remarks>
    private List<AssistantSuggestion> AssistantSuggestions()
    {
        var suggestions = new List<AssistantSuggestion>();

        foreach (var row in _model.Repositories.Where(item => item.Health.Verdict != HealthVerdict.Recoverable).Take(2))
        {
            suggestions.Add(new AssistantSuggestion(
                $"Why is {row.Title} not recoverable?",
                [
                    new AssistantEvidence($"status of {row.Title}", row.Summary),
                    new AssistantEvidence($"findings for {row.Title}", row.Detail)
                ]));
        }

        if (_model.Repositories.Count > 0)
        {
            suggestions.Add(new AssistantSuggestion(
                "What is protected on this PC, and is any of it at risk?",
                [
                    new AssistantEvidence(
                        "protected sources",
                        string.Join(
                            "\n",
                            _model.Repositories.Select(row => $"{row.Title}: {row.Summary}")))
                ]));
        }
        else
        {
            suggestions.Add(new AssistantSuggestion("What does Fortiq protect me against?", []));
        }

        suggestions.Add(new AssistantSuggestion("What is a recovery phrase, and why does Fortiq keep asking about mine?", []));
        return suggestions;
    }

    private void RenderSettings()
    {
        Select("Settings");
        var body = new StackPanel { Spacing = 20, Margin = new Thickness(32, 26) };
        body.Children.Add(Header("Settings", "Preferences, theme, Windows Service lifecycle, and paths."));

        // 1. Theme Configuration
        var themeGroup = new StackPanel { Spacing = 10 };
        themeGroup.Children.Add(Text("Appearance & Theme", 16, FontWeight.SemiBold, Ink));
        themeGroup.Children.Add(Text("Follow the Windows setting, or pick one and keep it.", 12, FontWeight.Normal, Muted));

        var themeSelector = new ComboBox
        {
            ItemsSource = new[] { "Match Windows", "Light", "Dark" },
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
            AppTheme.Apply(selectedTheme);
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
        // It does not run as NT SERVICE\Fortiq. The installer registers it under the local system
        // account and adds that service identity to its token, which is what the directory
        // permissions are written against - the identity restricts who may read Fortiq's files, not
        // what the service itself may do. Saying it ran with "only the permissions Fortiq needs" was
        // a least-privilege claim this build does not earn.
        "Running" => "Running in the background under the local system account, so scheduled backups happen with Fortiq closed.",
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
        if (NeedsElevation(operatorIsEnough: true))
        {
            await RunElevatedAsync("--prove", repository.Health.RepositoryId);
            return;
        }

        await _model.ProveRecoveryAsync(repository, CancellationToken.None);
    }

    private async Task BackupNowAsync(RepositoryRowViewModel repository)
    {
        if (NeedsElevation(operatorIsEnough: true))
        {
            await RunElevatedAsync("--backup", repository.Health.RepositoryId);
            return;
        }

        await _model.BackupNowAsync(repository, CancellationToken.None);
    }

    /// <summary>
    /// Whether this instance has to hand the work to a raised one.
    /// </summary>
    /// <remarks>
    /// A member of the local Fortiq Operators group may back up and prove recovery as themselves, so
    /// asking them to elevate would be a prompt with nothing behind it - and the point of the group is
    /// that an everyday backup does not need a client running with full rights over the machine.
    ///
    /// Protecting a folder is not in that set and passes false: provisioning makes the service read a
    /// path of the caller's choosing and hands back the phrase that opens the result, so it stays with
    /// administrators however this session is being run.
    /// </remarks>
    private bool NeedsElevation(bool operatorIsEnough)
    {
        if (!_installed || !OperatingSystem.IsWindows() || WindowsPrivilegeChecker.IsElevated())
        {
            return false;
        }

        return !operatorIsEnough || !FortiqOperatorsGroup.IsCurrentUserMember();
    }

    /// <summary>
    /// Runs one operation in an instance Windows has raised, and waits for it.
    /// </summary>
    /// <remarks>
    /// The same shape as protecting a folder: one prompt, one window that does the one thing, and this
    /// window still here afterwards. What stood here before, for the recovery drill, asked the person
    /// to reopen the whole application as an administrator and then closed it - which leaves a backup
    /// client holding full rights on the machine for as long as it is open, and loses whatever they
    /// were looking at, to save a prompt they are going to see anyway.
    /// </remarks>
    private async Task RunElevatedAsync(string verb, string repositoryId)
    {
        if (_elevating)
        {
            return;
        }

        var executable = Path.Combine(AppContext.BaseDirectory, "Fortiq.Desktop.exe");
        if (!File.Exists(executable))
        {
            await ShowNoticeAsync(
                "Fortiq could not be started",
                $"'{executable}' is missing, so this operation cannot be run with the permissions it " +
                "needs. Reinstall Fortiq.");
            return;
        }

        _elevating = true;
        RenderActive();
        try
        {
            using var elevated = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                ArgumentList = { verb, repositoryId }
            });

            if (elevated is null)
            {
                return;
            }

            await elevated.WaitForExitAsync();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Declining the prompt is an answer, not a fault, and saying so beats a button that
            // appears to do nothing.
            await ShowNoticeAsync(
                "Permission not granted",
                verb == "--backup"
                    ? "Running a backup needs administrator approval, because the background service reads "
                        + "the folder and unlocks the repository with a key bound to this machine. Nothing was changed."
                    : "Running a recovery drill needs administrator approval, because the background service "
                        + "restores the snapshot and records the proof. Nothing was changed.");
            return;
        }
        finally
        {
            _elevating = false;
        }

        // Whatever the elevated pass did, this window's picture of the machine is now out of date.
        await RefreshAsync();
    }

    /// <summary>
    /// Asks for the whole application to be reopened elevated, for the one action that needs it.
    /// </summary>
    /// <remarks>
    /// Starting and stopping the Windows service is done in this process through the service control
    /// manager, so unlike a backup or a drill there is no one-shot instance to hand it to. It is also
    /// the rarest thing anybody does here, which is why the coarse route is acceptable for it and was
    /// not acceptable for the operations people use every week.
    /// </remarks>
    private async Task<bool> EnsurePrivilegesAsync()
    {
        if (!NeedsElevation(operatorIsEnough: false))
        {
            return true;
        }

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
        Accessible.Keys(dialog, reopen);
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
                explanation.Text = "Fortiq was not reopened. " + PlainFailure.Describe(error);
            }
        };
        await dialog.ShowDialog(this);
        if (launched) ExplicitExit();
        return false;
    }

    /// <summary>Opens one source's own settings, and refreshes if anything changed.</summary>
    /// <remarks>
    /// Reading a schedule needs no privilege, so the window opens without a prompt on every machine.
    /// Saving is what may need the service, and the adapter behind the window raises that as a plain
    /// failure the window shows - rather than this screen guessing in advance and demanding elevation
    /// from somebody who only wanted to look.
    /// </remarks>
    private async Task OpenSourceSettingsAsync(RepositoryRowViewModel repository)
    {
        if (_sourceSettings is null)
        {
            return;
        }

        var window = new SourceSettingsWindow(_sourceSettings(repository.Health.RepositoryId, repository.Title));
        await window.ShowDialog(this);

        if (window.Changed)
        {
            _historyEvents = null;
            await RefreshAsync();
        }
    }

    private async Task ProtectAsync()
    {
        // No device key used to stop the wizard from opening at all, behind a dialog explaining
        // that automatic backups were unavailable. But a device key is what lets Fortiq unlock the
        // repository unattended - it is not what lets somebody protect a folder. Refusing to protect
        // anything, on a machine where protecting things works, is a far larger loss than the
        // scheduling it was guarding. The wizard is told what is unavailable and says so in place.

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
        // A notice with one button is the clearest case for Escape and Enter meaning the same thing.
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
        Accessible.Keys(dialog, ok);
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
        // A failure is red, a success is green, and anything else is amber. The history reads from
        // receipts that record both outcomes, so "not obviously good" and "went wrong" had to stop
        // being the same colour.
        var statusColor = heading ? color
            : fourth is "Recoverable" or "Completed" or "Healthy" or "Verified" or "Succeeded" ? Recoverable
            : fourth is "Failed" or "At risk" ? Failure
            : Unproven;
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

    /// <summary>
    /// Ends the assistant's process, if one was ever started.
    /// </summary>
    /// <remarks>
    /// Synchronously, and on every path out of the window. Disposal here is a kill and a wait for a
    /// process that is already being killed, so it costs milliseconds - and the alternative is a
    /// llama-server holding a gigabyte of memory after Fortiq has visibly closed, which is not
    /// something a person would connect to Fortiq or know how to end.
    /// </remarks>
    private void StopAssistant()
    {
        var assistant = _assistant;
        _assistant = null;

        try
        {
            assistant?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or TaskCanceledException)
        {
            // Already gone, which is the outcome this wanted.
        }
    }

    public void ExplicitExit()
    {
        StopAssistant();
        _isExplicitExit = true;
        _disposeTray?.Invoke();
        Close();
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}
