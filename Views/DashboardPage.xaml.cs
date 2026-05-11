using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
        PopulateTimeframeCombo();
        UpdateXAxisLabels();

        Loaded   += (_, _) => ViewModel.HistoryUpdated += OnHistoryUpdated;
        Unloaded += (_, _) => ViewModel.HistoryUpdated -= OnHistoryUpdated;
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
        UpdateTrimBar();   // keep the trim selection in sync with the data range
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
    /// (or the whole range when no filter is set). Used by both chart redraws.
    /// </summary>
    private (int start, int end, double timeframeMinutes) GetActiveRange(int fullLength)
    {
        if (_filterStart.HasValue || _filterEnd.HasValue)
        {
            var ts = ViewModel.GetTimestamps();
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

        return (0, fullLength, ViewModel.HistoryTimeframeMinutes);
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
        var (rangeStart, rangeEnd, timeframeMinutes) = GetActiveRange(fullHistory.Length);

        // Slice to the active range (filter or full history)
        int n = Math.Max(0, rangeEnd - rangeStart);
        double cap;
        double xStep;
        double xStart;

        if (_filterStart.HasValue || _filterEnd.HasValue)
        {
            // Filter mode: data fills the chart from left to right
            cap    = n;
            xStep  = n > 1 ? w / (n - 1.0) : w;
            xStart = 0;
        }
        else
        {
            // Rolling window mode: align rightmost sample with "now"
            cap = ViewModel.HistoryCapacity;
            if (cap <= 0 || cap > n) cap = n;
            xStep  = cap > 1 ? w / (cap - 1.0) : w;
            xStart = (cap - n) * xStep;
        }

        if (n < 2)
        {
            SocLine.Points = [];
            SocFill.Points = [];
            HideTicks(_socTicks);
            return "";
        }

        var linePoints = new PointCollection();
        var fillPoints = new PointCollection();

        fillPoints.Add(new Point(xStart, h));

        for (int i = 0; i < n; i++)
        {
            double x = xStart + i * xStep;
            double y = h * (1.0 - fullHistory[rangeStart + i] / 100.0);
            linePoints.Add(new Point(x, y));
            fillPoints.Add(new Point(x, y));
        }

        fillPoints.Add(new Point(xStart + (n - 1) * xStep, h));

        SocLine.Points = linePoints;
        SocFill.Points = fillPoints;

        return UpdateTimeTicks(w, h, cap, n, timeframeMinutes, _socTicks);
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
        var (rangeStart, rangeEnd, timeframeMinutes) = GetActiveRange(fullV.Length);
        int n = Math.Max(0, rangeEnd - rangeStart);

        if (n < 2)
        {
            VoltageLine.Points = new PointCollection();
            CurrentLine.Points = new PointCollection();
            HideTicks(_viTicks);
            return "";
        }

        // Slice both series to the active range
        var voltages = new double[n];
        var currents = new double[n];
        for (int i = 0; i < n; i++)
        {
            voltages[i] = fullV[rangeStart + i];
            currents[i] = fullI[rangeStart + i];
        }

        double vMin    = voltages.Min();
        double vMax    = voltages.Max();
        double vRawMin = Math.Floor(vMin   / 5.0) * 5.0;
        double vRawMax = Math.Ceiling(vMax / 5.0) * 5.0;
        if (vRawMax <= vRawMin) vRawMax = vRawMin + 5.0;
        double vRange  = vRawMax - vRawMin;

        double iMin    = currents.Min();
        double iMax    = currents.Max();
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

        double cap;
        double xStep;
        double xStart;

        if (_filterStart.HasValue || _filterEnd.HasValue)
        {
            cap    = n;
            xStep  = n > 1 ? w / (n - 1.0) : w;
            xStart = 0;
        }
        else
        {
            cap = ViewModel.HistoryCapacity;
            if (cap <= 0 || cap > n) cap = n;
            xStep  = cap > 1 ? w / (cap - 1.0) : w;
            xStart = (cap - n) * xStep;
        }

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

        return UpdateTimeTicks(w, h, cap, n, timeframeMinutes, _viTicks);
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

    private static string UpdateTimeTicks(double w, double h, double cap, int n,
        double timeframeMinutes, TextBlock[] ticks)
    {
        double totalSeconds = timeframeMinutes > 0
            ? timeframeMinutes * 60.0
            : Math.Max(1, n - 1);

        int targetTicks = (int)Math.Clamp(w / 70.0, 6, 18);
        var (unit, divisor, step) = PickAxisScale(totalSeconds, targetTicks);
        string fmt = step >= 1.0 ? "F0" : "F1";

        double totalUnits = totalSeconds / divisor;
        double xStep      = cap > 1 ? w / (cap - 1.0) : w;
        double xStart     = (cap - n) * xStep;

        int used = 0;
        for (double v = 0.0; v <= totalUnits + 1e-9 && used < ticks.Length; v += step)
        {
            double frac = totalUnits > 0 ? v / totalUnits : 0;
            double xPos = n > 1 ? xStart + frac * (n - 1) * xStep : frac * w;
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

    private record ExportOptions(int Width, int Height, string Format);

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

        var opts = await ShowExportDialog(root);
        if (opts is null) return;

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

    /// <summary>
    /// Shows the export dialog (size + aspect + format). Returns null on cancel.
    /// Time range is set globally via the trim bar on the dashboard.
    /// </summary>
    private static async Task<ExportOptions?> ShowExportDialog(XamlRoot root)
    {
        // ── Controls ──────────────────────────────────────────────────────
        var aspect = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex       = 0
        };
        aspect.Items.Add(new ComboBoxItem { Content = "4:3 — paper / Origin default",    Tag = "1.3333" });
        aspect.Items.Add(new ComboBoxItem { Content = "3:2 — photo / wide paper",        Tag = "1.5"    });
        aspect.Items.Add(new ComboBoxItem { Content = "16:9 — slide / video",            Tag = "1.7778" });
        aspect.Items.Add(new ComboBoxItem { Content = "φ — golden 1.618:1",              Tag = "1.6180" });
        aspect.Items.Add(new ComboBoxItem { Content = "1:1 — square (correlation)",      Tag = "1.0"    });
        aspect.Items.Add(new ComboBoxItem { Content = "Custom — set height manually",    Tag = "0"      });

        var widthBox = new NumberBox
        {
            HorizontalAlignment     = HorizontalAlignment.Stretch,
            Value = 600, Minimum = 200, Maximum = 4000,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 50, LargeChange = 100
        };
        var heightBox = new NumberBox
        {
            HorizontalAlignment     = HorizontalAlignment.Stretch,
            Value = 450, Minimum = 150, Maximum = 4000,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 25, LargeChange = 100,
            IsEnabled = false   // default aspect 4:3 is locked
        };

        var format = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex       = 0
        };
        format.Items.Add(new ComboBoxItem { Content = "PNG — raster, lossless", Tag = "png" });
        format.Items.Add(new ComboBoxItem { Content = "JPG — raster, smaller",  Tag = "jpg" });
        format.Items.Add(new ComboBoxItem { Content = "SVG — vector, editable", Tag = "svg" });

        // ── Behavior: aspect-locked width/height linking ─────────────────
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

        aspect.SelectionChanged += (_, _) =>
        {
            double a = GetAspect();
            heightBox.IsEnabled = a <= 0;
            RecomputeHeight();
        };
        widthBox.ValueChanged += (_, _) => RecomputeHeight();

        // ── Layout ────────────────────────────────────────────────────────
        // Helper factories — small caption above a control, both stretched.
        TextBlock FieldLabel(string text) => new()
        {
            Text       = text,
            FontSize   = 12,
            Opacity    = 0.7,
            Margin     = new Thickness(0, 0, 0, 4)
        };

        TextBlock SectionHeader(string text) => new()
        {
            Text       = text,
            FontSize   = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Opacity    = 0.9,
            Margin     = new Thickness(0, 4, 0, 6)
        };

        Border Divider() => new()
        {
            Height     = 1,
            Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Margin     = new Thickness(0, 10, 0, 6)
        };

        var panel = new StackPanel { Spacing = 0, MinWidth = 440 };

        // — Section: Dimensions —
        panel.Children.Add(SectionHeader("Dimensions"));

        panel.Children.Add(FieldLabel("Aspect ratio"));
        panel.Children.Add(aspect);

        // Width + Height side-by-side, each in its own grid column
        var sizeGrid = new Grid
        {
            ColumnSpacing = 12,
            Margin        = new Thickness(0, 10, 0, 0)
        };
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sizeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sizeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var wLab = FieldLabel("Width (px)");   Grid.SetColumn(wLab, 0); Grid.SetRow(wLab, 0);
        var hLab = FieldLabel("Height (px)");  Grid.SetColumn(hLab, 1); Grid.SetRow(hLab, 0);
        Grid.SetColumn(widthBox,  0);          Grid.SetRow(widthBox,  1);
        Grid.SetColumn(heightBox, 1);          Grid.SetRow(heightBox, 1);

        sizeGrid.Children.Add(wLab);
        sizeGrid.Children.Add(hLab);
        sizeGrid.Children.Add(widthBox);
        sizeGrid.Children.Add(heightBox);

        panel.Children.Add(sizeGrid);

        // — Section: Format —
        panel.Children.Add(Divider());
        panel.Children.Add(SectionHeader("File format"));
        panel.Children.Add(format);

        panel.Children.Add(new TextBlock
        {
            Text = "SVG exports the chart drawing area only (curves and ticks). " +
                   "For complete figures with axis labels included, choose PNG or JPG.\n\n" +
                   "Tip: use the trim bar below the V/I chart to select a specific " +
                   "time segment before exporting.",
            FontSize     = 11,
            Opacity      = 0.55,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 10, 0, 0)
        });

        var dialog = new ContentDialog
        {
            Title             = "Export chart",
            Content           = panel,
            PrimaryButtonText = "Save…",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Primary,
            XamlRoot          = root
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;

        string fmt = "png";
        if (format.SelectedItem is ComboBoxItem fi && fi.Tag is string ft) fmt = ft;

        return new ExportOptions((int)widthBox.Value, (int)heightBox.Value, fmt);
    }

    /// <summary>
    /// Renders the chart to a raster image at the requested dimensions.
    /// Temporarily resizes the chart card so the chart re-layouts at the
    /// target size, snapshots it, then restores the original dimensions.
    /// </summary>
    private static async Task SaveAsRaster(
        FrameworkElement renderElement,
        Border card,
        Canvas mainCanvas,
        FrameworkElement[] heightSyncs,
        StorageFile file,
        ExportOptions opts,
        bool jpeg)
    {
        // Snapshot original dimensions
        double oldMaxW = card.MaxWidth;
        double oldH    = mainCanvas.Height;
        var    oldSync = heightSyncs.Select(s => s.Height).ToArray();

        try
        {
            // Resize chart card so it actually re-layouts to target size
            card.MaxWidth      = opts.Width;
            mainCanvas.Height  = opts.Height;
            foreach (var s in heightSyncs) s.Height = opts.Height;

            card.UpdateLayout();
            renderElement.UpdateLayout();
            await Task.Yield();           // let layout settle one cycle
            renderElement.UpdateLayout(); // second pass — picks up new chart canvas size

            // Snapshot
            var rt = new RenderTargetBitmap();
            await rt.RenderAsync(renderElement);

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
        finally
        {
            // Restore original dimensions
            card.MaxWidth     = oldMaxW;
            mainCanvas.Height = oldH;
            for (int i = 0; i < heightSyncs.Length; i++)
                heightSyncs[i].Height = oldSync[i];
        }
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

    // ── Trim bar (video-editor style time range selector) ─────────────────
    // Two draggable handles set the visible time window; the charts re-render
    // to the selected range. Reset returns to the rolling-window mode.

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
        RedrawSocChart();
        RedrawVIChart();
    }

    private void Thumb_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_draggingStart)
        {
            // Snap to "no trim at start" if dropped at the leftmost edge
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
        RedrawSocChart();
        RedrawVIChart();
    }

    private void ResetTrim_Click(object sender, RoutedEventArgs e)
    {
        _filterStart = null;
        _filterEnd   = null;
        UpdateTrimBar();
        RedrawSocChart();
        RedrawVIChart();
    }

    /// <summary>
    /// Repositions the trim track, selection, and handles based on the current
    /// data range (earliest..latest) and active filter (_filterStart/_filterEnd).
    /// </summary>
    private void UpdateTrimBar()
    {
        if (TrimCanvas is null) return;

        double w = TrimCanvas.ActualWidth;
        if (w <= 0) return;

        // Track always spans the full canvas
        TrimTrack.Width = w;
        Canvas.SetLeft(TrimTrack, 0);

        var earliest = ViewModel.EarliestTimestamp;
        var latest   = ViewModel.LatestTimestamp;

        bool noData = earliest is null || latest is null || earliest == latest;

        // Disable interaction when there's no usable range
        StartThumb.IsHitTestVisible = !noData;
        EndThumb.IsHitTestVisible   = !noData;
        ResetTrimBtn.IsEnabled      = _filterStart.HasValue || _filterEnd.HasValue;

        if (noData)
        {
            DataStartLabel.Text = "—";
            DataEndLabel.Text   = "—";
            TrimRangeText.Text  = "No data captured yet";
            // Park handles at extremes
            Canvas.SetLeft(StartThumb, 0);
            Canvas.SetLeft(EndThumb,   Math.Max(0, w - EndThumb.Width));
            Canvas.SetLeft(TrimSelection, 0);
            TrimSelection.Width = 0;
            return;
        }

        DataStartLabel.Text = earliest!.Value.ToString("HH:mm:ss");
        DataEndLabel.Text   = latest!.Value.ToString("HH:mm:ss");

        var trimStart = _filterStart ?? earliest.Value;
        var trimEnd   = _filterEnd   ?? latest.Value;

        double startX = TimestampToX(trimStart, earliest.Value, latest.Value, w);
        double endX   = TimestampToX(trimEnd,   earliest.Value, latest.Value, w);

        // Center the thumbs on their target X
        Canvas.SetLeft(StartThumb, startX - StartThumb.Width / 2);
        Canvas.SetLeft(EndThumb,   endX   - EndThumb.Width   / 2);

        // Selection highlight between the two handles
        Canvas.SetLeft(TrimSelection, startX);
        TrimSelection.Width = Math.Max(0, endX - startX);

        // Status line
        TimeSpan duration = trimEnd - trimStart;
        string durStr = duration.TotalHours    >= 1 ? $"{duration.TotalHours:0.#} h"
                      : duration.TotalMinutes  >= 1 ? $"{duration.TotalMinutes:0.#} min"
                      :                                $"{duration.TotalSeconds:0} s";

        TrimRangeText.Text = (_filterStart.HasValue || _filterEnd.HasValue)
            ? $"Trim: {trimStart:HH:mm:ss} → {trimEnd:HH:mm:ss}  ·  {durStr}"
            : $"Full range: {trimStart:HH:mm:ss} → {trimEnd:HH:mm:ss}  ·  {durStr}  (drag a handle to trim)";
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
