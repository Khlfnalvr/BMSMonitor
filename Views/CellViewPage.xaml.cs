using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using BMSMonitor.Services;
using BMSMonitor.ViewModels;
using Windows.Foundation;
using Windows.UI;

namespace BMSMonitor.Views;

public sealed partial class CellViewPage : Page
{
    private MainViewModel ViewModel => App.ViewModel;
    private Services.LocalizationManager Lang => App.Lang;
    private bool _cellPopupOpen;
    private Flyout? _cellChartFlyout;
    private bool _ntcPopupOpen;
    private Flyout? _ntcChartFlyout;
    private const int MaxTicks = 24;
    private const int TempSensorCount = 10;
    private readonly Polyline[] _tempLines = new Polyline[TempSensorCount];
    private readonly Border[] _tempLegendItems = new Border[TempSensorCount];
    private TextBlock[] _tempTicks = [];
    private int? _selectedTempSensor;
    private DateTime? _filterStart;
    private DateTime? _filterEnd;
    private DateTime[]? _tsCache;

    public CellViewPage()
    {
        InitializeComponent();
        InitializeTempChart();
        InitializeTempLegend();
        ApplyTempChartColors();
        UpdateTempXAxisLabels();

        Loaded += (_, _) =>
        {
            ViewModel.HistoryUpdated += OnHistoryUpdated;
            ViewModel.HistoryReset += OnHistoryReset;
            OnHistoryUpdated();
        };
        Unloaded += (_, _) =>
        {
            ViewModel.HistoryUpdated -= OnHistoryUpdated;
            ViewModel.HistoryReset -= OnHistoryReset;
        };
    }

    private void InitializeTempChart()
    {
        _tempTicks = new TextBlock[MaxTicks];
        for (int i = 0; i < MaxTicks; i++)
        {
            _tempTicks[i] = MakeTickLabel();
            TempChartCanvas.Children.Add(_tempTicks[i]);
        }

        for (int s = 0; s < TempSensorCount; s++)
        {
            var line = new Polyline
            {
                StrokeThickness = 1.5,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                Stroke = new SolidColorBrush(TempSensorColor(s)),
                Tag = s
            };
            int sensor = s;
            line.PointerEntered += (_, _) =>
            {
                _selectedTempSensor = sensor;
                UpdateTempSpotlight();
            };
            line.PointerExited += (_, _) =>
            {
                if (_selectedTempSensor == sensor) _selectedTempSensor = null;
                UpdateTempSpotlight();
            };
            line.Tapped += (_, _) =>
            {
                _selectedTempSensor = _selectedTempSensor == sensor ? null : sensor;
                UpdateTempSpotlight();
            };
            _tempLines[s] = line;
            TempChartCanvas.Children.Add(line);
        }
    }

    private void InitializeTempLegend()
    {
        for (int s = 0; s < TempSensorCount; s++)
        {
            int sensor = s;
            var color = TempSensorColor(s);

            var dot = new Rectangle
            {
                Width = 12,
                Height = 12,
                RadiusX = 2,
                RadiusY = 2,
                Fill = new SolidColorBrush(color),
                VerticalAlignment = VerticalAlignment.Center
            };

            var label = new TextBlock
            {
                Text = $"NTC {s + 1}",
                FontSize = 10,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center
            };

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var border = new Border
            {
                Padding = new Thickness(4, 2, 4, 2),
                CornerRadius = new CornerRadius(3),
                Child = panel,
                Tag = sensor
            };

            border.PointerEntered += (_, _) =>
            {
                _selectedTempSensor = sensor;
                UpdateTempSpotlight();
            };
            border.PointerExited += (_, _) =>
            {
                if (_selectedTempSensor == sensor) _selectedTempSensor = null;
                UpdateTempSpotlight();
            };
            border.Tapped += (_, _) =>
            {
                _selectedTempSensor = _selectedTempSensor == sensor ? null : sensor;
                UpdateTempSpotlight();
            };

            panel.Children.Add(dot);
            panel.Children.Add(label);
            _tempLegendItems[s] = border;
            TempLegendPanel.Children.Add(border);
        }
    }

    private static Color TempSensorColor(int index) => index switch
    {
        0 => Color.FromArgb(255, 244, 67, 54),
        1 => Color.FromArgb(255, 33, 150, 243),
        2 => Color.FromArgb(255, 76, 175, 80),
        3 => Color.FromArgb(255, 255, 152, 0),
        4 => Color.FromArgb(255, 156, 39, 176),
        5 => Color.FromArgb(255, 0, 188, 212),
        6 => Color.FromArgb(255, 205, 220, 57),
        7 => Color.FromArgb(255, 255, 87, 34),
        8 => Color.FromArgb(255, 121, 85, 72),
        9 => Color.FromArgb(255, 158, 158, 158),
        _ => Color.FromArgb(255, 128, 128, 128),
    };

    private void ApplyTempChartColors()
    {
        var gridBrush = new SolidColorBrush(Color.FromArgb(35, 128, 128, 128));
        TempGridH1.Stroke = gridBrush;
        TempGridH2.Stroke = gridBrush;
        TempGridH3.Stroke = gridBrush;
    }

