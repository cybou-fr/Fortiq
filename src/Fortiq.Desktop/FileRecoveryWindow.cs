using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
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
    private string? _target;

    public FileRecoveryWindow(FileRecoveryViewModel model)
    {
        _model = model;
        Title = "Restore files - Fortiq";
        Width = 760;
        Height = 680;
        MinWidth = 620;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _kit = new PathPickerControl(this, "Recovery kit folder", "Choose the folder containing kit.json and the recovery envelopes.");
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
        _kit.PathChanged += _ => Refresh();
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
        _snapshots.SelectionChanged += (_, _) => Refresh();
        _restore.Click += async (_, _) =>
        {
            if (_snapshots.SelectedItem is RecoverySnapshot snapshot && _target is { } target)
                await _model.RestoreAsync(snapshot, target);
        };
        _cancel.Click += (_, _) => _model.Cancel();
        _reset.Click += (_, _) =>
        {
            _model.Clear();
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
            { _status.Text = "Files were restored, but the folder could not be opened. " + error.Message; }
        };
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => Close();
        _selectionFields.Children.Add(new TextBlock { Text = "Backup to restore" });
        _selectionFields.Children.Add(_snapshots);
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
            _model.PropertyChanged -= ModelChanged;
            _model.Clear();
            ClearSecretFields();
        };
        AutomationProperties.SetName(_snapshots, "Backup to restore");
        Refresh();
    }

    private void ModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) => Refresh();

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
        _snapshots.IsEnabled = !_model.Busy && !_model.Completed;
        _parent.IsEnabled = !_model.Busy && !_model.Completed;
        _restore.IsEnabled = !_model.Busy && !_model.Completed && _snapshots.SelectedItem is RecoverySnapshot && _target is not null;
        _cancel.IsEnabled = _model.Busy;
        _open.IsVisible = _model.Completed;
        _progress.IsVisible = _model.Busy;
        _status.Text = _model.Status;
        _destination.Text = _target is null ? "Select an existing destination parent folder." : "New recovery folder: " + _target;
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
