using CommunityToolkit.Mvvm.ComponentModel;
using BMSMonitor.Models;

namespace BMSMonitor.ViewModels;

public partial class CellViewModel : ObservableObject
{
    public int Index { get; init; }
    public string Label => $"C{Index:D2}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VoltageText))]
    private double _voltage;

    [ObservableProperty] private CellState _state;
    [ObservableProperty] private bool _isBalancing;

    public string VoltageText => $"{Voltage:F3} V";

    // ── Session statistics ───────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatMinText))]
    [NotifyPropertyChangedFor(nameof(StatDriftText))]
    private double _statMin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatMaxText))]
    [NotifyPropertyChangedFor(nameof(StatDriftText))]
    private double _statMax;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatAvgText))]
    private double _statAvg;

    public string StatMinText   => _statMin > 0 ? $"{_statMin:F3}" : "—";
    public string StatMaxText   => _statMax > 0 ? $"{_statMax:F3}" : "—";
    public string StatAvgText   => _statAvg > 0 ? $"{_statAvg:F3}" : "—";
    public string StatDriftText => (_statMax > 0 && _statMin > 0)
        ? $"{(_statMax - _statMin) * 1000:F1}" : "—";

    public void ResetStats() { StatMin = 0; StatMax = 0; StatAvg = 0; }
}
