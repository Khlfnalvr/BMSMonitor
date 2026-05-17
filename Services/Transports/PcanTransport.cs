using System.Runtime.InteropServices;
using BMSMonitor.Models;

namespace BMSMonitor.Services.Transports;

/// <summary>
/// PEAK PCAN-USB adapter via the proprietary <c>PCANBasic.dll</c> driver.
///
/// Requires the PEAK driver package to be installed on the host. The
/// adapter sits directly on the BMS CAN bus and presents itself as one of
/// PCAN_USBBUS1..8; we receive the original eight-frame BMS protocol and
/// hand it to <see cref="CanFrameAssembler"/>.
///
/// If PCANBasic.dll is not reachable, <see cref="IsAvailable"/> returns
/// false and the UI surfaces "driver missing" to the user.
/// </summary>
internal sealed class PcanTransport : IBmsTransport
{
    // ── PCAN-Basic native interop ─────────────────────────────────────────
    private const string PCanBasicDll = "PCANBasic.dll";

    private const ushort PCAN_NONEBUS  = 0x00;
    private const ushort PCAN_USBBUS1  = 0x51;
    private const ushort PCAN_USBBUS2  = 0x52;
    private const ushort PCAN_USBBUS3  = 0x53;
    private const ushort PCAN_USBBUS4  = 0x54;
    private const ushort PCAN_USBBUS5  = 0x55;
    private const ushort PCAN_USBBUS6  = 0x56;
    private const ushort PCAN_USBBUS7  = 0x57;
    private const ushort PCAN_USBBUS8  = 0x58;

    private const ushort PCAN_BAUD_1M    = 0x0014;
    private const ushort PCAN_BAUD_800K  = 0x0016;
    private const ushort PCAN_BAUD_500K  = 0x001C;
    private const ushort PCAN_BAUD_250K  = 0x011C;
    private const ushort PCAN_BAUD_125K  = 0x031C;
    private const ushort PCAN_BAUD_100K  = 0x432F;
    private const ushort PCAN_BAUD_50K   = 0x472F;
    private const ushort PCAN_BAUD_20K   = 0x532F;
    private const ushort PCAN_BAUD_10K   = 0x672F;

    private const uint PCAN_ERROR_OK         = 0x00000;
    private const uint PCAN_ERROR_QRCVEMPTY  = 0x00020;

    [StructLayout(LayoutKind.Sequential)]
    private struct TPCANMsg
    {
        public uint ID;
        public byte MSGTYPE;
        public byte LEN;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] DATA;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TPCANTimestamp
    {
        public uint   millis;
        public ushort millis_overflow;
        public ushort micros;
    }

    [DllImport(PCanBasicDll, EntryPoint = "CAN_Initialize")]
    private static extern uint CAN_Initialize(ushort channel, ushort btr0btr1, byte hwType, uint ioPort, ushort interrupt);

    [DllImport(PCanBasicDll, EntryPoint = "CAN_Uninitialize")]
    private static extern uint CAN_Uninitialize(ushort channel);

    [DllImport(PCanBasicDll, EntryPoint = "CAN_Read")]
    private static extern uint CAN_Read(ushort channel, out TPCANMsg msg, out TPCANTimestamp ts);

    [DllImport(PCanBasicDll, EntryPoint = "CAN_GetStatus")]
    private static extern uint CAN_GetStatus(ushort channel);

    // ── IBmsTransport surface ─────────────────────────────────────────────
    public event Action<BmsData>? DataReceived;
    public event Action<string>?  StatusChanged;
    public event Action<string>?  ErrorOccurred;

    public bool   IsConnected     => _channel != PCAN_NONEBUS;
    public string Channel         => _channelName ?? "";
    public int    Bitrate         { get; private set; }     // CAN bitrate in kbit/s
    public string BitrateText     => $"{Bitrate} kbit/s";
    public string ChannelName     { get; private set; } = "";
    public int    FramesReceived  { get; private set; }
    public int    ParseErrors     { get; private set; }

    public int    DefaultBitrateKbps => 500;
    public bool   IsAvailable        => IsDriverReachable();

    public CanChannel[] EnumerateChannels() =>
    [
        new("PCAN-USB 1", "PCAN-USB 1") { PcanHandle = PCAN_USBBUS1 },
        new("PCAN-USB 2", "PCAN-USB 2") { PcanHandle = PCAN_USBBUS2 },
        new("PCAN-USB 3", "PCAN-USB 3") { PcanHandle = PCAN_USBBUS3 },
        new("PCAN-USB 4", "PCAN-USB 4") { PcanHandle = PCAN_USBBUS4 },
        new("PCAN-USB 5", "PCAN-USB 5") { PcanHandle = PCAN_USBBUS5 },
        new("PCAN-USB 6", "PCAN-USB 6") { PcanHandle = PCAN_USBBUS6 },
        new("PCAN-USB 7", "PCAN-USB 7") { PcanHandle = PCAN_USBBUS7 },
        new("PCAN-USB 8", "PCAN-USB 8") { PcanHandle = PCAN_USBBUS8 },
    ];

