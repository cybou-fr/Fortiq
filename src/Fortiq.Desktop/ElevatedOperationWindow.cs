using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using static Fortiq.Desktop.DesignTokens;

namespace Fortiq.Desktop;

/// <summary>What an elevated one-shot pass was asked to do.</summary>
public enum ElevatedOperation
{
    /// <summary>Back the repository up now.</summary>
    Backup,

    /// <summary>Restore from the repository into a scratch directory and check what came back.</summary>
    Prove
}

/// <summary>
/// One privileged operation, in a window of its own, run by an instance that exists only for it.
/// </summary>
/// <remarks>
/// The service refuses a caller who does not hold its privileges, and the desktop runs unelevated -
/// so a backup or a recovery drill started from the main window needs a pass that Windows has raised.
/// Protecting a folder already worked this way. Proving recovery did not: it told the person to reopen
/// the whole application as an administrator, and closed itself so they would. That leaves a backup
/// client running with full rights on the machine for as long as it stays open, having discarded
/// whatever they were looking at, in exchange for one prompt.
///
/// This window is the other half of that fix. It shows what is running rather than a frozen dashboard,
/// says plainly what came of it, and exits when the person closes it. Its exit code is for the parent:
/// zero when the operation succeeded, non-zero otherwise.
/// </remarks>
public sealed class ElevatedOperationWindow : Window
{
    private readonly ElevatedOperation _operation;
    private readonly string _repositoryId;
    private readonly Func<string, CancellationToken, Task<(bool Success, string? Failure)>> _run;
    private readonly TextBlock _headline;
    private readonly TextBlock _detail;
    private readonly Button _close;

    /// <summary>False until the operation has reported success. Read by the process's exit code.</summary>
    public bool Succeeded { get; private set; }

    public ElevatedOperationWindow(
        ElevatedOperation operation,
        string repositoryId,
        Func<string, CancellationToken, Task<(bool Success, string? Failure)>> run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        _operation = operation;
        _repositoryId = repositoryId;
        _run = run ?? throw new ArgumentNullException(nameof(run));

        Title = operation == ElevatedOperation.Backup ? "Fortiq — Backing up" : "Fortiq — Recovery drill";
        Icon = FortiqBrand.WindowIcon();
        Width = 520;
        Height = 260;
        CanResize = false;
        Background = CanvasBackground;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _headline = new TextBlock
        {
            Text = operation == ElevatedOperation.Backup ? "Backing up…" : "Running the recovery drill…",
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = Ink,
            TextWrapping = TextWrapping.Wrap
        };

        _detail = new TextBlock
        {
            Text = operation == ElevatedOperation.Backup
                ? "Fortiq is reading the folder and writing what changed. Leave this window open; it closes when you do."
                : "Fortiq is restoring the newest snapshot into a scratch directory and checking what came back.",
            FontSize = 13,
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap
        };

        _close = new Button
        {
            Content = "Close",
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(18, 9),
            CornerRadius = new CornerRadius(6),
            Background = Brand,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0)
        };
        _close.Click += (_, _) => Close();
        AutomationProperties.SetName(_headline, "Operation status");
        AutomationProperties.SetLiveSetting(_headline, AutomationLiveSetting.Assertive);

        // Escape does nothing while the operation is running, because the button is disabled and
        // closing the window would not stop the work: it is happening in this process, and the
        // person would be left with no way to see how it ended.
        Accessible.Keys(this, _close, cancel: () =>
        {
            if (_close.IsEnabled)
            {
                Close();
            }
        });

        Content = new StackPanel
        {
            Margin = new Thickness(28, 26),
            Spacing = 16,
            Children = { _headline, _detail, _close }
        };

        Opened += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            var (success, failure) = await _run(_repositoryId, CancellationToken.None);
            Succeeded = success;
            if (success)
            {
                _headline.Text = _operation == ElevatedOperation.Backup ? "Backup finished" : "Recovery proven";
                _headline.Foreground = Recoverable;
                _detail.Text = _operation == ElevatedOperation.Backup
                    ? "A new snapshot was written and recorded. Fortiq's status will show it."
                    : "A real restore completed and what came back was reconciled against the snapshot.";
                _detail.Foreground = Muted;
            }
            else
            {
                _headline.Text = _operation == ElevatedOperation.Backup ? "The backup did not finish" : "Recovery was not proven";
                _headline.Foreground = Failure;
                _detail.Text = failure ?? "No reason was given.";
                _detail.Foreground = Ink;
            }
        }
        catch (Exception error)
        {
            Succeeded = false;
            _headline.Text = _operation == ElevatedOperation.Backup ? "The backup did not finish" : "Recovery was not proven";
            _headline.Foreground = Failure;
            _detail.Text = Fortiq.Desktop.ViewModels.PlainFailure.Describe(error);
            _detail.Foreground = Ink;
        }
        finally
        {
            _close.IsEnabled = true;
        }
    }
}
