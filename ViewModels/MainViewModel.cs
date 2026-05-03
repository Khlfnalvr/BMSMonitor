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

    public SerialPortService Serial  { get; } = new();
    public LoggingService    Logging { get; } = new();

    // --- Pack level ---
    [ObservableProperty][NotifyPropertyChangedFor(nameof(PackVoltageText))] private double _packVoltage;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(SocText))]         private double _soc;
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

    // --- SOC history (circular buffer, UI-thread only) ---
    public const int HistoryCapacity = 120;            // 2 min at 1 Hz
    private readonly double[] _socRing = new double[HistoryCapacity];
    private int _ringHead;   // next write slot
    private int _ringCount;  // valid samples so far
    public event Action? HistoryUpdated;

    // --- Formatted text ---
    public string PackVoltageText => HasData ? $"{PackVoltage:F2} V" : "— V";
    public string SocText         => HasData ? $"{Soc:F1} %"        : "— %";
    public string CurrentText     => !HasData ? "— A"
                                   : Current >= 0 ? $"+{Current:F2} A" : $"{Current:F2} A";
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
    private const int StreamCapacity = 10;
    public BmsConfig Config { get; } = new();

    public MainViewModel(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;

        for (int i = 0; i < 20; i++) Cells.Add(new CellViewModel { Index = i + 1 });
        for (int i = 0; i < 10; i++) Temperatures.Add(new TempViewModel { Index = i + 1 });

        // All data flows from the ESP32 over serial. No simulation.
        Serial.DataReceived  += data => _dispatcherQueue.TryEnqueue(() => ApplyData(data));
        Serial.StatusChanged += msg  => _dispatcherQueue.TryEnqueue(() => OnSerialStatus(msg));
        Serial.ErrorOccurred += msg  => _dispatcherQueue.TryEnqueue(() => ConnectionStatus = "Error: " + msg);
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
        Logging.Log(data);

        // ── Live data stream (newest at top) ─────────────────────────
        DataStream.Insert(0, new LogRow
        {
            Timestamp   = DateTime.Now.ToString("HH:mm:ss"),
            Soc         = $"{data.Soc:F1}",
            PackVoltage = $"{data.PackVoltage:F2}",
            Current     = data.Current >= 0 ? $"+{data.Current:F2}" : $"{data.Current:F2}",
            Status      = data.Status,
            MinCell     = $"{data.Cells.Min():F3}",
            MaxCell     = $"{data.Cells.Max():F3}",
            Delta       = $"{(data.Cells.Max() - data.Cells.Min()) * 1000:F1}",
            Balancing   = $"{data.Balancing.Count(b => b)}"
        });
        if (DataStream.Count > StreamCapacity)
            DataStream.RemoveAt(StreamCapacity);

        // Push SOC into circular buffer and notify chart.
        _socRing[_ringHead] = data.Soc;
        _ringHead  = (_ringHead + 1) % HistoryCapacity;
        if (_ringCount < HistoryCapacity) _ringCount++;
        HistoryUpdated?.Invoke();
    }

    // Returns samples in chronological order (oldest → newest).
    public double[] GetSocHistory()
    {
        var result = new double[_ringCount];
        int start = (_ringHead - _ringCount + HistoryCapacity) % HistoryCapacity;
        for (int i = 0; i < _ringCount; i++)
            result[i] = _socRing[(start + i) % HistoryCapacity];
        return result;
    }

    private CellState GetCellState(double voltage)
    {
        if (voltage >= Config.OvervoltageThreshold)  return CellState.Overvoltage;
        if (voltage <  Config.UndervoltageThreshold) return CellState.Undervoltage;
        if (voltage <  Config.LowVoltageWarning)     return CellState.Low;
        return CellState.Normal;
    }
}
