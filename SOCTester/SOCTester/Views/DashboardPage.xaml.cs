using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SOCTester.ViewModels;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace SOCTester.Views;

public sealed partial class DashboardPage : Page
{
    private MainViewModel ViewModel => App.ViewModel;

    public DashboardPage()
    {
        InitializeComponent();
        ApplyChartColors();

        Loaded   += (_, _) => ViewModel.HistoryUpdated += OnHistoryUpdated;
        Unloaded += (_, _) => ViewModel.HistoryUpdated -= OnHistoryUpdated;
    }

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
    }

    private void OnHistoryUpdated() => RedrawSocChart();

    private void SocChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        => RedrawSocChart();

    private void RedrawSocChart()
    {
        double w = SocChartCanvas.ActualWidth;
        double h = SocChartCanvas.ActualHeight;
        if (w == 0 || h == 0) return;

        UpdateGridLine(GridLine25, w, h * 0.75);
        UpdateGridLine(GridLine50, w, h * 0.50);
        UpdateGridLine(GridLine75, w, h * 0.25);

        double[] history = ViewModel.GetSocHistory();
        int n = history.Length;
        if (n < 2)
        {
            SocLine.Points = [];
            SocFill.Points = [];
            return;
        }

        double cap    = MainViewModel.HistoryCapacity;
        double xStep  = w / (cap - 1.0);
        double xStart = (cap - n) * xStep;

        var line = new PointCollection();
        var fill = new PointCollection { new Point(xStart, h) };

        for (int i = 0; i < n; i++)
        {
            double x = xStart + i * xStep;
            double y = h * (1.0 - history[i] / 100.0);
            line.Add(new Point(x, y));
            fill.Add(new Point(x, y));
        }
        fill.Add(new Point(xStart + (n - 1) * xStep, h));

        SocLine.Points = line;
        SocFill.Points = fill;
    }

    private static void UpdateGridLine(Line line, double width, double y)
    {
        line.X1 = 0; line.X2 = width;
        line.Y1 = y; line.Y2 = y;
    }
}
