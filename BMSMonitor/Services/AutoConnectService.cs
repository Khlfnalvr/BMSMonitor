using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace BMSMonitor.Services;

/// <summary>
/// Always-on auto-connect service. Starts scanning on <see cref="Start"/> and
/// keeps trying until a verified ESP32 BMS frame is received on a COM port.
///
/// Two special states:
///   • <b>Suspended</b> — set when the <i>user</i> clicks Disconnect. Scanning
///     stops so the device is not immediately reconnected against the user's
///     wishes. Call <see cref="ResumeReconnect"/> (or the user clicks Connect
///     manually) to un-suspend.
///   • <b>Unexpected disconnect</b> — port disappeared while connected (USB
///     unplug). Scanning resumes automatically; <i>suspended</i> is NOT set.
/// </summary>
public sealed class AutoConnectService : IDisposable
{
    private readonly SerialPortService _serial;
    private readonly object            _lock        = new();
    private Timer?                     _timer;
    private HashSet<string>            _knownPorts  = [];
    private HashSet<string>            _failedPorts = [];
    private DateTime                   _lastFailClear = DateTime.MinValue;
    private bool                       _suspended   = false;
    private int                        _polling     = 0;   // concurrent-call guard

    public int  BaudRate    { get; set; } = 115200;
    public bool IsSuspended { get { lock (_lock) return _suspended; } }

    /// <summary>Status messages for UI — always DispatcherQueue.TryEnqueue before touching controls.</summary>
    public event Action<string>? Notification;

    public AutoConnectService(SerialPortService serial) => _serial = serial;

    // ── Lifecycle ─────────────────────────────────────────────────────────

    /// <summary>Call once at startup. Begins scanning immediately.</summary>
    public void Start(int baudRate = 115200)
    {
        lock (_lock)
        {
            BaudRate       = baudRate;
            _knownPorts    = [];          // treat all existing ports as candidates
            _failedPorts   = [];
            _suspended     = false;
            _lastFailClear = DateTime.Now;

            _timer?.Dispose();
            _timer = new Timer(Poll, null,
                TimeSpan.FromMilliseconds(500),   // first tick in 0.5 s
                TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>
    /// Call before <see cref="SerialPortService.Disconnect"/> when the user
    /// explicitly clicks the Disconnect button. Prevents auto-reconnect.
    /// </summary>
    public void SuspendReconnect()
    {
        lock (_lock) { _suspended = true; }
        Notification?.Invoke("Auto-connect ditangguhkan — klik Connect untuk menghubungkan kembali.");
    }

    /// <summary>
    /// Call when the user manually initiates a Connect. Re-enables auto-reconnect
    /// so that if the port drops later it will scan again automatically.
    /// </summary>
    public void ResumeReconnect()
    {
        lock (_lock)
        {
            _suspended   = false;
            _failedPorts = [];
        }
    }

    public void Dispose()
    {
        var t = Interlocked.Exchange(ref _timer, null);
        t?.Dispose();
    }

    // ── Timer callback ────────────────────────────────────────────────────

    private void Poll(object? _)
    {
        // Drop overlapping ticks — probe() blocks for up to 3.5 s
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;
        try   { DoPoll(); }
        finally { Interlocked.Exchange(ref _polling, 0); }
    }

    private void DoPoll()
    {
        string? portToProbe  = null;
        bool    doDisconnect = false;
        int     baud;

        // Decide action inside lock (no blocking I/O here)
        lock (_lock)
        {
            baud = BaudRate;
            var current = new HashSet<string>(SerialPortService.GetAvailablePorts());

            // ① Active port disappeared (unexpected USB unplug)
            if (_serial.IsConnected &&
                _serial.PortName is string active && !current.Contains(active))
            {
                doDisconnect   = true;
                _suspended     = false;   // resume scanning — this was NOT a manual disconnect
                _failedPorts   = [];
                _lastFailClear = DateTime.Now;
                _knownPorts    = current;
            }
            // ② User manually disconnected — do nothing until ResumeReconnect()
            else if (_suspended)
            {
                _knownPorts = current;
                return;
            }
            // ③ Already connected and port still present
            else if (_serial.IsConnected)
            {
                _knownPorts = current;
                return;
            }
            // ④ Not connected, not suspended → find a candidate
            else
            {
                // Periodically unblock ports that failed probing
                if ((DateTime.Now - _lastFailClear).TotalSeconds > 30)
                {
                    _failedPorts   = [];
                    _lastFailClear = DateTime.Now;
                }

                // New ports (appeared since last poll) take priority
                portToProbe = current.Except(_knownPorts)
                    .Concat(current.Except(_failedPorts))
                    .Distinct()
                    .FirstOrDefault();

                _knownPorts = current;
            }
        }

        // Execute outside lock (can block several seconds)
        if (doDisconnect)
        {
            _serial.Disconnect();
            Notification?.Invoke("Perangkat terputus — mencari ESP32 BMS…");
            return;
        }

        if (portToProbe is null || _serial.IsConnected) return;

        // Probe: verify it's actually an ESP32 BMS before connecting
        Notification?.Invoke($"Mendeteksi {portToProbe} @ {baud} — menunggu frame BMS…");
        bool verified = SerialPortService.Probe(portToProbe, baud, timeoutMs: 3500);

        lock (_lock)
        {
            if (_suspended) return;     // user disconnected while we were probing

            if (!verified)
            {
                _failedPorts.Add(portToProbe);
                Notification?.Invoke($"{portToProbe} — bukan ESP32 BMS, dilewati.");
                return;
            }
        }

        // Probe passed → full connect
        Notification?.Invoke($"{portToProbe} terverifikasi sebagai ESP32 BMS — menghubungkan…");
        bool ok = _serial.Connect(portToProbe, baud);

        if (!ok)
        {
            lock (_lock) { _failedPorts.Add(portToProbe); }
            Notification?.Invoke($"{portToProbe} — gagal terhubung setelah verifikasi.");
        }
        // On success, Serial.StatusChanged fires and updates the rest of the UI.
    }
}
