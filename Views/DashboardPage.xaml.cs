using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using BMSMonitor.Models;
using BMSMonitor.ViewModels;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT.Interop;
using BMSMonitor.Services;

namespace BMSMonitor.Views;

public sealed partial class DashboardPage : Page
{
    private MainViewModel ViewModel => App.ViewModel;
    private Services.LocalizationManager Lang => App.Lang;
    public ObservableCollection<AlertRecord> DashboardAlerts { get; } = new();

    // ── Dynamic X-axis tick pools ──────────────────────────────────────────
    // Pre-allocated once; reused on every redraw (no UIElement churn).
    private const int MaxTicks = 24;
    private TextBlock[] _socTicks  = [];
    private TextBlock[] _viTicks   = [];

    // Cap polyline points to roughly one per pixel of chart width.
    // Drawing 18 000 points on an 800 px canvas wastes memory and GPU
    // cycles — sub-pixel detail is invisible. Stride-decimation keeps
    // the visual shape while collapsing the per-redraw allocation to
    // something the compositor can chew through cheaply.
    private static int MaxRenderPoints(double width) =>
        Math.Max(64, (int)Math.Ceiling(width));

    // Computes a stride such that ceil(n / stride) <= maxPoints.
    private static int StrideFor(int n, int maxPoints)
    {
        if (n <= maxPoints || maxPoints <= 1) return 1;
        int stride = (n + maxPoints - 1) / maxPoints;
        return stride < 1 ? 1 : stride;
    }

    // ── Temperature sensor polylines (10 sensors) ──────────────────────────
    // ── Time-range filter (used only during export) ────────────────────────
    // When set, RedrawSocChart/RedrawVIChart trim data to this range.
    // Cleared automatically after the snapshot is written.
    private DateTime? _filterStart;
    private DateTime? _filterEnd;

    public DashboardPage()
    {
        InitializeComponent();
        InitializeTickPools();
        ApplyChartColors();
        RefreshUnitLabels();
        UpdateXAxisLabels();

        // Stretch the chart row to fill the viewport.
        //
        // Star sizing (RowDefinition Height="*") does NOT work inside a
        // ScrollViewer because the viewport hands the child infinite
        // available height — star rows then degenerate to auto-sized
        // content, leaving dead space below. The fix is to compute the
        // remaining height explicitly and push it onto the chart cards every
        // time the viewport or top section resizes.
        DashboardScroller.SizeChanged += (_, _) => SyncChartRowHeight();
        DashboardScroller.ViewChanged += (_, _) => SyncChartRowHeight();
        LayoutUpdated += (_, _) => SyncChartRowHeight();

        Loaded   += (_, _) =>
        {
            ViewModel.HistoryUpdated += OnHistoryUpdated;
            ViewModel.HistoryReset   += OnHistoryReset;
            App.Notifications.AlertFired += OnDashboardAlertFired;
            RefreshDashboardAlerts();
            SyncChartRowHeight();
        };
        Unloaded += (_, _) =>
        {
            ViewModel.HistoryUpdated -= OnHistoryUpdated;
            ViewModel.HistoryReset   -= OnHistoryReset;
            App.Notifications.AlertFired -= OnDashboardAlertFired;
        };
    }

    // Floor for each chart card, in pixels. Below this we keep the natural
    // chart size and let the dashboard scroll.
    private const double ChartCardMinFloor = 280;
    // DashboardRoot padding + row gap + chart section header gap.
    private const double ChartViewportChromeHeight = 16 + 16 + 8 + 6;
    // A little breathing room avoids a 1-2 px overflow from layout rounding,
    // which otherwise makes the ScrollViewer show a bar on maximized windows.
    private const double ChartScrollbarSlack = 10;

    private void SyncChartRowHeight()
    {
        if (DashboardTopGrid is null || SocChartCard is null || VIChartCard is null) return;
        bool sideBySide = ChartsGrid?.ActualWidth >= 1100;
        ChartsRow0.Height = new GridLength(1, GridUnitType.Star);
        ChartsRow1.Height = sideBySide
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);

        double pageHeight = GetPageViewportHeight();
        if (pageHeight <= 0) return;

        double topH = DashboardTopGrid.ActualHeight;
        if (topH <= 0) return;

        double headerH = Math.Max(SaveSocBtn?.ActualHeight ?? 0, SaveViBtn?.ActualHeight ?? 0);
        if (headerH <= 0) headerH = 28;

        double available = pageHeight - topH - headerH - ChartViewportChromeHeight;
        bool needsScroll = !sideBySide || available < ChartCardMinFloor;
        double target = needsScroll
            ? ChartCardMinFloor
            : Math.Max(ChartCardMinFloor, available - ChartScrollbarSlack);

        DashboardScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

