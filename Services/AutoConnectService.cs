using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace BMSMonitor.Services;

/// <summary>
/// Always-on auto-connect for whichever transport the user has selected.
/// Enumerates candidate channels via the live backend
/// (<see cref="CanBusService.Channels"/>), probes each at the user-chosen
/// bitrate, and holds the first one that returns a valid BMS sample.
///
///   • <b>Suspended</b> — set when the user clicks Disconnect; scanning halts
///     until <see cref="ResumeReconnect"/> is called.
///   • <b>Unexpected drop</b> — backend reports disconnect; scanning resumes
///     automatically (no suspension).
///   • <b>Mode change</b> — failed-channel list is cleared so the new backend
///     starts with a fresh scan.
/// </summary>
public sealed class AutoConnectService : IDisposable
{
    private readonly CanBusService _can;
    private readonly object        _lock          = new();
    private Timer?                 _timer;
    private HashSet<string>        _failedPorts   = new(StringComparer.OrdinalIgnoreCase);
    private DateTime               _lastFailClear = DateTime.MinValue;
    private bool                   _suspended     = false;
    private int                    _polling       = 0;   // concurrent-call guard

    /// <summary>The user-chosen bitrate, in Kbps. Used as the lookup key
    /// into <see cref="CanBusService.Bitrates"/> for the active backend.</summary>
    public int  BitrateKbps    { get; set; }
    public bool IsSuspended    { get { lock (_lock) return _suspended; } }
    public bool IsEnabled      => !IsSuspended;

    private int _reconnectIntervalSec = 2;
    public int ReconnectIntervalSec
    {
        get { lock (_lock) return _reconnectIntervalSec; }
        set
        {
            int v = Math.Clamp(value, 1, 60);
            lock (_lock)
            {
                if (_reconnectIntervalSec == v) return;
                _reconnectIntervalSec = v;
                _timer?.Change(TimeSpan.FromMilliseconds(500),
                               TimeSpan.FromSeconds(v));
            }
        }
    }
    public int ProbeTimeoutMs { get; set; } = 3000;

    public event Action<string>? Notification;

    public AutoConnectService(CanBusService can)
    {
        _can = can;
        BitrateKbps = can.DefaultBitrate;

        // Reset scan state when the user changes transport so the new
        // backend starts with a clean slate.
        _can.ModeChanged += _ =>
        {
            lock (_lock)
            {
                _failedPorts.Clear();
                _lastFailClear = DateTime.Now;
                BitrateKbps    = _can.DefaultBitrate;
            }
        };
    }

    public void Start(int bitrateKbps)
    {
        lock (_lock)
        {
            BitrateKbps    = bitrateKbps;
            _failedPorts.Clear();
            _suspended     = false;
            _lastFailClear = DateTime.Now;

            _timer?.Dispose();
            _timer = new Timer(Poll, null,
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromSeconds(_reconnectIntervalSec));
        }
    }

    public void SuspendReconnect()
    {
        lock (_lock) { _suspended = true; }
        Notification?.Invoke("Auto-connect ditangguhkan — klik Connect untuk menghubungkan kembali.");
    }

    public void ResumeReconnect()
    {
        lock (_lock)
        {
            _suspended = false;
            _failedPorts.Clear();
        }
    }

    public void Dispose()
    {
        var t = Interlocked.Exchange(ref _timer, null);
        t?.Dispose();
    }

    private void Poll(object? _)
    {
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;
        try   { DoPoll(); }
        finally { Interlocked.Exchange(ref _polling, 0); }
    }

    private void DoPoll()
    {
        CanChannel? candidate = null;
        CanBitrate? bitrate   = null;
        int kbps;

        lock (_lock)
        {
            kbps = BitrateKbps;

            if (_can.IsConnected) return;
            if (_suspended)        return;

            // Periodically retry ports that failed earlier (USB re-enumeration,
            // device replugged, etc.).
            if ((DateTime.Now - _lastFailClear).TotalSeconds > 30)
            {
                _failedPorts.Clear();
                _lastFailClear = DateTime.Now;
            }

            // Resolve the bitrate against the *current* backend — switching
            // mode swaps the available bitrate set.
            bitrate = _can.Bitrates.FirstOrDefault(b => b.Kbps == kbps)
                   ?? _can.Bitrates.FirstOrDefault(b => b.Kbps == _can.DefaultBitrate)
                   ?? _can.Bitrates.FirstOrDefault();
            if (bitrate is null) return;

            foreach (var ch in _can.Channels)
            {
                if (_failedPorts.Contains(ch.PortName)) continue;
                candidate = ch;
                break;
            }
        }

        if (candidate is null || _can.IsConnected) return;

        Notification?.Invoke($"Mendeteksi {candidate.DisplayName} @ {bitrate.DisplayName} — menunggu data BMS…");
        bool verified;
        try { verified = _can.Probe(candidate, bitrate, timeoutMs: ProbeTimeoutMs); }
        catch { verified = false; }

        lock (_lock)
        {
            if (_suspended) return;
            if (!verified)
            {
                _failedPorts.Add(candidate.PortName);
                Notification?.Invoke($"{candidate.DisplayName} — tidak ada data BMS, dilewati.");
                return;
            }
        }

        Notification?.Invoke($"{candidate.DisplayName} terverifikasi — menghubungkan…");
        bool ok = _can.Connect(candidate, bitrate);

        if (!ok)
        {
            lock (_lock) { _failedPorts.Add(candidate.PortName); }
            Notification?.Invoke($"{candidate.DisplayName} — gagal terhubung setelah verifikasi.");
        }
    }
}
