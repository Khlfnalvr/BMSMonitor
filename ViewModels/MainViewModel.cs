using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using BMSMonitor.Models;
using BMSMonitor.Services;
using Microsoft.UI.Dispatching;
using System.IO;

namespace BMSMonitor.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcherQueue;

    public SerialPortService  Serial      { get; } = new();
    public LoggingService     Logging     { get; } = new();
    public AutoConnectService AutoConnect { get; }

    // --- Pack level ---
    [ObservableProperty][NotifyPropertyChangedFor(nameof(PackVoltageText))] private double _packVoltage;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(SocText))][NotifyPropertyChangedFor(nameof(RemainingCapacityText))] private double _soc;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CurrentText))]     private double _current;
    [ObservableProperty] private string _packStatus = "—";

    // --- Voltage summary ---
    [ObservableProperty][NotifyPropertyChangedFor(nameof(MinCellText))] private double _minCellVoltage;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(MaxCellText))] private double _maxCellVoltage;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(AvgCellText))] private double _avgCellVoltage;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(DeltaText))]   private double _deltaVoltage;

    // --- Balancing ---
    [ObservableProperty][NotifyPropertyChangedFor(nameof(BalancingText))] private int _balancingCount;

    // --- Connection ---
    [ObservableProperty] private string _connectionStatus = "Not connected — open Control Panel to connect ESP32";
    [ObservableProperty] private bool   _isConnected;
    [ObservableProperty] private string _dataSourceText = "SOURCE: NOT CONNECTED";

    // --- Live data indicator ---
    [ObservableProperty] private bool _hasData;

    // --- SOC / V / I history (UI-thread only) ---
    public static readonly (double Minutes, string Label)[] TimeframeOptions =
    [
        (0.5, "30s"),
        (1,   "1 min"),
        (2,   "2 min"),
        (5,   "5 min"),
        (10,  "10 min"),
        (0,   "All"),
    ];

    private double _historyTimeframeMinutes = 2;
    public double HistoryTimeframeMinutes
    {
        get => _historyTimeframeMinutes;
        set
        {
            if (Math.Abs(_historyTimeframeMinutes - value) < 0.001) return;
            _historyTimeframeMinutes = value;
            TrimHistoryBuffers();
            _dispatcherQueue.TryEnqueue(() => HistoryUpdated?.Invoke());
        }
    }

    // Returns the sample capacity: 0 means unlimited (All).
    public int HistoryCapacity =>
        _historyTimeframeMinutes > 0 ? (int)(_historyTimeframeMinutes * 60) : 0;

    // Queue<T> dequeues from front in O(1) — much cheaper than List.RemoveAt(0).
    private readonly Queue<double>   _socHistory     = new(120);
    private readonly Queue<double>   _voltageHistory = new(120);
    private readonly Queue<double>   _currentHistory = new(120);
    private readonly Queue<DateTime> _timestamps     = new(120);

    private void TrimHistoryBuffers()
    {
        int cap = HistoryCapacity;
        if (cap <= 0) return;
        while (_socHistory.Count     > cap) _socHistory.Dequeue();
        while (_voltageHistory.Count > cap) _voltageHistory.Dequeue();
        while (_currentHistory.Count > cap) _currentHistory.Dequeue();
        while (_timestamps.Count     > cap) _timestamps.Dequeue();
    }

    public event Action? HistoryUpdated;

    // --- Formatted text ---
    public string PackVoltageText       => HasData ? $"{PackVoltage:F2} V" : "— V";
    public string SocText               => HasData ? $"{Soc:F1} %"        : "— %";
    public string CurrentText           => !HasData ? "— A"
                                         : Current >= 0 ? $"+{Current:F2} A" : $"{Current:F2} A";
    public string RemainingCapacityText => !HasData ? "— mAh"
                                         : $"{Soc / 100.0 * Config.NominalCapacityAh * 1000:N0} mAh";
    public string MinCellText     => HasData ? $"{MinCellVoltage:F3} V" : "— V";
    public string MaxCellText     => HasData ? $"{MaxCellVoltage:F3} V" : "— V";
    public string AvgCellText     => HasData ? $"{AvgCellVoltage:F3} V" : "— V";
    public string DeltaText       => HasData ? $"{DeltaVoltage * 1000:F1} mV" : "— mV";
    public string BalancingText   => !HasData      ? "—"
                                   : BalancingCount > 0 ? $"{BalancingCount} cells balancing"
                                                        : "Not balancing";

    // --- Collections ---
    public ObservableCollection<CellViewModel> Cells        { get; } = new();
    public ObservableCollection<TempViewModel> Temperatures { get; } = new();
    public ObservableCollection<LogRow>        DataStream   { get; } = new();
    public ObservableCollection<LogColumn>     LogColumns   { get; } = LogColumn.CreateDefaults();
    private const int StreamCapacity = 20;
    public BmsConfig Config { get; } = new();

    // --- Playback ---
    public PlaybackService Playback { get; } = new();

    public MainViewModel(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        AutoConnect      = new AutoConnectService(Serial);
        AutoConnect.Start();                               // always-on, begins scanning immediately

        for (int i = 0; i < 20; i++) Cells.Add(new CellViewModel { Index = i + 1 });
        for (int i = 0; i < 10; i++) Temperatures.Add(new TempViewModel { Index = i + 1 });

        // Live serial — skip if a playback file is loaded (don't overwrite review data).
        Serial.DataReceived  += data => { if (!Playback.IsLoaded) _dispatcherQueue.TryEnqueue(() => ApplyData(data)); };
        Serial.StatusChanged += msg  => _dispatcherQueue.TryEnqueue(() => OnSerialStatus(msg));
        Serial.ErrorOccurred += msg  => _dispatcherQueue.TryEnqueue(() => ConnectionStatus = "Error: " + msg);

        // Playback frames feed through the same pipeline as live data.
        Playback.FrameChanged += data => _dispatcherQueue.TryEnqueue(() => ApplyData(data));
    }

    partial void OnHasDataChanged(bool value)
    {
        // Re-trigger formatted-text bindings when HasData flips.
        OnPropertyChanged(nameof(PackVoltageText));
        OnPropertyChanged(nameof(SocText));
        OnPropertyChanged(nameof(CurrentText));
        OnPropertyChanged(nameof(MinCellText));
        OnPropertyChanged(nameof(MaxCellText));
        OnPropertyChanged(nameof(AvgCellText));
        OnPropertyChanged(nameof(DeltaText));
        OnPropertyChanged(nameof(BalancingText));
        OnPropertyChanged(nameof(RemainingCapacityText));
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
            DataSourceText = "SOURCE: NOT CONNECTED";
            HasData        = false;   // last frame stays in fields, but UI shows "—"
        }
    }

    public void ApplyData(BmsData data)
    {
        PackVoltage = data.PackVoltage;
        Soc         = data.Soc;
        Current     = data.Current;
        PackStatus  = data.Status;

        MinCellVoltage = data.Cells.Min();
        MaxCellVoltage = data.Cells.Max();
        AvgCellVoltage = data.Cells.Average();
        DeltaVoltage   = MaxCellVoltage - MinCellVoltage;

        int balCount = 0;
        for (int i = 0; i < 20; i++)
        {
            Cells[i].Voltage     = data.Cells[i];
            Cells[i].IsBalancing = data.Balancing[i];
            Cells[i].State       = GetCellState(data.Cells[i]);
            if (data.Balancing[i]) balCount++;
        }
        BalancingCount = balCount;

        for (int i = 0; i < 10; i++)
            Temperatures[i].Temperature = data.Temps[i];

        HasData = true;
        if (!Playback.IsLoaded) Logging.Log(data);   // don't re-log playback frames

        // ── Check for abnormal conditions ─────────────────────────────
        App.Notifications.CheckAndNotify(data, Config);

        // ── Live data stream (newest at top) ─────────────────────────
        var row = new LogRow();
        foreach (var col in LogColumns.Where(c => c.IsEnabled))
            row.Values.Add(col.GetString(DateTime.Now, data));
        DataStream.Insert(0, row);
        if (DataStream.Count > StreamCapacity)
            DataStream.RemoveAt(StreamCapacity);

        // Push SOC / V / I + timestamp into history queues and notify chart.
        _socHistory.Enqueue(data.Soc);
        _voltageHistory.Enqueue(data.PackVoltage);
        _currentHistory.Enqueue(data.Current);
        _timestamps.Enqueue(DateTime.Now);
        TrimHistoryBuffers();
        HistoryUpdated?.Invoke();
    }

    // Returns samples in chronological order (oldest → newest).
    public double[] GetSocHistory() => _socHistory.ToArray();

    // Returns V and I samples in chronological order (oldest → newest).
    public (double[] voltages, double[] currents) GetViHistory() =>
        (_voltageHistory.ToArray(), _currentHistory.ToArray());

    // Returns timestamps in chronological order — one per sample,
    // aligned with GetSocHistory()/GetViHistory() by index.
    public DateTime[] GetTimestamps() => _timestamps.ToArray();

    public DateTime? EarliestTimestamp => _timestamps.Count > 0 ? _timestamps.Peek() : null;
    public DateTime? LatestTimestamp   =>
        _timestamps.Count > 0 ? _timestamps.ToArray()[^1] : null;

    private CellState GetCellState(double voltage)
    {
        if (voltage >= Config.OvervoltageThreshold)  return CellState.Overvoltage;
        if (voltage <  Config.UndervoltageThreshold) return CellState.Undervoltage;
        if (voltage <  Config.LowVoltageWarning)     return CellState.Low;
        return CellState.Normal;
    }
}
