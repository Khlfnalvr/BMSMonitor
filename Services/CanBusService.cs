using BMSMonitor.Models;
using BMSMonitor.Services.Transports;

namespace BMSMonitor.Services;

/// <summary>
/// Facade over the three supported BMS transports (ESP-serial, SLCAN,
/// PEAK-PCAN). Public events / properties stay stable so the UI, view
/// models, and auto-connect service don't need to know which backend is
/// live — they just call this class.
///
/// Switch backends via <see cref="Mode"/>; the old backend is disposed and
/// a new one is wired up. Switching while connected forces a disconnect
/// first (callers should normally Disconnect explicitly before changing).
/// </summary>
public class CanBusService : IDisposable
{
    private TransportMode    _mode;
    private IBmsTransport    _backend;

    public CanBusService() : this(TransportMode.EspSerial) { }

    public CanBusService(TransportMode mode)
    {
        _mode    = mode;
        _backend = Create(mode);
        Wire(_backend);
    }

    // ── Mode switching ────────────────────────────────────────────────────
    public TransportMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            if (IsConnected) Disconnect();

            Unwire(_backend);
            try { _backend.Dispose(); } catch { }

            _mode    = value;
            _backend = Create(value);
            Wire(_backend);

            ModeChanged?.Invoke(value);
            // Surface a status message so the UI updates the connection label.
            StatusChanged?.Invoke($"Transport switched to {value}");
        }
    }

    private static IBmsTransport Create(TransportMode mode) => mode switch
    {
        TransportMode.Slcan     => new SlcanTransport(),
        TransportMode.Pcan      => new PcanTransport(),
        TransportMode.EspSerial => new EspSerialTransport(),
        _                       => new EspSerialTransport(),
    };

    private void Wire(IBmsTransport b)
    {
        b.DataReceived  += OnData;
        b.StatusChanged += OnStatus;
        b.ErrorOccurred += OnError;
    }

    private void Unwire(IBmsTransport b)
    {
        b.DataReceived  -= OnData;
        b.StatusChanged -= OnStatus;
        b.ErrorOccurred -= OnError;
    }

    private void OnData  (BmsData d) => DataReceived ?.Invoke(d);
    private void OnStatus(string m)  => StatusChanged?.Invoke(m);
    private void OnError (string m)  => ErrorOccurred?.Invoke(m);

    // ── Public events ─────────────────────────────────────────────────────
    public event Action<BmsData>?       DataReceived;
    public event Action<string>?        StatusChanged;
    public event Action<string>?        ErrorOccurred;
    public event Action<TransportMode>? ModeChanged;

    // ── Live state ────────────────────────────────────────────────────────
    public bool   IsConnected    => _backend.IsConnected;
    public string Channel        => _backend.Channel;
    public int    Bitrate        => _backend.Bitrate;
    public string BitrateText    => _backend.BitrateText;
    public string ChannelName    => _backend.ChannelName;
    public int    FramesReceived => _backend.FramesReceived;
    public int    ParseErrors    => _backend.ParseErrors;

    // ── Mode-aware pickers ────────────────────────────────────────────────
    public CanChannel[] Channels           => _backend.EnumerateChannels();
    public CanBitrate[] Bitrates           => _backend.GetBitrates();
    public int          DefaultBitrate     => _backend.DefaultBitrateKbps;
    public bool         IsDriverAvailable  => _backend.IsAvailable;

    // ── Lifecycle ─────────────────────────────────────────────────────────
    public bool Connect(CanChannel channel, CanBitrate bitrate) =>
        _backend.Connect(channel, bitrate);

    public void Disconnect() => _backend.Disconnect();

    public bool Probe(CanChannel channel, CanBitrate bitrate, int timeoutMs) =>
        _backend.Probe(channel, bitrate, timeoutMs);

    public void Dispose()
    {
        try { _backend.Dispose(); } catch { }
    }
}
