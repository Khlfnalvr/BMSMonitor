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
}