    private void UpdateTempSpotlight()
    {
        for (int s = 0; s < TempSensorCount; s++)
        {
            bool spotlight = _selectedTempSensor == null || _selectedTempSensor == s;
            _tempLines[s].Opacity = spotlight ? 1.0 : 0.15;
            _tempLines[s].StrokeThickness = spotlight ? 2.5 : 0.8;

            if (_tempLegendItems[s].Child is StackPanel sp)
            {
                sp.Opacity = spotlight ? 1.0 : 0.25;
                _tempLegendItems[s].Background = spotlight
                    ? new SolidColorBrush(Color.FromArgb(
                        30,
                        TempSensorColor(s).R,
                        TempSensorColor(s).G,
                        TempSensorColor(s).B))
                    : null;
            }
        }
    }

    private static TextBlock MakeTickLabel() => new()
    {
        FontSize = 9,
        Opacity = 0.55,
        FontFamily = new FontFamily("Consolas"),
        TextAlignment = TextAlignment.Center,
        Visibility = Visibility.Collapsed
    };

    private void OnHistoryUpdated()
    {
        _tsCache = ViewModel.GetTimestamps();
        string unit = RedrawTempChart();
        UpdateTempXAxisLabels(unit);
        UpdateTrimBar();
        _tsCache = null;
    }

    private void OnHistoryReset()
    {
        _filterStart = null;
        _filterEnd = null;
        UpdateTrimBar();
    }

    private DateTime[] GetCachedTimestamps() => _tsCache ?? ViewModel.GetTimestamps();

