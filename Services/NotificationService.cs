using System.Collections.Generic;
using Microsoft.Windows.AppNotifications;
using BMSMonitor.Models;

namespace BMSMonitor.Services;

public class NotificationService
{
    private readonly Dictionary<string, DateTime> _lastNotified = new();
    private readonly TimeSpan _cooldown = TimeSpan.FromSeconds(30);
    private bool _registered;

    private readonly List<AlertRecord> _history  = new();
    private readonly object            _histLock = new();
    private const int MaxHistory = 200;

    public event Action<AlertRecord>? AlertFired;

    public IReadOnlyList<AlertRecord> GetHistory()
    {
        lock (_histLock) return _history.ToList();
    }

    public void ClearHistory()
    {
        lock (_histLock) _history.Clear();
    }

    public void Register()
    {
        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch
        {
            _registered = false;
        }
    }

    public void CheckAndNotify(BmsData data, BmsConfig config)
    {
        if (!_registered) return;
        EvaluateAndFire(data, config);
    }

    // Inlines the previous list-allocating EvaluateConditions: each alert is
    // dispatched directly on detection so the hot per-frame call no longer
    // allocates a List<(string,string,string)> and tuple boxes per frame.
    private void EvaluateAndFire(BmsData data, BmsConfig config)
    {
        var cells = data.Cells;
        var temps = data.Temps;

        double cellMin = cells[0], cellMax = cells[0];

        // Cell voltages
        for (int i = 0; i < cells.Length; i++)
        {
            double v = cells[i];
            if (v < cellMin) cellMin = v;
            if (v > cellMax) cellMax = v;

            if (v >= config.OvervoltageThreshold)
                Fire($"ov_{i}", "BMS - Overvoltage",
                    $"Cell {i + 1} at {v:F3}V — exceeds {config.OvervoltageThreshold:F2}V cutoff");
            else if (v <= config.UndervoltageThreshold)
                Fire($"uv_{i}", "BMS - Undervoltage",
                    $"Cell {i + 1} at {v:F3}V — below {config.UndervoltageThreshold:F2}V cutoff");
        }

        // Current
        if (data.Current >= config.MaxChargeCurrent)
            Fire("oc_chg", "BMS - Overcurrent",
                $"Charge current {data.Current:F1}A — exceeds {config.MaxChargeCurrent:F0}A limit");
        if (Math.Abs(data.Current) >= config.MaxDischargeCurrent)
            Fire("oc_dsg", "BMS - Overcurrent",
                $"Discharge current {Math.Abs(data.Current):F1}A — exceeds {config.MaxDischargeCurrent:F0}A limit");

        // Temperatures
        for (int i = 0; i < temps.Length; i++)
        {
            double t = temps[i];
            if (t >= config.OverTempCutoff)
                Fire($"otc_{i}", "BMS - Temperature Critical",
                    $"Sensor {i + 1} at {t:F0}°C — exceeds {config.OverTempCutoff:F0}°C cutoff");
            else if (t >= config.OverTempWarning)
                Fire($"otw_{i}", "BMS - Temperature Warning",
                    $"Sensor {i + 1} at {t:F0}°C — exceeds {config.OverTempWarning:F0}°C warning");
        }

        // Cell imbalance — uses the min/max we already computed above
        double delta = cellMax - cellMin;
        if (delta >= config.BalancingStartDelta)
            Fire("imb", "BMS - Cell Imbalance",
                $"Delta {delta * 1000:F1}mV — exceeds {config.BalancingStartDelta * 1000:F0}mV threshold");
    }

    private void Fire(string key, string title, string body)
    {
        if (!CanNotify(key)) return;

        var rec = new AlertRecord(DateTime.Now, title, body);
        lock (_histLock)
        {
            if (_history.Count >= MaxHistory) _history.RemoveAt(0);
            _history.Add(rec);
        }
        AlertFired?.Invoke(rec);

        try
        {
            string xml = $"<toast><visual><binding template='ToastGeneric'>" +
                         $"<text>{title}</text><text>{body}</text>" +
                         $"</binding></visual></toast>";
            AppNotificationManager.Default.Show(new AppNotification(xml));
        }
        catch
        {
            // Notification failed — will retry on next cycle
        }
    }

    private bool CanNotify(string key)
    {
        if (_lastNotified.TryGetValue(key, out var last) && DateTime.Now - last < _cooldown)
            return false;
        _lastNotified[key] = DateTime.Now;
        return true;
    }
}
