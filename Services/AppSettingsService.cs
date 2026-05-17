using System.IO;
using System.Text.Json;

namespace BMSMonitor.Services;

public class AppSettings
{
    // BmsConfig thresholds
    public double NominalCapacityAh      { get; set; } = 20.0;
    public double MaxDod                 { get; set; } = 80;
    public double MaxChargeCurrent       { get; set; } = 20;
    public double MaxDischargeCurrent    { get; set; } = 40;
    public double OvervoltageThreshold   { get; set; } = 4.20;
    public double UndervoltageThreshold  { get; set; } = 2.80;
    public double LowVoltageWarning      { get; set; } = 3.00;
    public double OverTempWarning        { get; set; } = 60;
    public double OverTempCutoff         { get; set; } = 70;
    public double BalancingStartDeltaMv  { get; set; } = 20;
    public double BalancingStopDeltaMv   { get; set; } = 5;

    // Transport selection. 0 = EspSerial, 1 = Slcan, 2 = Pcan — kept as int
    // so settings.json stays human-editable. Falls through to EspSerial on
    // missing / unknown values.
    public int    TransportMode          { get; set; } = 0;

    // Bitrate key in Kbps. Means UART baud / 1000 for ESP, CAN bus speed for
    // SLCAN & PCAN. Resolved against the active backend's bitrate list at
    // startup; unrecognised values fall through to that backend's default.
    public int    CanBitrateKbps         { get; set; } = 115;
    public int    ReconnectIntervalSec   { get; set; } = 2;
    public int    ProbeTimeoutMs         { get; set; } = 3000;
    public bool   AutoConnectEnabled     { get; set; } = true;
}

public static class AppSettingsService
{
    private static readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BMSMonitor", "settings.json");

    private static readonly JsonSerializerOptions _opts =
        new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new();
        }
        catch { }
        return new();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, _opts));
        }
        catch { }
    }
}
