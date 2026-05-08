using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using BMSMonitor.Models;
using BMSMonitor.Services;
using BMSMonitor.ViewModels;
using Windows.Storage.Pickers;

namespace BMSMonitor.Views;

public sealed partial class LoggingPage : Page
{
    private MainViewModel  ViewModel => App.ViewModel;
    private LoggingService Logging   => ViewModel.Logging;

    private string _selectedFolder = LoggingService.DefaultLogsFolder;
    private DispatcherQueueTimer? _durationTimer;

    // Map ComboBox index → LogFormat
    private static readonly LogFormat[] Formats =
        [LogFormat.Csv, LogFormat.Tsv, LogFormat.Excel, LogFormat.Json];

    // Column config persists across navigation (static)
    private static readonly ObservableCollection<LogColumn> _columns =
        LogColumn.CreateDefaults();

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

        ViewModel.DataStream.CollectionChanged += (_, _) => UpdateStreamPlaceholder();
        UpdateStreamPlaceholder();

        // Bind column list (static collection — already populated)
        ColumnListView.ItemsSource = _columns;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Logging.StateChanged -= OnStateChanged;
        ViewModel.DataStream.CollectionChanged -= (_, _) => UpdateStreamPlaceholder();
        _durationTimer?.Stop();
        _durationTimer = null;
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

        StateText.Text       = active ? "Logging" : "Idle";
        SampleText.Text      = Logging.SampleCount.ToString("N0");
        StartStopBtn.Content = active ? "Stop Logging" : "Start Logging";

        // Lock controls while recording
        FileNameBox.IsEnabled  = !active;
        FormatBox.IsEnabled    = !active;
        BrowseBtn.IsEnabled    = !active;

        // Lock column config while recording
        ColumnListView.IsEnabled  = !active;
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
            HintText.Text     = "Recording in progress. Stop to close / write the file.";
        }
        else
        {
            FolderText.Text   = _selectedFolder;
            FullPathText.Text = BuildPreviewPath();
            HintText.Text     = ViewModel.IsConnected
                ? "Ready. Press Start Logging to begin recording."
                : "Connect to the ESP32 first, then start logging.";
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

    // ── Column settings handlers ──────────────────────────────────────────

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var col in _columns) col.IsEnabled = true;
    }

    private void DeselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var col in _columns) col.IsEnabled = false;
    }

    private void ResetColumns_Click(object sender, RoutedEventArgs e)
    {
        var defaults = LogColumn.CreateDefaults();
        _columns.Clear();
        foreach (var col in defaults) _columns.Add(col);
    }

    private void ToggleCells_Click(object sender, RoutedEventArgs e)
        => ToggleGroup("Cells");

    private void ToggleBal_Click(object sender, RoutedEventArgs e)
        => ToggleGroup("Balancing");

    private void ToggleTemp_Click(object sender, RoutedEventArgs e)
        => ToggleGroup("Temperatures");

    private static void ToggleGroup(string group)
    {
        var groupCols  = _columns.Where(c => c.Group == group).ToList();
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
            Logging.Start(path, GetSelectedFormat(), _columns);
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
