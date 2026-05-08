using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SOCTester.Models;
using SOCTester.Services;
using Microsoft.UI.Dispatching;

namespace SOCTester.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcherQueue;

    public SerialPortService Serial  { get; } = new();
    public LoggingService    Logging { get; } = new();
    public SocConfig         Config  { get; } = new();

    // ── Latest reading from device ────────────────────────────────────────
    [ObservableProperty][NotifyPropertyChangedFor(nameof(SocText))]            private double _socFromDevice;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(VoltageText))]        private double _packVoltage;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CurrentText))]
                       [NotifyPropertyChangedFor(nameof(PowerText))]            private double _current;
    [ObservableProperty]                                                         private string _statusText = "idle";

    // ── On-host estimates ─────────────────────────────────────────────────
    [ObservableProperty][NotifyPropertyChangedFor(nameof(SocVoltageText))]      private double _socVoltage;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(SocCoulombText))]      private double _socCoulomb;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(SocFinalText))]
                       [NotifyPropertyChangedFor(nameof(SocFinal))]              private double _socEstimated;

    // ── Cumulative ───────────────────────────────────────────────────────
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CoulombCountText))]    private double _coulombCountAh;   // Ah
    [ObservableProperty][NotifyPropertyChangedFor(nameof(EnergyText))]          private double _energyWh;         // Wh

    // ── Connection ───────────────────────────────────────────────────────
    [ObservableProperty] private string _connectionStatus = "Not connected — open Control Panel to connect";
    [ObservableProperty] private bool   _isConnected;
    [ObservableProperty] private string _dataSourceText   = "SOURCE: NOT CONNECTED";
    [ObservableProperty] private bool   _hasData;

    // ── SOC history ring buffer (UI-thread only) ─────────────────────────
    public const int HistoryCapacity = 240;          // 4 min at 1 Hz
    private readonly double[] _socRing = new double[HistoryCapacity];
    private int _ringHead;
    private int _ringCount;
    public event Action? HistoryUpdated;

    private DateTime? _lastSampleTime;
    private bool _coulombInitialized;

    // ── Live data stream table (20 most recent rows, newest first) ────────
    public ObservableCollection<LogRow> DataStream { get; } = new();
    private const int StreamCapacity = 20;

    // ── Formatted text ───────────────────────────────────────────────────
    public string SocText        => HasData ? $"{SocFromDevice:F1} %" : "— %";
    public string SocVoltageText => HasData ? $"{SocVoltage:F1} %"    : "— %";
    public string SocCoulombText => HasData ? $"{SocCoulomb:F1} %"    : "— %";
    public string SocFinalText   => HasData ? $"{SocEstimated:F1} %"  : "— %";
    public double SocFinal       => SocEstimated;

    public string VoltageText => HasData ? $"{PackVoltage:F2} V" : "— V";
    public string CurrentText =>
        !HasData ? "— A"
        : Current >= 0 ? $"+{Current:F2} A" : $"{Current:F2} A";
    public string PowerText =>
        !HasData ? "— W"
        : $"{Math.Abs(PackVoltage * Current):F1} W";

    public string CoulombCountText => HasData ? $"{CoulombCountAh:F3} Ah" : "— Ah";
    public string EnergyText       => HasData ? $"{EnergyWh:F1} Wh"       : "— Wh";

    public MainViewModel(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;

        Serial.DataReceived  += data => _dispatcherQueue.TryEnqueue(() => ApplyData(data));
        Serial.StatusChanged += msg  => _dispatcherQueue.TryEnqueue(() => OnSerialStatus(msg));
        Serial.ErrorOccurred += msg  => _dispatcherQueue.TryEnqueue(() => ConnectionStatus = "Error: " + msg);
    }

    partial void OnHasDataChanged(bool value)
    {
        OnPropertyChanged(nameof(SocText));
        OnPropertyChanged(nameof(SocVoltageText));
        OnPropertyChanged(nameof(SocCoulombText));
        OnPropertyChanged(nameof(SocFinalText));
        OnPropertyChanged(nameof(VoltageText));
        OnPropertyChanged(nameof(CurrentText));
        OnPropertyChanged(nameof(PowerText));
        OnPropertyChanged(nameof(CoulombCountText));
        OnPropertyChanged(nameof(EnergyText));
    }

    private void OnSerialStatus(string msg)
    {
        ConnectionStatus = msg;
        IsConnected      = Serial.IsConnected;

        if (Serial.IsConnected)
        {
            DataSourceText = $"SOURCE: {Serial.PortName} @ {Serial.BaudRate}";
        }
        else
        {
            DataSourceText  = "SOURCE: NOT CONNECTED";
            HasData         = false;
            _lastSampleTime = null;
        }
    }

    public void ApplyData(SocData data)
    {
        // Apply current zero-offset calibration
        double current = data.Current - Config.CurrentZeroOffset;

        SocFromDevice = data.Soc;
        PackVoltage   = data.PackVoltage;
        Current       = current;
        StatusText    = data.Status;

        // ── On-host SOC estimates ─────────────────────────────────────
        SocVoltage = EstimateSocFromVoltage(data.PackVoltage);

        // Coulomb counting: integrate current over dt
        var now = DateTime.Now;
        if (!_coulombInitialized)
        {
            CoulombCountAh      = 0;
            EnergyWh            = 0;
            SocCoulomb          = Config.InitialSoc;
            _coulombInitialized = true;
        }
        else if (_lastSampleTime is { } prev)
        {
            double dtHours  = (now - prev).TotalHours;
            double deltaAh  = current * dtHours;          // +charging, −discharging
            double deltaWh  = data.PackVoltage * deltaAh;
            CoulombCountAh += deltaAh;
            EnergyWh       += deltaWh;
            double socShift = (deltaAh / Math.Max(0.1, Config.NominalCapacityAh)) * 100.0;
            SocCoulomb     = Math.Clamp(SocCoulomb + socShift, 0.0, 100.0);
        }
        _lastSampleTime = now;

        // Choose final SOC per algorithm
        SocEstimated = Config.Algorithm switch
        {
            SocAlgorithm.DeviceReported   => data.Soc,
            SocAlgorithm.VoltageBased     => SocVoltage,
            SocAlgorithm.CoulombCounting  => SocCoulomb,
            SocAlgorithm.Hybrid           => HybridSoc(SocVoltage, SocCoulomb, current),
            _                             => data.Soc
        };

        HasData = true;

        // Push final SOC into history buffer for the chart
        _socRing[_ringHead] = SocEstimated;
        _ringHead = (_ringHead + 1) % HistoryCapacity;
        if (_ringCount < HistoryCapacity) _ringCount++;
        HistoryUpdated?.Invoke();

        // ── Live data stream (newest at top) ─────────────────────────
        DataStream.Insert(0, new LogRow
        {
            Timestamp    = now.ToString("HH:mm:ss"),
            Soc          = $"{data.Soc:F1}",
            SocVoltage   = $"{SocVoltage:F1}",
            SocCoulomb   = $"{SocCoulomb:F1}",
            PackVoltage  = $"{data.PackVoltage:F3}",
            Current      = current >= 0 ? $"+{current:F3}" : $"{current:F3}",
            Power        = $"{Math.Abs(data.PackVoltage * current):F2}",
            CoulombCount = $"{CoulombCountAh:F4}",
            Energy       = $"{EnergyWh:F2}",
            Status       = data.Status
        });
        if (DataStream.Count > StreamCapacity)
            DataStream.RemoveAt(StreamCapacity);

        // CSV log
        Logging.Log(
            socFromDevice:  data.Soc,
            socVoltage:     SocVoltage,
            socCoulomb:     SocCoulomb,
            packVoltage:    data.PackVoltage,
            current:        current,
            power:          Math.Abs(data.PackVoltage * current),
            coulombCountAh: CoulombCountAh,
            energyWh:       EnergyWh,
            status:         data.Status);
    }

    /// <summary>Linear interpolation between EmptyVoltage and FullVoltage.</summary>
    private double EstimateSocFromVoltage(double v)
    {
        double lo = Config.EmptyVoltage;
        double hi = Config.FullVoltage;
        if (hi <= lo) return 0;
        double pct = (v - lo) / (hi - lo) * 100.0;
        return Math.Clamp(pct, 0.0, 100.0);
    }

    /// <summary>Voltage-anchored hybrid: trust voltage at rest, coulomb under load.</summary>
    private double HybridSoc(double socV, double socC, double current)
    {
        // |I| < 0.5 A → at rest, anchor to voltage and reset coulomb counter
        if (Math.Abs(current) < 0.5)
        {
            SocCoulomb = socV;   // re-anchor
            return socV;
        }
        return socC;
    }

    public void ResetCoulombCounter(double initialSocPct)
    {
        CoulombCountAh      = 0;
        EnergyWh            = 0;
        SocCoulomb          = Math.Clamp(initialSocPct, 0.0, 100.0);
        Config.InitialSoc   = initialSocPct;
        _coulombInitialized = true;
    }

    public double[] GetSocHistory()
    {
        var result = new double[_ringCount];
        int start  = (_ringHead - _ringCount + HistoryCapacity) % HistoryCapacity;
        for (int i = 0; i < _ringCount; i++)
            result[i] = _socRing[(start + i) % HistoryCapacity];
        return result;
    }
}