    private void TempChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        string unit = RedrawTempChart();
        if (!string.IsNullOrEmpty(unit))
            TempNowLabel.Text = $"time ({unit})";
    }

    private void UpdateTempXAxisLabels(string tempUnit = "")
    {
        TempTimeAgoLabel.Text = GetSampleRateLabel();
        TempNowLabel.Text = string.IsNullOrEmpty(tempUnit) ? "" : $"time ({tempUnit})";
    }

    private string GetSampleRateLabel()
    {
        var earliest = ViewModel.EarliestTimestamp;
        var latest = ViewModel.LatestTimestamp;
        int count = ViewModel.HistorySampleCount;

        if (count < 2 || !earliest.HasValue || !latest.HasValue) return "— sample/s";

        double sec = (latest.Value - earliest.Value).TotalSeconds;
        if (sec <= 0) return "— sample/s";

        double rate = (count - 1) / sec;
        if (rate >= 10) return $"{rate:F0} samples/s";
        if (rate >= 1) return $"{rate:F1} samples/s";
        return $"{1.0 / rate:F1} s/sample";
    }

    private (int start, int end) GetActiveRange(int fullLength)
    {
        var ts = GetCachedTimestamps();

        if (_filterStart.HasValue || _filterEnd.HasValue)
        {
            int s = 0;
            int eIdx = Math.Min(ts.Length, fullLength);

            if (_filterStart.HasValue)
            {
                s = -1;
                for (int i = 0; i < ts.Length; i++)
                    if (ts[i] >= _filterStart.Value) { s = i; break; }
                if (s < 0) s = ts.Length;
            }

            if (_filterEnd.HasValue)
            {
                eIdx = 0;
                for (int i = ts.Length - 1; i >= 0; i--)
                    if (ts[i] <= _filterEnd.Value) { eIdx = i + 1; break; }
            }

            if (eIdx < s) eIdx = s;
            return (Math.Clamp(s, 0, fullLength), Math.Clamp(eIdx, 0, fullLength));
        }

        return (0, fullLength);
    }

    private string RedrawTempChart()
    {
        double w = TempChartCanvas.ActualWidth;
        double h = TempChartCanvas.ActualHeight;
        if (w == 0 || h == 0) return "";

        UpdateGridLine(TempGridH1, w, h * 0.25);
        UpdateGridLine(TempGridH2, w, h * 0.50);
        UpdateGridLine(TempGridH3, w, h * 0.75);

        double[][] allTemps = ViewModel.GetTempHistory();
        if (allTemps.Length < TempSensorCount || allTemps[0].Length < 2)
        {
            foreach (var line in _tempLines) line.Points = [];
            HideTicks(_tempTicks);
            return "";
        }

        int fullLength = allTemps[0].Length;
        var timestamps = GetCachedTimestamps();
        if (timestamps.Length < fullLength)
        {
            foreach (var line in _tempLines) line.Points = [];
            HideTicks(_tempTicks);
            return "";
        }

        var (rangeStart, rangeEnd) = GetActiveRange(fullLength);
        int n = Math.Max(0, rangeEnd - rangeStart);

        if (n < 2)
        {
            foreach (var line in _tempLines) line.Points = [];
            HideTicks(_tempTicks);
            return "";
        }

        double tMin = double.MaxValue;
        double tMax = double.MinValue;
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

        const double fontH = 11.0;
        const double unitLabelH = 14.0;
        CLabel4.Text = $"{tMax:F0}"; Canvas.SetTop(CLabel4, unitLabelH);
        CLabel3.Text = $"{tMax - tRange * 0.25:F0}"; Canvas.SetTop(CLabel3, h * 0.25 - fontH / 2);
        CLabel2.Text = $"{tMax - tRange * 0.50:F0}"; Canvas.SetTop(CLabel2, h * 0.50 - fontH / 2);
        CLabel1.Text = $"{tMax - tRange * 0.75:F0}"; Canvas.SetTop(CLabel1, h * 0.75 - fontH / 2);
        CLabel0.Text = $"{tMin:F0}"; Canvas.SetTop(CLabel0, h - fontH);

        double fMin = tMin * 9.0 / 5.0 + 32.0;
        double fMax = tMax * 9.0 / 5.0 + 32.0;
        double fRange = fMax - fMin;
        FLabel4.Text = $"{fMax:F0}"; Canvas.SetTop(FLabel4, unitLabelH);
        FLabel3.Text = $"{fMax - fRange * 0.25:F0}"; Canvas.SetTop(FLabel3, h * 0.25 - fontH / 2);
        FLabel2.Text = $"{fMax - fRange * 0.50:F0}"; Canvas.SetTop(FLabel2, h * 0.50 - fontH / 2);
        FLabel1.Text = $"{fMax - fRange * 0.75:F0}"; Canvas.SetTop(FLabel1, h * 0.75 - fontH / 2);
        FLabel0.Text = $"{fMin:F0}"; Canvas.SetTop(FLabel0, h - fontH);

        DateTime tStartTs = timestamps[rangeStart];
        DateTime tEndTs = timestamps[rangeStart + n - 1];
        double totalSec = Math.Max(1e-9, (tEndTs - tStartTs).TotalSeconds);

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

    private static int MaxRenderPoints(double width) =>
        Math.Max(64, (int)Math.Ceiling(width));

    private static int StrideFor(int n, int maxPoints)
    {
        if (n <= maxPoints || maxPoints <= 1) return 1;
        int stride = (n + maxPoints - 1) / maxPoints;
        return stride < 1 ? 1 : stride;
    }

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
            tb.Text = display.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
            tb.Visibility = Visibility.Visible;

            double halfW = tb.Text.Length * 2.8;
            double left = Math.Clamp(xPos - halfW, 0, Math.Max(0, w - halfW * 2));
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

        if (totalSeconds < 90) { unit = "seconds"; divisor = 1.0; }
        else if (totalSeconds < 5400) { unit = "minutes"; divisor = 60.0; }
        else { unit = "hours"; divisor = 3600.0; }

        double totalInUnit = totalSeconds / divisor;
        double rawStep = totalInUnit / Math.Max(1, targetTicks - 1);
        return (unit, divisor, NiceStep(rawStep));
    }

    private static double NiceStep(double rawStep)
    {
        if (rawStep <= 0) return 1;
        double exponent = Math.Floor(Math.Log10(rawStep));
        double pow = Math.Pow(10, exponent);
        double fraction = rawStep / pow;

        double niceFraction =
              fraction < 1.5 ? 1
            : fraction < 3 ? 2
            : fraction < 7 ? 5
            : 10;

        return niceFraction * pow;
    }

    private async void SaveTempChart_Click(object sender, RoutedEventArgs e)
    {
        var root = TempChartArea.XamlRoot;
        if (root is null) return;

        var prevStart = _filterStart;
        var prevEnd = _filterEnd;

        await ChartExportService.SaveChartFlowAsync(
            root,
            TempChartArea,
            TempChartCanvas,
            "BMS_Temp_Chart",
            ViewModel.GetTimestamps(),
            opts =>
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
                            _filterEnd = baseTime.AddSeconds(opts.EndSec.Value);
                    }
                    OnHistoryUpdated();
                }

                return Task.CompletedTask;
            },
            () =>
            {
                _filterStart = prevStart;
                _filterEnd = prevEnd;
                OnHistoryUpdated();
                return Task.CompletedTask;
            },
            handler => ViewModel.HistoryUpdated += handler,
            handler => ViewModel.HistoryUpdated -= handler,
            DispatcherQueue);
    }

    private void ResetStats_Click(object sender, RoutedEventArgs e) => ViewModel.ResetCellStats();

    private void CellRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not Button button)
            return;

        button.Tag = args.Index + 1;
        button.Click -= CellButton_Click;
        button.Click += CellButton_Click;
        button.Tapped -= CellTile_Tapped;
        button.Tapped += CellTile_Tapped;
        button.PointerReleased -= CellTile_PointerReleased;
        button.PointerReleased += CellTile_PointerReleased;
    }

    private void NtcRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not Button button)
            return;

        button.Tag = args.Index + 1;
        button.Click -= NtcButton_Click;
        button.Click += NtcButton_Click;
    }

    private void NtcButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;

        int ntcNumber = fe.Tag is int tagIndex
            ? tagIndex
            : fe.DataContext is TempViewModel tvm ? tvm.Index : 0;

        int ntcIdx = ntcNumber - 1;
        if (ntcIdx < 0 || ntcIdx >= 10) return;

        try
        {
            ShowNtcChartFlyout(ntcIdx, fe);
        }
        catch (Exception ex)
        {
            _ntcChartFlyout = null;
            _ntcPopupOpen = false;
            System.Diagnostics.Debug.WriteLine($"NTC chart flyout failed: {ex}");
        }
    }

    private void ShowNtcChartFlyout(int sensorIndex, FrameworkElement anchor)
    {
        if (_ntcPopupOpen) return;

        int sensorNum = sensorIndex + 1;
        const double popupWidth = 440;
        const double yAxisWidth = 54;
        const double chartWidth = 360;
        const double chartHeight = 150;
        const double xAxisHeight = 30;

        var canvas      = new Canvas { Width = chartWidth, Height = chartHeight, HorizontalAlignment = HorizontalAlignment.Stretch };
        var yAxisCanvas = new Canvas { Width = yAxisWidth, Height = chartHeight };
        var xAxisCanvas = new Canvas { Width = chartWidth, Height = xAxisHeight };

        var gridBrush = new SolidColorBrush(Color.FromArgb(35, 128, 128, 128));
        for (int g = 0; g < 3; g++)
            canvas.Children.Add(new Line { StrokeThickness = 1, Stroke = gridBrush });

        // Temperature line uses the orange accent (matches "warm" association)
        var polyline = new Polyline
        {
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            Stroke = new SolidColorBrush(Color.FromArgb(255, 255, 152, 0))
        };
        canvas.Children.Add(polyline);

        var chartGrid = new Grid { Width = yAxisWidth + chartWidth };
        chartGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(yAxisWidth) });
        chartGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(chartWidth) });
        chartGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(chartHeight) });
        chartGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(xAxisHeight) });
        Grid.SetColumn(yAxisCanvas, 0); Grid.SetRow(yAxisCanvas, 0);
        Grid.SetColumn(canvas, 1);      Grid.SetRow(canvas, 0);
        Grid.SetColumn(xAxisCanvas, 1); Grid.SetRow(xAxisCanvas, 1);
        chartGrid.Children.Add(yAxisCanvas);
        chartGrid.Children.Add(canvas);
        chartGrid.Children.Add(xAxisCanvas);

        var title = new TextBlock
        {
            Text = $"NTC {sensorNum} Temperature History",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var closeButton = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(10, 3, 10, 3)
        };
        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 0);
        Grid.SetColumn(closeButton, 1);
        header.Children.Add(title);
        header.Children.Add(closeButton);

        var currentLabel = new TextBlock
        {
            Text = $"Current: {ViewModel.Temperatures[sensorIndex].TempText}",
            FontSize = 12,
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 0, 6)
        };
        var rangeLabel = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.65,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.WrapWholeWords
        };

        string FormatElapsed(double seconds)
        {
            if (seconds < 60) return $"{seconds:0}s";
            var time = TimeSpan.FromSeconds(seconds);
            return time.TotalHours >= 1
                ? $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}"
                : $"{time.Minutes:D2}:{time.Seconds:D2}";
        }

        void AddCanvasLabel(Canvas target, string text, double left, double top, double width, TextAlignment alignment, double opacity = 0.7)
        {
            var label = new TextBlock
            {
                Text = text,
                Width = width,
                FontSize = 10,
                Opacity = opacity,
                TextAlignment = alignment
            };
            Canvas.SetLeft(label, left);
            Canvas.SetTop(label, top);
            target.Children.Add(label);
        }

        void Redraw()
        {
            double w = canvas.ActualWidth;
            double h = canvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            var rawHist = ViewModel.GetTempSensorHistory(sensorIndex);
            var hist = rawHist
                .Select(t => UnitFormatter.ToDisplayTemperature(t, ViewModel.TemperatureUnit))
                .ToArray();
            var timestamps = ViewModel.GetTimestamps();
            int n = hist.Length;
            currentLabel.Text = $"Current: {ViewModel.Temperatures[sensorIndex].TempText}";

            int gi = 0;
            foreach (var c in canvas.Children)
            {
                if (c is Line line && gi < 3)
                {
                    line.X1 = 0;
                    line.X2 = w;
                    line.Y1 = h * 0.25 * (gi + 1);
                    line.Y2 = h * 0.25 * (gi + 1);
                    gi++;
                }
            }

            double tMin, tMax, tR;
            if (n >= 2)
            {
                double m = Math.Max(0.5, (hist.Max() - hist.Min()) * 0.1);
                tMin = hist.Min() - m;
                tMax = hist.Max() + m;
                tR = tMax - tMin;
                if (tR <= 0) tR = 1.0;
            }
            else
            {
                double center = sensorIndex >= 0 && sensorIndex < ViewModel.Temperatures.Count
                    ? UnitFormatter.ToDisplayTemperature(ViewModel.Temperatures[sensorIndex].Temperature, ViewModel.TemperatureUnit)
                    : 25;
                tMin = center - 5;
                tMax = center + 5;
                tR = tMax - tMin;
            }

            yAxisCanvas.Children.Clear();
            xAxisCanvas.Children.Clear();
            AddCanvasLabel(yAxisCanvas, "Temp", 0, 0, yAxisWidth - 4, TextAlignment.Right, 0.85);
            AddCanvasLabel(yAxisCanvas, $"({ViewModel.TemperatureSymbol})", 0, 12, yAxisWidth - 4, TextAlignment.Right, 0.85);
            AddCanvasLabel(yAxisCanvas, $"{tMax:F1}", 0, 29, yAxisWidth - 4, TextAlignment.Right);
            AddCanvasLabel(yAxisCanvas, $"{(tMin + tR / 2):F1}", 0, h / 2 - 7, yAxisWidth - 4, TextAlignment.Right);
            AddCanvasLabel(yAxisCanvas, $"{tMin:F1}", 0, h - 14, yAxisWidth - 4, TextAlignment.Right);

            var leftTick = "0s";
            var midTick = n > 1 ? $"{(n - 1) / 2}" : "0";
            var rightTick = n > 1 ? $"{n - 1}" : "0";
            var xAxisTitle = "Sample #";
            if (timestamps.Length == n && n > 1)
            {
                double totalSec = Math.Max(0, (timestamps[^1] - timestamps[0]).TotalSeconds);
                leftTick = "0s";
                midTick = FormatElapsed(totalSec / 2.0);
                rightTick = FormatElapsed(totalSec);
                xAxisTitle = totalSec < 60
                    ? "Elapsed time (s)"
                    : totalSec < 3600 ? "Elapsed time (mm:ss)" : "Elapsed time (h:mm:ss)";
            }
            AddCanvasLabel(xAxisCanvas, leftTick, 0, 0, 70, TextAlignment.Left);
            AddCanvasLabel(xAxisCanvas, midTick, chartWidth / 2 - 35, 0, 70, TextAlignment.Center);
            AddCanvasLabel(xAxisCanvas, rightTick, chartWidth - 70, 0, 70, TextAlignment.Right);
            AddCanvasLabel(xAxisCanvas, xAxisTitle, 0, 15, chartWidth, TextAlignment.Center, 0.85);

            if (n < 2)
            {
                polyline.Points = [];
                rangeLabel.Text = n == 1
                    ? "1 sample collected. Waiting for more samples to draw a line."
                    : "No temperature history yet. Waiting for live/playback samples.";
                return;
            }

            // Position points by real elapsed time so variable Hz rates render
            // correctly (matches the dashboard chart behavior).
            double totalSecForX = 1.0;
            bool useTimestamps = timestamps.Length == n;
            if (useTimestamps)
                totalSecForX = Math.Max(1e-9, (timestamps[^1] - timestamps[0]).TotalSeconds);

            int maxPoints = Math.Max(64, (int)Math.Ceiling(w));
            int stride = n > maxPoints ? (n + maxPoints - 1) / maxPoints : 1;
            var pts = new PointCollection();
            int lastEmitted = -1;
            for (int j = 0; j < n; j += stride)
            {
                double x = useTimestamps
                    ? w * (timestamps[j] - timestamps[0]).TotalSeconds / totalSecForX
                    : j * (w / Math.Max(1, n - 1));
                pts.Add(new Point(x, h * (1.0 - (hist[j] - tMin) / tR)));
                lastEmitted = j;
            }
            if (lastEmitted != n - 1)
            {
                int j = n - 1;
                double x = useTimestamps
                    ? w * (timestamps[j] - timestamps[0]).TotalSeconds / totalSecForX
                    : w;
                pts.Add(new Point(x, h * (1.0 - (hist[j] - tMin) / tR)));
            }
            polyline.Points = pts;
            rangeLabel.Text = $"{n} samples  ·  {hist.Min():F1}-{hist.Max():F1} {ViewModel.TemperatureSymbol}  ·  Range {hist.Max() - hist.Min():F1} {ViewModel.TemperatureSymbol}";
        }

        canvas.SizeChanged += (_, _) => Redraw();

        var panel = new StackPanel { Width = popupWidth, Padding = new Thickness(10), Spacing = 0 };
        panel.Children.Add(header);
        panel.Children.Add(currentLabel);
        panel.Children.Add(chartGrid);
        panel.Children.Add(rangeLabel);

        var presenterStyle = new Style(typeof(FlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.WidthProperty, popupWidth + 20));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, popupWidth + 20));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 320d));
        presenterStyle.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(12)));

        var flyout = new Flyout
        {
            Content = panel,
            FlyoutPresenterStyle = presenterStyle,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft
        };
        closeButton.Click += (_, _) => flyout.Hide();

        ViewModel.HistoryUpdated += Redraw;
        flyout.Closed += (_, _) =>
        {
            ViewModel.HistoryUpdated -= Redraw;
            if (ReferenceEquals(_ntcChartFlyout, flyout))
                _ntcChartFlyout = null;
            _ntcPopupOpen = false;
        };

        try
        {
            _ntcPopupOpen = true;
            _ntcChartFlyout = flyout;
            flyout.ShowAt(anchor);
            _ = DispatcherQueue.TryEnqueue(Redraw);
        }
        catch (Exception ex)
        {
            ViewModel.HistoryUpdated -= Redraw;
            _ntcChartFlyout = null;
            _ntcPopupOpen = false;
            System.Diagnostics.Debug.WriteLine($"NTC chart flyout failed: {ex}");
        }
    }

    private void CellButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
            ShowCellChartFlyout(fe);
    }

    private void CellTile_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is FrameworkElement fe)
            ShowCellChartFlyout(fe);
    }

    private void CellTile_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is FrameworkElement fe)
            ShowCellChartFlyout(fe);
    }

    private void ShowCellChartFlyout(FrameworkElement anchor)
    {
        if (_cellPopupOpen)
            return;

        int cellNumber = anchor.Tag is int tagIndex
            ? tagIndex
            : anchor.DataContext is CellViewModel cvm ? cvm.Index : 0;

        int cellIdx = cellNumber - 1;
        if (cellIdx < 0 || cellIdx >= 20) return;

        try
        {
            ShowCellChartFlyout(cellIdx, anchor);
        }
        catch (Exception ex)
        {
            _cellChartFlyout = null;
            _cellPopupOpen = false;
            System.Diagnostics.Debug.WriteLine($"Cell chart flyout failed: {ex}");
        }
    }

    private void ShowCellChartFlyout(int cellIndex, FrameworkElement anchor)
    {
        int cellNum = cellIndex + 1;
        const double popupWidth = 440;
        const double yAxisWidth = 54;
        const double chartWidth = 360;
        const double chartHeight = 150;
        const double xAxisHeight = 30;
        var canvas = new Canvas { Width = chartWidth, Height = chartHeight, HorizontalAlignment = HorizontalAlignment.Stretch };
        var yAxisCanvas = new Canvas { Width = yAxisWidth, Height = chartHeight };
        var xAxisCanvas = new Canvas { Width = chartWidth, Height = xAxisHeight };

        var gridBrush = new SolidColorBrush(Color.FromArgb(35, 128, 128, 128));
        for (int g = 0; g < 3; g++)
            canvas.Children.Add(new Line { StrokeThickness = 1, Stroke = gridBrush });

        var polyline = new Polyline
        {
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            Stroke = new SolidColorBrush(Color.FromArgb(255, 33, 150, 243))
        };
        canvas.Children.Add(polyline);

        var chartGrid = new Grid { Width = yAxisWidth + chartWidth };
        chartGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(yAxisWidth) });
        chartGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(chartWidth) });
        chartGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(chartHeight) });
        chartGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(xAxisHeight) });
        Grid.SetColumn(yAxisCanvas, 0);
        Grid.SetRow(yAxisCanvas, 0);
        Grid.SetColumn(canvas, 1);
        Grid.SetRow(canvas, 0);
        Grid.SetColumn(xAxisCanvas, 1);
        Grid.SetRow(xAxisCanvas, 1);
        chartGrid.Children.Add(yAxisCanvas);
        chartGrid.Children.Add(canvas);
        chartGrid.Children.Add(xAxisCanvas);

        var title = new TextBlock
        {
            Text = $"Cell C{cellNum:D2} Voltage History",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var closeButton = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(10, 3, 10, 3)
        };
        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 0);
        Grid.SetColumn(closeButton, 1);
        header.Children.Add(title);
        header.Children.Add(closeButton);

        var currentLabel = new TextBlock
        {
            Text = $"Current: {ViewModel.Cells[cellIndex].VoltageText}",
            FontSize = 12,
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 0, 6)
        };
        var rangeLabel = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.65,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.WrapWholeWords
        };

        string FormatElapsed(double seconds)
        {
            if (seconds < 60)
                return $"{seconds:0}s";

            var time = TimeSpan.FromSeconds(seconds);
            return time.TotalHours >= 1
                ? $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}"
                : $"{time.Minutes:D2}:{time.Seconds:D2}";
        }

        void AddCanvasLabel(Canvas target, string text, double left, double top, double width, TextAlignment alignment, double opacity = 0.7)
        {
            var label = new TextBlock
            {
                Text = text,
                Width = width,
                FontSize = 10,
                Opacity = opacity,
                TextAlignment = alignment
            };
            Canvas.SetLeft(label, left);
            Canvas.SetTop(label, top);
            target.Children.Add(label);
        }

        void Redraw()
        {
            double w = canvas.ActualWidth;
            double h = canvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            var rawHist = ViewModel.GetCellHistory(cellIndex);
            var hist = rawHist
                .Select(v => UnitFormatter.ToDisplayVoltage(v, ViewModel.VoltageUnit))
                .ToArray();
            var timestamps = ViewModel.GetTimestamps();
            int n = hist.Length;
            currentLabel.Text = $"Current: {ViewModel.Cells[cellIndex].VoltageText}";
            string valueFormat = ViewModel.VoltageUnit == "mV" ? "F1" : "F3";
            double minPadding = ViewModel.VoltageUnit == "mV" ? 10.0 : 0.01;
            double fallbackPadding = ViewModel.VoltageUnit == "mV" ? 50.0 : 0.05;
            double minRange = ViewModel.VoltageUnit == "mV" ? 100.0 : 0.1;

            int gi = 0;
            foreach (var c in canvas.Children)
            {
                if (c is Line line && gi < 3)
                {
                    line.X1 = 0;
                    line.X2 = w;
                    line.Y1 = h * 0.25 * (gi + 1);
                    line.Y2 = h * 0.25 * (gi + 1);
                    gi++;
                }
            }

            double vMin;
            double vMax;
            double vR;
            if (n >= 2)
            {
                double m = Math.Max(minPadding, (hist.Max() - hist.Min()) * 0.1);
                vMin = hist.Min() - m;
                vMax = hist.Max() + m;
                vR = vMax - vMin;
                if (vR <= 0) vR = minRange;
            }
            else
            {
                double center = cellIndex >= 0 && cellIndex < ViewModel.Cells.Count
                    ? UnitFormatter.ToDisplayVoltage(ViewModel.Cells[cellIndex].Voltage, ViewModel.VoltageUnit)
                    : 0;
                vMin = center > 0 ? center - fallbackPadding : 0;
                vMax = center > 0 ? center + fallbackPadding : minRange;
                vR = vMax - vMin;
            }

            yAxisCanvas.Children.Clear();
            xAxisCanvas.Children.Clear();
            AddCanvasLabel(yAxisCanvas, "Voltage", 0, 0, yAxisWidth - 4, TextAlignment.Right, 0.85);
            AddCanvasLabel(yAxisCanvas, $"({ViewModel.VoltageSymbol})", 0, 12, yAxisWidth - 4, TextAlignment.Right, 0.85);
            AddCanvasLabel(yAxisCanvas, vMax.ToString(valueFormat), 0, 29, yAxisWidth - 4, TextAlignment.Right);
            AddCanvasLabel(yAxisCanvas, (vMin + vR / 2).ToString(valueFormat), 0, h / 2 - 7, yAxisWidth - 4, TextAlignment.Right);
            AddCanvasLabel(yAxisCanvas, vMin.ToString(valueFormat), 0, h - 14, yAxisWidth - 4, TextAlignment.Right);

            var leftTick = "0s";
            var midTick = n > 1 ? $"{(n - 1) / 2}" : "0";
            var rightTick = n > 1 ? $"{n - 1}" : "0";
            var xAxisTitle = "Sample #";
            if (timestamps.Length == n && n > 1)
            {
                double totalSec = Math.Max(0, (timestamps[^1] - timestamps[0]).TotalSeconds);
                leftTick = "0s";
                midTick = FormatElapsed(totalSec / 2.0);
                rightTick = FormatElapsed(totalSec);
                xAxisTitle = totalSec < 60
                    ? "Elapsed time (s)"
                    : totalSec < 3600 ? "Elapsed time (mm:ss)" : "Elapsed time (h:mm:ss)";
            }
            AddCanvasLabel(xAxisCanvas, leftTick, 0, 0, 70, TextAlignment.Left);
            AddCanvasLabel(xAxisCanvas, midTick, chartWidth / 2 - 35, 0, 70, TextAlignment.Center);
            AddCanvasLabel(xAxisCanvas, rightTick, chartWidth - 70, 0, 70, TextAlignment.Right);
            AddCanvasLabel(xAxisCanvas, xAxisTitle, 0, 15, chartWidth, TextAlignment.Center, 0.85);

            if (n < 2)
            {
                polyline.Points = [];
                rangeLabel.Text = n == 1
                    ? "1 sample collected. Waiting for more samples to draw a line."
                    : "No voltage history yet. Waiting for live/playback samples.";
                return;
            }

            double xs = n > 1 ? w / (n - 1.0) : w;
            // Cap polyline at ~one point per pixel so long sessions don't
            // balloon the popup's PointCollection (~chart width is enough
            // resolution; extra points are invisible).
            int maxPoints = Math.Max(64, (int)Math.Ceiling(w));
            int stride = n > maxPoints ? (n + maxPoints - 1) / maxPoints : 1;
            var pts = new PointCollection();
            int lastEmitted = -1;
            for (int j = 0; j < n; j += stride)
            {
                pts.Add(new Point(j * xs, h * (1.0 - (hist[j] - vMin) / vR)));
                lastEmitted = j;
            }
            if (lastEmitted != n - 1)
                pts.Add(new Point((n - 1) * xs, h * (1.0 - (hist[n - 1] - vMin) / vR)));
            polyline.Points = pts;
            rangeLabel.Text = $"{n} samples  ·  {hist.Min().ToString(valueFormat)}-{hist.Max().ToString(valueFormat)} {ViewModel.VoltageSymbol}  ·  Delta {(hist.Max() - hist.Min()).ToString(valueFormat)} {ViewModel.VoltageSymbol}";
        }

        canvas.SizeChanged += (_, _) => Redraw();

        var panel = new StackPanel { Width = popupWidth, Padding = new Thickness(10), Spacing = 0 };
        panel.Children.Add(header);
        panel.Children.Add(currentLabel);
        panel.Children.Add(chartGrid);
        panel.Children.Add(rangeLabel);

        var presenterStyle = new Style(typeof(FlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.WidthProperty, popupWidth + 20));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, popupWidth + 20));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 320d));
        presenterStyle.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(12)));

        var flyout = new Flyout
        {
            Content = panel,
            FlyoutPresenterStyle = presenterStyle,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft
        };
        closeButton.Click += (_, _) => flyout.Hide();

        ViewModel.HistoryUpdated += Redraw;
        flyout.Closed += (_, _) =>
        {
            ViewModel.HistoryUpdated -= Redraw;
            if (ReferenceEquals(_cellChartFlyout, flyout))
                _cellChartFlyout = null;
            _cellPopupOpen = false;
        };

        try
        {
            _cellPopupOpen = true;
            _cellChartFlyout = flyout;
            flyout.ShowAt(anchor);
            _ = DispatcherQueue.TryEnqueue(Redraw);
        }
        catch (Exception ex)
        {
            ViewModel.HistoryUpdated -= Redraw;
            _cellChartFlyout = null;
            _cellPopupOpen = false;
            System.Diagnostics.Debug.WriteLine($"Cell chart flyout failed: {ex}");
        }
    }

    // ── Trim bar (drives the Temperature History chart's _filterStart/_filterEnd) ──
    private bool _draggingStart;
    private bool _draggingEnd;

    private void TrimCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateTrimBar();

    private void StartThumb_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _draggingStart = true;
        StartThumb.CapturePointer(e.Pointer);
    }

    private void EndThumb_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _draggingEnd = true;
        EndThumb.CapturePointer(e.Pointer);
    }

    private void Thumb_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingStart && !_draggingEnd) return;

        var earliest = ViewModel.EarliestTimestamp;
        var latest   = ViewModel.LatestTimestamp;
        if (earliest is null || latest is null || earliest == latest) return;

        double w = TrimCanvas.ActualWidth;
        if (w <= 0) return;

        double xCanvas = e.GetCurrentPoint(TrimCanvas).Position.X;
        xCanvas = Math.Clamp(xCanvas, 0, w);

        var ts = XToTimestamp(xCanvas, earliest.Value, latest.Value, w);

        if (_draggingStart)
        {
            var endTs = _filterEnd ?? latest.Value;
            if (ts >= endTs) ts = endTs.AddSeconds(-1);
            _filterStart = ts;
        }
        else if (_draggingEnd)
        {
            var startTs = _filterStart ?? earliest.Value;
            if (ts <= startTs) ts = startTs.AddSeconds(1);
            _filterEnd = ts;
        }

        UpdateTrimBar();
        RedrawTempChart();
    }

    private void Thumb_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_draggingStart)
        {
            var earliest = ViewModel.EarliestTimestamp;
            if (earliest is not null && _filterStart is not null &&
                (_filterStart.Value - earliest.Value).TotalSeconds < 1)
                _filterStart = null;
            StartThumb.ReleasePointerCapture(e.Pointer);
        }
        if (_draggingEnd)
        {
            var latest = ViewModel.LatestTimestamp;
            if (latest is not null && _filterEnd is not null &&
                (latest.Value - _filterEnd.Value).TotalSeconds < 1)
                _filterEnd = null;
            EndThumb.ReleasePointerCapture(e.Pointer);
        }
        _draggingStart = false;
        _draggingEnd   = false;

        UpdateTrimBar();
        RedrawTempChart();
    }

    private void ResetTrim_Click(object sender, RoutedEventArgs e)
    {
        _filterStart = null;
        _filterEnd   = null;
        UpdateTrimBar();
        RedrawTempChart();
    }

    private void UpdateTrimBar()
    {
        if (TrimCanvas is null) return;

        double w = TrimCanvas.ActualWidth;
        if (w <= 0) return;

        TrimTrack.Width = w;
        Canvas.SetLeft(TrimTrack, 0);

        var earliest = ViewModel.EarliestTimestamp;
        var latest   = ViewModel.LatestTimestamp;

        bool noData = earliest is null || latest is null || earliest == latest;

        StartThumb.IsHitTestVisible = !noData;
        EndThumb.IsHitTestVisible   = !noData;
        ResetTrimBtn.IsEnabled      = _filterStart.HasValue || _filterEnd.HasValue;

        if (noData)
        {
            DataStartLabel.Text = "—";
            DataEndLabel.Text   = "—";
            TrimRangeText.Text  = "No data captured yet";
            Canvas.SetLeft(StartThumb, 0);
            Canvas.SetLeft(EndThumb,   Math.Max(0, w - EndThumb.Width));
            Canvas.SetLeft(TrimSelection, 0);
            TrimSelection.Width = 0;
            return;
        }

        DataStartLabel.Text = "00:00:00";
        DataEndLabel.Text   = FormatElapsedSpan(latest!.Value - earliest!.Value);

        var trimStart = _filterStart ?? earliest.Value;
        var trimEnd   = _filterEnd   ?? latest.Value;

        double startX = TimestampToX(trimStart, earliest.Value, latest.Value, w);
        double endX   = TimestampToX(trimEnd,   earliest.Value, latest.Value, w);

        Canvas.SetLeft(StartThumb, startX - StartThumb.Width / 2);
        Canvas.SetLeft(EndThumb,   endX   - EndThumb.Width   / 2);

        Canvas.SetLeft(TrimSelection, startX);
        TrimSelection.Width = Math.Max(0, endX - startX);

        TimeSpan trimStartElapsed = trimStart - earliest.Value;
        TimeSpan trimEndElapsed   = trimEnd   - earliest.Value;
        TimeSpan duration         = trimEnd   - trimStart;

        string durStr = duration.TotalHours    >= 1 ? $"{duration.TotalHours:0.#} h"
                      : duration.TotalMinutes  >= 1 ? $"{duration.TotalMinutes:0.#} min"
                      :                                $"{duration.TotalSeconds:0} s";

        string startStr = FormatElapsedSpan(trimStartElapsed);
        string endStr   = FormatElapsedSpan(trimEndElapsed);

        TrimRangeText.Text = (_filterStart.HasValue || _filterEnd.HasValue)
            ? $"Trim: {startStr} → {endStr}  ·  {durStr}"
            : $"Full range: {startStr} → {endStr}  ·  {durStr}  (drag a handle to trim)";
    }

    private static string FormatElapsedSpan(TimeSpan ts)
    {
        if (ts.Ticks < 0) ts = TimeSpan.Zero;
        int hours = (int)Math.Floor(ts.TotalHours);
        return $"{hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private static double TimestampToX(DateTime t, DateTime earliest, DateTime latest, double w)
    {
        double totalSec = (latest - earliest).TotalSeconds;
        if (totalSec <= 0) return 0;
        double frac = (t - earliest).TotalSeconds / totalSec;
        return Math.Clamp(frac, 0, 1) * w;
    }

    private static DateTime XToTimestamp(double x, DateTime earliest, DateTime latest, double w)
    {
        if (w <= 0) return earliest;
        double frac = Math.Clamp(x / w, 0, 1);
        double totalSec = (latest - earliest).TotalSeconds;
        return earliest.AddSeconds(frac * totalSec);
    }
}
