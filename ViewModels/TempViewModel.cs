using CommunityToolkit.Mvvm.ComponentModel;
using BMSMonitor.Services;

namespace BMSMonitor.ViewModels;

public partial class TempViewModel : ObservableObject
{
    public int Index { get; init; }
    public string Label => $"NTC {Index}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TempText), nameof(TempRaw))]
    private double _temperature;

    private string _temperatureUnit = "C";

    public string TemperatureUnit
    {
        get => _temperatureUnit;
        set
        {
            var normalized = UnitFormatter.NormalizeTemperatureUnit(value);
            if (SetProperty(ref _temperatureUnit, normalized))
                OnPropertyChanged(nameof(TempText));
        }
    }

    public string TempText => UnitFormatter.FormatTemperature(Temperature, TemperatureUnit);
    public double TempRaw => Temperature;
}
