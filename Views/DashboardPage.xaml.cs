using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using BMSMonitor.ViewModels;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace BMSMonitor.Views;

public sealed partial class DashboardPage : Page
{
    private MainViewModel ViewModel => App.ViewModel;
    private Services.LocalizationManager Lang => App.Lang;

    // ── Dynamic X-axis tick pools ──────────────────────────────────────────
    // Pre-allocated once; reused on every redraw (no UIElement churn).
    private const int MaxTicks = 24;
    private TextBlock[] _socTicks = [];
    private TextBlock[] _viTicks  = [];

    // ── Chart layout defaults ──────────────────────────────────────────────
    // 4:3 / 600×450 ≈ Origin Pro page (16.5 × 12 cm) — standard research figure.
    private const double DefaultAspect = 4.0 / 3.0;
    private const double DefaultWidth  = 600;
    private const double DefaultHeight = 450;
    private bool _applyingLayout;   // re-entrancy guard for slider events

    public DashboardPage()
    {
        InitializeComponent();
        InitializeTickPools();
        ApplyChartColors();
        PopulateTimeframeCombo();
        UpdateXAxisLabels();
        ApplyChartLayout();   // apply default dimensions on first load

        Loaded   += (_, _) => ViewModel.HistoryUpdated += OnHistoryUpdated;
        Unloaded += (_, _) => ViewModel.HistoryUpdated -= OnHistoryUpdated;
    }

    // ── Chart layout (size & aspect ratio) ────────────────────────────────

    private double GetSelectedAspect()
    {
        if (AspectCombo?.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
            double.TryParse(tag, NumberStyles.Any, CultureInfo.InvariantCulture, out double a))
            return a;
        return DefaultAspect;
    }

    private void AspectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ApplyChartLayout();

