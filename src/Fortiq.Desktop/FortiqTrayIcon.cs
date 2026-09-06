using Avalonia.Controls;

namespace Fortiq.Desktop;

/// <summary>
/// Manages the Windows notification area (system tray) icon and context menu for Fortiq.
/// </summary>
public sealed class FortiqTrayIcon : IDisposable
{
    private readonly TrayIcon _trayIcon;
    private readonly NativeMenuItem _statusItem;
    private readonly Action _onOpen;
    private readonly Action _onExit;
    private bool _disposed;

    public FortiqTrayIcon(Action onOpen, Action onExit, Action? onOpenRecovery = null, Action? onVerifyNow = null)
    {
        _onOpen = onOpen ?? throw new ArgumentNullException(nameof(onOpen));
        _onExit = onExit ?? throw new ArgumentNullException(nameof(onExit));

        _trayIcon = new TrayIcon
        {
            Icon = FortiqBrand.WindowIcon(),
            ToolTipText = "Fortiq — checking…",
            IsVisible = true
        };

        var menu = new NativeMenu();

        var openItem = new NativeMenuItem("Open Fortiq");
        openItem.Click += (_, _) => _onOpen();
        menu.Add(openItem);

        if (onOpenRecovery is not null)
        {
            var recoveryItem = new NativeMenuItem("Emergency File Recovery…");
            recoveryItem.Click += (_, _) => onOpenRecovery();
            menu.Add(recoveryItem);
        }

        if (onVerifyNow is not null)
        {
            // Was "Verify Backups Now", which is not what the action does: it re-reads the health
            // report the service publishes. Nothing is verified by clicking it, and a person who
            // believed otherwise would come away thinking their backups had just been checked.
            var refreshItem = new NativeMenuItem("Refresh status");
            refreshItem.Click += (_, _) => onVerifyNow();
            menu.Add(refreshItem);
        }

        menu.Add(new NativeMenuItemSeparator());

        _statusItem = new NativeMenuItem("Status: Checking…")
        {
            IsEnabled = false
        };
        menu.Add(_statusItem);

        menu.Add(new NativeMenuItemSeparator());

        var exitItem = new NativeMenuItem("Exit Fortiq");
        exitItem.Click += (_, _) => _onExit();
        menu.Add(exitItem);

        _trayIcon.Menu = menu;
        _trayIcon.Clicked += (_, _) => _onOpen();

        if (Avalonia.Application.Current is not null)
        {
            var icons = TrayIcon.GetIcons(Avalonia.Application.Current);
            if (icons is null)
            {
                icons = new TrayIcons();
                TrayIcon.SetIcons(Avalonia.Application.Current, icons);
            }
            icons.Add(_trayIcon);
        }
    }

    public void UpdateStatus(string status)
    {
        if (_disposed) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            _statusItem.Header = $"Status: {status}";
            _trayIcon.ToolTipText = $"Fortiq — {status}";
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _trayIcon.IsVisible = false;
        if (Avalonia.Application.Current is not null)
        {
            var icons = TrayIcon.GetIcons(Avalonia.Application.Current);
            icons?.Remove(_trayIcon);
        }
        if (_trayIcon is IDisposable disposableTray)
        {
            disposableTray.Dispose();
        }
    }
}
