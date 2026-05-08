using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SOCTester.Services;
using SOCTester.ViewModels;
using Windows.Storage.Pickers;

namespace SOCTester.Views;

public sealed partial class LoggingPage : Page
{
    private MainViewModel  ViewModel => App.ViewModel;
    private LoggingService Logging   => ViewModel.Logging;

    private string _selectedFolder = LoggingService.DefaultLogsFolder;
    private DispatcherQueueTimer? _durationTimer;

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

        // Show/hide stream placeholder when collection changes
        ViewModel.DataStream.CollectionChanged += (_, _) => UpdateStreamPlaceholder();
        UpdateStreamPlaceholder();
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

        StateText.Text       = active ? "Logging" : "Idle";
        SampleText.Text      = Logging.SampleCount.ToString("N0");
        StartStopBtn.Content = active ? "Stop Logging" : "Start Logging";

        // Lock folder/filename controls while recording
        FileNameBox.IsEnabled = !active;
        BrowseBtn.IsEnabled   = !active;

        if (active)
        {
            FolderText.Text   = Path.GetDirectoryName(Logging.FilePath) ?? _selectedFolder;
            FileNameBox.Text  = Path.GetFileName(Logging.FilePath) ?? "";
            FullPathText.Text = Logging.FilePath ?? "";
            HintText.Text     = "Recording in progress. Stop to close the file.";
        }
        else
        {
            FolderText.Text   = _selectedFolder;
            FullPathText.Text = BuildPreviewPath();
            HintText.Text     = ViewModel.IsConnected
                ? "Ready. Press Start Logging to begin recording."
                : "Connect to the device first, then start logging.";
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
            DurationText.Text = Logging.SampleCount > 0 ? Logging.Duration.ToString(@"mm\:ss") : "—";
        }
    }

    private string BuildPreviewPath()
    {
        string name = FileNameBox?.Text.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
            name = LoggingService.GenerateFileName();
        if (!name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            name += ".csv";
        return Path.Combine(_selectedFolder, name);
    }

    // ── Event handlers ─────────────────────────────────────────────────────────

    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();

        // Required for unpackaged apps: initialise with window handle
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentWindow);
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
            Logging.Start(path);
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
