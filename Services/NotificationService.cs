using Microsoft.Windows.AppNotifications;
using BMSMonitor.Models;

namespace BMSMonitor.Services;

public class NotificationService
{
    private readonly Dictionary<string, DateTime> _lastNotified = new();
    private readonly TimeSpan _cooldown = TimeSpan.FromSeconds(30);
    private bool _registered;

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

        var alerts = EvaluateConditions(data, config);
        foreach (var (key, title, body) in alerts)
        {
            if (!CanNotify(key)) continue;
            try
            {
                string xml = $@"<toast><visual><binding template='ToastGeneric'>
                    <text>{title}</text>
                    <text>{body}</text>
                </binding></visual></toast>";
                AppNotificationManager.Default.Show(new AppNotification(xml));
            }
            catch
            {
                // Notification failed — will retry on next cycle
            }
        }
    }

    private List<(string Key, string Title, string Body)> EvaluateConditions(BmsData data, BmsConfig config)
    {
        var alerts = new List<(string, string, string)>();

        // Cell voltages
        for (int i = 0; i < data.Cells.Length; i++)
        {
            if (data.Cells[i] >= config.OvervoltageThreshold)
                alerts.Add(($"ov_{i}", "BMS - Overvoltage",
                    $"Cell {i + 1} at {data.Cells[i]:F3}V — exceeds {config.OvervoltageThreshold:F2}V cutoff"));
            else if (data.Cells[i] <= config.UndervoltageThreshold)
                alerts.Add(($"uv_{i}", "BMS - Undervoltage",
                    $"Cell {i + 1} at {data.Cells[i]:F3}V — below {config.UndervoltageThreshold:F2}V cutoff"));
        }

        // Current
        if (data.Current >= config.MaxChargeCurrent)
            alerts.Add(($"oc_chg", "BMS - Overcurrent",
                $"Charge current {data.Current:F1}A — exceeds {config.MaxChargeCurrent:F0}A limit"));
        if (Math.Abs(data.Current) >= config.MaxDischargeCurrent)
            alerts.Add(($"oc_dsg", "BMS - Overcurrent",
                $"Discharge current {Math.Abs(data.Current):F1}A — exceeds {config.MaxDischargeCurrent:F0}A limit"));

        // Temperatures
        for (int i = 0; i < data.Temps.Length; i++)
        {
            if (data.Temps[i] >= config.OverTempCutoff)
                alerts.Add(($"otc_{i}", "BMS - Temperature Critical",
                    $"Sensor {i + 1} at {data.Temps[i]:F0}°C — exceeds {config.OverTempCutoff:F0}°C cutoff"));
            else if (data.Temps[i] >= config.OverTempWarning)
                alerts.Add(($"otw_{i}", "BMS - Temperature Warning",
                    $"Sensor {i + 1} at {data.Temps[i]:F0}°C — exceeds {config.OverTempWarning:F0}°C warning"));
        }

        // Cell imbalance
        double delta = data.Cells.Max() - data.Cells.Min();
        if (delta >= config.BalancingStartDelta)
            alerts.Add(("imb", "BMS - Cell Imbalance",
                $"Delta {delta * 1000:F1}mV — exceeds {config.BalancingStartDelta * 1000:F0}mV threshold"));

        return alerts;
    }

    private bool CanNotify(string key)
    {
        if (_lastNotified.TryGetValue(key, out var last) && DateTime.Now - last < _cooldown)
            return false;
        _lastNotified[key] = DateTime.Now;
        return true;
    }
}
