using System.IO;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using BMSMonitor.ViewModels;
using BMSMonitor.Views;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace BMSMonitor;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    private Services.LocalizationManager Lang => App.Lang;

    private readonly Dictionary<string, Type> _pages = new()
    {
        { "Dashboard",    typeof(DashboardPage) },
        { "CellView",     typeof(CellViewPage) },
        { "ControlPanel", typeof(ControlPanelPage) },
        { "Logging",      typeof(LoggingPage) },
        { "Playback",     typeof(PlaybackPage) }
    };

    private bool _pbSeeking; // suppress slider feedback loop

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _clock;
    private bool _initializing;

    public MainWindow()
    {
        ViewModel = new MainViewModel(DispatcherQueue);
        InitializeComponent();
        Title = "BMS Monitor";

        ApplyMicaBackdrop();
        InitializeTitleBar();
        SetAppIcon();
        InitializeTheme();

        _clock = DispatcherQueue.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
        _clock.Start();

        // Playback bar — subscribe so it appears/updates whenever the service fires
        ViewModel.Playback.StateChanged += OnPlaybackStateChanged;

        // Shrink the drag region whenever window size or toggle size changes.
        SizeChanged += (_, _) => UpdateTitleBarLayout();
        ThemeToggleArea.SizeChanged += (_, _) => UpdateTitleBarLayout();
    }

    // ── Icon ─────────────────────────────────────────────────────────────
    private void SetAppIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);
    }

    // ── Mica ─────────────────────────────────────────────────────────────
    private void ApplyMicaBackdrop()
    {
        if (MicaController.IsSupported())
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
    }

    // ── Title bar ─────────────────────────────────────────────────────────
    private void InitializeTitleBar()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
    }

    // Position toggle next to caption buttons and shrink AppTitleBar so the
    // toggle area is outside the drag region (no passthrough tricks needed).
    private void UpdateTitleBarLayout()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        var root = Content?.XamlRoot;
        if (root == null) return;

        double scale      = root.RasterizationScale;
        double rightInset = AppWindow.TitleBar.RightInset / scale;
        if (rightInset <= 0) rightInset = 138; // Win11 three-button fallback

        // Shift toggle left of caption buttons
        ThemeToggleArea.Margin = new Thickness(0, 0, rightInset, 0);

        // Exclude toggle area from the drag region
        double toggleWidth = ThemeToggleArea.ActualWidth;
        if (toggleWidth > 0)
            AppTitleBar.Margin = new Thickness(0, 0, rightInset + toggleWidth, 0);
    }

    private void UpdateTitleBarColors(ElementTheme theme)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        var bar = AppWindow.TitleBar;
        bool dark = theme == ElementTheme.Dark;

        bar.ButtonBackgroundColor         = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor         = dark ? Colors.White : Colors.Black;
        bar.ButtonInactiveForegroundColor = dark
            ? Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x66, 0x00, 0x00, 0x00);
        bar.ButtonHoverBackgroundColor = dark
            ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x1A, 0x00, 0x00, 0x00);
        bar.ButtonHoverForegroundColor   = dark ? Colors.White : Colors.Black;
        bar.ButtonPressedBackgroundColor = dark
            ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x33, 0x00, 0x00, 0x00);
        bar.ButtonPressedForegroundColor = dark ? Colors.White : Colors.Black;
    }

    // ── Theme ─────────────────────────────────────────────────────────────
    private void InitializeTheme()
    {
        // Follow system dark/light preference on first launch.
        var uiSettings = new UISettings();
        var bg   = uiSettings.GetColorValue(UIColorType.Background);
        bool dark = bg.R < 128;

        _initializing    = true;
        ThemeSwitch.IsOn = dark;
        _initializing    = false;

        ApplyTheme(dark ? ElementTheme.Dark : ElementTheme.Light);
    }

    private void ApplyTheme(ElementTheme theme)
    {
        if (Content is FrameworkElement fe)
            fe.RequestedTheme = theme;
        UpdateTitleBarColors(theme);
    }

    private void ThemeSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        ApplyTheme(ThemeSwitch.IsOn ? ElementTheme.Dark : ElementTheme.Light);
    }

    // ── Navigation ────────────────────────────────────────────────────────
    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        NavView.SelectedItem = NavView.MenuItems[0];
        UpdateTitleBarLayout();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            if (_pages.TryGetValue(tag, out var pageType))
                ContentFrame.Navigate(pageType);
    }

    // ── Playback bar ──────────────────────────────────────────────────────
    private void OnPlaybackStateChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var pb = ViewModel.Playback;
            PlaybackBar.Visibility = pb.IsLoaded
                ? Microsoft.UI.Xaml.Visibility.Visible
                : Microsoft.UI.Xaml.Visibility.Collapsed;

            if (!pb.IsLoaded) return;

            PbFileText.Text    = pb.FileName;
            PbFrameText.Text   = $"{pb.CurrentFrame + 1} / {pb.TotalFrames}  ·  {pb.CurrentTimestamp}";
            // E769 = Play, E103 = Pause  (Segoe MDL2 Assets)
            PbPlayPauseIcon.Glyph = pb.IsPlaying ? "" : "";

            _pbSeeking = true;
            PbSlider.Maximum = Math.Max(1, pb.TotalFrames - 1);
            PbSlider.Value   = pb.CurrentFrame;
            _pbSeeking = false;
        });
    }

    private void PbPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Playback.IsPlaying) ViewModel.Playback.Pause();
        else                              ViewModel.Playback.Play();
    }

    private void PbFirst_Click(object sender, RoutedEventArgs e)
        => ViewModel.Playback.SeekTo(0);

    private void PbLast_Click(object sender, RoutedEventArgs e)
        => ViewModel.Playback.SeekTo(ViewModel.Playback.TotalFrames - 1);

    private void PbClose_Click(object sender, RoutedEventArgs e)
        => ViewModel.Playback.Unload();

    private void PbSlider_ValueChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_pbSeeking) return;
        ViewModel.Playback.SeekTo((int)Math.Round(e.NewValue));
    }
}
