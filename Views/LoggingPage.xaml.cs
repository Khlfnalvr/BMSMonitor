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

    // The fixed column layout used for both the live preview and the on-disk
    // log file. Sourced from ViewModel.LogColumns (defaults — all enabled).
    private ObservableCollection<LogColumn> Columns => ViewModel.LogColumns;

    // ── Stream table cache ─────────────────────────────────────────────────
    // Grid structure is built ONCE on first load (columns are no longer
    // user-configurable). On data updates only TextBlock.Text is changed —
    // no UIElement churn.
    private List<LogColumn>? _streamColumns;
    private TextBlock[,]?    _streamCells;
    private const int StreamCapacity = 20;

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

        ViewModel.DataStream.CollectionChanged += OnDataStreamChanged;

        // Build the live-preview grid once. Columns are fixed (default set).
        BuildStreamStructure();
        RefreshStreamData();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Logging.StateChanged -= OnStateChanged;
        ViewModel.DataStream.CollectionChanged -= OnDataStreamChanged;
        _durationTimer?.Stop();
        _durationTimer = null;
    }

    // ── Stream table event handlers ───────────────────────────────────────

    // New data arrived — just update text, no structural rebuild.
    private void OnDataStreamChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        UpdateStreamPlaceholder();
        RefreshStreamData();
    }

    private void UpdateStreamPlaceholder()
    {
        StreamEmptyText.Visibility = ViewModel.DataStream.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ── Stream table rendering ────────────────────────────────────────────

    /// <summary>
    /// Builds the fixed grid structure (column defs, header, separator, and
    /// pre-allocated data rows). Called once on page load.
    /// </summary>
    private void BuildStreamStructure()
    {
        StreamContainer.Children.Clear();
        StreamContainer.RowDefinitions.Clear();
        StreamContainer.ColumnDefinitions.Clear();
        _streamCells   = null;
        _streamColumns = null;

        var enabled = Columns.Where(c => c.IsEnabled).ToList();
        if (enabled.Count == 0)
        {
            StreamEmptyText.Visibility = Visibility.Visible;
            return;
        }
        StreamEmptyText.Visibility = Visibility.Collapsed;

        int colCount = enabled.Count;

        // Column definitions — auto-width, last column takes remaining space
        for (int i = 0; i < colCount; i++)
        {
            StreamContainer.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = i == colCount - 1
                    ? new GridLength(1, GridUnitType.Star)
                    : GridLength.Auto
            });
        }

        // ── Row 0: Header ──
        StreamContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < colCount; i++)
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

        // ── Row 1: Separator ──
        StreamContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var sep = new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"]
        };
        Grid.SetRow(sep, 1);
        Grid.SetColumnSpan(sep, colCount);
        StreamContainer.Children.Add(sep);

        // ── Rows 2…21: Pre-allocated data cells ──
        var cells = new TextBlock[StreamCapacity, colCount];
        for (int r = 0; r < StreamCapacity; r++)
        {
            StreamContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < colCount; c++)
            {
                var tb = new TextBlock
                {
                    FontSize = 12,
                    Opacity = 0.8,
                    FontFamily = new FontFamily("Consolas"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(c == 0 ? 0 : 12, 5, 0, 5),
                    Visibility = Visibility.Collapsed   // hidden until data arrives
                };
                Grid.SetColumn(tb, c);
                Grid.SetRow(tb, r + 2);
                StreamContainer.Children.Add(tb);
                cells[r, c] = tb;
            }
        }

        _streamCells   = cells;
        _streamColumns = enabled;
    }

    /// <summary>
    /// Updates only the text of pre-allocated data cells — no UIElement allocation.
    /// Called on every data-stream update.
    /// </summary>
    private void RefreshStreamData()
    {
        if (_streamCells is null || _streamColumns is null) return;

        int rowCount = Math.Min(ViewModel.DataStream.Count, StreamCapacity);
        int colCount = _streamColumns.Count;

        for (int r = 0; r < StreamCapacity; r++)
        {
            bool hasData = r < rowCount;
            var  logRow  = hasData ? ViewModel.DataStream[r] : null;

            for (int c = 0; c < colCount; c++)
            {
                var tb = _streamCells[r, c];
                if (hasData && logRow != null && c < logRow.Values.Count)
                {
                    tb.Text = logRow.Values[c];
                    if (tb.Visibility != Visibility.Visible)
                        tb.Visibility = Visibility.Visible;
                }
                else
                {
                    if (tb.Visibility != Visibility.Collapsed)
                        tb.Visibility = Visibility.Collapsed;
                }
            }
        }
    }

    // ── State / UI ────────────────────────────────────────────────────────

    private void OnStateChanged() => DispatcherQueue.TryEnqueue(RefreshUi);

    private void RefreshUi()
    {
        bool active = Logging.IsLogging;

        StateText.Text       = active ? Lang.Log_Logging : Lang.Log_Idle;
        SampleText.Text      = Logging.SampleCount.ToString("N0");
        StartStopBtn.Content = active ? Lang.Log_StopLogging : Lang.Log_StartLogging;

        // Lock controls while recording
        FileNameBox.IsEnabled  = !active;
        FormatBox.IsEnabled    = !active;
        BrowseBtn.IsEnabled    = !active;

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
