using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
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

    private static string AppProductName =>
        typeof(App).Assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product
        ?? "Software BMS ICO";

    private static string AppVersion =>
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(App).Assembly.GetName().Version?.ToString(3)
        ?? "1.6.0";

    private string AboutProductText => $"{Lang.Ui_About_Product}: {AppProductName}";
    private string AboutVersionText => $"{Lang.Ui_About_Version}: {AppVersion}";
    private string AboutLicenseText => $"{Lang.Ui_About_License}: ICO Laboratory proprietary license";
    private string AboutCopyrightText => $"{Lang.Ui_About_Copyright}: (C) 2026 ICO Laboratory";

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
            UpdateUnitMenuState();
            Bindings.Update();
        };
        UpdateLangMenuState();
        UpdateUnitMenuState();

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

    // ── Unit menu ─────────────────────────────────────────────────────────
    private void UpdateUnitMenuState()
    {
        UnitTempC.IsChecked = ViewModel.TemperatureUnit == "C";
        UnitTempF.IsChecked = ViewModel.TemperatureUnit == "F";
        UnitVoltageV.IsChecked = ViewModel.VoltageUnit == "V";
        UnitVoltageMv.IsChecked = ViewModel.VoltageUnit == "mV";
        UnitCapacityMah.IsChecked = ViewModel.CapacityUnit == "mAh";
        UnitCapacityAh.IsChecked = ViewModel.CapacityUnit == "Ah";
    }

    private void UnitTemperature_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem item || item.Tag is not string unit)
            return;

        ViewModel.SetTemperatureUnit(unit);
        UpdateUnitMenuState();
    }

    private void UnitVoltage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem item || item.Tag is not string unit)
            return;

        ViewModel.SetVoltageUnit(unit);
        UpdateUnitMenuState();
    }

    private void UnitCapacity_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem item || item.Tag is not string unit)
            return;

        ViewModel.SetCapacityUnit(unit);
        UpdateUnitMenuState();
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

    private void MenuTour_Click(object sender, RoutedEventArgs e)
    {
        ShowTourOverlay();
    }

    private void ShowTourOverlay()
    {
        if (Content?.XamlRoot is null) return;

        double rootWidth = Content.XamlRoot.Size.Width;
        double rootHeight = Content.XamlRoot.Size.Height;
        double dialogWidth = Math.Min(620, Math.Max(320, rootWidth - 96));
        double contentWidth = Math.Max(300, dialogWidth - 60);

        TourDialogCard.Width = dialogWidth;
        TourDialogCard.MaxHeight = Math.Max(420, rootHeight - 96);
        TourDialogCard.Background = TourSurfaceBrush();
        TourDialogTitle.Text = TourText("Tour BMS Monitor", "BMS Monitor Tour");
        TourOpenControlPanelButton.Content = TourText("Buka Control Panel", "Open Control Panel");
        TourCloseButton.Content = TourText("Tutup", "Close");
        TourContentHost.Content = BuildTourContent(contentWidth, Math.Max(260, rootHeight - 250));
        TourOverlay.Visibility = Visibility.Visible;
    }

    private void TourOpenControlPanel_Click(object sender, RoutedEventArgs e)
    {
        HideTourOverlay();
        SelectControlPanelForTour();
    }

    private void TourClose_Click(object sender, RoutedEventArgs e)
    {
        HideTourOverlay();
    }

    private void HideTourOverlay()
    {
        TourOverlay.Visibility = Visibility.Collapsed;
        TourContentHost.Content = null;
    }

    private UIElement BuildTourContent(double availableWidth, double maxHeight)
    {
        const double scrollGutterWidth = 18;
        double scrollWidth = Math.Min(520, Math.Max(300, availableWidth));
        double panelWidth = Math.Max(280, scrollWidth - scrollGutterWidth);

        var panel = new StackPanel
        {
            Width = panelWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            Spacing = 14
        };

        panel.Children.Add(BuildTourHero(panelWidth));
        panel.Children.Add(BuildFlowCard(panelWidth));

        AddTourSection(
            panel,
            TourText("HALAMAN UTAMA", "MAIN PAGES"),
            ("\uE9D2", Lang.Nav_Dashboard, TourText(
                "Pantau tegangan pack, SOC, arus, status pack, ringkasan sel, dan grafik historis utama.",
                "Monitor pack voltage, SOC, current, pack status, cell summaries, and the main history charts.")),
            ("\uE8A5", Lang.Nav_CellView, TourText(
                "Lihat 20 sel dan 10 sensor NTC. Klik sel atau sensor untuk membuka grafik historis item tersebut.",
                "Inspect 20 cells and 10 NTC sensors. Click a cell or sensor to open its history chart.")),
            ("\uE713", Lang.Nav_ControlPanel, TourText(
                "Atur port COM, baud rate, auto-connect, kapasitas baterai, ambang proteksi, batas arus, dan balancing.",
                "Configure COM port, baud rate, auto-connect, battery capacity, protection thresholds, current limits, and balancing.")),
            ("\uE81C", Lang.Nav_Logging, TourText(
                "Rekam data live ke CSV, TSV, Excel, atau JSON, pilih folder output, dan pantau 20 frame terbaru.",
                "Record live data to CSV, TSV, Excel, or JSON, choose the output folder, and watch the latest 20 frames.")),
            ("\uE768", Lang.Nav_Playback, TourText(
                "Muat file log CSV lalu putar ulang data. Semua halaman ikut berubah seperti sedang menerima data live.",
                "Load a CSV log and replay it. Every page updates as if live data were coming in.")));

        AddTourSection(
            panel,
            TourText("TOMBOL CEPAT DI TITLE BAR", "TITLE BAR QUICK BUTTONS"),
            ("\uE7E7", TourText("Alert", "Alerts"), TourText(
                "Ikon lonceng membuka riwayat alert dan badge merah menampilkan jumlah alert baru.",
                "The bell opens alert history, and the red badge shows the number of unread alerts.")),
            ("\uE839", TourText("Serial", "Serial"), TourText(
                "Akses cepat untuk refresh port, memilih COM dan baud rate, lalu connect atau disconnect tanpa pindah halaman.",
                "Quick access to refresh ports, choose COM and baud rate, then connect or disconnect without changing pages.")),
            ("\uE12B", TourText("Bahasa", "Language"), TourText(
                "Ganti bahasa aplikasi langsung dari title bar.",
                "Switch the application language directly from the title bar.")),
            ("\uE708", TourText("Tema", "Theme"), TourText(
                "Beralih antara mode terang dan gelap sesuai kondisi kerja.",
                "Switch between light and dark mode for the current workspace.")));

        AddTourSection(
            panel,
            TourText("MENU, PLAYBACK, DAN STATUS", "MENUS, PLAYBACK, AND STATUS"),
            ("\uE8A5", Lang.Ui_Menu_View, TourText(
                "Tampilkan atau sembunyikan halaman di navigation bar agar workspace tetap ringkas.",
                "Show or hide pages in the navigation bar to keep the workspace focused.")),
            ("\uE9D2", Lang.Ui_Menu_Unit, TourText(
                "Pilih satuan temperatur, tegangan, dan kapasitas yang paling nyaman untuk dibaca.",
                "Choose the temperature, voltage, and capacity units that are easiest to read.")),
            ("\uE946", Lang.Ui_Menu_About, TourText(
                "Lihat nama produk, versi aplikasi, lisensi, dan informasi dasar lainnya.",
                "View product name, app version, license, and other basic information.")),
            ("\uE946", Lang.Ui_Menu_Tour, TourText(
                "Buka panduan fitur ini kapan saja dari menu title bar.",
                "Open this feature guide anytime from the title bar menu.")),
            ("\uE72C", Lang.Ui_Menu_RefreshApp, TourText(
                "Muat ulang halaman aktif saat tampilan grafik, dropdown, atau state visual perlu disegarkan.",
                "Reload the active page when charts, dropdowns, or visual state need a refresh.")),
            ("\uE768", TourText("Playback bar", "Playback bar"), TourText(
                "Saat file log dimuat, gunakan first, play atau pause, last, slider frame, dan unload untuk kembali ke live mode.",
                "When a log is loaded, use first, play or pause, last, the frame slider, and unload to return to live mode.")),
            ("\uE81E", TourText("Status bar", "Status bar"), TourText(
                "Bagian bawah menampilkan sumber data, status koneksi, dan jam aplikasi.",
                "The bottom bar shows data source, connection status, and the app clock.")));

        panel.Children.Add(new Border
        {
            Height = 20,
            IsHitTestVisible = false,
            Opacity = 0
        });

        return new ScrollViewer
        {
            Width = scrollWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
            Content = panel,
            MaxHeight = maxHeight,
            HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto
        };
    }

    private FrameworkElement BuildTourHero(double panelWidth)
    {
        double textWidth = Math.Max(220, panelWidth - 42 - 14 - 32);
        var text = new StackPanel { Spacing = 4, Width = textWidth };
        text.Children.Add(new TextBlock
        {
            Text = TourText("Kenali area kerja utama", "Get familiar with the workspace"),
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        text.Children.Add(new TextBlock
        {
            Text = TourText(
                "Panduan ini merangkum halaman, tombol, menu, playback, dan status bar agar pengguna baru langsung tahu harus mulai dari mana.",
                "This guide summarizes pages, buttons, menus, playback, and the status bar so new users know where to start."),
            FontSize = 13,
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap
        });

        var layout = new Grid { ColumnSpacing = 14 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = BuildTourIconShell("\uE946", 42);
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(text, 1);
        layout.Children.Add(icon);
        layout.Children.Add(text);

        return new Border
        {
            Width = panelWidth,
            Background = TourRaisedSurfaceBrush(),
            BorderBrush = TourStrokeBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Child = layout
        };
    }

    private FrameworkElement BuildFlowCard(double panelWidth)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(BuildTourSectionHeader(TourText("ALUR DISARANKAN", "RECOMMENDED FLOW")));
        panel.Children.Add(BuildTourFlowStep("1", TourText(
            "Hubungkan ESP32 lewat tombol Serial atau halaman Control Panel.",
            "Connect ESP32 from the Serial button or Control Panel."), panelWidth));
        panel.Children.Add(BuildTourFlowStep("2", TourText(
            "Pantau kondisi pack di Dashboard, lalu buka Cell View untuk detail tiap sel.",
            "Watch pack condition on Dashboard, then open Cell View for per-cell detail."), panelWidth));
        panel.Children.Add(BuildTourFlowStep("3", TourText(
            "Gunakan Logging untuk merekam sesi pengujian dan Playback untuk analisis ulang.",
            "Use Logging to record test sessions and Playback for later analysis."), panelWidth));

        return new Border
        {
            Width = panelWidth,
            Background = TourRaisedSurfaceBrush(),
            BorderBrush = TourStrokeBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Child = panel
        };
    }

    private void AddTourSection(
        StackPanel panel,
        string title,
        params (string Glyph, string Title, string Body)[] items)
    {
        panel.Children.Add(BuildTourSectionHeader(title));
        panel.Children.Add(BuildTourGrid(panel.Width, items));
    }

    private FrameworkElement BuildTourSectionHeader(string text) => new TextBlock
    {
        Text = text,
        FontSize = 11,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        CharacterSpacing = 80,
        Opacity = 0.58,
        Margin = new Thickness(0, 4, 0, -4)
    };

    private FrameworkElement BuildTourGrid(
        double panelWidth,
        (string Glyph, string Title, string Body)[] items)
    {
        int columns = 1;
        var grid = new Grid { Width = panelWidth, ColumnSpacing = 10, RowSpacing = 10 };
        for (int i = 0; i < columns; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < items.Length; i++)
        {
            int row = i / columns;
            while (grid.RowDefinitions.Count <= row)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var card = BuildTourCard(items[i].Glyph, items[i].Title, items[i].Body, panelWidth);
            Grid.SetRow(card, row);
            Grid.SetColumn(card, i % columns);
            grid.Children.Add(card);
        }

        return grid;
    }

    private FrameworkElement BuildTourCard(string glyph, string title, string body, double panelWidth)
    {
        double textWidth = Math.Max(220, panelWidth - 34 - 10 - 24);
        var text = new StackPanel { Spacing = 2, Width = textWidth };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        text.Children.Add(new TextBlock
        {
            Text = body,
            FontSize = 12,
            Opacity = 0.68,
            LineHeight = 17,
            TextWrapping = TextWrapping.Wrap
        });

        var layout = new Grid { ColumnSpacing = 10 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = BuildTourIconShell(glyph, 34);
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(text, 1);
        layout.Children.Add(icon);
        layout.Children.Add(text);

        return new Border
        {
            Width = panelWidth,
            Background = TourRaisedSurfaceBrush(),
            BorderBrush = TourStrokeBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = layout
        };
    }

    private FrameworkElement BuildTourIconShell(string glyph, double size)
    {
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(6),
            Background = TourIconSurfaceBrush(),
            Child = new FontIcon
            {
                FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
                Glyph = glyph,
                FontSize = size >= 40 ? 20 : 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    private FrameworkElement BuildTourFlowStep(string number, string body, double panelWidth)
    {
        double textWidth = Math.Max(220, panelWidth - 24 - 10 - 28);
        var layout = new Grid { ColumnSpacing = 10 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = TourIconSurfaceBrush(),
            Child = new TextBlock
            {
                Text = number,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var text = new TextBlock
        {
            Text = body,
            Width = textWidth,
            FontSize = 12,
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        Grid.SetColumn(badge, 0);
        Grid.SetColumn(text, 1);
        layout.Children.Add(badge);
        layout.Children.Add(text);
        return layout;
    }

    private void SelectControlPanelForTour()
    {
        foreach (var (nav, toggle) in NavToggles())
        {
            if (nav.Tag is not string tag || tag != "ControlPanel")
                continue;

            if (nav.Visibility != Visibility.Visible)
            {
                ApplyNavVisibility(nav, toggle, true);
                SaveNavVisibility();
            }

            NavView.SelectedItem = nav;
            return;
        }
    }

    private string TourText(string id, string en)
        => Lang.CurrentLanguage is "id" or "ms" ? id : en;

    private bool IsDarkTheme()
        => Content is FrameworkElement fe && fe.RequestedTheme == ElementTheme.Dark;

    private Brush TourSurfaceBrush()
        => new SolidColorBrush(IsDarkTheme()
            ? Color.FromArgb(0xFF, 0x24, 0x24, 0x24)
            : Color.FromArgb(0xFF, 0xFA, 0xFA, 0xFA));

    private Brush TourRaisedSurfaceBrush()
        => new SolidColorBrush(IsDarkTheme()
            ? Color.FromArgb(0xFF, 0x32, 0x32, 0x32)
            : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

    private Brush TourIconSurfaceBrush()
        => new SolidColorBrush(IsDarkTheme()
            ? Color.FromArgb(0xFF, 0x42, 0x42, 0x42)
            : Color.FromArgb(0xFF, 0xEE, 0xEE, 0xEE));

    private Brush TourStrokeBrush()
        => new SolidColorBrush(IsDarkTheme()
            ? Color.FromArgb(0xFF, 0x45, 0x45, 0x45)
            : Color.FromArgb(0xFF, 0xDD, 0xDD, 0xDD));

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
        // Flash the taskbar (FlashWindowEx) on Error/Alert severity so a
        // minimized or background window still draws the user's eye even if
        // the Action Center toast is dismissed or suppressed by Focus Assist.
        App.Notifications.CriticalAlertFired += _ =>
            DispatcherQueue.TryEnqueue(FlashTaskbarForCriticalAlert);
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

    // Flash the taskbar button (and caption when foreground) until the user
    // brings the window forward. Intentionally does NOT restore a minimized
    // window — the toast already surfaced the alert; flashing is the gentler
    // attention cue when toasts are suppressed.
    private void FlashTaskbarForCriticalAlert()
    {
        if (_hwnd == IntPtr.Zero) return;
        var info = new FLASHWINFO
        {
            cbSize    = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd      = _hwnd,
            dwFlags   = FLASHW_ALL | FLASHW_TIMERNOFG,
            uCount    = 0,
            dwTimeout = 0,
        };
        FlashWindowEx(ref info);
    }

    private const int GWLP_WNDPROC = -4;
    private const uint WM_GETMINMAXINFO = 0x0024;
    private const uint WM_NCRBUTTONDOWN = 0x00A4;
    private const uint WM_NCRBUTTONUP = 0x00A5;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const int HTCAPTION = 2;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    // FlashWindowEx flags
    private const uint FLASHW_CAPTION    = 0x00000001;
    private const uint FLASHW_TRAY       = 0x00000002;
    private const uint FLASHW_ALL        = FLASHW_CAPTION | FLASHW_TRAY;
    private const uint FLASHW_TIMERNOFG  = 0x0000000C;

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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }
}
