using System.IO;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SOCTester.ViewModels;
using SOCTester.Views;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace SOCTester;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    private readonly Dictionary<string, Type> _pages = new()
    {
        { "Dashboard",    typeof(DashboardPage) },
        { "ControlPanel", typeof(ControlPanelPage) },
        { "Logging",      typeof(LoggingPage) }
    };

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _clock;
    private bool _initializing;

    public MainWindow()
    {
        ViewModel = new MainViewModel(DispatcherQueue);
        InitializeComponent();
        Title = "SOC Tester";

        ApplyMicaBackdrop();
        InitializeTitleBar();
        SetAppIcon();
        InitializeTheme();

        _clock = DispatcherQueue.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
        _clock.Start();

        SizeChanged += (_, _) => UpdateTitleBarLayout();
        ThemeToggleArea.SizeChanged += (_, _) => UpdateTitleBarLayout();
    }

    private void SetAppIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);
    }

    private void ApplyMicaBackdrop()
    {
        if (MicaController.IsSupported())
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
    }

    private void InitializeTitleBar()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
    }

    private void UpdateTitleBarLayout()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        var root = Content?.XamlRoot;
        if (root == null) return;

        double scale      = root.RasterizationScale;
        double rightInset = AppWindow.TitleBar.RightInset / scale;
        if (rightInset <= 0) rightInset = 138;

        ThemeToggleArea.Margin = new Thickness(0, 0, rightInset, 0);

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

    private void InitializeTheme()
    {
        var uiSettings = new UISettings();
        var bg = uiSettings.GetColorValue(UIColorType.Background);
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
}