        if (Math.Abs(SocChartCard.MinHeight - target) > 0.5)
            SocChartCard.MinHeight = target;
        if (Math.Abs(VIChartCard.MinHeight - target) > 0.5)
            VIChartCard.MinHeight = target;
    }

    private double GetPageViewportHeight()
    {
        DependencyObject? node = this;
        while ((node = VisualTreeHelper.GetParent(node)) is not null)
        {
            if (node is Frame frame && frame.ActualHeight > 0)
                return frame.ActualHeight;
        }

        if (ActualHeight > 0) return ActualHeight;
        if (DashboardScroller?.ActualHeight > 0) return DashboardScroller.ActualHeight;
        return 0;
    }

    private void RefreshDashboardAlerts()
    {
        DashboardAlerts.Clear();
        foreach (var rec in App.Notifications.GetHistory()
                     .Where(IsDashboardAlert)
                     .Reverse()
                     .Take(8))
        {
            DashboardAlerts.Add(rec);
        }

        UpdateDashboardAlertState();
    }

    private void OnDashboardAlertFired(AlertRecord rec)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsDashboardAlert(rec)) return;

            DashboardAlerts.Insert(0, rec);
            while (DashboardAlerts.Count > 8)
                DashboardAlerts.RemoveAt(DashboardAlerts.Count - 1);

            UpdateDashboardAlertState();
        });
    }

    private static bool IsDashboardAlert(AlertRecord rec)
        => rec.Severity is AlertSeverity.Alert or AlertSeverity.Warning or AlertSeverity.Error;

    private void UpdateDashboardAlertState()
    {
        DashboardNoAlertsState.Visibility = DashboardAlerts.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        DashboardAlertListView.Visibility = DashboardAlerts.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
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

    private void UpdateXAxisLabels(string socUnit = "", string viUnit = "")
    {
        string rate = GetSampleRateLabel();

        SocTimeAgoLabel.Text  = rate;
        SocNowLabel.Text      = string.IsNullOrEmpty(socUnit) ? "" : $"time ({socUnit})";
        VITimeAgoLabel.Text   = rate;
        VINowLabel.Text       = string.IsNullOrEmpty(viUnit)  ? "" : $"time ({viUnit})";
    }

    // Real sample rate computed from the actual elapsed time between the
    // first and last timestamp — not hardcoded. Falls back to a placeholder
    // until at least two samples have been captured.
    private string GetSampleRateLabel()
    {
        var earliest = ViewModel.EarliestTimestamp;
        var latest   = ViewModel.LatestTimestamp;
        int count    = ViewModel.HistorySampleCount;

        if (count < 2 || !earliest.HasValue || !latest.HasValue) return "— sample/s";

        double sec = (latest.Value - earliest.Value).TotalSeconds;
        if (sec <= 0) return "— sample/s";

        double rate = (count - 1) / sec;
        if (rate >= 10) return $"{rate:F0} samples/s";
        if (rate >= 1)  return $"{rate:F1} samples/s";
        return $"{1.0 / rate:F1} s/sample";
    }

    // ── Chart colors ──────────────────────────────────────────────────────
    private void ApplyChartColors()
    {
        var ui     = new UISettings();
        var accent = ui.GetColorValue(UIColorType.Accent);
        SocLine.Stroke   = new SolidColorBrush(accent);
        SocFill.Fill     = new SolidColorBrush(accent);
        SocLegendRect.Fill = new SolidColorBrush(accent);

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
    private void RefreshUnitLabels()
    {
        VLegendText.Text = Lang.Dash_VoltageV.Replace("(V)", $"({ViewModel.VoltageSymbol})");
    }

    // Cached once per redraw cycle so the three Redraw* methods don't each
    // re-snapshot the Queue<DateTime>. Significantly cuts allocations during
    // the high-frequency HistoryUpdated path.
    private DateTime[]? _tsCache;

    private void OnHistoryUpdated()
    {
        RefreshUnitLabels();
        _tsCache = ViewModel.GetTimestamps();
        string socUnit  = RedrawSocChart();
        string viUnit   = RedrawVIChart();
        UpdateXAxisLabels(socUnit, viUnit);
        _tsCache = null;
    }

    private DateTime[] GetCachedTimestamps() => _tsCache ?? ViewModel.GetTimestamps();

    /// <summary>Clear any active trim filter when history is replaced.</summary>
    private void OnHistoryReset()
    {
        _filterStart = null;
        _filterEnd   = null;
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

    /// <summary>
    /// Returns the indices into the full history that match the active filter
    /// (or the whole range when no filter is set), along with the real elapsed
    /// time of the selected range in minutes. The duration is derived from
    /// timestamps so the X-axis reflects actual wall-clock time regardless of
    /// the data sample rate.
    /// </summary>
    private (int start, int end, double timeframeMinutes) GetActiveRange(int fullLength)
    {
        var ts = GetCachedTimestamps();

        if (_filterStart.HasValue || _filterEnd.HasValue)
        {
            int s = 0, eIdx = ts.Length;

            if (_filterStart.HasValue)
            {
                s = -1;
                for (int i = 0; i < ts.Length; i++)
                    if (ts[i] >= _filterStart.Value) { s = i; break; }
                if (s < 0) s = ts.Length;  // start is after the latest sample
            }
            if (_filterEnd.HasValue)
            {
                eIdx = 0;
                for (int i = ts.Length - 1; i >= 0; i--)
                    if (ts[i] <= _filterEnd.Value) { eIdx = i + 1; break; }
            }

            if (eIdx < s) eIdx = s;

            // Range duration drives the X-axis tick scale
            double minutes = 0;
            if (_filterStart.HasValue && _filterEnd.HasValue)
                minutes = (_filterEnd.Value - _filterStart.Value).TotalMinutes;
            else if (ts.Length > 0 && eIdx > s)
                minutes = (ts[eIdx - 1] - ts[s]).TotalMinutes;

            return (s, eIdx, minutes);
        }

        // No filter: derive the chart duration from the actual time span
        // between the first and last sample. The old fallback of (n - 1)
        // assumed exactly 1 Hz — which lies whenever the CAN bus sends data
        // at a different cadence.
        double minutesActual = (ts.Length >= 2)
            ? (ts[^1] - ts[0]).TotalMinutes
            : 0;
        return (0, fullLength, minutesActual);
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

        double[] fullHistory = ViewModel.GetSocHistory();
        var (rangeStart, rangeEnd, _) = GetActiveRange(fullHistory.Length);
        int n = Math.Max(0, rangeEnd - rangeStart);

        if (n < 2)
        {
            SocLine.Points = [];
            SocFill.Points = [];
            HideTicks(_socTicks);
            return "";
        }

        // Position each sample horizontally by its actual elapsed time, not
        // by its index. If the BMS sends frames faster than 1 Hz the indices
        // would lie about how much real time has passed.
        DateTime[] timestamps = GetCachedTimestamps();
        DateTime tStart = timestamps[rangeStart];
        DateTime tEnd   = timestamps[rangeStart + n - 1];
        double totalSec = Math.Max(1e-9, (tEnd - tStart).TotalSeconds);

        var linePoints = new PointCollection();
        var fillPoints = new PointCollection();

        fillPoints.Add(new Point(0, h));

        int stride = StrideFor(n, MaxRenderPoints(w));
        int lastEmitted = -1;
        for (int i = 0; i < n; i += stride)
        {
            double x = w * (timestamps[rangeStart + i] - tStart).TotalSeconds / totalSec;
            double y = h * (1.0 - fullHistory[rangeStart + i] / 100.0);
            linePoints.Add(new Point(x, y));
            fillPoints.Add(new Point(x, y));
            lastEmitted = i;
        }
        // Always include the final sample so the curve reaches the right edge.
        if (lastEmitted != n - 1)
        {
            int i = n - 1;
            double x = w * (timestamps[rangeStart + i] - tStart).TotalSeconds / totalSec;
            double y = h * (1.0 - fullHistory[rangeStart + i] / 100.0);
            linePoints.Add(new Point(x, y));
            fillPoints.Add(new Point(x, y));
        }

        fillPoints.Add(new Point(w, h));

        SocLine.Points = linePoints;
        SocFill.Points = fillPoints;

        return UpdateTimeTicks(w, h, totalSec, _socTicks);
    }

    private string RedrawVIChart()
    {
        double w = VIChartCanvas.ActualWidth;
        double h = VIChartCanvas.ActualHeight;
        if (w == 0 || h == 0) return "";

        UpdateGridLine(VIGridH1, w, h * 0.25);
        UpdateGridLine(VIGridH2, w, h * 0.50);
        UpdateGridLine(VIGridH3, w, h * 0.75);

        var (fullV, fullI) = ViewModel.GetViHistory();
        string voltageUnit = ViewModel.VoltageUnit;
        var (rangeStart, rangeEnd, _) = GetActiveRange(fullV.Length);
        int n = Math.Max(0, rangeEnd - rangeStart);

        if (n < 2)
        {
            VoltageLine.Points = new PointCollection();
            CurrentLine.Points = new PointCollection();
            HideTicks(_viTicks);
            return "";
        }

        // Compute min/max in a single pass over the active range — no slice
        // arrays, no LINQ enumerator allocations.
        double vMin = double.MaxValue, vMax = double.MinValue;
        double iMin = double.MaxValue, iMax = double.MinValue;
        for (int i = rangeStart; i < rangeEnd; i++)
        {
            double v = UnitFormatter.ToDisplayVoltage(fullV[i], voltageUnit); if (v < vMin) vMin = v; if (v > vMax) vMax = v;
            double c = fullI[i]; if (c < iMin) iMin = c; if (c > iMax) iMax = c;
        }
        double voltageStep = voltageUnit == "mV" ? 5000.0 : 5.0;
        double vRawMin = Math.Floor(vMin   / voltageStep) * voltageStep;
        double vRawMax = Math.Ceiling(vMax / voltageStep) * voltageStep;
        if (vRawMax <= vRawMin) vRawMax = vRawMin + voltageStep;
        double vRange  = vRawMax - vRawMin;

        double iRawMin = Math.Floor(iMin   / 5.0) * 5.0;
        double iRawMax = Math.Ceiling(iMax / 5.0) * 5.0;
        if (iRawMax <= iRawMin) iRawMax = iRawMin + 5.0;
        double iRange  = iRawMax - iRawMin;

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

        // Position points by real elapsed time (see RedrawSocChart for rationale)
        DateTime[] timestamps = GetCachedTimestamps();
        DateTime tStart = timestamps[rangeStart];
        DateTime tEnd   = timestamps[rangeStart + n - 1];
        double totalSec = Math.Max(1e-9, (tEnd - tStart).TotalSeconds);

        var vPoints = new PointCollection();
        var iPoints = new PointCollection();

        int stride = StrideFor(n, MaxRenderPoints(w));
        int lastEmitted = -1;
        for (int j = 0; j < n; j += stride)
        {
            int idx = rangeStart + j;
            double x  = w * (timestamps[idx] - tStart).TotalSeconds / totalSec;
            double vy = h * (1.0 - (UnitFormatter.ToDisplayVoltage(fullV[idx], voltageUnit) - vRawMin) / vRange);
            double iy = h * (1.0 - (fullI[idx] - iRawMin) / iRange);
            vPoints.Add(new Point(x, vy));
            iPoints.Add(new Point(x, iy));
            lastEmitted = j;
        }
        if (lastEmitted != n - 1)
        {
            int idx = rangeStart + n - 1;
            double x  = w * (timestamps[idx] - tStart).TotalSeconds / totalSec;
            double vy = h * (1.0 - (UnitFormatter.ToDisplayVoltage(fullV[idx], voltageUnit) - vRawMin) / vRange);
            double iy = h * (1.0 - (fullI[idx] - iRawMin) / iRange);
            vPoints.Add(new Point(x, vy));
            iPoints.Add(new Point(x, iy));
        }

        VoltageLine.Points = vPoints;
        CurrentLine.Points = iPoints;

        return UpdateTimeTicks(w, h, totalSec, _viTicks);
    }

#if false
    /// <returns>The x-axis unit ("seconds" / "minutes" / "hours") or empty if no data.</returns>
    private string RedrawTempChart()
    {
        double w = TempChartCanvas.ActualWidth;
        double h = TempChartCanvas.ActualHeight;
        if (w == 0 || h == 0) return "";

        // Horizontal grid lines
        UpdateGridLine(TempGridH1, w, h * 0.25);
        UpdateGridLine(TempGridH2, w, h * 0.50);
        UpdateGridLine(TempGridH3, w, h * 0.75);

        double[][] allTemps = ViewModel.GetTempHistory();
        if (allTemps.Length == 0 || allTemps[0].Length < 2)
        {
            foreach (var line in _tempLines) line.Points = [];
            HideTicks(_tempTicks);
            return "";
        }

        int n = allTemps[0].Length;
        var (rangeStart, rangeEnd, _) = GetActiveRange(n);
        n = Math.Max(0, rangeEnd - rangeStart);

        if (n < 2)
        {
            foreach (var line in _tempLines) line.Points = [];
            HideTicks(_tempTicks);
            return "";
        }

        // ── Auto-range all sensors together ──────────────────────────────
        double tMin = double.MaxValue, tMax = double.MinValue;
        for (int s = 0; s < TempSensorCount; s++)
        {
            for (int i = rangeStart; i < rangeEnd; i++)
            {
                double t = allTemps[s][i];
                if (t < tMin) tMin = t;
                if (t > tMax) tMax = t;
            }
        }
        if (tMax <= tMin) tMax = tMin + 10;
        double tRange = tMax - tMin;

        // ── Celsius Y-axis labels (left) ─────────────────────────────────
        const double fontH = 11.0;
        const double unitLabelH = 14.0;   // space reserved for "°C" / "°F" header
        CLabel4.Text = $"{tMax:F0}";                     Canvas.SetTop(CLabel4, unitLabelH);
        CLabel3.Text = $"{tMax - tRange * 0.25:F0}";    Canvas.SetTop(CLabel3, h * 0.25 - fontH / 2);
        CLabel2.Text = $"{tMax - tRange * 0.50:F0}";    Canvas.SetTop(CLabel2, h * 0.50 - fontH / 2);
        CLabel1.Text = $"{tMax - tRange * 0.75:F0}";    Canvas.SetTop(CLabel1, h * 0.75 - fontH / 2);
        CLabel0.Text = $"{tMin:F0}";                     Canvas.SetTop(CLabel0, h - fontH);

        // ── Fahrenheit Y-axis labels (right) ─────────────────────────────
        double fMin = tMin * 9.0 / 5.0 + 32.0;
        double fMax = tMax * 9.0 / 5.0 + 32.0;
        double fRange = fMax - fMin;
        FLabel4.Text = $"{fMax:F0}";                     Canvas.SetTop(FLabel4, unitLabelH);
        FLabel3.Text = $"{fMax - fRange * 0.25:F0}";    Canvas.SetTop(FLabel3, h * 0.25 - fontH / 2);
        FLabel2.Text = $"{fMax - fRange * 0.50:F0}";    Canvas.SetTop(FLabel2, h * 0.50 - fontH / 2);
        FLabel1.Text = $"{fMax - fRange * 0.75:F0}";    Canvas.SetTop(FLabel1, h * 0.75 - fontH / 2);
        FLabel0.Text = $"{fMin:F0}";                     Canvas.SetTop(FLabel0, h - fontH);

        // Position points by real elapsed time (see RedrawSocChart for rationale)
        DateTime[] timestamps = GetCachedTimestamps();
        DateTime tStartTs = timestamps[rangeStart];
        DateTime tEndTs   = timestamps[rangeStart + n - 1];
        double totalSec  = Math.Max(1e-9, (tEndTs - tStartTs).TotalSeconds);

        // ── Build polylines for each sensor ──────────────────────────────
        // Decimate to ~one point per pixel — 10 sensors × thousands of
        // samples otherwise produces enormous PointCollections every
        // redraw, which dominates the app's memory cost.
        int stride = StrideFor(n, MaxRenderPoints(w));
        for (int s = 0; s < TempSensorCount; s++)
        {
            var points = new PointCollection();
            int lastEmitted = -1;
            var series = allTemps[s];
            for (int j = 0; j < n; j += stride)
            {
                double x = w * (timestamps[rangeStart + j] - tStartTs).TotalSeconds / totalSec;
                double y = h * (1.0 - (series[rangeStart + j] - tMin) / tRange);
                points.Add(new Point(x, y));
                lastEmitted = j;
            }
            if (lastEmitted != n - 1)
            {
                int j = n - 1;
                double x = w * (timestamps[rangeStart + j] - tStartTs).TotalSeconds / totalSec;
                double y = h * (1.0 - (series[rangeStart + j] - tMin) / tRange);
                points.Add(new Point(x, y));
            }
            _tempLines[s].Points = points;
        }

        return UpdateTimeTicks(w, h, totalSec, _tempTicks);
    }

    // ── Tick rendering ────────────────────────────────────────────────────

#endif
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

    // Renders evenly-spaced X-axis ticks across the chart, with the labels
    // computed from real elapsed seconds. Tick positions are proportional
    // along the canvas — they match the timestamp-based point placement.
    private static string UpdateTimeTicks(double w, double h, double totalSeconds, TextBlock[] ticks)
    {
        if (totalSeconds <= 0) totalSeconds = 1;

        int targetTicks = (int)Math.Clamp(w / 70.0, 6, 18);
        var (unit, divisor, step) = PickAxisScale(totalSeconds, targetTicks);
        string fmt = step >= 1.0 ? "F0" : "F1";

        double totalUnits = totalSeconds / divisor;

        int used = 0;
        for (double v = 0.0; v <= totalUnits + 1e-9 && used < ticks.Length; v += step)
        {
            double frac = totalUnits > 0 ? v / totalUnits : 0;
            double xPos = frac * w;
            double display = Math.Round(v, fmt == "F1" ? 1 : 0);

            var tb = ticks[used];
            tb.Text       = display.ToString(fmt, CultureInfo.InvariantCulture);
            tb.Visibility = Visibility.Visible;

            double halfW = tb.Text.Length * 2.8;
            double left  = Math.Clamp(xPos - halfW, 0, Math.Max(0, w - halfW * 2));
            Canvas.SetLeft(tb, left);
            Canvas.SetTop(tb, h - 14);

            used++;
        }

        for (int i = used; i < ticks.Length; i++)
            ticks[i].Visibility = Visibility.Collapsed;

        return unit;
    }

    private static (string unit, double divisor, double step)
        PickAxisScale(double totalSeconds, int targetTicks)
    {
        string unit;
        double divisor;

        if (totalSeconds < 90)        { unit = "seconds"; divisor = 1.0;    }
        else if (totalSeconds < 5400) { unit = "minutes"; divisor = 60.0;   }
        else                          { unit = "hours";   divisor = 3600.0; }

        double totalInUnit = totalSeconds / divisor;
        double rawStep     = totalInUnit / Math.Max(1, targetTicks - 1);
        return (unit, divisor, NiceStep(rawStep));
    }

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

    // ── Save chart (with size + aspect + format dialog) ────────────────────

    private record ExportOptions(int Width, int Height, string Format, int? StartSec = null, int? EndSec = null);

    private async void SaveSocChart_Click(object sender, RoutedEventArgs e)
    {
        await SaveChartFlow(
            renderElement: SocChartArea,
            card:          SocChartCard,
            mainCanvas:    SocChartCanvas,
            heightSyncs:   new FrameworkElement[] { SocYAxisGrid },
            defaultName:   "BMS_SOC_Chart");
    }

    private async void SaveVIChart_Click(object sender, RoutedEventArgs e)
    {
        await SaveChartFlow(
            renderElement: VIChartArea,
            card:          VIChartCard,
            mainCanvas:    VIChartCanvas,
            heightSyncs:   new FrameworkElement[] { VAxisCanvas, IAxisCanvas },
            defaultName:   "BMS_VI_Chart");
    }

    private async Task SaveChartFlow(
        FrameworkElement renderElement,
        Border card,
        Canvas mainCanvas,
        FrameworkElement[] heightSyncs,
        string defaultName)
    {
        var root = renderElement.XamlRoot;
        if (root is null) return;

        var timestamps = ViewModel.GetTimestamps();
        var opts = await ShowExportDialog(root, renderElement, timestamps);
        if (opts is null) return;

        // Apply time filter for this export only
        var prevStart = _filterStart;
        var prevEnd   = _filterEnd;
        try
        {
            if (opts.StartSec.HasValue || opts.EndSec.HasValue)
            {
                var ts = ViewModel.GetTimestamps();
                if (ts.Length > 0)
                {
                    var baseTime = ts[0];
                    if (opts.StartSec.HasValue)
                        _filterStart = baseTime.AddSeconds(opts.StartSec.Value);
                    if (opts.EndSec.HasValue)
                        _filterEnd   = baseTime.AddSeconds(opts.EndSec.Value);
                }
                OnHistoryUpdated(); // redraw with filter applied
            }

            // File save picker
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName      = $"{defaultName}_{DateTime.Now:yyyyMMdd_HHmmss}"
            };

            switch (opts.Format)
            {
                case "png": picker.FileTypeChoices.Add("PNG image",   new[] { ".png" }); break;
                case "jpg": picker.FileTypeChoices.Add("JPEG image",  new[] { ".jpg" }); break;
                case "svg": picker.FileTypeChoices.Add("SVG vector",  new[] { ".svg" }); break;
            }

            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.CurrentWindow));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            try
            {
                if (opts.Format == "svg")
                    await SaveAsSvg(mainCanvas, file, opts.Width, opts.Height);
                else
                    await SaveAsRaster(renderElement, card, mainCanvas, heightSyncs,
                                       file, opts, opts.Format == "jpg");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Save failed: {ex}");
                var err = new ContentDialog
                {
                    Title           = "Save failed",
                    Content         = ex.Message,
                    CloseButtonText = "OK",
                    XamlRoot        = root
                };
                try { await err.ShowAsync(); } catch { }
            }
        }
        finally
        {
            _filterStart = prevStart;
            _filterEnd   = prevEnd;
            OnHistoryUpdated(); // restore normal view
        }
    }

    /// <summary>
    /// Shows the export dialog with live preview and a visual trim bar.
    /// Returns null on cancel.
    /// </summary>
    private async Task<ExportOptions?> ShowExportDialog(
        XamlRoot root, FrameworkElement previewElement, DateTime[] timestamps)
    {
        var totalSec = timestamps.Length > 1
            ? (int)(timestamps[^1] - timestamps[0]).TotalSeconds
            : 0;

        // Dialog-level trim state (seconds from data start)
        double trimStart = 0;
        double trimEnd   = totalSec;
        bool draggingStart = false;
        bool draggingEnd   = false;

        // ── Controls ──────────────────────────────────────────────────────
        var aspect = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0
        };
        aspect.Items.Add(new ComboBoxItem { Content = "4:3 — paper / Origin default",    Tag = "1.3333" });
        aspect.Items.Add(new ComboBoxItem { Content = "3:2 — photo / wide paper",        Tag = "1.5"    });
        aspect.Items.Add(new ComboBoxItem { Content = "16:9 — slide / video",            Tag = "1.7778" });
        aspect.Items.Add(new ComboBoxItem { Content = "φ — golden 1.618:1",              Tag = "1.6180" });
        aspect.Items.Add(new ComboBoxItem { Content = "1:1 — square (correlation)",      Tag = "1.0"    });
        aspect.Items.Add(new ComboBoxItem { Content = "Custom — set height manually",    Tag = "0"      });

        var widthBox = new NumberBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Value = 800, Minimum = 200, Maximum = 4000,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 50, LargeChange = 100
        };
        var heightBox = new NumberBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Value = 600, Minimum = 150, Maximum = 4000,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 25, LargeChange = 100,
            IsEnabled = false
        };

        var format = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0
        };
        format.Items.Add(new ComboBoxItem { Content = "PNG — raster, lossless", Tag = "png" });
        format.Items.Add(new ComboBoxItem { Content = "JPG — raster, smaller",  Tag = "jpg" });
        format.Items.Add(new ComboBoxItem { Content = "SVG — vector, editable", Tag = "svg" });

        // Preview image
        var previewImage = new Image
        {
            Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center,
            MaxHeight = 200, Margin = new Thickness(0, 6, 0, 8)
        };

        // ── Trim bar canvas ──────────────────────────────────────────────────
        var trimCanvas = new Canvas { Height = 34, HorizontalAlignment = HorizontalAlignment.Stretch };

        var trimTrack = new Border
        {
            Background = (Brush)Application.Current.Resources["ControlFillColorTertiaryBrush"],
            Height = 8, CornerRadius = new CornerRadius(4)
        };
        Canvas.SetTop(trimTrack, 13);
        trimCanvas.Children.Add(trimTrack);

        var trimSelection = new Border
        {
            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            Opacity = 0.45, Height = 8, CornerRadius = new CornerRadius(4)
        };
        Canvas.SetTop(trimSelection, 13);
        trimCanvas.Children.Add(trimSelection);

        var startThumb = new Border
        {
            Width = 14, Height = 30, CornerRadius = new CornerRadius(3),
            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["AccentFillColorTertiaryBrush"],
            BorderThickness = new Thickness(1),
        };
        Canvas.SetTop(startThumb, 2);
        trimCanvas.Children.Add(startThumb);

        var endThumb = new Border
        {
            Width = 14, Height = 30, CornerRadius = new CornerRadius(3),
            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["AccentFillColorTertiaryBrush"],
            BorderThickness = new Thickness(1),
        };
        Canvas.SetTop(endThumb, 2);
        trimCanvas.Children.Add(endThumb);

        // Trim bar range label
        var trimLabel = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"), FontSize = 11, Opacity = 0.6
        };

        void UpdateTrimDisplay()
        {
            double w = trimCanvas.ActualWidth;
            if (w <= 0) return;

            trimTrack.Width = w;
            Canvas.SetLeft(trimTrack, 0);

            double startX = totalSec > 0 ? (trimStart / totalSec) * w : 0;
            double endX   = totalSec > 0 ? (trimEnd   / totalSec) * w : w;

            Canvas.SetLeft(startThumb, startX - startThumb.Width / 2);
            Canvas.SetLeft(endThumb,   endX   - endThumb.Width   / 2);
            Canvas.SetLeft(trimSelection, startX);
            trimSelection.Width = Math.Max(0, endX - startX);

            trimLabel.Text = $"{(int)trimStart:N0}s – {(int)trimEnd:N0}s  ({(int)(trimEnd - trimStart)}s)";
        }

        trimCanvas.SizeChanged += (_, _) => UpdateTrimDisplay();

        // ── Preview updater (debounced) — declared early so trim handlers can call it ──
        CancellationTokenSource? previewCts = null;
        async void SchedulePreviewUpdate()
        {
            previewCts?.Cancel();
            previewCts = new CancellationTokenSource();
            var token = previewCts.Token;
            try
            {
                await Task.Delay(250, token);
                if (!token.IsCancellationRequested)
                {
                    var src = await RenderPreviewAsync(previewElement,
                        Math.Min(420, (int)widthBox.Value),
                        Math.Min(280, (int)heightBox.Value));
                    if (!token.IsCancellationRequested && src is not null)
                        previewImage.Source = src;
                }
            }
            catch { }
        }

        // ── Trim bar pointer handlers ────────────────────────────────────────
        startThumb.PointerPressed += (_, e) => { draggingStart = true; startThumb.CapturePointer(e.Pointer); };
        endThumb.PointerPressed   += (_, e) => { draggingEnd   = true; endThumb.CapturePointer(e.Pointer); };

        var trimMoved = new PointerEventHandler((_, e) =>
        {
            if (!draggingStart && !draggingEnd) return;
            double w = trimCanvas.ActualWidth;
            if (w <= 0 || totalSec <= 0) return;

            double x = Math.Clamp(e.GetCurrentPoint(trimCanvas).Position.X, 0, w);
            double sec = (x / w) * totalSec;

            if (draggingStart)
            {
                if (sec >= trimEnd) sec = trimEnd - 1;
                if (sec < 0) sec = 0;
                trimStart = sec;
            }
            else if (draggingEnd)
            {
                if (sec <= trimStart) sec = trimStart + 1;
                if (sec > totalSec) sec = totalSec;
                trimEnd = sec;
            }

            UpdateTrimDisplay();
            SchedulePreviewUpdate();
        });

        var trimReleased = new PointerEventHandler((_, e) =>
        {
            if (draggingStart) { startThumb.ReleasePointerCapture(e.Pointer); draggingStart = false; }
            if (draggingEnd)   { endThumb.ReleasePointerCapture(e.Pointer);   draggingEnd   = false; }
            UpdateTrimDisplay();
        });

        startThumb.PointerMoved += trimMoved;
        endThumb.PointerMoved   += trimMoved;
        startThumb.PointerReleased += trimReleased;
        endThumb.PointerReleased   += trimReleased;
        startThumb.PointerCaptureLost += trimReleased;
        endThumb.PointerCaptureLost   += trimReleased;

        // ── Aspect-locked linking ──────────────────────────────────────────
        bool internalUpdate = false;

        double GetAspect()
        {
            if (aspect.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
                double.TryParse(tag, NumberStyles.Any, CultureInfo.InvariantCulture, out double a))
                return a;
            return 0;
        }

        void RecomputeHeight()
        {
            if (internalUpdate) return;
            double a = GetAspect();
            if (a > 0)
            {
                internalUpdate = true;
                heightBox.Value = Math.Round(widthBox.Value / a);
                internalUpdate = false;
            }
        }

        // ── Live data subscription ──────────────────────────────────────────
        void OnDataUpdated() => SchedulePreviewUpdate();
        ViewModel.HistoryUpdated += OnDataUpdated;

        // Initial preview
        _ = Task.Run(async () => { await Task.Delay(150); DispatcherQueue.TryEnqueue(SchedulePreviewUpdate); });

        // ── Event wiring ────────────────────────────────────────────────────
        aspect.SelectionChanged += (_, _) =>
        {
            double a = GetAspect();
            heightBox.IsEnabled = a <= 0;
            RecomputeHeight();
            SchedulePreviewUpdate();
        };
        widthBox.ValueChanged  += (_, _) => { RecomputeHeight(); SchedulePreviewUpdate(); };
        heightBox.ValueChanged += (_, _) => SchedulePreviewUpdate();

        // ── Layout helpers ──────────────────────────────────────────────────
        TextBlock FieldLabel(string text) => new()
        {
            Text = text, FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 0, 0, 4)
        };
        TextBlock SectionHeader(string text) => new()
        {
            Text = text, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Opacity = 0.9, Margin = new Thickness(0, 4, 0, 6)
        };
        Border Divider() => new()
        {
            Height = 1, Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Margin = new Thickness(0, 10, 0, 6)
        };

        // ── Build dialog panel ──────────────────────────────────────────────
        var panel = new StackPanel { Spacing = 0, MinWidth = 500 };

        // — Preview —
        panel.Children.Add(SectionHeader("Preview (live)"));
        panel.Children.Add(previewImage);

        // — Time range with trim bar —
        panel.Children.Add(Divider());
        panel.Children.Add(SectionHeader("Time range"));
        panel.Children.Add(new TextBlock
        {
            Text = $"Drag the handles to select a segment. Full range: 0 – {totalSec} s ({totalSec / 60.0:F1} min)",
            FontSize = 11, Opacity = 0.5, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        });
        panel.Children.Add(trimCanvas);
        panel.Children.Add(trimLabel);

        // — Dimensions —
        panel.Children.Add(Divider());
        panel.Children.Add(SectionHeader("Dimensions"));
        panel.Children.Add(FieldLabel("Aspect ratio"));
        panel.Children.Add(aspect);

        var sizeGrid = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 10, 0, 0) };
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sizeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sizeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var wLab = FieldLabel("Width (px)");  Grid.SetColumn(wLab, 0); Grid.SetRow(wLab, 0);
        var hLab = FieldLabel("Height (px)"); Grid.SetColumn(hLab, 1); Grid.SetRow(hLab, 0);
        Grid.SetColumn(widthBox,  0); Grid.SetRow(widthBox,  1);
        Grid.SetColumn(heightBox, 1); Grid.SetRow(heightBox, 1);
        sizeGrid.Children.Add(wLab); sizeGrid.Children.Add(hLab);
        sizeGrid.Children.Add(widthBox); sizeGrid.Children.Add(heightBox);
        panel.Children.Add(sizeGrid);

        // — Format —
        panel.Children.Add(Divider());
        panel.Children.Add(SectionHeader("File format"));
        panel.Children.Add(format);

        // ── Show dialog ─────────────────────────────────────────────────────
        var dialog = new ContentDialog
        {
            Title = "Export chart", Content = panel,
            PrimaryButtonText = "Save…", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = root
        };

        var result = await dialog.ShowAsync();

        // Cleanup
        ViewModel.HistoryUpdated -= OnDataUpdated;

        if (result != ContentDialogResult.Primary) return null;

        string fmt = "png";
        if (format.SelectedItem is ComboBoxItem fi && fi.Tag is string ft) fmt = ft;

        return new ExportOptions(
            (int)widthBox.Value, (int)heightBox.Value, fmt,
            StartSec: trimStart > 0 ? (int)trimStart : null,
            EndSec:   trimEnd < totalSec ? (int)trimEnd : null);
    }

    /// <summary>
    /// Renders a small preview thumbnail of the given element.
    /// </summary>
    private static async Task<ImageSource?> RenderPreviewAsync(
        FrameworkElement element, int maxW, int maxH)
    {
        try
        {
            double srcW = element.ActualWidth;
            double srcH = element.ActualHeight;
            if (srcW <= 0 || srcH <= 0) return null;

            double scale = Math.Min(maxW / srcW, maxH / srcH);
            int w = Math.Max(1, (int)(srcW * scale));
            int h = Math.Max(1, (int)(srcH * scale));

            var rt = new RenderTargetBitmap();
            await rt.RenderAsync(element, w, h);

            var pixels = await rt.GetPixelsAsync();
            var softwareBitmap = new SoftwareBitmap(
                BitmapPixelFormat.Bgra8, rt.PixelWidth, rt.PixelHeight, BitmapAlphaMode.Premultiplied);
            softwareBitmap.CopyFromBuffer(pixels);

            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(softwareBitmap);
            return source;
        }
        catch { return null; }
    }

    /// <summary>
    /// Renders the chart to a raster image at the requested dimensions.
    /// The chart layout is NOT resized — font sizes, line widths and tick
    /// proportions stay identical regardless of output resolution.
    /// Only the pixel density changes.
    /// </summary>
    private static async Task SaveAsRaster(
        FrameworkElement renderElement,
        Border _0, Canvas _1, FrameworkElement[] _2,
        StorageFile file, ExportOptions opts, bool jpeg)
    {
        var rt = new RenderTargetBitmap();
        await rt.RenderAsync(renderElement, opts.Width, opts.Height);

        var pixels = await rt.GetPixelsAsync();
        var bytes  = new byte[pixels.Length];
        DataReader.FromBuffer(pixels).ReadBytes(bytes);

        var encoderId = jpeg ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId;

        using var stream  = await file.OpenAsync(FileAccessMode.ReadWrite);
        var       encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)rt.PixelWidth, (uint)rt.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync();
    }

    /// <summary>
    /// Serializes the chart canvas's primitive shapes into an SVG document.
    /// The viewBox preserves the chart's logical coordinate space so the
    /// output renders cleanly at any size in the SVG width/height attribute.
    /// </summary>
    private static async Task SaveAsSvg(Canvas canvas, StorageFile file, int width, int height)
    {
        double srcW = canvas.ActualWidth;
        double srcH = canvas.ActualHeight;
        if (srcW <= 0 || srcH <= 0)
            throw new InvalidOperationException("Chart canvas has no size.");

        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();

        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" " +
                      $"width=\"{width}\" height=\"{height}\" " +
                      $"viewBox=\"0 0 {srcW.ToString("0.##", inv)} {srcH.ToString("0.##", inv)}\">");

        foreach (var child in canvas.Children)
        {
            if (child is not FrameworkElement fe || fe.Visibility != Visibility.Visible)
                continue;

            switch (child)
            {
                case Line l:
                    sb.AppendLine(SvgLine(l, inv));
                    break;
                case Polyline pl:
                    sb.AppendLine(SvgPolyline(pl, inv));
                    break;
                case Polygon pg:
                    sb.AppendLine(SvgPolygon(pg, inv));
                    break;
                case TextBlock tb:
                    sb.AppendLine(SvgText(tb, inv));
                    break;
            }
        }

        sb.AppendLine("</svg>");

        await FileIO.WriteTextAsync(file, sb.ToString());
    }

    private static string SvgLine(Line l, CultureInfo inv)
    {
        string stroke   = BrushToHex(l.Stroke);
        string dashAttr = l.StrokeDashArray is { Count: > 0 } da
            ? $" stroke-dasharray=\"{string.Join(",", da.Select(d => d.ToString("0.##", inv)))}\""
            : "";
        return $"<line x1=\"{l.X1.ToString("0.##", inv)}\" y1=\"{l.Y1.ToString("0.##", inv)}\" " +
               $"x2=\"{l.X2.ToString("0.##", inv)}\" y2=\"{l.Y2.ToString("0.##", inv)}\" " +
               $"stroke=\"{stroke}\" stroke-width=\"{l.StrokeThickness.ToString("0.##", inv)}\"{dashAttr}/>";
    }

    private static string SvgPolyline(Polyline p, CultureInfo inv)
    {
        if (p.Points.Count == 0) return "";
        string points  = string.Join(" ",
            p.Points.Select(pt => $"{pt.X.ToString("0.##", inv)},{pt.Y.ToString("0.##", inv)}"));
        string stroke  = BrushToHex(p.Stroke);
        string fill    = p.Fill is null ? "none" : BrushToHex(p.Fill);
        string dashAttr = p.StrokeDashArray is { Count: > 0 } da
            ? $" stroke-dasharray=\"{string.Join(",", da.Select(d => d.ToString("0.##", inv)))}\""
            : "";
        return $"<polyline points=\"{points}\" stroke=\"{stroke}\" fill=\"{fill}\" " +
               $"stroke-width=\"{p.StrokeThickness.ToString("0.##", inv)}\"{dashAttr} stroke-linejoin=\"round\"/>";
    }

    private static string SvgPolygon(Polygon p, CultureInfo inv)
    {
        if (p.Points.Count == 0) return "";
        string points = string.Join(" ",
            p.Points.Select(pt => $"{pt.X.ToString("0.##", inv)},{pt.Y.ToString("0.##", inv)}"));
        string fill   = BrushToHex(p.Fill);
        string fo     = p.Opacity.ToString("0.##", inv);
        return $"<polygon points=\"{points}\" fill=\"{fill}\" fill-opacity=\"{fo}\"/>";
    }

    private static string SvgText(TextBlock tb, CultureInfo inv)
    {
        double x = double.IsNaN(Canvas.GetLeft(tb)) ? 0 : Canvas.GetLeft(tb);
        double y = double.IsNaN(Canvas.GetTop(tb))  ? 0 : Canvas.GetTop(tb);
        // SVG text is baseline-anchored; offset by ascender
        y += tb.FontSize * 0.85;
        string color  = BrushToHex(tb.Foreground);
        string fo     = tb.Opacity.ToString("0.##", inv);
        string family = tb.FontFamily?.Source ?? "sans-serif";
        return $"<text x=\"{x.ToString("0.##", inv)}\" y=\"{y.ToString("0.##", inv)}\" " +
               $"font-family=\"{family}\" font-size=\"{tb.FontSize.ToString("0.##", inv)}\" " +
               $"fill=\"{color}\" fill-opacity=\"{fo}\">{XmlEscape(tb.Text)}</text>";
    }

    private static string BrushToHex(Brush? b)
    {
        if (b is SolidColorBrush scb)
            return $"#{scb.Color.R:X2}{scb.Color.G:X2}{scb.Color.B:X2}";
        return "#808080";
    }

    private static string XmlEscape(string? s) =>
        (s ?? "")
            .Replace("&",  "&amp;")
            .Replace("<",  "&lt;")
            .Replace(">",  "&gt;")
            .Replace("\"", "&quot;");

}
