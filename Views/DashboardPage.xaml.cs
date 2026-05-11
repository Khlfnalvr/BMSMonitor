using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using BMSMonitor.ViewModels;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace BMSMonitor.Views;

public sealed partial class DashboardPage : Page
{
    private MainViewModel ViewModel => App.ViewModel;
    private Services.LocalizationManager Lang => App.Lang;

    public DashboardPage()
    {
        InitializeComponent();
        ApplyChartColors();

        Loaded   += (_, _) => ViewModel.HistoryUpdated += OnHistoryUpdated;
        Unloaded += (_, _) => ViewModel.HistoryUpdated -= OnHistoryUpdated;
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
        RedrawSocChart();
        RedrawVIChart();
    }

    private void SocChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        => RedrawSocChart();

    private void VIChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        => RedrawVIChart();

    private void RedrawSocChart()
    {
        double w = SocChartCanvas.ActualWidth;
        double h = SocChartCanvas.ActualHeight;
        if (w == 0 || h == 0) return;

        // Horizontal grid lines at 25 / 50 / 75 %
        UpdateGridLine(GridLine25, w, h * 0.75);
        UpdateGridLine(GridLine50, w, h * 0.50);
        UpdateGridLine(GridLine75, w, h * 0.25);

        double[] history = ViewModel.GetSocHistory();
        int      n       = history.Length;

        if (n < 2)
        {
            SocLine.Points = [];
            SocFill.Points = [];
            return;
        }

        // Oldest sample is placed at the left edge proportional to how full
        // the buffer is — so the line always terminates at the right edge.
        double cap    = MainViewModel.HistoryCapacity;
        double xStep  = w / (cap - 1.0);
        double xStart = (cap - n) * xStep;

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
    }

    private static void UpdateGridLine(Line line, double width, double y)
    {
        line.X1 = 0;
        line.X2 = width;
        line.Y1 = y;
        line.Y2 = y;
    }

    // ── V/I dual-axis chart ───────────────────────────────────────────────
    private void RedrawVIChart()
    {
        double w = VIChartCanvas.ActualWidth;
        double h = VIChartCanvas.ActualHeight;
        if (w == 0 || h == 0) return;

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
            return;
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
        const double fontH = 11.0;   // approximate TextBlock height for 9pt

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
        double cap    = MainViewModel.HistoryCapacity;
        double xStep  = w / (cap - 1.0);
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
    }
}
