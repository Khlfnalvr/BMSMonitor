using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace BMSMonitor.Services;

/// <summary>
/// Always-on auto-connect for the CAN bus. Cycles through the PCAN-USB
/// channels and tries the user-selected bitrate first, then falls back
/// to the other standard rates. Confirms via <see cref="CanBusService.Probe"/>
/// that a BMS heartbeat (CAN ID 0x100) is present before holding the channel.
///
///   • <b>Suspended</b> — set when the user clicks Disconnect; scanning halts
///     until <see cref="ResumeReconnect"/> is called. Prevents the auto-loop
///     from immediately undoing a manual disconnect.
///   • <b>Unexpected drop</b> — driver reports the channel is gone; scanning
///     resumes automatically (no suspension).
/// </summary>
public sealed class AutoConnectService : IDisposable
{
    private readonly CanBusService _can;
    private readonly object        _lock        = new();
    private Timer?                 _timer;
    private HashSet<ushort>        _failedChans = [];
    private DateTime               _lastFailClear = DateTime.MinValue;
    private bool                   _suspended   = false;
    private int                    _polling     = 0;   // concurrent-call guard

    public ushort BitrateCode    { get; set; } = CanBusService.DefaultBitrateCode;
    public int    BitrateKbps    { get; set; } = CanBusService.DefaultBitrate;
    public bool   IsSuspended    { get { lock (_lock) return _suspended; } }
    public bool   IsEnabled      => !IsSuspended;

    // User-tunable scan/probe timing. Changing ReconnectIntervalSec
    // recreates the underlying timer so the next tick honours the new
    // interval immediately.
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

    /// <summary>Status messages for UI — always DispatcherQueue.TryEnqueue before touching controls.</summary>
    public event Action<string>? Notification;

    public AutoConnectService(CanBusService can) => _can = can;

    // ── Lifecycle ─────────────────────────────────────────────────────────

    /// <summary>Call once at startup. Begins scanning immediately.</summary>
    public void Start(ushort btr0btr1, int bitrateKbps)
    {
        lock (_lock)
        {
            BitrateCode    = btr0btr1;
            BitrateKbps    = bitrateKbps;
            _failedChans   = [];
            _suspended     = false;
            _lastFailClear = DateTime.Now;

            _timer?.Dispose();
            _timer = new Timer(Poll, null,
                TimeSpan.FromMilliseconds(500),   // first tick in 0.5 s
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
            _suspended   = false;
            _failedChans = [];
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
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;
        try   { DoPoll(); }
        finally { Interlocked.Exchange(ref _polling, 0); }
    }

    private void DoPoll()
    {
        ushort  candidate    = CanBusService.PCAN_NONEBUS;
        string  candidateNm  = "";
        ushort  btr;
        int     kbps;

        lock (_lock)
        {
            btr  = BitrateCode;
            kbps = BitrateKbps;

            // Already connected — nothing to scan.
            if (_can.IsConnected) return;
            if (_suspended)        return;

            // Periodically retry channels that failed earlier.
            if ((DateTime.Now - _lastFailClear).TotalSeconds > 30)
            {
                _failedChans   = [];
                _lastFailClear = DateTime.Now;
            }

            foreach (var ch in CanBusService.Channels)
            {
                if (_failedChans.Contains(ch.Handle)) continue;
                candidate    = ch.Handle;
                candidateNm  = ch.DisplayName;
                break;
            }
        }

        if (candidate == CanBusService.PCAN_NONEBUS || _can.IsConnected) return;

        Notification?.Invoke($"Mendeteksi {candidateNm} @ {kbps} kbit/s — menunggu frame BMS…");
        bool verified;
        try { verified = CanBusService.Probe(candidate, btr, timeoutMs: ProbeTimeoutMs); }
        catch { verified = false; }

        lock (_lock)
        {
            if (_suspended) return;
            if (!verified)
            {
                _failedChans.Add(candidate);
                Notification?.Invoke($"{candidateNm} — tidak ada frame BMS, dilewati.");
                return;
            }
        }

        Notification?.Invoke($"{candidateNm} terverifikasi sebagai BMS — menghubungkan…");
        bool ok = _can.Connect(candidate, btr, kbps, candidateNm);

        if (!ok)
        {
            lock (_lock) { _failedChans.Add(candidate); }
            Notification?.Invoke($"{candidateNm} — gagal terhubung setelah verifikasi.");
        }
    }
}