    private void WidthSlider_ValueChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_applyingLayout) return;
        ApplyChartLayout();
    }

    private void HeightSlider_ValueChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_applyingLayout) return;
        ApplyChartLayout();
    }

    private void ResetChartSize_Click(object sender, RoutedEventArgs e)
    {
        _applyingLayout = true;
        AspectCombo.SelectedIndex = 0;     // 4:3 paper default
        WidthSlider.Value         = DefaultWidth;
        HeightSlider.Value        = DefaultHeight;
        _applyingLayout = false;
        ApplyChartLayout();
    }

    /// <summary>
    /// Recomputes chart card dimensions from the current control values.
    /// When aspect ≠ Free: height = width / aspect (height slider read-only).
    /// When aspect = Free: height comes directly from the height slider.
    /// </summary>
    private void ApplyChartLayout()
    {
        if (SocChartCard is null) return;   // not yet initialized

        double w = WidthSlider.Value;
        double aspect = GetSelectedAspect();
        bool freeMode = aspect <= 0;
        double h = freeMode ? HeightSlider.Value : w / aspect;

        // Sync the height slider when aspect is locked (without re-firing handler)
        _applyingLayout = true;
        HeightSlider.IsEnabled = freeMode;
        if (!freeMode) HeightSlider.Value = h;
        _applyingLayout = false;

        WidthValue.Text  = w.ToString("0", CultureInfo.InvariantCulture);
        HeightValue.Text = h.ToString("0", CultureInfo.InvariantCulture);

        // ── SOC chart ──
        SocChartCard.MaxWidth   = w;
        SocChartCanvas.Height   = h;
        SocYAxisGrid.Height     = h;

        // ── V/I chart ──
        VIChartCard.MaxWidth    = w;
        VIChartCanvas.Height    = h;
        VAxisCanvas.Height      = h;
        IAxisCanvas.Height      = h;

        // Canvas SizeChanged will fire and trigger redraw — no explicit call needed.
    }

    private void InitializeTickPools()
    {
        _socTicks = new TextBlock[MaxTicks];
        _viTicks  = new TextBlock[MaxTicks];
        for (int i = 0; i < MaxTicks; i++)
        {
            _socTicks[i] = MakeTickLabel();
            SocChartCanvas.Children.Add(_socTicks[i]);
            _viTicks[i] = MakeTickLabel();
            VIChartCanvas.Children.Add(_viTicks[i]);
        }
    }

    private static TextBlock MakeTickLabel() => new()
    {
        FontSize        = 9,
        Opacity         = 0.55,
        FontFamily      = new FontFamily("Consolas"),
        TextAlignment   = TextAlignment.Center,
        Visibility      = Visibility.Collapsed
    };

    private void PopulateTimeframeCombo()
    {
        TimeframeCombo.Items.Clear();
        foreach (var (minutes, label) in MainViewModel.TimeframeOptions)
            TimeframeCombo.Items.Add(label);
        for (int i = 0; i < MainViewModel.TimeframeOptions.Length; i++)
        {
            if (Math.Abs(MainViewModel.TimeframeOptions[i].Minutes - ViewModel.HistoryTimeframeMinutes) < 0.001)
            {
                TimeframeCombo.SelectedIndex = i;
                break;
            }
        }
    }

    private void TimeframeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TimeframeCombo.SelectedIndex < 0 ||
            TimeframeCombo.SelectedIndex >= MainViewModel.TimeframeOptions.Length)
            return;

        ViewModel.HistoryTimeframeMinutes = MainViewModel.TimeframeOptions[TimeframeCombo.SelectedIndex].Minutes;
        UpdateXAxisLabels();
    }

    /// <summary>
    /// Updates the bottom legend strip under each chart.
    /// Left: window size (e.g. "Window: 2 min · 1 sample/s").
    /// Right: time unit (e.g. "time (seconds)" / "(minutes)" / "(hours)").
    /// </summary>
    private void UpdateXAxisLabels(string socUnit = "", string viUnit = "")
    {
        string windowLabel;
        if (ViewModel.HistoryTimeframeMinutes > 0)
        {
            double mins = ViewModel.HistoryTimeframeMinutes;
            windowLabel = mins >= 1
                ? $"Window: {mins:0.#} min  ·  1 sample/s"
                : $"Window: {mins * 60:0} s  ·  1 sample/s";
        }
        else
        {
            windowLabel = "Window: All data  ·  1 sample/s";
        }

        SocTimeAgoLabel.Text = windowLabel;
        SocNowLabel.Text     = string.IsNullOrEmpty(socUnit) ? "" : $"time ({socUnit})";
        VITimeAgoLabel.Text  = windowLabel;
        VINowLabel.Text      = string.IsNullOrEmpty(viUnit)  ? "" : $"time ({viUnit})";
    }

    // ── Chart colors ──────────────────────────────────────────────────────
    private void ApplyChartColors()
    {
        var ui     = new UISettings();
        var accent = ui.GetColorValue(UIColorType.Accent);
        SocLine.Stroke = new SolidColorBrush(accent);
        SocFill.Fill   = new SolidColorBrush(accent);

        var gridBrush = new SolidColorBrush(Color.FromArgb(35, 128, 128, 128));
        GridLine25.Stroke = gridBrush;
        GridLine50.Stroke = gridBrush;
        GridLine75.Stroke = gridBrush;

        // V/I chart — voltage = blue, current = orange
        VoltageLine.Stroke = new SolidColorBrush(Color.FromArgb(255,  33, 150, 243));
        CurrentLine.Stroke = new SolidColorBrush(Color.FromArgb(255, 255, 152,   0));
        VLegendRect.Fill   = new SolidColorBrush(Color.FromArgb(255,  33, 150, 243));
        ILegendRect.Fill   = new SolidColorBrush(Color.FromArgb(255, 255, 152,   0));
        VIGridH1.Stroke    = gridBrush;
        VIGridH2.Stroke    = gridBrush;
        VIGridH3.Stroke    = gridBrush;
    }

    // ── Chart drawing ─────────────────────────────────────────────────────
    private void OnHistoryUpdated()
    {
        string socUnit = RedrawSocChart();
        string viUnit  = RedrawVIChart();
        UpdateXAxisLabels(socUnit, viUnit);
    }

    private void SocChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        string u = RedrawSocChart();
        if (!string.IsNullOrEmpty(u))
            SocNowLabel.Text = $"time ({u})";
    }

    private void VIChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        string u = RedrawVIChart();
        if (!string.IsNullOrEmpty(u))
            VINowLabel.Text = $"time ({u})";
    }

    /// <returns>The x-axis unit ("seconds" / "minutes" / "hours") or empty if no data.</returns>
    private string RedrawSocChart()
    {
        double w = SocChartCanvas.ActualWidth;
        double h = SocChartCanvas.ActualHeight;
        if (w == 0 || h == 0) return "";

        // Horizontal grid lines at 25 / 50 / 75 %
        UpdateGridLine(GridLine25, w, h * 0.75);
        UpdateGridLine(GridLine50, w, h * 0.50);
        UpdateGridLine(GridLine75, w, h * 0.25);

        double[] history = ViewModel.GetSocHistory();
        int      n       = history.Length;

        // Use effective capacity: in "All" mode capacity is 0 → use actual count.
        double cap    = ViewModel.HistoryCapacity;
        if (cap <= 0 || cap > n) cap = n;
        double xStep  = cap > 1 ? w / (cap - 1.0) : w;
        double xStart = (cap - n) * xStep;

        if (n < 2)
        {
            SocLine.Points = [];
            SocFill.Points = [];
            HideTicks(_socTicks);
            return "";
        }

        var linePoints = new PointCollection();
        var fillPoints = new PointCollection();

        // Fill polygon: start at bottom-left corner
        fillPoints.Add(new Point(xStart, h));

        for (int i = 0; i < n; i++)
        {
            double x = xStart + i * xStep;
            double y = h * (1.0 - history[i] / 100.0);
            linePoints.Add(new Point(x, y));
            fillPoints.Add(new Point(x, y));
        }

        // Close fill polygon at bottom-right corner
        fillPoints.Add(new Point(xStart + (n - 1) * xStep, h));

        SocLine.Points = linePoints;
        SocFill.Points = fillPoints;

        // ── X-axis time ticks ──────────────────────────────────────────────
        return UpdateTimeTicks(w, h, cap, n, ViewModel.HistoryTimeframeMinutes, _socTicks);
    }

    // ── V/I dual-axis chart ───────────────────────────────────────────────
    private string RedrawVIChart()
    {
        double w = VIChartCanvas.ActualWidth;
        double h = VIChartCanvas.ActualHeight;
        if (w == 0 || h == 0) return "";

        // Always draw horizontal grid lines
        UpdateGridLine(VIGridH1, w, h * 0.25);
        UpdateGridLine(VIGridH2, w, h * 0.50);
        UpdateGridLine(VIGridH3, w, h * 0.75);

        var (voltages, currents) = ViewModel.GetViHistory();
        int n = voltages.Length;

        if (n < 2)
        {
            VoltageLine.Points = new PointCollection();
            CurrentLine.Points = new PointCollection();
            HideTicks(_viTicks);
            return "";
        }

        // ── Auto-range voltage (nearest 5 V boundary) ──────────────────
        double vMin    = voltages.Min();
        double vMax    = voltages.Max();
        double vRawMin = Math.Floor(vMin   / 5.0) * 5.0;
        double vRawMax = Math.Ceiling(vMax / 5.0) * 5.0;
        if (vRawMax <= vRawMin) vRawMax = vRawMin + 5.0;
        double vRange  = vRawMax - vRawMin;

        // ── Auto-range current (nearest 5 A boundary) ──────────────────
        double iMin    = currents.Min();
        double iMax    = currents.Max();
        double iRawMin = Math.Floor(iMin   / 5.0) * 5.0;
        double iRawMax = Math.Ceiling(iMax / 5.0) * 5.0;
        if (iRawMax <= iRawMin) iRawMax = iRawMin + 5.0;
        double iRange  = iRawMax - iRawMin;

        // ── Position axis labels at 0 / 25 / 50 / 75 / 100 % ──────────
        const double fontH = 11.0;

        VLabel4.Text = $"{vRawMax:F0}";                     Canvas.SetTop(VLabel4, 0);
        VLabel3.Text = $"{vRawMax - vRange * 0.25:F0}";    Canvas.SetTop(VLabel3, h * 0.25 - fontH / 2);
        VLabel2.Text = $"{vRawMax - vRange * 0.50:F0}";    Canvas.SetTop(VLabel2, h * 0.50 - fontH / 2);
        VLabel1.Text = $"{vRawMax - vRange * 0.75:F0}";    Canvas.SetTop(VLabel1, h * 0.75 - fontH / 2);
        VLabel0.Text = $"{vRawMin:F0}";                     Canvas.SetTop(VLabel0, h - fontH);

        ILabel4.Text = $"{iRawMax:F1}";                     Canvas.SetTop(ILabel4, 0);
        ILabel3.Text = $"{iRawMax - iRange * 0.25:F1}";    Canvas.SetTop(ILabel3, h * 0.25 - fontH / 2);
        ILabel2.Text = $"{iRawMax - iRange * 0.50:F1}";    Canvas.SetTop(ILabel2, h * 0.50 - fontH / 2);
        ILabel1.Text = $"{iRawMax - iRange * 0.75:F1}";    Canvas.SetTop(ILabel1, h * 0.75 - fontH / 2);
        ILabel0.Text = $"{iRawMin:F1}";                     Canvas.SetTop(ILabel0, h - fontH);

        // ── Build polylines ────────────────────────────────────────────
        double cap    = ViewModel.HistoryCapacity;
        if (cap <= 0 || cap > n) cap = n;
        double xStep  = cap > 1 ? w / (cap - 1.0) : w;
        double xStart = (cap - n) * xStep;

        var vPoints = new PointCollection();
        var iPoints = new PointCollection();

        for (int j = 0; j < n; j++)
        {
            double x  = xStart + j * xStep;
            double vy = h * (1.0 - (voltages[j] - vRawMin) / vRange);
            double iy = h * (1.0 - (currents[j] - iRawMin) / iRange);
            vPoints.Add(new Point(x, vy));
            iPoints.Add(new Point(x, iy));
        }

        VoltageLine.Points = vPoints;
        CurrentLine.Points = iPoints;

        // ── X-axis time ticks ──────────────────────────────────────────────
        return UpdateTimeTicks(w, h, cap, n, ViewModel.HistoryTimeframeMinutes, _viTicks);
    }

    // ── Tick rendering ────────────────────────────────────────────────────

    private static void UpdateGridLine(Line line, double width, double y)
    {
        line.X1 = 0;
        line.X2 = width;
        line.Y1 = y;
        line.Y2 = y;
    }

    private static void HideTicks(TextBlock[] ticks)
    {
        for (int i = 0; i < ticks.Length; i++)
            ticks[i].Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Draws X-axis tick labels. Tick count and step are chosen automatically
    /// so values stay short (1–3 digits) and the spacing fits the canvas width.
    /// </summary>
    /// <returns>The time unit shown ("seconds" / "minutes" / "hours").</returns>
    private static string UpdateTimeTicks(double w, double h, double cap, int n,
        double timeframeMinutes, TextBlock[] ticks)
    {
        // Total seconds the X-axis spans
        double totalSeconds = timeframeMinutes > 0
            ? timeframeMinutes * 60.0
            : Math.Max(1, n - 1);   // "All" mode: assume 1 Hz sampling

        // Pick how many ticks to try for — wider canvas → more ticks (~70 px each)
        int targetTicks = (int)Math.Clamp(w / 70.0, 6, 18);

        var (unit, divisor, step) = PickAxisScale(totalSeconds, targetTicks);

        // Format: integer if step is whole, one decimal otherwise (e.g. 0.5)
        string fmt = step >= 1.0 ? "F0" : "F1";

        double totalUnits = totalSeconds / divisor;
        double xStep      = cap > 1 ? w / (cap - 1.0) : w;
        double xStart     = (cap - n) * xStep;

        int used = 0;
        // Iterate ticks 0 .. totalUnits (inclusive), stepping by `step`
        for (double v = 0.0; v <= totalUnits + 1e-9 && used < ticks.Length; v += step)
        {
            // Position: fraction along the timeframe
            double frac = totalUnits > 0 ? v / totalUnits : 0;
            double xPos = n > 1 ? xStart + frac * (n - 1) * xStep : frac * w;

            // Round value to avoid floating-point noise (0.30000000004 → 0.3)
            double display = Math.Round(v, fmt == "F1" ? 1 : 0);

            var tb = ticks[used];
            tb.Text       = display.ToString(fmt, CultureInfo.InvariantCulture);
            tb.Visibility = Visibility.Visible;

            // Center label under tick X (≈ 5.5 px per char at Consolas 9pt)
            double halfW = tb.Text.Length * 2.8;
            double left  = Math.Clamp(xPos - halfW, 0, Math.Max(0, w - halfW * 2));
            Canvas.SetLeft(tb, left);
            Canvas.SetTop(tb, h - 14);

            used++;
        }

        // Hide unused ticks in the pool
        for (int i = used; i < ticks.Length; i++)
            ticks[i].Visibility = Visibility.Collapsed;

        return unit;
    }

    /// <summary>
    /// Chooses a sensible (unit, step) for the X axis so values stay short.
    /// Returns the unit string, the seconds-per-unit divisor, and the step
    /// expressed in that unit (e.g. 5 seconds, 0.5 minutes).
    /// </summary>
    private static (string unit, double divisor, double step)
        PickAxisScale(double totalSeconds, int targetTicks)
    {
        string unit;
        double divisor;

        if (totalSeconds < 90)
        {
            unit = "seconds"; divisor = 1.0;
        }
        else if (totalSeconds < 5400)   // < 90 min
        {
            unit = "minutes"; divisor = 60.0;
        }
        else
        {
            unit = "hours";   divisor = 3600.0;
        }

        double totalInUnit = totalSeconds / divisor;
        double rawStep     = totalInUnit / Math.Max(1, targetTicks - 1);
        double step        = NiceStep(rawStep);

        return (unit, divisor, step);
    }

    /// <summary>
    /// Snaps a raw interval to a "nice" value: 1, 2, 5 × 10^k.
    /// Produces clean tick labels like 0, 5, 10 — never 0, 4.27, 8.54.
    /// </summary>
    private static double NiceStep(double rawStep)
    {
        if (rawStep <= 0) return 1;
        double exponent = Math.Floor(Math.Log10(rawStep));
        double pow      = Math.Pow(10, exponent);
        double fraction = rawStep / pow;

        double niceFraction =
              fraction < 1.5 ? 1
            : fraction < 3   ? 2
            : fraction < 7   ? 5
            :                  10;

        return niceFraction * pow;
    }

    // ── Save chart as PNG ──────────────────────────────────────────────────
    private async void SaveSocChart_Click(object sender, RoutedEventArgs e)
        => await SaveElementAsPng(SocChartArea, "BMS_SOC_Chart");

    private async void SaveVIChart_Click(object sender, RoutedEventArgs e)
        => await SaveElementAsPng(VIChartArea, "BMS_VI_Chart");

    private async Task SaveElementAsPng(FrameworkElement element, string defaultName)
    {
        try
        {
            var renderTarget = new RenderTargetBitmap();
            await renderTarget.RenderAsync(element, (int)element.ActualWidth, (int)element.ActualHeight);

            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = $"{defaultName}_{DateTime.Now:yyyyMMdd_HHmmss}.png"
            };
            picker.FileTypeChoices.Add("PNG Image", new[] { ".png" });

            var hwnd = WindowNative.GetWindowHandle(App.CurrentWindow);
            InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file == null) return;

            var pixels = await renderTarget.GetPixelsAsync();
            var reader = DataReader.FromBuffer(pixels);
            var bytes = new byte[pixels.Length];
            reader.ReadBytes(bytes);

            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)renderTarget.PixelWidth, (uint)renderTarget.PixelHeight, 96, 96, bytes);
            await encoder.FlushAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SaveChart failed: {ex.Message}");
        }
    }
}
