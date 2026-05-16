using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
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

    public CellViewPage()
    {
        InitializeComponent();
    }

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

            var hist = ViewModel.GetTempSensorHistory(sensorIndex);
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
                    ? ViewModel.Temperatures[sensorIndex].Temperature
                    : 25;
                tMin = center - 5;
                tMax = center + 5;
                tR = tMax - tMin;
            }

            yAxisCanvas.Children.Clear();
            xAxisCanvas.Children.Clear();
            AddCanvasLabel(yAxisCanvas, "Temp", 0, 0, yAxisWidth - 4, TextAlignment.Right, 0.85);
            AddCanvasLabel(yAxisCanvas, "(°C)", 0, 12, yAxisWidth - 4, TextAlignment.Right, 0.85);
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
            rangeLabel.Text = $"{n} samples  ·  {hist.Min():F1}-{hist.Max():F1} °C  ·  Range {hist.Max() - hist.Min():F1} °C";
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

            var hist = ViewModel.GetCellHistory(cellIndex);
            var timestamps = ViewModel.GetTimestamps();
            int n = hist.Length;
            currentLabel.Text = $"Current: {ViewModel.Cells[cellIndex].VoltageText}";

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
                double m = Math.Max(0.01, (hist.Max() - hist.Min()) * 0.1);
                vMin = hist.Min() - m;
                vMax = hist.Max() + m;
                vR = vMax - vMin;
                if (vR <= 0) vR = 0.1;
            }
            else
            {
                double center = cellIndex >= 0 && cellIndex < ViewModel.Cells.Count
                    ? ViewModel.Cells[cellIndex].Voltage
                    : 0;
                vMin = center > 0 ? center - 0.05 : 0;
                vMax = center > 0 ? center + 0.05 : 1;
                vR = vMax - vMin;
            }

            yAxisCanvas.Children.Clear();
            xAxisCanvas.Children.Clear();
            AddCanvasLabel(yAxisCanvas, "Voltage", 0, 0, yAxisWidth - 4, TextAlignment.Right, 0.85);
            AddCanvasLabel(yAxisCanvas, "(V)", 0, 12, yAxisWidth - 4, TextAlignment.Right, 0.85);
            AddCanvasLabel(yAxisCanvas, $"{vMax:F3}", 0, 29, yAxisWidth - 4, TextAlignment.Right);
            AddCanvasLabel(yAxisCanvas, $"{(vMin + vR / 2):F3}", 0, h / 2 - 7, yAxisWidth - 4, TextAlignment.Right);
            AddCanvasLabel(yAxisCanvas, $"{vMin:F3}", 0, h - 14, yAxisWidth - 4, TextAlignment.Right);

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
            rangeLabel.Text = $"{n} samples  ·  {hist.Min():F3}-{hist.Max():F3} V  ·  Delta {hist.Max() - hist.Min():F3} V";
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
}
