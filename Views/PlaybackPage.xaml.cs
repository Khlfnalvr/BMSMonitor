using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using BMSMonitor.Services;
using BMSMonitor.ViewModels;
using Windows.Storage.Pickers;

namespace BMSMonitor.Views;

public sealed partial class PlaybackPage : Page
{
    private MainViewModel  ViewModel => App.ViewModel;
    private PlaybackService Playback  => ViewModel.Playback;

    private static readonly double[] Speeds = { 1.0, 2.0, 5.0, 10.0 };

    public PlaybackPage()
    {
        InitializeComponent();
        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Playback.StateChanged += OnPlaybackStateChanged;
        RefreshUi();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Playback.StateChanged -= OnPlaybackStateChanged;
    }

    private void OnPlaybackStateChanged() => DispatcherQueue.TryEnqueue(RefreshUi);

    private void RefreshUi()
    {
        bool loaded = Playback.IsLoaded;
        UnloadBtn.IsEnabled = loaded;

        if (loaded)
        {
            FilePathText.Text    = Playback.FileName;
            TotalFramesText.Text = Playback.TotalFrames.ToString("N0");

            // Duration estimate: assume ~1 Hz logging
            var dur = TimeSpan.FromSeconds(Playback.TotalFrames);
            DurationText.Text = dur.TotalHours >= 1
                ? $"{(int)dur.TotalHours:D2}:{dur.Minutes:D2}:{dur.Seconds:D2}"
                : $"{dur.Minutes:D2}:{dur.Seconds:D2}";

            LoadStatusText.Text = $"Loaded {Playback.TotalFrames:N0} frames from \"{Playback.FileName}\".";
            HintText.Text = Playback.IsPlaying
                ? "Playback in progress. Use the bar at the bottom to pause or seek."
                : "File loaded. Use the playback bar at the bottom to play.";
        }
        else
        {
            FilePathText.Text    = "No file loaded";
            TotalFramesText.Text = "—";
            DurationText.Text    = "—";
            LoadStatusText.Text  = "Browse and open a BMS Monitor log file (.csv, .tsv, .xlsx, .json).";
            HintText.Text        = "Browse and load a log file above, then use the playback bar that appears at the bottom of the window.";
        }
    }

    // ── Event handlers ───────────────────────────────────────────────────

    private async void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        var hwnd   = WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        foreach (var ext in PlaybackService.SupportedExtensions)
            picker.FileTypeFilter.Add(ext);

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        LoadStatusText.Text = "Loading…";
        var err = Playback.LoadFile(file.Path);
        if (err is not null)
            LoadStatusText.Text = $"Error: {err}";
    }

    private void Unload_Click(object sender, RoutedEventArgs e) => Playback.Unload();

    private void SpeedBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int idx = SpeedBox.SelectedIndex;
        if (idx < 0 || idx >= Speeds.Length) return;
        Playback.PlaybackSpeed = Speeds[idx];

        // Restart timer at new speed if currently playing
        if (Playback.IsPlaying) { Playback.Pause(); Playback.Play(); }
    }
}
