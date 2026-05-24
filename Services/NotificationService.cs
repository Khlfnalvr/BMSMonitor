using System.Collections.Generic;
using System.Net;
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
    /// <summary>
    /// Fired for Error/Alert-severity records only. UI layer hooks this to
    /// flash the taskbar so a minimized window still draws the user's eye.
    /// </summary>
    public event Action<AlertRecord>? CriticalAlertFired;

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

    /// <summary>
    /// Logs an application diagnostic event (parse error, connection change, etc.)
    /// directly to the alert history without a Windows toast and without cooldown.
    /// </summary>
    public void LogDiagnostic(AlertSeverity severity, string title, string body)
    {
        var rec = new AlertRecord(DateTime.Now, title, body, severity);
        lock (_histLock)
        {
            if (_history.Count >= MaxHistory) _history.RemoveAt(0);
            _history.Add(rec);
        }
        AlertFired?.Invoke(rec);
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

        // Cell voltages — check hard cutoffs first, then soft warnings.
        for (int i = 0; i < cells.Length; i++)
        {
            double v = cells[i];
            if (v < cellMin) cellMin = v;
            if (v > cellMax) cellMax = v;

            if (v >= config.OvervoltageThreshold)
                Fire($"ov_{i}", "BMS - Overvoltage",
                    $"Cell {i + 1} at {v:F3}V — exceeds {config.OvervoltageThreshold:F2}V cutoff",
                    AlertSeverity.Alert);
            else if (v >= config.HighVoltageWarning)
                Fire($"ovw_{i}", "BMS - High Voltage Warning",
                    $"Cell {i + 1} at {v:F3}V — exceeds {config.HighVoltageWarning:F2}V warning",
                    AlertSeverity.Warning);
            else if (v <= config.UndervoltageThreshold)
                Fire($"uv_{i}", "BMS - Undervoltage",
                    $"Cell {i + 1} at {v:F3}V — below {config.UndervoltageThreshold:F2}V cutoff",
                    AlertSeverity.Alert);
            else if (v > 0 && v <= config.LowVoltageWarning)
                Fire($"uvw_{i}", "BMS - Low Voltage Warning",
                    $"Cell {i + 1} at {v:F3}V — below {config.LowVoltageWarning:F2}V warning",
                    AlertSeverity.Warning);
        }

        // Current
        if (data.Current >= config.MaxChargeCurrent)
            Fire("oc_chg", "BMS - Overcurrent",
                $"Charge current {data.Current:F1}A — exceeds {config.MaxChargeCurrent:F0}A limit",
                AlertSeverity.Alert);
        if (Math.Abs(data.Current) >= config.MaxDischargeCurrent)
            Fire("oc_dsg", "BMS - Overcurrent",
                $"Discharge current {Math.Abs(data.Current):F1}A — exceeds {config.MaxDischargeCurrent:F0}A limit",
                AlertSeverity.Alert);

        // Temperatures
        for (int i = 0; i < temps.Length; i++)
        {
            double t = temps[i];
            if (t >= config.OverTempCutoff)
                Fire($"otc_{i}", "BMS - Temperature Critical",
                    $"Sensor {i + 1} at {t:F0}°C — exceeds {config.OverTempCutoff:F0}°C cutoff",
                    AlertSeverity.Alert);
            else if (t >= config.OverTempWarning)
                Fire($"otw_{i}", "BMS - Temperature Warning",
                    $"Sensor {i + 1} at {t:F0}°C — exceeds {config.OverTempWarning:F0}°C warning",
                    AlertSeverity.Warning);
        }

        // Cell imbalance — uses the min/max we already computed above
        double delta = cellMax - cellMin;
        if (delta >= config.BalancingStartDelta)
            Fire("imb", "BMS - Cell Imbalance",
                $"Delta {delta * 1000:F1}mV — exceeds {config.BalancingStartDelta * 1000:F0}mV threshold",
                AlertSeverity.Warning);
    }

    private void Fire(string key, string title, string body,
                      AlertSeverity severity = AlertSeverity.Alert)
    {
        if (!CanNotify(key)) return;

        var rec = new AlertRecord(DateTime.Now, title, body, severity);
        lock (_histLock)
        {
            if (_history.Count >= MaxHistory) _history.RemoveAt(0);
            _history.Add(rec);
        }
        AlertFired?.Invoke(rec);

        bool critical = severity is AlertSeverity.Error or AlertSeverity.Alert;
        if (critical) CriticalAlertFired?.Invoke(rec);

        try
        {
            // scenario="urgent" (Win11) bypasses Focus Assist when the app is
            // on the priority list and keeps the toast sticky in Action Center
            // — gives the user a chance to react even when minimized.
            string scenarioAttr = critical ? " scenario='urgent'" : string.Empty;
            string xml =
                $"<toast{scenarioAttr}>" +
                  $"<visual><binding template='ToastGeneric'>" +
                    $"<text>{Escape(title)}</text>" +
                    $"<text>{Escape(body)}</text>" +
                  $"</binding></visual>" +
                $"</toast>";
            AppNotificationManager.Default.Show(new AppNotification(xml));
        }
        catch
        {
            // Notification failed — will retry on next cycle
        }
    }

    private static string Escape(string s) => WebUtility.HtmlEncode(s);

    private bool CanNotify(string key)
    {
        if (_lastNotified.TryGetValue(key, out var last) && DateTime.Now - last < _cooldown)
            return false;
        _lastNotified[key] = DateTime.Now;
        return true;
    }
}
