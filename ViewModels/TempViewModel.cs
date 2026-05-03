using CommunityToolkit.Mvvm.ComponentModel;

namespace BMSMonitor.ViewModels;

public partial class TempViewModel : ObservableObject
{
    public int Index { get; init; }
    public string Label => $"NTC {Index}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TempText), nameof(TempRaw))]
    private double _temperature;

    public string TempText => $"{Temperature:F1} °C";
    public double TempRaw => Temperature;
}
