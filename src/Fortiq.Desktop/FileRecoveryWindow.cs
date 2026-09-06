using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Fortiq.Desktop.Controls;
using Fortiq.Desktop.ViewModels;

namespace Fortiq.Desktop;

public sealed class FileRecoveryWindow : Window
{
    private readonly FileRecoveryViewModel _model;
    private readonly TextBox _repository = new() { PlaceholderText = "Local repository path or s3:https://endpoint/bucket" };
    private readonly TextBox _phrase = new() { PasswordChar = '\u2022' };
    private readonly TextBox _accessKey = new();
    private readonly TextBox _secretKey = new() { PasswordChar = '\u2022' };
    private readonly TextBox _region = new();
    private readonly ComboBox _snapshots = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _destination = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _load = new() { Content = "Find backups" };
    private readonly Button _restore = new() { Content = "Restore all files from selected backup" };
    private readonly Button _cancel = new() { Content = "Cancel operation" };
    private readonly Button _reset = new() { Content = "Use another recovery kit" };
    private readonly Button _open = new() { Content = "Open restored folder" };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 5 };
    private readonly StackPanel _selectionFields = new() { Spacing = 12 };
    private readonly StackPanel _accessFields = new() { Spacing = 10 };
    private readonly PathPickerControl _kit;
    private readonly PathPickerControl _parent;

    // Snapshot Explorer controls
    private readonly RadioButton _restoreAllRadio = new() { Content = "Restore entire backup", IsChecked = true, GroupName = "RestoreMode" };
    private readonly RadioButton _restoreSpecificRadio = new() { Content = "Restore specific file or folder", IsChecked = false, GroupName = "RestoreMode" };
    private readonly TextBox _fileSearch = new() { PlaceholderText = "Search files in this backup (name or path)..." };

    /// <summary>
    /// Holds the search back until typing pauses.
    /// </summary>
    /// <remarks>
    /// Filtering scans every path in the snapshot, on the UI thread, and a backup of a documents
    /// folder is easily six figures of them. Running that per keystroke meant the window stopped
    /// repainting while somebody typed a filename - the letters appeared behind the cursor and the
    /// list lagged several characters back. One pass after the last keystroke is the same answer for
    /// a fraction of the work.
    /// </remarks>
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly ListBox _fileList = new() { Height = 220, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _fileListStatus = new() { FontSize = 12, Foreground = Brushes.Gray };
    private readonly StackPanel _explorerPanel = new() { Spacing = 8 };

    private string? _target;
    private RecoverySnapshot? _lastLoadedSnapshot;

    public FileRecoveryWindow(FileRecoveryViewModel model)
    {
        _model = model;
        Title = "Restore files - Fortiq";
        Width = 840;
        Height = 740;
        MinWidth = 720;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _kit = new PathPickerControl(this, "Recovery kit folder", "The folder Fortiq wrote when it protected this data. It holds a file named kit.json.");
        _parent = new PathPickerControl(this, "Destination parent folder", "Fortiq will create a new subfolder here. Choose a location outside your original source, repository and kit.");
        _parent.PathChanged += path =>
        {
            try
            {
                _target = string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ? null
                    : Path.Combine(Path.GetFullPath(path), "Fortiq-restored-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)
                        + "-" + Guid.NewGuid().ToString("N")[..8]);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
            { _target = null; }
            Refresh();
        };
        _repository.TextChanged += (_, _) => Refresh();
        _phrase.TextChanged += (_, _) => Refresh();
        _kit.PathChanged += path =>
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                var kitFile = Path.Combine(path, "kit.json");
                if (File.Exists(kitFile))
                {
                    try
                    {
                        var json = File.ReadAllText(kitFile);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("repositoryLocator", out var locProp))
                        {
                            var loc = locProp.GetString();
                            if (!string.IsNullOrWhiteSpace(loc))
                            {
                                _repository.Text = loc;
                            }
                        }
                    }
                    catch
                    {
                        // Best effort detection
                    }
                }
            }
            Refresh();
        };
        _accessFields.Children.Add(Field("Backup repository", _repository));
        _accessFields.Children.Add(_kit);
        _accessFields.Children.Add(Field("Recovery phrase (held only for this session)", _phrase));
        _accessFields.Children.Add(new Expander
        {
            Header = "S3 credentials (only for object storage)",
            Content = new StackPanel { Spacing = 8, Children =
            {
                Field("Access key", _accessKey), Field("Secret key", _secretKey), Field("Region (optional)", _region)
            } }
        });
        _load.Click += async (_, _) =>
        {
            await _model.LoadAsync(new FileRecoveryAccess(_repository.Text?.Trim() ?? "", _kit.SelectedPath,
                _phrase.Text ?? "", _accessKey.Text ?? "", _secretKey.Text ?? "", _region.Text ?? ""));
            ClearSecretFields();
        };

        _snapshots.SelectionChanged += async (_, _) =>
        {
            Refresh();
            if (_snapshots.SelectedItem is RecoverySnapshot snapshot && !ReferenceEquals(_lastLoadedSnapshot, snapshot))
            {
                _lastLoadedSnapshot = snapshot;
                await _model.LoadFilesAsync(snapshot);
            }
        };

        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            _model.SetSearchQuery(_fileSearch.Text ?? string.Empty);
            UpdateFileList();
        };

        _fileSearch.TextChanged += (_, _) =>
        {
            // Restarting on every keystroke is what makes this a debounce rather than a delay: the
            // filter runs once, after the person stops typing.
            _searchDebounce.Stop();
            _searchDebounce.Start();
        };

        _fileList.ItemTemplate = CreateFileItemTemplate();
        _fileList.SelectionChanged += (_, _) =>
        {
            _model.SelectedFile = _fileList.SelectedItem as SnapshotFileItem;
            Refresh();
        };

        _restoreAllRadio.IsCheckedChanged += (_, _) =>
        {
            _model.RestoreSpecificItem = _restoreSpecificRadio.IsChecked == true;
            Refresh();
        };
        _restoreSpecificRadio.IsCheckedChanged += (_, _) =>
        {
            _model.RestoreSpecificItem = _restoreSpecificRadio.IsChecked == true;
            Refresh();
        };

        _restore.Click += async (_, _) =>
        {
            if (_snapshots.SelectedItem is RecoverySnapshot snapshot && _target is { } target)
            {
                var specificPath = _restoreSpecificRadio.IsChecked == true && _model.SelectedFile is { } file
                    ? file.Path
                    : null;
                await _model.RestoreAsync(snapshot, target, specificPath);
            }
        };

        _cancel.Click += (_, _) => _model.Cancel();
        _reset.Click += (_, _) =>
        {
            _model.Clear();
            _lastLoadedSnapshot = null;
            ClearSecretFields();
            Refresh();
        };
        _open.Click += (_, _) =>
        {
            try
            {
                if (_model.RestoredTarget is { } target && Directory.Exists(target))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException)
            { _status.Text = "Files were restored, but the folder could not be opened. " + PlainFailure.Describe(error); }
        };
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => Close();

        // Build Explorer Panel
        _explorerPanel.Children.Add(new TextBlock { Text = "What to restore", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) });
        var radioRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 20,
            Children = { _restoreAllRadio, _restoreSpecificRadio }
        };
        _explorerPanel.Children.Add(radioRow);
        _explorerPanel.Children.Add(_fileSearch);
        _explorerPanel.Children.Add(_fileListStatus);
        _explorerPanel.Children.Add(_fileList);

        _selectionFields.Children.Add(new TextBlock { Text = "Backup to restore", FontWeight = FontWeight.SemiBold });
        _selectionFields.Children.Add(_snapshots);
        _selectionFields.Children.Add(_explorerPanel);
        _selectionFields.Children.Add(_parent);
        _selectionFields.Children.Add(_destination);

        var body = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 14,
            Children =
            {
                new TextBlock { Text = "Restore your files", FontSize = 24, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Use your recovery kit and phrase, even on a new computer. No running Fortiq service is required. This restores files; it does not change your protection verdict.", TextWrapping = TextWrapping.Wrap },
                _accessFields, _load, _reset, _selectionFields
            }
        };
        var layout = new DockPanel();
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(24, 12),
            HorizontalAlignment = HorizontalAlignment.Right, Children = { _cancel, close }
        };
        var fixedActions = new StackPanel
        {
            Margin = new Thickness(24, 8), Spacing = 10,
            Children = { _progress, _status, _restore, _open, footer }
        };
        DockPanel.SetDock(fixedActions, Dock.Bottom);
        layout.Children.Add(fixedActions);
        layout.Children.Add(new ScrollViewer { Content = body });
        Content = layout;
        _model.PropertyChanged += ModelChanged;
        Closing += (_, e) =>
        {
            if (!_model.Busy) return;
            e.Cancel = true;
            _model.Cancel();
        };
        Closed += (_, _) =>
        {
            // A tick left pending would rebuild the list of a window that is gone.
            _searchDebounce.Stop();
            _model.PropertyChanged -= ModelChanged;
            _model.Clear();
            _lastLoadedSnapshot = null;
            ClearSecretFields();
        };
        AutomationProperties.SetName(_snapshots, "Backup to restore");
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape && !_model.Busy)
            {
                Close();
            }
        };
        Refresh();
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

    private void ModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) => Refresh();

    private void UpdateFileList()
    {
        _fileList.ItemsSource = _model.FilteredFiles;
        if (_model.FilesLoading)
        {
            _fileListStatus.Text = "Reading file index from backup...";
        }
        else if (_model.Files.Count == 0)
        {
            _fileListStatus.Text = "No files found in this backup.";
        }
        else if (!string.IsNullOrWhiteSpace(_model.SearchFilterSummary))
        {
            _fileListStatus.Text = _model.SearchFilterSummary;
        }
        else
        {
            _fileListStatus.Text = $"{_model.Files.Count:N0} files in backup. Select one to restore.";
        }
    }

    private void Refresh()
    {
        _accessFields.IsEnabled = !_model.Busy && _model.Snapshots.Count == 0;
        _load.IsEnabled = !_model.Busy && _model.Snapshots.Count == 0
            && !string.IsNullOrWhiteSpace(_repository.Text) && !string.IsNullOrWhiteSpace(_phrase.Text)
            && !string.IsNullOrWhiteSpace(_kit.SelectedPath);
        _load.IsVisible = _model.Snapshots.Count == 0;
        _selectionFields.IsVisible = _model.Snapshots.Count > 0;
        _accessFields.IsVisible = _model.Snapshots.Count == 0;
        _reset.IsVisible = _model.Snapshots.Count > 0;
        _restore.IsVisible = _model.Snapshots.Count > 0 && !_model.Completed;
        _reset.IsEnabled = !_model.Busy;

        if (!ReferenceEquals(_snapshots.ItemsSource, _model.Snapshots))
        {
            _snapshots.ItemsSource = _model.Snapshots;
            _snapshots.SelectedIndex = _model.Snapshots.Count > 0 ? 0 : -1;
        }

        var isSpecific = _restoreSpecificRadio.IsChecked == true;
        _fileSearch.IsVisible = isSpecific;
        _fileList.IsVisible = isSpecific;
        _fileListStatus.IsVisible = isSpecific;
        _fileList.IsEnabled = !_model.Busy && !_model.Completed;
        _fileSearch.IsEnabled = !_model.Busy && !_model.Completed;

        UpdateFileList();

        _snapshots.IsEnabled = !_model.Busy && !_model.Completed;
        _parent.IsEnabled = !_model.Busy && !_model.Completed;

        var hasValidSelection = !isSpecific || _model.SelectedFile is not null;
        _restore.IsEnabled = !_model.Busy && !_model.Completed && _snapshots.SelectedItem is RecoverySnapshot && _target is not null && hasValidSelection;

        if (isSpecific && _model.SelectedFile is { } file)
        {
            _restore.Content = file.IsDirectory
                ? $"Restore folder: {file.DisplayName}"
                : $"Restore file: {file.Name} ({file.FormattedSize})";
        }
        else
        {
            _restore.Content = "Restore all files from selected backup";
        }

        _cancel.IsEnabled = _model.Busy;
        _open.IsVisible = _model.Completed;
        _progress.IsVisible = _model.Busy || _model.FilesLoading;
        _status.Text = _model.Status;
        _destination.Text = _target is null ? "Select an existing destination parent folder." : "New recovery folder: " + _target;
    }

    private static FuncDataTemplate<SnapshotFileItem> CreateFileItemTemplate()
    {
        return new FuncDataTemplate<SnapshotFileItem>((item, _) =>
        {
            if (item is null) return new TextBlock();

            var icon = new TextBlock
            {
                Text = item.IsDirectory ? "📁 " : "📄 ",
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };

            var nameBlock = new TextBlock
            {
                Text = item.DisplayName,
                FontWeight = FontWeight.Medium,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };

            var sizeBlock = new TextBlock
            {
                Text = item.FormattedSize,
                FontSize = 11,
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };

            var topRow = new DockPanel();
            DockPanel.SetDock(sizeBlock, Dock.Right);
            topRow.Children.Add(sizeBlock);
            topRow.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { icon, nameBlock }
            });

            var pathBlock = new TextBlock
            {
                Text = item.Path,
                FontSize = 10,
                Foreground = Brushes.DarkGray,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(20, 1, 0, 0)
            };

            return new StackPanel
            {
                Margin = new Thickness(2, 2),
                Children = { topRow, pathBlock }
            };
        });
    }

    private void ClearSecretFields()
    {
        _phrase.Text = string.Empty;
        _secretKey.Text = string.Empty;
        _accessKey.Text = string.Empty;
    }

    private static StackPanel Field(string name, Control control)
    {
        AutomationProperties.SetName(control, name);
        return new StackPanel { Spacing = 4, Children = { new TextBlock { Text = name }, control } };
    }
}
