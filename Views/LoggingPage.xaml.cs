using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using BMSMonitor.Models;
using BMSMonitor.Services;
using BMSMonitor.ViewModels;
using Windows.Storage.Pickers;

namespace BMSMonitor.Views;

public sealed partial class LoggingPage : Page
{
    private MainViewModel  ViewModel => App.ViewModel;
    private LoggingService Logging   => ViewModel.Logging;
    private Services.LocalizationManager Lang => App.Lang;

    private string _selectedFolder = LoggingService.DefaultLogsFolder;
    private DispatcherQueueTimer? _durationTimer;

    // Map ComboBox index → LogFormat
    private static readonly LogFormat[] Formats =
        [LogFormat.Csv, LogFormat.Tsv, LogFormat.Excel, LogFormat.Json];

    // Filtered view — only enabled columns, shown in the horizontal drag strip
    private static readonly ObservableCollection<LogColumn> ActiveColumns = new();
    private static bool _activeColsInitialized;

    // Column config lives in ViewModel.LogColumns (persists across navigation)
    private ObservableCollection<LogColumn> Columns => ViewModel.LogColumns;

    public LoggingPage()
    {
        InitializeComponent();
        FolderText.Text   = _selectedFolder;
        FullPathText.Text = BuildPreviewPath();

        FileNameBox.TextChanged += (_, _) => FullPathText.Text = BuildPreviewPath();

        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Logging.StateChanged += OnStateChanged;
        RefreshUi();

        _durationTimer = DispatcherQueue.CreateTimer();
        _durationTimer.Interval = TimeSpan.FromSeconds(1);
        _durationTimer.Tick += (_, _) => UpdateDuration();
        _durationTimer.Start();

        ViewModel.DataStream.CollectionChanged += (_, _) => { UpdateStreamPlaceholder(); RebuildStreamTable(); };

        // Bind column collections and wire events
        EnsureActiveColumnsInitialized();
        ActiveColumnsList.ItemsSource = ActiveColumns;
        ColumnGrid.ItemsSource = Columns;
        ActiveColumnsList.DragItemsCompleted += ActiveColumnsList_DragItemsCompleted;

        // Rebuild stream table when columns change (IsEnabled toggle, reorder, reset)
        Columns.CollectionChanged += (_, _) => RebuildStreamTable();

        RebuildStreamTable();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Logging.StateChanged -= OnStateChanged;
        ViewModel.DataStream.CollectionChanged -= (_, _) => { UpdateStreamPlaceholder(); RebuildStreamTable(); };
        _durationTimer?.Stop();
        _durationTimer = null;
    }

