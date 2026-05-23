using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using BMSMonitor.Models;
using BMSMonitor.Services;
using BMSMonitor.ViewModels;
using BMSMonitor.Views;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace BMSMonitor;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    private Services.LocalizationManager Lang => App.Lang;

    public ObservableCollection<AlertRecord> AlertHistory { get; } = new();
    private int _unreadAlerts = 0;

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
    private IntPtr _hwnd;
    private IntPtr _oldWndProc;
    private WndProcDelegate? _newWndProc;

    public MainWindow()
    {
        ViewModel = new MainViewModel(DispatcherQueue);
        InitializeComponent();
        Title = "BMS Monitor";

        ApplyMicaBackdrop();
        InitializeTitleBar();
        SetAppIcon();
        InstallMinimumWindowSizeHook();
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

        // The theme button's tooltip is set in code, so it doesn't refresh
        // through {x:Bind ...}. Refresh it whenever the language switches.
        // Same for the language button's tooltip and checked-state.
        Lang.PropertyChanged += (_, _) =>
        {
            RefreshThemeButtonTooltip();
            UpdateLangMenuState();
            RefreshSerialButtonTooltip();
            SyncCapConnectButton();
        };
        UpdateLangMenuState();

        InitSerialFlyout();
        InitAlertFlyout();
    }

    public void MaximizeOnLaunch()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();
    }

    private void InstallMinimumWindowSizeHook()
    {
        _hwnd = WindowNative.GetWindowHandle(this);
        if (_hwnd == IntPtr.Zero) return;

        _newWndProc = WindowProc;
        _oldWndProc = SetWindowLongPtr(
            _hwnd,
            GWLP_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_newWndProc));

        Closed += (_, _) =>
        {
            if (_hwnd != IntPtr.Zero && _oldWndProc != IntPtr.Zero)
                SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _oldWndProc);
        };
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_NCRBUTTONDOWN && wParam == new IntPtr(HTCAPTION))
            return IntPtr.Zero;

        if (msg == WM_NCRBUTTONUP && wParam == new IntPtr(HTCAPTION))
        {
            ShowTitleBarCustomizeMenuFromCursor();
            return IntPtr.Zero;
        }

        if (msg == WM_CONTEXTMENU && IsCursorInTitleBar())
        {
            ShowTitleBarCustomizeMenuFromCursor();
            return IntPtr.Zero;
        }

        var result = CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);

        if (msg == WM_GETMINMAXINFO)
        {
            var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            int minWidth = GetHalfScreenWidth(hWnd);
            if (minWidth > 0 && info.ptMinTrackSize.x < minWidth)
            {
                info.ptMinTrackSize.x = minWidth;
                Marshal.StructureToPtr(info, lParam, false);
            }
        }

        return result;
    }

    private bool IsCursorInTitleBar()
    {
        if (!GetCursorPos(out var screenPoint)) return false;
        if (!ScreenToClient(_hwnd, ref screenPoint)) return false;

        double scale = Content?.XamlRoot?.RasterizationScale ?? 1;
        return screenPoint.y / scale <= AppTitleBar.Height;
    }

    private void ShowTitleBarCustomizeMenuFromCursor()
    {
        if (!GetCursorPos(out var screenPoint)) return;
        if (!ScreenToClient(_hwnd, ref screenPoint)) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            double scale = Content?.XamlRoot?.RasterizationScale ?? 1;
            TitleBarCustomizeMenu.ShowAt(
                NavView,
                new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
                {
                    Position = new Point(screenPoint.x / scale, screenPoint.y / scale)
                });
        });
    }

    private static int GetHalfScreenWidth(IntPtr hWnd)
    {
        IntPtr monitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return 0;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return 0;

        return Math.Max(0, (info.rcWork.right - info.rcWork.left) / 2);
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

        _initializing = true;
        ApplyTheme(dark ? ElementTheme.Dark : ElementTheme.Light);
        _initializing = false;
    }

    private void ApplyTheme(ElementTheme theme)
    {
        if (Content is FrameworkElement fe)
            fe.RequestedTheme = theme;
        UpdateTitleBarColors(theme);
        UpdateThemeButton(theme);
    }

    // Caption-style theme button: shows the icon for the mode the user would
    // switch INTO (sun when dark, moon when light) — matches the Visual
    // Studio Installer pattern.
    private void UpdateThemeButton(ElementTheme theme)
    {
        bool dark = theme == ElementTheme.Dark;
        // Segoe Fluent Icons:
        //   E706 = Brightness (sun) — shown when dark mode is active
        //   E708 = QuietHours (moon) — shown when light mode is active
        ThemeIcon.Glyph = dark ? "" : "";
        RefreshThemeButtonTooltip();
    }

    private void RefreshThemeButtonTooltip()
    {
        if (ThemeBtn is null) return;
        bool dark = Content is FrameworkElement fe && fe.RequestedTheme == ElementTheme.Dark;
        ToolTipService.SetToolTip(ThemeBtn, dark ? Lang.Ui_SwitchToLight : Lang.Ui_SwitchToDark);
    }

    private void ThemeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        var current = Content is FrameworkElement fe ? fe.RequestedTheme : ElementTheme.Default;
        // Default counts as light for the toggle direction
        bool currentlyDark = current == ElementTheme.Dark;
        ApplyTheme(currentlyDark ? ElementTheme.Light : ElementTheme.Dark);
    }

    // ── Language picker ───────────────────────────────────────────────────
    private void UpdateLangMenuState()
    {
        if (LangBtn is null) return;
        string cur = Lang.CurrentLanguage;
        LangItemId.IsChecked = (cur == "id");
        LangItemMs.IsChecked = (cur == "ms");
        LangItemEn.IsChecked = (cur == "en");
        LangItemNl.IsChecked = (cur == "nl");
        LangItemZh.IsChecked = (cur == "zh");
        ToolTipService.SetToolTip(LangBtn, Lang.Ui_ChangeLanguage);
    }

    private void LangItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem item && item.Tag is string tag)
            Lang.CurrentLanguage = tag;
    }

    // ── Navigation ────────────────────────────────────────────────────────
    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        // Apply persisted nav-item visibility, then select the first visible.
        ApplyNavVisibilityFromSettings();
        NavView.SelectedItem = FirstVisibleNavItem() ?? NavView.MenuItems[0];
        UpdateTitleBarLayout();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            if (_pages.TryGetValue(tag, out var pageType))
                ContentFrame.Navigate(pageType);
    }

    // ── Logo customize menu ──────────────────────────────────────────────
    private void TitleBar_RightTapped(
        object sender,
        Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        var position = e.GetPosition(NavView);
        if (position.Y > AppTitleBar.Height) return;

        e.Handled = true;
        TitleBarCustomizeMenu.ShowAt(
            NavView,
            new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
            {
                Position = position
            });
    }

    private (NavigationViewItem nav, ToggleMenuFlyoutItem toggle)[] NavToggles() =>
    [
        (NavDashboard,    ViewNavDashboard),
        (NavCellView,     ViewNavCellView),
        (NavControlPanel, ViewNavControlPanel),
        (NavLogging,      ViewNavLogging),
        (NavPlayback,     ViewNavPlayback),
    ];

    private void ApplyNavVisibilityFromSettings()
    {
        var s = AppSettingsService.Load();
        ApplyNavVisibility(NavDashboard,    ViewNavDashboard,    s.ShowNav_Dashboard);
        ApplyNavVisibility(NavCellView,     ViewNavCellView,     s.ShowNav_CellView);
        ApplyNavVisibility(NavControlPanel, ViewNavControlPanel, s.ShowNav_ControlPanel);
        ApplyNavVisibility(NavLogging,      ViewNavLogging,      s.ShowNav_Logging);
        ApplyNavVisibility(NavPlayback,     ViewNavPlayback,     s.ShowNav_Playback);
    }

    private static void ApplyNavVisibility(NavigationViewItem item, ToggleMenuFlyoutItem toggle, bool show)
    {
        item.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        toggle.IsChecked = show;
    }

    private NavigationViewItem? FirstVisibleNavItem()
    {
        foreach (var (nav, _) in NavToggles())
            if (nav.Visibility == Visibility.Visible) return nav;
        return null;
    }

    private void ViewNavToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem toggle || toggle.Tag is not string tag)
            return;

        var match = NavToggles().FirstOrDefault(t => t.nav.Tag is string s && s == tag);
        if (match.nav is null) return;

        // Don't allow hiding the last visible item — there must always be at
        // least one page to land on.
        if (!toggle.IsChecked &&
            NavToggles().Count(t => t.nav.Visibility == Visibility.Visible) <= 1)
        {
            toggle.IsChecked = true;
            return;
        }

        match.nav.Visibility = toggle.IsChecked ? Visibility.Visible : Visibility.Collapsed;

        // If the hidden item was selected, jump to the first visible one.
        if (!toggle.IsChecked && ReferenceEquals(NavView.SelectedItem, match.nav))
            NavView.SelectedItem = FirstVisibleNavItem();

        SaveNavVisibility();
    }

    private void SaveNavVisibility()
    {
        var s = AppSettingsService.Load();
        s.ShowNav_Dashboard    = ViewNavDashboard.IsChecked;
        s.ShowNav_CellView     = ViewNavCellView.IsChecked;
        s.ShowNav_ControlPanel = ViewNavControlPanel.IsChecked;
        s.ShowNav_Logging      = ViewNavLogging.IsChecked;
        s.ShowNav_Playback     = ViewNavPlayback.IsChecked;
        AppSettingsService.Save(s);
    }

    private void RefreshApp_Click(object sender, RoutedEventArgs e)
    {
        // Re-navigate to the current page so it tears down and rebuilds —
        // covers stale chart visuals, ComboBox state, etc.
        if (NavView.SelectedItem is not NavigationViewItem item ||
            item.Tag is not string tag ||
            !_pages.TryGetValue(tag, out var pageType))
            return;

        ContentFrame.Content = null;
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

    // ── Alert history ────────────────────────────────────────────────────
    private void InitAlertFlyout()
    {
        App.Notifications.AlertFired += OnAlertFired;
        NoAlertsText.Visibility = Visibility.Visible;

        // Route serial errors (parse failures, port errors) to the alert history.
        ViewModel.Serial.ErrorOccurred += msg =>
            App.Notifications.LogDiagnostic(AlertSeverity.Error, "Serial Error", msg);

        // Log meaningful connection state changes — skip intermediate status strings.
        ViewModel.Serial.StatusChanged += msg =>
        {
            AlertSeverity sev;
            if (msg.StartsWith("Connected"))             sev = AlertSeverity.Info;
            else if (msg == "Disconnected")              sev = AlertSeverity.Warning;
            else return;   // skip "Mendeteksi…" and other intermediate strings
            App.Notifications.LogDiagnostic(sev, "Connection", msg);
        };

        // Note: auto-connect probe notifications are intentionally NOT piped
        // into the alert history. They fire every scan cycle and would drown
        // out genuinely critical alerts (overvoltage, overtemp, etc.).
        // The Control Panel still shows them inline as a live status label.
    }

    private void OnAlertFired(AlertRecord rec)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            AlertHistory.Add(rec);
            _unreadAlerts++;
            UpdateAlertBadge();
            NoAlertsText.Visibility = Visibility.Collapsed;
        });
    }

    private void UpdateAlertBadge()
    {
        if (_unreadAlerts > 0)
        {
            AlertBadge.Visibility = Visibility.Visible;
            AlertBadgeText.Text   = _unreadAlerts > 99 ? "99+" : _unreadAlerts.ToString();
        }
        else
        {
            AlertBadge.Visibility = Visibility.Collapsed;
        }
    }

    private void AlertFlyout_Opening(object sender, object e)
    {
        _unreadAlerts = 0;
        UpdateAlertBadge();
    }

    private void ClearAlerts_Click(object sender, RoutedEventArgs e)
    {
        AlertHistory.Clear();
        App.Notifications.ClearHistory();
        _unreadAlerts = 0;
        UpdateAlertBadge();
        NoAlertsText.Visibility = Visibility.Visible;
    }

    // ── Caption-bar serial picker ────────────────────────────────────────
    private void InitSerialFlyout()
    {
        PopulateCapBauds();
        RefreshCapChannels();
        RefreshSerialButtonTooltip();
        UpdateSerialStatusDot();

        // Mirror connection status into the flyout text + the status dot.
        ViewModel.Serial.StatusChanged += msg => DispatcherQueue.TryEnqueue(() =>
        {
            CapConnStatus.Text = msg;
            SyncCapConnectButton();
            UpdateSerialStatusDot();
        });

        // Re-sync channel preselection whenever the flyout opens, so the
        // dropdown reflects the live channel even if the user connected
        // from the Control Panel instead.
        SerialFlyout.Opening += (_, _) =>
        {
            RefreshCapChannels();
            SyncCapConnectButton();
        };
    }

    private void RefreshSerialButtonTooltip()
    {
        if (SerialBtn is null) return;
        ToolTipService.SetToolTip(SerialBtn, Lang.Ui_SerialQuickAccess);
    }

    private void UpdateSerialStatusDot()
    {
        if (SerialStatusDot is null) return;
        // Green when connected, dimmed when idle. Tinted from theme
        // resources so it adapts to dark/light mode automatically.
        SerialStatusDot.Fill = ViewModel.Serial.IsConnected
            ? new SolidColorBrush(Color.FromArgb(0xFF, 0x25, 0xC6, 0x85))
            : new SolidColorBrush(Color.FromArgb(0xCC, 0x9E, 0x9E, 0x9E));
    }

    private void PopulateCapBauds()
    {
        CapSerialBaud.Items.Clear();
        foreach (var b in ViewModel.Serial.Bitrates)
            CapSerialBaud.Items.Add(new ComboBoxItem { Content = b.DisplayName, Tag = b });

        int defBaud = ViewModel.Serial.DefaultBitrate;
        for (int i = 0; i < CapSerialBaud.Items.Count; i++)
        {
            if (CapSerialBaud.Items[i] is ComboBoxItem item &&
                item.Tag is SerialBaud br &&
                br.Baud == defBaud)
            {
                CapSerialBaud.SelectedIndex = i;
                return;
            }
        }
        if (CapSerialBaud.SelectedIndex < 0 && CapSerialBaud.Items.Count > 0)
            CapSerialBaud.SelectedIndex = 0;
    }

    private void RefreshCapChannels()
    {
        var previous = (CapSerialPort.SelectedItem as ComboBoxItem)?.Tag as SerialPortInfo;
        CapSerialPort.Items.Clear();

        if (!ViewModel.Serial.IsDriverAvailable)
        {
            CapSerialPort.PlaceholderText = Lang.Ctrl_PhNoPorts;
            return;
        }

        foreach (var ch in ViewModel.Serial.Channels)
            CapSerialPort.Items.Add(new ComboBoxItem { Content = ch.DisplayName, Tag = ch });

        CapSerialPort.PlaceholderText = Lang.Ctrl_PhScanning;

        // Prefer the live channel — fall back to whatever the user picked last,
        // then default to the first entry.
        string live = ViewModel.Serial.Channel;     // "" when not connected
        for (int i = 0; i < CapSerialPort.Items.Count; i++)
        {
            if (CapSerialPort.Items[i] is ComboBoxItem it &&
                it.Tag is SerialPortInfo c &&
                (string.Equals(c.PortName, live, StringComparison.OrdinalIgnoreCase)
                 || (string.IsNullOrEmpty(live) && previous != null
                     && string.Equals(c.PortName, previous.PortName, StringComparison.OrdinalIgnoreCase))))
            {
                CapSerialPort.SelectedIndex = i;
                return;
            }
        }
        if (CapSerialPort.Items.Count > 0)
            CapSerialPort.SelectedIndex = 0;
    }

    private void SyncCapConnectButton()
    {
        if (CapConnectBtn is null) return;
        bool connected = ViewModel.Serial.IsConnected;
        CapConnectBtn.Content   = connected ? Lang.Ctrl_Disconnect : Lang.Ctrl_Connect;
        CapSerialPort.IsEnabled = !connected;
        CapSerialBaud.IsEnabled = !connected;
        if (!connected) CapConnStatus.Text = Lang.Ctrl_NotConnected;
    }

    private void CapRefresh_Click(object sender, RoutedEventArgs e) => RefreshCapChannels();

    private void CapConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Serial.IsConnected)
        {
            ViewModel.AutoConnect.SuspendReconnect();
            ViewModel.Serial.Disconnect();
            return;
        }

        if (CapSerialPort.SelectedItem is not ComboBoxItem chItem ||
            chItem.Tag is not SerialPortInfo channel)
        {
            CapConnStatus.Text = Lang.Fb_SelectChannelMsg;
            return;
        }

        if (CapSerialBaud.SelectedItem is not ComboBoxItem brItem ||
            brItem.Tag is not SerialBaud bitrate)
        {
            CapConnStatus.Text = Lang.Fb_SelectChannelMsg;
            return;
        }

        ViewModel.AutoConnect.Baud = bitrate.Baud;
        ViewModel.AutoConnect.ResumeReconnect();
        ViewModel.Serial.Connect(channel, bitrate);
    }

    private const int GWLP_WNDPROC = -4;
    private const uint WM_GETMINMAXINFO = 0x0024;
    private const uint WM_NCRBUTTONDOWN = 0x00A4;
    private const uint WM_NCRBUTTONUP = 0x00A5;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const int HTCAPTION = 2;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    private delegate IntPtr WndProcDelegate(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(
        IntPtr hWnd,
        int nIndex,
        IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW", SetLastError = true)]
    private static extern IntPtr CallWindowProc(
        IntPtr lpPrevWndFunc,
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