    public CanBitrate[] GetBitrates() =>
    [
        new(0, 1000, "1 Mbit/s")     { PcanCode = PCAN_BAUD_1M   },
        new(0,  800, "800 kbit/s")   { PcanCode = PCAN_BAUD_800K },
        new(0,  500, "500 kbit/s")   { PcanCode = PCAN_BAUD_500K },
        new(0,  250, "250 kbit/s")   { PcanCode = PCAN_BAUD_250K },
        new(0,  125, "125 kbit/s")   { PcanCode = PCAN_BAUD_125K },
        new(0,  100, "100 kbit/s")   { PcanCode = PCAN_BAUD_100K },
        new(0,   50,  "50 kbit/s")   { PcanCode = PCAN_BAUD_50K  },
        new(0,   20,  "20 kbit/s")   { PcanCode = PCAN_BAUD_20K  },
        new(0,   10,  "10 kbit/s")   { PcanCode = PCAN_BAUD_10K  },
    ];

    private static bool IsDriverReachable()
    {
        try { _ = CAN_GetStatus(PCAN_NONEBUS); return true; }
        catch (DllNotFoundException) { return false; }
        catch { return true; }
    }

    // ── Internal state ────────────────────────────────────────────────────
    private ushort _channel = PCAN_NONEBUS;
    private string? _channelName;
    private CancellationTokenSource? _cts;
    private Task?  _readTask;
    private readonly CanFrameAssembler _asm = new();

    public bool Probe(CanChannel channel, CanBitrate bitrate, int timeoutMs)
    {
        if (channel.PcanHandle == 0 || bitrate.PcanCode == 0) return false;

        uint open;
        try { open = CAN_Initialize(channel.PcanHandle, bitrate.PcanCode, 0, 0, 0); }
        catch (DllNotFoundException) { return false; }
        catch { return false; }
        if (open != PCAN_ERROR_OK) return false;

        try
        {
            var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                uint rc = CAN_Read(channel.PcanHandle, out var msg, out _);
                if (rc == PCAN_ERROR_OK)
                {
                    if (msg.ID == 0x100 && msg.LEN >= 6) return true;
                }
                else if (rc == PCAN_ERROR_QRCVEMPTY) Thread.Sleep(20);
                else                                 Thread.Sleep(50);
            }
            return false;
        }
        finally
        {
            try { CAN_Uninitialize(channel.PcanHandle); } catch { }
        }
    }

    public bool Connect(CanChannel channel, CanBitrate bitrate)
    {
        if (IsConnected) Disconnect();
        if (channel.PcanHandle == 0 || bitrate.PcanCode == 0)
        {
            ErrorOccurred?.Invoke("Pilihan channel/bitrate tidak valid untuk PCAN.");
            return false;
        }

        uint rc;
        try { rc = CAN_Initialize(channel.PcanHandle, bitrate.PcanCode, 0, 0, 0); }
        catch (DllNotFoundException)
        {
            ErrorOccurred?.Invoke("PCANBasic.dll tidak ditemukan — install driver PEAK PCAN-Basic.");
            return false;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Inisialisasi PCAN gagal: {ex.Message}");
            return false;
        }

        if (rc != PCAN_ERROR_OK)
        {
            ErrorOccurred?.Invoke($"Inisialisasi PCAN gagal (kode 0x{rc:X}).");
            return false;
        }

        _channel       = channel.PcanHandle;
        _channelName   = channel.PortName;
        Bitrate        = bitrate.Kbps;
        ChannelName    = channel.DisplayName;
        FramesReceived = 0;
        ParseErrors    = 0;
        _asm.Reset();

        _cts      = new CancellationTokenSource();
        _readTask = Task.Run(() => ReadLoop(_cts.Token));

        StatusChanged?.Invoke($"Connected — {channel.DisplayName} @ {bitrate.Kbps} kbit/s (PCAN)");
        return true;
    }

    public void Disconnect()
    {
        try { _cts?.Cancel(); } catch { }
        try { _readTask?.Wait(1500); } catch { }
        if (_channel != PCAN_NONEBUS)
        {
            try { CAN_Uninitialize(_channel); } catch { }
        }
        _channel     = PCAN_NONEBUS;
        _channelName = null;
        _cts?.Dispose();
        _cts         = null;
        _readTask    = null;
        StatusChanged?.Invoke("Disconnected");
    }

    private void ReadLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _channel != PCAN_NONEBUS)
        {
            uint rc;
            TPCANMsg msg;
            try { rc = CAN_Read(_channel, out msg, out _); }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"PCAN read error: {ex.Message}");
                Thread.Sleep(200);
                continue;
            }

            if (rc == PCAN_ERROR_QRCVEMPTY) { Thread.Sleep(2); continue; }
            if (rc != PCAN_ERROR_OK)        { Thread.Sleep(20); continue; }

            var snap = _asm.HandleFrame(msg.ID, msg.DATA, msg.LEN);
            if (snap is not null)
            {
                FramesReceived++;
                DataReceived?.Invoke(snap);
            }
        }
    }

    public void Dispose() => Disconnect();
}
