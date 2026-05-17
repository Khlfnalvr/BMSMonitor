using BMSMonitor.Models;

namespace BMSMonitor.Services;

/// <summary>
/// Which physical pipe the app uses to receive BMS data. Picked at runtime
/// from the Control Panel and persisted to settings.json. New backends plug
/// in here; everything else (UI, autoconnect, viewmodel) is mode-agnostic.
/// </summary>
public enum TransportMode
{
    /// <summary>ESP32 forwards a JSON snapshot per line over its USB-CDC port. No CAN hardware on the PC.</summary>
    EspSerial = 0,

    /// <summary>USB-to-CAN adapter speaking the SLCAN/LAWICEL ASCII protocol over a virtual COM port (CANable, USB-CAN-A …).</summary>
    Slcan     = 1,

    /// <summary>PEAK PCAN-USB adapter via the proprietary PCANBasic.dll driver.</summary>
    Pcan      = 2,
}

/// <summary>One pickable source — a COM port name (ESP / SLCAN) or a PCAN handle.</summary>
public record CanChannel(string PortName, string DisplayName)
{
    /// <summary>PCAN_USBBUSn handle when the backend is PCAN; 0 otherwise.</summary>
    public ushort PcanHandle { get; init; } = 0;
}

/// <summary>
/// One pickable bitrate. The same record is used by all three backends but
/// each one reads a different field:
///   • ESP serial — <see cref="Baud"/> is the UART baud rate (115 200 …)
///   • SLCAN     — <see cref="Kbps"/> is the CAN bus speed (10 … 1000)
///   • PCAN      — <see cref="PcanCode"/> is the BTR0BTR1 register pair
/// <see cref="Kbps"/> is what gets persisted to settings.json.
/// </summary>
public record CanBitrate(int Baud, int Kbps, string DisplayName)
{
    public ushort PcanCode { get; init; } = 0;

    /// <summary>SLCAN bus-speed command digit (0..8); -1 when N/A.</summary>
    public int SlcanCode { get; init; } = -1;
}

/// <summary>
/// Internal contract every transport backend implements. The facade
/// <see cref="CanBusService"/> holds one of these at a time and forwards
/// public events / methods to it.
/// </summary>
internal interface IBmsTransport : IDisposable
{
    // Events — mirrored to CanBusService consumers
    event Action<BmsData>? DataReceived;
    event Action<string>?  StatusChanged;
    event Action<string>?  ErrorOccurred;

    // Live state
    bool   IsConnected    { get; }
    string Channel        { get; }
    int    Bitrate        { get; }
    /// <summary>Bitrate formatted with the right unit (baud vs kbit/s) for the active backend.</summary>
    string BitrateText    { get; }
    string ChannelName    { get; }
    int    FramesReceived { get; }
    int    ParseErrors    { get; }

    // Pickers — mode-specific
    CanChannel[] EnumerateChannels();
    CanBitrate[] GetBitrates();
    int          DefaultBitrateKbps { get; }
    bool         IsAvailable        { get; }

    // Lifecycle
    bool Connect(CanChannel channel, CanBitrate bitrate);
    void Disconnect();
    bool Probe  (CanChannel channel, CanBitrate bitrate, int timeoutMs);
}