    private void EnsureActiveColumnsInitialized()
    {
        if (_activeColsInitialized) return;
        _activeColsInitialized = true;

        foreach (var col in Columns.Where(c => c.IsEnabled))
            ActiveColumns.Add(col);

        foreach (var col in Columns)
            col.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName != nameof(LogColumn.IsEnabled) || s is not LogColumn lc) return;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (lc.IsEnabled)
                    {
                        if (!ActiveColumns.Contains(lc))
                            ActiveColumns.Add(lc);
                    }
                    else
                    {
                        ActiveColumns.Remove(lc);
                    }
                    RebuildStreamTable();
                });
            };
    }

    private void ActiveColumnsList_DragItemsCompleted(object sender, DragItemsCompletedEventArgs e)
    {
        // Sync Columns order to match the user's drag-reordered ActiveColumns
        var disabled = Columns.Where(c => !c.IsEnabled).ToList();
        var reordered = ActiveColumns.Concat(disabled).ToList();
        Columns.Clear();
        foreach (var col in reordered) Columns.Add(col);

        RebuildStreamTable();
    }

    private void UpdateStreamPlaceholder()
    {
        StreamEmptyText.Visibility = ViewModel.DataStream.Count == 0
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
    }

    private void OnStateChanged() => DispatcherQueue.TryEnqueue(RefreshUi);

    private void RefreshUi()
    {
        bool active = Logging.IsLogging;
        var  fmt    = GetSelectedFormat();

        StateText.Text       = active ? Lang.Log_Logging : Lang.Log_Idle;
        SampleText.Text      = Logging.SampleCount.ToString("N0");
        StartStopBtn.Content = active ? Lang.Log_StopLogging : Lang.Log_StartLogging;

        // Lock controls while recording
        FileNameBox.IsEnabled  = !active;
        FormatBox.IsEnabled    = !active;
        BrowseBtn.IsEnabled    = !active;

        // Lock column config while recording
        ActiveColumnsList.IsEnabled = !active;
        SelectAllBtn.IsEnabled    = !active;
        DeselectAllBtn.IsEnabled  = !active;
        ResetColsBtn.IsEnabled    = !active;
        ToggleCellsBtn.IsEnabled  = !active;
        ToggleBalBtn.IsEnabled    = !active;
        ToggleTempBtn.IsEnabled   = !active;

        if (active)
        {
            FolderText.Text   = Path.GetDirectoryName(Logging.FilePath) ?? _selectedFolder;
            FileNameBox.Text  = Path.GetFileName(Logging.FilePath) ?? "";
            FullPathText.Text = Logging.FilePath ?? "";
            HintText.Text     = Lang.Log_RecordingHint;
        }
        else
        {
            FolderText.Text   = _selectedFolder;
            FullPathText.Text = BuildPreviewPath();
            HintText.Text     = ViewModel.IsConnected
                ? Lang.Log_ReadyHint
                : Lang.Log_ConnectHint;
        }

        UpdateDuration();
    }

    private void UpdateDuration()
    {
        if (Logging.IsLogging)
        {
            var d = Logging.Duration;
            DurationText.Text = d.TotalHours >= 1
                ? $"{(int)d.TotalHours:D2}:{d.Minutes:D2}:{d.Seconds:D2}"
                : $"{d.Minutes:D2}:{d.Seconds:D2}";
        }
        else
        {
            DurationText.Text = Logging.SampleCount > 0
                ? Logging.Duration.ToString(@"mm\:ss")
                : "—";
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private LogFormat GetSelectedFormat()
    {
        int idx = FormatBox?.SelectedIndex ?? 0;
        return idx >= 0 && idx < Formats.Length ? Formats[idx] : LogFormat.Csv;
    }

    private string BuildPreviewPath()
    {
        var    fmt  = GetSelectedFormat();
        var    ext  = LoggingService.ExtensionFor(fmt);
        string name = FileNameBox?.Text.Trim() ?? "";

        if (string.IsNullOrEmpty(name))
            return Path.Combine(_selectedFolder, LoggingService.GenerateFileName(fmt));

        name = Path.GetFileNameWithoutExtension(name) + ext;
        return Path.Combine(_selectedFolder, name);
    }

    // ── Dynamic stream table ─────────────────────────────────────────────

    private void RebuildStreamTable()
    {
        StreamContainer.Children.Clear();
        StreamContainer.RowDefinitions.Clear();
        StreamContainer.ColumnDefinitions.Clear();

        var enabled = Columns.Where(c => c.IsEnabled).ToList();
        if (enabled.Count == 0)
        {
            StreamEmptyText.Visibility = Visibility.Visible;
            return;
        }
        StreamEmptyText.Visibility = Visibility.Collapsed;

        // Column defs — auto-width, last takes remaining space
        for (int i = 0; i < enabled.Count; i++)
        {
            StreamContainer.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = i == enabled.Count - 1
                    ? new GridLength(1, GridUnitType.Star)
                    : GridLength.Auto
            });
        }

        // ── Header row (row 0) ──
        StreamContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < enabled.Count; i++)
        {
            var tb = new TextBlock
            {
                Text = enabled[i].Label,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(i == 0 ? 0 : 12, 9, 0, 7)
            };
            Grid.SetColumn(tb, i);
            Grid.SetRow(tb, 0);
            StreamContainer.Children.Add(tb);
        }

        // ── Separator (row 1) ──
        StreamContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var sep = new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"]
        };
        Grid.SetRow(sep, 1);
        Grid.SetColumnSpan(sep, enabled.Count);
        StreamContainer.Children.Add(sep);

        // ── Data rows ──
        int row = 2;
        foreach (var logRow in ViewModel.DataStream)
        {
            StreamContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < enabled.Count && i < logRow.Values.Count; i++)
            {
                var tb = new TextBlock
                {
                    Text = logRow.Values[i],
                    FontSize = 12,
                    Opacity = 0.8,
                    FontFamily = new FontFamily("Consolas"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(i == 0 ? 0 : 12, 5, 0, 5)
                };
                Grid.SetColumn(tb, i);
                Grid.SetRow(tb, row);
                StreamContainer.Children.Add(tb);
            }
            row++;
        }
    }

    // ── Column settings handlers ──────────────────────────────────────────

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var col in Columns) col.IsEnabled = true;
    }

    private void DeselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var col in Columns) col.IsEnabled = false;
    }

    private void ResetColumns_Click(object sender, RoutedEventArgs e)
    {
        var defaults = LogColumn.CreateDefaults();
        Columns.Clear();
        foreach (var col in defaults) Columns.Add(col);

        // Rebuild ActiveColumns from the new defaults
        ActiveColumns.Clear();
        foreach (var col in Columns.Where(c => c.IsEnabled))
            ActiveColumns.Add(col);
    }

    private void ToggleCells_Click(object sender, RoutedEventArgs e)
        => ToggleGroup("Cells");

    private void ToggleBal_Click(object sender, RoutedEventArgs e)
        => ToggleGroup("Balancing");

    private void ToggleTemp_Click(object sender, RoutedEventArgs e)
        => ToggleGroup("Temperatures");

    private void ToggleGroup(string group)
    {
        var groupCols  = Columns.Where(c => c.Group == group).ToList();
        bool allEnabled = groupCols.All(c => c.IsEnabled);
        foreach (var col in groupCols) col.IsEnabled = !allEnabled;
    }

    // ── Event handlers ────────────────────────────────────────────────────

    private void FormatBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FullPathText is null) return;   // fired during InitializeComponent — ignore
        FullPathText.Text = BuildPreviewPath();
    }

    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        var hwnd   = WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            _selectedFolder   = folder.Path;
            FolderText.Text   = folder.Path;
            FullPathText.Text = BuildPreviewPath();
        }
    }

    private void StartStopBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Logging.IsLogging)
        {
            Logging.Stop();
        }
        else
        {
            string path = BuildPreviewPath();
            Logging.Start(path, GetSelectedFormat(), Columns);
        }
    }

    private void OpenFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        string folder = Logging.IsLogging
            ? (Path.GetDirectoryName(Logging.FilePath) ?? _selectedFolder)
            : _selectedFolder;
        Directory.CreateDirectory(folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = folder,
            UseShellExecute = true
        });
    }
}
