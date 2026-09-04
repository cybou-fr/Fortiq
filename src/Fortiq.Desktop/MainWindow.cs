using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Fortiq.Desktop.ViewModels;
using Fortiq.Monitoring;

namespace Fortiq.Desktop;

/// <summary>
/// The one screen: what exists, whether Fortiq will claim it is recoverable, and the button that
/// makes that claim true. Built in code rather than markup so the whole window is one file to read.
/// </summary>
/// <remarks>
/// Every decision the screen makes lives in <see cref="RepositoriesViewModel"/>, which is tested. The
/// window arranges controls and does nothing else, so what a person is told cannot drift from what
/// the tests hold to.
/// </remarks>
public sealed class MainWindow : Window
{
    private readonly RepositoriesViewModel _model;
    private readonly TextBlock _headline = new() { FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _failure = new() { Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private readonly Button _refresh = new() { Content = "Refresh" };
    private readonly Button _protect = new() { Content = "Protect a folder..." };
    private readonly Func<ProtectRepositoryViewModel>? _wizard;

    public MainWindow(RepositoriesViewModel model, Func<ProtectRepositoryViewModel>? wizard = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _wizard = wizard;
        _protect.IsEnabled = wizard is not null;

        Title = "Fortiq";
        Width = 820;
        Height = 560;

        _refresh.Click += async (_, _) => await RefreshAsync();
        _protect.Click += async (_, _) => await ProtectAsync();
        _model.PropertyChanged += (_, _) => Render();

        Content = new DockPanel
        {
            Margin = new Thickness(20),
            LastChildFill = true,
            Children =
            {
                Header(),
                new ScrollViewer { Content = _rows }
            }
        };

        Opened += async (_, _) => await RefreshAsync();
    }

    private StackPanel Header()
    {
        var header = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 0, 16) };
        header.Children.Add(_headline);
        header.Children.Add(_failure);
        header.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _protect, _refresh }
        });

        DockPanel.SetDock(header, Dock.Top);
        return header;
    }

    /// <summary>
    /// Runs the wizard and then refreshes, so a repository created here appears with the verdict it
    /// actually has - unproven - rather than as a success message the person walks away from.
    /// </summary>
    private async Task ProtectAsync()
    {
        if (_wizard is null)
        {
            return;
        }

        var window = new ProtectRepositoryWindow(_wizard());
        await window.ShowDialog(this);
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        await _model.RefreshAsync(CancellationToken.None);
        Render();
    }

    private void Render()
    {
        _headline.Text = _model.Headline;
        _failure.Text = _model.Failure;
        _failure.IsVisible = _model.Failure is { Length: > 0 };
        _refresh.IsEnabled = !_model.Busy;

        _rows.Children.Clear();
        foreach (var repository in _model.Repositories)
        {
            _rows.Children.Add(Row(repository));
        }
    }

    private Border Row(RepositoryRowViewModel repository)
    {
        var prove = new Button
        {
            Content = "Prove recovery",
            IsEnabled = repository.CanProveRecovery && !_model.Busy,
            HorizontalAlignment = HorizontalAlignment.Left
        };

        prove.Click += async (_, _) =>
        {
            await _model.ProveRecoveryAsync(repository, CancellationToken.None);
            Render();
        };

        return new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Gray,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = repository.Title, FontWeight = FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = repository.Summary,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Verdict(repository.Health.Verdict)
                    },
                    new TextBlock { Text = repository.Detail, TextWrapping = TextWrapping.Wrap, Opacity = 0.75 },
                    prove
                }
            }
        };
    }

    /// <summary>
    /// Unproven is deliberately not green. A repository nobody has restored from looks finished to a
    /// person, and that impression is the thing this product exists to remove.
    /// </summary>
    private static IBrush Verdict(HealthVerdict verdict) => verdict switch
    {
        HealthVerdict.Recoverable => Brushes.SeaGreen,
        HealthVerdict.Unproven => Brushes.Goldenrod,
        _ => Brushes.OrangeRed
    };
}
