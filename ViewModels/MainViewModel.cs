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

    // 0 = unlimited (keep all samples); chart range is now controlled by the
    // trim bar on the dashboard, not by a fixed rolling window.
    private double _historyTimeframeMinutes = 0;
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
    private readonly Queue<double[]> _tempHistory    = new(120);  // 10 values per sample
    private readonly Queue<double[]> _cellHistory    = new(120);  // 20 cell voltages per sample
    private readonly Queue<DateTime> _timestamps     = new(120);

    private void TrimHistoryBuffers()
    {
        int cap = HistoryCapacity;
        if (cap <= 0) return;
        while (_socHistory.Count     > cap) _socHistory.Dequeue();
        while (_voltageHistory.Count > cap) _voltageHistory.Dequeue();
        while (_currentHistory.Count > cap) _currentHistory.Dequeue();
        while (_tempHistory.Count    > cap) _tempHistory.Dequeue();
        while (_cellHistory.Count    > cap) _cellHistory.Dequeue();
        while (_timestamps.Count     > cap) _timestamps.Dequeue();
    }

    public event Action? HistoryUpdated;
    /// <summary>Fired when history is bulk-replaced or cleared (playback load/unload).</summary>
    public event Action? HistoryReset;

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

        // Playback frames feed through the same pipeline as live data
        // (the chart history is pre-populated by FileLoaded, so ApplyData
        // will skip the enqueue step while a file is loaded).
        Playback.FrameChanged += data => _dispatcherQueue.TryEnqueue(() => ApplyData(data));

        // When a file is loaded, bulk-populate the chart history with every
        // sample at once so the trim bar reflects the full recording duration.
        Playback.FileLoaded += frames => _dispatcherQueue.TryEnqueue(() => BulkLoadHistory(frames));

        // Returning to live mode: clear the imported history so live samples
        // don't get appended to old playback data.
        Playback.FileUnloaded += () => _dispatcherQueue.TryEnqueue(ClearHistory);
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

        // Live-only side effects: logging, notifications, and history capture.
        // (Playback frames replay already-known data — no need to re-log or
        // re-enqueue them; the chart history is bulk-loaded on file open.)
        if (!Playback.IsLoaded)
        {
            Logging.Log(data);
            App.Notifications.CheckAndNotify(data, Config);

            _socHistory.Enqueue(data.Soc);
            _voltageHistory.Enqueue(data.PackVoltage);
            _currentHistory.Enqueue(data.Current);
            _tempHistory.Enqueue((double[])data.Temps.Clone());
            _cellHistory.Enqueue((double[])data.Cells.Clone());
            _timestamps.Enqueue(DateTime.Now);
            TrimHistoryBuffers();
        }

        // ── Live data stream (newest at top) — always shown ──────────
        var row = new LogRow();
        foreach (var col in LogColumns.Where(c => c.IsEnabled))
            row.Values.Add(col.GetString(DateTime.Now, data));
        DataStream.Insert(0, row);
        if (DataStream.Count > StreamCapacity)
            DataStream.RemoveAt(StreamCapacity);

        HistoryUpdated?.Invoke();
    }

    /// <summary>
    /// Replaces the chart history queues with every sample from a loaded file.
    /// Uses synthetic 1 Hz timestamps so the trim bar / chart show the full
    /// recording duration immediately (no need to play back through it).
    /// </summary>
    public void BulkLoadHistory(BmsData[] frames)
    {
        _socHistory.Clear();
        _voltageHistory.Clear();
        _currentHistory.Clear();
        _tempHistory.Clear();
        _cellHistory.Clear();
        _timestamps.Clear();

        if (frames.Length == 0)
        {
            HistoryUpdated?.Invoke();
            return;
        }

        // Anchor so latest sample aligns with "now"; earlier samples are
        // spaced 1 s apart (matches CSV sampling rate, and gives elapsed
        // labels starting from 00:00:00 in the trim bar).
        var baseTime = DateTime.Now.AddSeconds(-(frames.Length - 1));
        for (int i = 0; i < frames.Length; i++)
        {
            _socHistory.Enqueue(frames[i].Soc);
            _voltageHistory.Enqueue(frames[i].PackVoltage);
            _currentHistory.Enqueue(frames[i].Current);
            _tempHistory.Enqueue((double[])frames[i].Temps.Clone());
            _cellHistory.Enqueue((double[])frames[i].Cells.Clone());
            _timestamps.Enqueue(baseTime.AddSeconds(i));
        }

        HistoryReset?.Invoke();
        HistoryUpdated?.Invoke();
    }

    /// <summary>Clears every chart history queue. Used when unloading a file.</summary>
    public void ClearHistory()
    {
        _socHistory.Clear();
        _voltageHistory.Clear();
        _currentHistory.Clear();
        _tempHistory.Clear();
        _cellHistory.Clear();
        _timestamps.Clear();
        HistoryReset?.Invoke();
        HistoryUpdated?.Invoke();
    }

    // Returns samples in chronological order (oldest → newest).
    public double[] GetSocHistory() => _socHistory.ToArray();

    // Returns V and I samples in chronological order (oldest → newest).
    public (double[] voltages, double[] currents) GetViHistory() =>
        (_voltageHistory.ToArray(), _currentHistory.ToArray());

    // Returns temperature history as 10 separate series (one per sensor),
    // each in chronological order (oldest → newest).
    public double[][] GetTempHistory()
    {
        var all = _tempHistory.ToArray();
        if (all.Length == 0) return [];
        var result = new double[10][];
        for (int s = 0; s < 10; s++)
        {
            result[s] = new double[all.Length];
            for (int i = 0; i < all.Length; i++)
                result[s][i] = all[i][s];
        }
        return result;
    }

    // Returns voltage history for a single cell (0-indexed, 0-19).
    public double[] GetCellHistory(int cellIndex)
    {
        var all = _cellHistory.ToArray();
        if (all.Length == 0 || cellIndex < 0 || cellIndex >= 20) return [];
        var result = new double[all.Length];
        for (int i = 0; i < all.Length; i++)
            result[i] = all[i][cellIndex];
        return result;
    }

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
