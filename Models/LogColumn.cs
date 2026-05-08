using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace BMSMonitor.Models;

/// <summary>
/// One loggable column. Supports enable/disable toggle and drag-to-reorder
/// (the ObservableCollection order determines the output column order).
/// </summary>
public sealed class LogColumn : INotifyPropertyChanged
{
    public string Key   { get; init; } = "";
    public string Label { get; init; } = "";
    public string Group { get; init; } = "";

    private bool _isEnabled = true;
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    // ── Value extraction ──────────────────────────────────────────────────

    /// <summary>Formatted string for CSV / TSV output.</summary>
    public string GetString(DateTime ts, BmsData d)
    {
        switch (Key)
        {
            case "Timestamp":     return ts.ToString("yyyy-MM-dd HH:mm:ss.fff");
            case "PackVoltage_V": return d.PackVoltage.ToString("F3", CultureInfo.InvariantCulture);
            case "SOC_pct":       return d.Soc.ToString("F2", CultureInfo.InvariantCulture);
            case "Current_A":     return d.Current.ToString("F3", CultureInfo.InvariantCulture);
            case "Status":        return d.Status;
        }
        if (TryCellIndex(out int ci)) return d.Cells[ci].ToString("F4", CultureInfo.InvariantCulture);
        if (TryBalIndex (out int bi)) return d.Balancing[bi] ? "1" : "0";
        if (TryTempIndex(out int ti)) return d.Temps[ti].ToString("F2", CultureInfo.InvariantCulture);
        return "";
    }

    /// <summary>Typed object for Excel / JSON output.</summary>
    public object GetObject(DateTime ts, BmsData d)
    {
        switch (Key)
        {
            case "Timestamp":     return ts.ToString("yyyy-MM-dd HH:mm:ss.fff");
            case "PackVoltage_V": return d.PackVoltage;
            case "SOC_pct":       return d.Soc;
            case "Current_A":     return d.Current;
            case "Status":        return d.Status;
        }
        if (TryCellIndex(out int ci)) return d.Cells[ci];
        if (TryBalIndex (out int bi)) return (object)(d.Balancing[bi] ? 1 : 0);
        if (TryTempIndex(out int ti)) return d.Temps[ti];
        return "";
    }

    // ── Index parsers ─────────────────────────────────────────────────────

    private bool TryCellIndex(out int idx)
    {
        idx = 0;
        if (Key.StartsWith("Cell") && Key.EndsWith("_V") &&
            int.TryParse(Key[4..^2], out int n) && n is >= 1 and <= 20)
        { idx = n - 1; return true; }
        return false;
    }

    private bool TryBalIndex(out int idx)
    {
        idx = 0;
        if (Key.StartsWith("Bal") && !Key.Contains('_') &&
            int.TryParse(Key[3..], out int n) && n is >= 1 and <= 20)
        { idx = n - 1; return true; }
        return false;
    }

    private bool TryTempIndex(out int idx)
    {
        idx = 0;
        if (Key.StartsWith("Temp") && Key.EndsWith("_C") &&
            int.TryParse(Key[4..^2], out int n) && n is >= 1 and <= 10)
        { idx = n - 1; return true; }
        return false;
    }

    // ── Factory ───────────────────────────────────────────────────────────

    public static ObservableCollection<LogColumn> CreateDefaults()
    {
        var list = new ObservableCollection<LogColumn>
        {
            new() { Key = "Timestamp",     Label = "Timestamp",        Group = "Core" },
            new() { Key = "PackVoltage_V", Label = "Pack Voltage (V)", Group = "Core" },
            new() { Key = "SOC_pct",       Label = "SOC (%)",          Group = "Core" },
            new() { Key = "Current_A",     Label = "Current (A)",      Group = "Core" },
            new() { Key = "Status",        Label = "Status",           Group = "Core" },
        };
        for (int i = 1; i <= 20; i++)
            list.Add(new() { Key = $"Cell{i}_V", Label = $"Cell {i} (V)",    Group = "Cells" });
        for (int i = 1; i <= 20; i++)
            list.Add(new() { Key = $"Bal{i}",    Label = $"Balancing {i}",   Group = "Balancing" });
        for (int i = 1; i <= 10; i++)
            list.Add(new() { Key = $"Temp{i}_C", Label = $"Temp {i} (°C)",  Group = "Temperatures" });
        return list;
    }
}
