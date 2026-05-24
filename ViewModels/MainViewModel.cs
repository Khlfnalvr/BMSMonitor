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

    public SerialService      Serial      { get; }
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
    [ObservableProperty] private string _connectionStatus = "Not connected — open Control Panel to connect to the ESP32";
    [ObservableProperty] private bool   _isConnected;
    [ObservableProperty] private string _dataSourceText = "SOURCE: NOT CONNECTED";

    // --- Live data indicator ---
    [ObservableProperty] private bool _hasData;

    private string _temperatureUnit = "C";
    private string _voltageUnit = "V";
    private string _capacityUnit = "mAh";

    public string TemperatureUnit
    {
        get => _temperatureUnit;
        private set
        {
            var normalized = UnitFormatter.NormalizeTemperatureUnit(value);
            if (!SetProperty(ref _temperatureUnit, normalized)) return;

            OnPropertyChanged(nameof(TemperatureSymbol));
            foreach (var temp in Temperatures)
                temp.TemperatureUnit = normalized;
        }
    }

    public string VoltageUnit
    {
        get => _voltageUnit;
        private set
        {
            var normalized = UnitFormatter.NormalizeVoltageUnit(value);
            if (!SetProperty(ref _voltageUnit, normalized)) return;

            foreach (var cell in Cells)
                cell.VoltageUnit = normalized;

            OnPropertyChanged(nameof(VoltageSymbol));
            NotifyVoltageTextProperties();
        }
    }

    public string CapacityUnit
    {
        get => _capacityUnit;
        private set
        {
            var normalized = UnitFormatter.NormalizeCapacityUnit(value);
            if (!SetProperty(ref _capacityUnit, normalized)) return;

            OnPropertyChanged(nameof(CapacitySymbol));
            OnPropertyChanged(nameof(RemainingCapacityText));
        }
    }

    public string TemperatureSymbol => UnitFormatter.TemperatureSymbol(TemperatureUnit);
    public string VoltageSymbol => UnitFormatter.VoltageSymbol(VoltageUnit);
    public string CapacitySymbol => UnitFormatter.CapacitySymbol(CapacityUnit);

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

    // Cached endpoints — avoid _timestamps.ToArray()[^1] on every redraw.
    private DateTime? _cachedEarliest;
    private DateTime? _cachedLatest;

    // Hard ceiling on retained samples. Beyond this, oldest drop off
    // (rolling). At 1 Hz this is ~10 hours of history — well past any
    // sensible monitoring window, but keeps memory bounded so the
    // charts and trim bar can't grow forever.
    private const int MaxRetainedSamples = 36000;

    private void TrimHistoryBuffers()
    {
        int cap = HistoryCapacity;
        if (cap <= 0) cap = MaxRetainedSamples;
        while (_socHistory.Count     > cap) _socHistory.Dequeue();
        while (_voltageHistory.Count > cap) _voltageHistory.Dequeue();
        while (_currentHistory.Count > cap) _currentHistory.Dequeue();
        while (_tempHistory.Count    > cap) _tempHistory.Dequeue();
        while (_cellHistory.Count    > cap) _cellHistory.Dequeue();
        while (_timestamps.Count     > cap) _timestamps.Dequeue();
        _cachedEarliest = _timestamps.Count > 0 ? _timestamps.Peek() : (DateTime?)null;
        if (_timestamps.Count == 0) _cachedLatest = null;
    }

    public event Action? HistoryUpdated;
    /// <summary>Fired when history is bulk-replaced or cleared (playback load/unload).</summary>
    public event Action? HistoryReset;

    // --- Formatted text ---
    public string PackVoltageText       => HasData ? UnitFormatter.FormatPackVoltage(PackVoltage, VoltageUnit)
                                         : UnitFormatter.MissingWithUnit(VoltageSymbol);
    public string SocText               => HasData ? $"{Soc:F1} %"        : "— %";
    public string CurrentText           => !HasData ? "— A"
                                         : Current >= 0 ? $"+{Current:F2} A" : $"{Current:F2} A";
    public string RemainingCapacityText => !HasData ? UnitFormatter.MissingWithUnit(CapacitySymbol)
                                         : UnitFormatter.FormatCapacityFromAh(Soc / 100.0 * Config.NominalCapacityAh, CapacityUnit);
    public string MinCellText     => HasData ? UnitFormatter.FormatVoltage(MinCellVoltage, VoltageUnit) : UnitFormatter.MissingWithUnit(VoltageSymbol);
    public string MaxCellText     => HasData ? UnitFormatter.FormatVoltage(MaxCellVoltage, VoltageUnit) : UnitFormatter.MissingWithUnit(VoltageSymbol);
    public string AvgCellText     => HasData ? UnitFormatter.FormatVoltage(AvgCellVoltage, VoltageUnit) : UnitFormatter.MissingWithUnit(VoltageSymbol);
    public string DeltaText       => HasData ? UnitFormatter.FormatVoltageDelta(DeltaVoltage, VoltageUnit) : UnitFormatter.MissingWithUnit(VoltageSymbol);
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

    // --- Per-cell session statistics ---
    private readonly double[] _cellStatMin   = Enumerable.Repeat(double.MaxValue, 20).ToArray();
    private readonly double[] _cellStatMax   = new double[20];
    private readonly double[] _cellStatSum   = new double[20];
    private int               _cellStatCount = 0;

    public void ResetCellStats()
    {
        Array.Fill(_cellStatMin, double.MaxValue);
        Array.Fill(_cellStatMax, 0.0);
        Array.Fill(_cellStatSum, 0.0);
        _cellStatCount = 0;
        foreach (var c in Cells) c.ResetStats();
    }

    public MainViewModel(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;

        var savedSettings = AppSettingsService.Load();
        Serial      = new SerialService();
        AutoConnect = new AutoConnectService(Serial);

        ApplySettings(savedSettings);
        var savedBaud = Serial.Bitrates.FirstOrDefault(b => b.Baud == savedSettings.SerialBaud);
        AutoConnect.Start(savedBaud?.Baud ?? Serial.DefaultBitrate);

        for (int i = 0; i < 20; i++) Cells.Add(new CellViewModel { Index = i + 1, VoltageUnit = VoltageUnit });
        for (int i = 0; i < 10; i++) Temperatures.Add(new TempViewModel { Index = i + 1, TemperatureUnit = TemperatureUnit });

        // Live data — skip if a playback file is loaded (don't overwrite review data).
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
        NotifyVoltageTextProperties();
        OnPropertyChanged(nameof(SocText));
        OnPropertyChanged(nameof(CurrentText));
        OnPropertyChanged(nameof(BalancingText));
        OnPropertyChanged(nameof(RemainingCapacityText));
    }

    private void NotifyVoltageTextProperties()
    {
        OnPropertyChanged(nameof(PackVoltageText));
        OnPropertyChanged(nameof(MinCellText));
        OnPropertyChanged(nameof(MaxCellText));
        OnPropertyChanged(nameof(AvgCellText));
        OnPropertyChanged(nameof(DeltaText));
    }

    public void SetTemperatureUnit(string unit)
    {
        var normalized = UnitFormatter.NormalizeTemperatureUnit(unit);
        if (TemperatureUnit == normalized) return;

        TemperatureUnit = normalized;
        SaveSettings();
        HistoryUpdated?.Invoke();
    }

    public void SetVoltageUnit(string unit)
    {
        var normalized = UnitFormatter.NormalizeVoltageUnit(unit);
        if (VoltageUnit == normalized) return;

        VoltageUnit = normalized;
        SaveSettings();
        HistoryUpdated?.Invoke();
    }

    public void SetCapacityUnit(string unit)
    {
        var normalized = UnitFormatter.NormalizeCapacityUnit(unit);
        if (CapacityUnit == normalized) return;

        CapacityUnit = normalized;
        SaveSettings();
        HistoryUpdated?.Invoke();
    }

    private void OnSerialStatus(string msg)
    {
        ConnectionStatus = msg;
        IsConnected      = Serial.IsConnected;

        if (Serial.IsConnected)
        {
            DataSourceText = $"SOURCE: {Serial.ChannelName} @ {Serial.BitrateText}";
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

        // Single-pass min / max / sum — replaces three LINQ extension calls
        // (each iterates and allocates an enumerator). Hot path: this runs
        // once per CAN snapshot.
        var cells = data.Cells;
        double minV = cells[0], maxV = cells[0], sumV = 0;
        for (int i = 0; i < 20; i++)
        {
            double v = cells[i];
            if (v < minV) minV = v;
            if (v > maxV) maxV = v;
            sumV += v;
        }
        MinCellVoltage = minV;
        MaxCellVoltage = maxV;
        AvgCellVoltage = sumV / 20.0;
        DeltaVoltage   = maxV - minV;

        var bal = data.Balancing;
        int balCount = 0;
        for (int i = 0; i < 20; i++)
        {
            var cellVm = Cells[i];
            double v   = cells[i];
            bool   b   = bal[i];
            cellVm.Voltage     = v;
            cellVm.IsBalancing = b;
            cellVm.State       = GetCellState(v);
            if (b) balCount++;

            // Per-cell session statistics (skip invalid zero readings).
            if (v > 0)
            {
                if (v < _cellStatMin[i]) { _cellStatMin[i] = v; cellVm.StatMin = v; }
                if (v > _cellStatMax[i]) { _cellStatMax[i] = v; cellVm.StatMax = v; }
                _cellStatSum[i] += v;
            }
        }
        BalancingCount = balCount;
        _cellStatCount++;
        double invCount = 1.0 / _cellStatCount;
        for (int i = 0; i < 20; i++)
            if (cells[i] > 0) Cells[i].StatAvg = _cellStatSum[i] * invCount;

        var temps = data.Temps;
        for (int i = 0; i < 10; i++)
            Temperatures[i].Temperature = temps[i];

        HasData = true;

        // Live-only side effects: logging, notifications, and history capture.
        // (Playback frames replay already-known data — no need to re-log or
        // re-enqueue them; the chart history is bulk-loaded on file open.)
        if (!Playback.IsLoaded)
        {
            Logging.Log(data);
            App.Notifications.CheckAndNotify(data, Config);

            var now = DateTime.Now;
            _socHistory.Enqueue(data.Soc);
            _voltageHistory.Enqueue(data.PackVoltage);
            _currentHistory.Enqueue(data.Current);
            _tempHistory.Enqueue((double[])temps.Clone());
            _cellHistory.Enqueue((double[])cells.Clone());
            _timestamps.Enqueue(now);
            _cachedLatest = now;
            _cachedEarliest ??= now;
            TrimHistoryBuffers();
        }

        // ── Live data stream (newest at top) — always shown ──────────
        // Use a foreach over the underlying collection — avoids the LINQ
        // Where iterator allocation that previously ran every frame.
        var row     = new LogRow();
        var nowTs   = DateTime.Now;
        var columns = LogColumns;
        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            if (col.IsEnabled) row.Values.Add(col.GetString(nowTs, data));
        }
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
            _cachedEarliest = null;
            _cachedLatest   = null;
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
        _cachedEarliest = baseTime;
        _cachedLatest   = baseTime.AddSeconds(frames.Length - 1);
        TrimHistoryBuffers();

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
        _cachedEarliest = null;
        _cachedLatest   = null;
        ResetCellStats();
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

    // Returns temperature history for a single NTC sensor (0-indexed, 0-9).
    public double[] GetTempSensorHistory(int sensorIndex)
    {
        var all = _tempHistory.ToArray();
        if (all.Length == 0 || sensorIndex < 0 || sensorIndex >= 10) return [];
        var result = new double[all.Length];
        for (int i = 0; i < all.Length; i++)
            result[i] = all[i][sensorIndex];
        return result;
    }

    // Returns timestamps in chronological order — one per sample,
    // aligned with GetSocHistory()/GetViHistory() by index.
    public DateTime[] GetTimestamps() => _timestamps.ToArray();

    // Cached — updated on enqueue/dequeue. Reading these no longer
    // allocates a copy of the entire timestamps queue.
    public DateTime? EarliestTimestamp => _cachedEarliest;
    public DateTime? LatestTimestamp   => _cachedLatest;
    public int HistorySampleCount      => _timestamps.Count;

    private void ApplySettings(AppSettings s)
    {
        Config.NominalCapacityAh     = s.NominalCapacityAh;
        Config.MaxDod                = s.MaxDod;
        Config.MaxChargeCurrent      = s.MaxChargeCurrent;
        Config.MaxDischargeCurrent   = s.MaxDischargeCurrent;
        Config.OvervoltageThreshold  = s.OvervoltageThreshold;
        Config.HighVoltageWarning    = s.HighVoltageWarning;
        Config.UndervoltageThreshold = s.UndervoltageThreshold;
        Config.LowVoltageWarning     = s.LowVoltageWarning;
        Config.OverTempWarning       = s.OverTempWarning;
        Config.OverTempCutoff        = s.OverTempCutoff;
        Config.BalancingStartDelta   = s.BalancingStartDeltaMv / 1000.0;
        Config.BalancingStopDelta    = s.BalancingStopDeltaMv  / 1000.0;

        TemperatureUnit = s.TemperatureUnit;
        VoltageUnit     = s.VoltageUnit;
        CapacityUnit    = s.CapacityUnit;

        AutoConnect.ReconnectIntervalSec = s.ReconnectIntervalSec;
        AutoConnect.ProbeTimeoutMs       = s.ProbeTimeoutMs;
        if (!s.AutoConnectEnabled) AutoConnect.SuspendReconnect();
    }

    public void SaveSettings()
    {
        // Load first, mutate, save — so fields owned by other components
        // (e.g. MainWindow's nav visibility) survive a partial write.
        var s = AppSettingsService.Load();
        s.NominalCapacityAh     = Config.NominalCapacityAh;
        s.MaxDod                = Config.MaxDod;
        s.MaxChargeCurrent      = Config.MaxChargeCurrent;
        s.MaxDischargeCurrent   = Config.MaxDischargeCurrent;
        s.OvervoltageThreshold  = Config.OvervoltageThreshold;
        s.HighVoltageWarning    = Config.HighVoltageWarning;
        s.UndervoltageThreshold = Config.UndervoltageThreshold;
        s.LowVoltageWarning     = Config.LowVoltageWarning;
        s.OverTempWarning       = Config.OverTempWarning;
        s.OverTempCutoff        = Config.OverTempCutoff;
        s.BalancingStartDeltaMv = Config.BalancingStartDelta * 1000.0;
        s.BalancingStopDeltaMv  = Config.BalancingStopDelta  * 1000.0;
        s.SerialBaud            = AutoConnect.Baud;
        s.ReconnectIntervalSec  = AutoConnect.ReconnectIntervalSec;
        s.ProbeTimeoutMs        = AutoConnect.ProbeTimeoutMs;
        s.AutoConnectEnabled    = !AutoConnect.IsSuspended;
        s.TemperatureUnit       = TemperatureUnit;
        s.VoltageUnit           = VoltageUnit;
        s.CapacityUnit          = CapacityUnit;
        AppSettingsService.Save(s);
    }

    private CellState GetCellState(double voltage)
    {
        if (voltage >= Config.OvervoltageThreshold)  return CellState.Overvoltage;
        if (voltage <  Config.UndervoltageThreshold) return CellState.Undervoltage;
        if (voltage <  Config.LowVoltageWarning)     return CellState.Low;
        return CellState.Normal;
    }
}
