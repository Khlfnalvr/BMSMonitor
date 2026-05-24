using CommunityToolkit.Mvvm.ComponentModel;
using BMSMonitor.Models;
using BMSMonitor.Services;

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

    private string _voltageUnit = "V";

    public string VoltageUnit
    {
        get => _voltageUnit;
        set
        {
            var normalized = UnitFormatter.NormalizeVoltageUnit(value);
            if (!SetProperty(ref _voltageUnit, normalized)) return;

            OnPropertyChanged(nameof(VoltageText));
            OnPropertyChanged(nameof(StatMinText));
            OnPropertyChanged(nameof(StatMaxText));
            OnPropertyChanged(nameof(StatAvgText));
            OnPropertyChanged(nameof(StatDriftText));
        }
    }

    public string VoltageText => UnitFormatter.FormatVoltage(Voltage, VoltageUnit);

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

    public string StatMinText   => StatMin > 0 ? UnitFormatter.FormatVoltageValue(StatMin, VoltageUnit) : UnitFormatter.Missing;
    public string StatMaxText   => StatMax > 0 ? UnitFormatter.FormatVoltageValue(StatMax, VoltageUnit) : UnitFormatter.Missing;
    public string StatAvgText   => StatAvg > 0 ? UnitFormatter.FormatVoltageValue(StatAvg, VoltageUnit) : UnitFormatter.Missing;
    public string StatDriftText => (StatMax > 0 && StatMin > 0)
        ? UnitFormatter.FormatVoltageValue(StatMax - StatMin, VoltageUnit) : UnitFormatter.Missing;

    public void ResetStats() { StatMin = 0; StatMax = 0; StatAvg = 0; }
}
