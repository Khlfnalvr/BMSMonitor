using System.Runtime.InteropServices;
using BMSMonitor.Models;

namespace BMSMonitor.Services;

/// <summary>
/// CAN bus front-end backed by PEAK PCAN-Basic (PCANBasic.dll).
///
/// The ESP32-side firmware publishes the BMS frame split across eight
/// classic-CAN messages (8 bytes each); this service reassembles them
/// into a single <see cref="BmsData"/> snapshot and raises
/// <see cref="DataReceived"/> once a complete set has been observed.
///
/// Protocol (little-endian payload, units chosen to fit 8-byte frames):
///
///   ID 0x100  Pack overview
///     [0..1] pack voltage × 100  (uint16, V*100)
///     [2..3] current × 10        (int16,  A*10)
///     [4]    state-of-charge     (uint8,  %)
///     [5]    status enum         (0=idle 1=charging 2=discharging 3=fault 4=balancing)
///     [6]    balancing cell count(uint8, 0..20)
///     [7]    reserved
///
///   ID 0x101..0x105  Cell voltages — 4 cells × uint16 mV per frame
///     0x101 cells 1..4,  0x102 cells 5..8,  …,  0x105 cells 17..20
///
///   ID 0x110  Temperatures 1..8       (int8 °C × 8)
///   ID 0x111  Temperatures 9..10 + balancing bitmap
///     [0]    temp 9  (int8 °C)
///     [1]    temp 10 (int8 °C)
///     [2..5] balancing bitmap (uint32, bit i = cell i+1)
///     [6..7] reserved
/// </summary>
public class CanBusService : IDisposable
{
    // ── PCAN-Basic native interop ─────────────────────────────────────────
    private const string PCanBasicDll = "PCANBasic.dll";

    // Channel handles for USB (covers nearly all desk setups)
    public const ushort PCAN_NONEBUS  = 0x00;
    public const ushort PCAN_USBBUS1  = 0x51;
    public const ushort PCAN_USBBUS2  = 0x52;
    public const ushort PCAN_USBBUS3  = 0x53;
    public const ushort PCAN_USBBUS4  = 0x54;
    public const ushort PCAN_USBBUS5  = 0x55;
    public const ushort PCAN_USBBUS6  = 0x56;
    public const ushort PCAN_USBBUS7  = 0x57;
    public const ushort PCAN_USBBUS8  = 0x58;

    // Baud rate codes (BTR0BTR1) for standard CAN bitrates
    public const ushort PCAN_BAUD_1M    = 0x0014;
    public const ushort PCAN_BAUD_800K  = 0x0016;
    public const ushort PCAN_BAUD_500K  = 0x001C;
    public const ushort PCAN_BAUD_250K  = 0x011C;
    public const ushort PCAN_BAUD_125K  = 0x031C;
    public const ushort PCAN_BAUD_100K  = 0x432F;
    public const ushort PCAN_BAUD_95K   = 0xC34E;
    public const ushort PCAN_BAUD_83K   = 0x852B;
    public const ushort PCAN_BAUD_50K   = 0x472F;
    public const ushort PCAN_BAUD_47K   = 0x1414;
    public const ushort PCAN_BAUD_33K   = 0x8B2F;
    public const ushort PCAN_BAUD_20K   = 0x532F;
    public const ushort PCAN_BAUD_10K   = 0x672F;
    public const ushort PCAN_BAUD_5K    = 0x7F7F;

    // Status / return codes
    private const uint PCAN_ERROR_OK         = 0x00000;
    private const uint PCAN_ERROR_QRCVEMPTY  = 0x00020;

    [StructLayout(LayoutKind.Sequential)]
    private struct TPCANMsg
    {
        public uint ID;            // 11-bit (or 29-bit) frame ID
        public byte MSGTYPE;       // 0 = standard, 0x02 = remote, 0x04 = extended …
        public byte LEN;           // data length 0..8
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

    // ── Public surface ────────────────────────────────────────────────────
    public event Action<BmsData>? DataReceived;
    public event Action<string>?  StatusChanged;
    public event Action<string>?  ErrorOccurred;

    public bool   IsConnected     => _channel != PCAN_NONEBUS;
    public ushort Channel         => _channel;
    public int    Bitrate         { get; private set; }
    public string ChannelName     { get; private set; } = "";
    public int    FramesReceived  { get; private set; }
    public int    ParseErrors     { get; private set; }

    // ── Available channels / bitrates ─────────────────────────────────────
    public record CanChannel(ushort Handle, string DisplayName);
    public record CanBitrate(ushort Btr, int Kbps, string DisplayName);

    public static CanChannel[] Channels =>
    [
        new(PCAN_USBBUS1, "PCAN-USB 1"),
        new(PCAN_USBBUS2, "PCAN-USB 2"),
        new(PCAN_USBBUS3, "PCAN-USB 3"),
        new(PCAN_USBBUS4, "PCAN-USB 4"),
        new(PCAN_USBBUS5, "PCAN-USB 5"),
        new(PCAN_USBBUS6, "PCAN-USB 6"),
        new(PCAN_USBBUS7, "PCAN-USB 7"),
        new(PCAN_USBBUS8, "PCAN-USB 8"),
    ];

    // Standard CAN bitrates — 500 kbit/s is the de-facto BMS default.
    public static CanBitrate[] Bitrates =>
    [
        new(PCAN_BAUD_1M,   1000, "1 Mbit/s"),
        new(PCAN_BAUD_800K,  800, "800 kbit/s"),
        new(PCAN_BAUD_500K,  500, "500 kbit/s"),
        new(PCAN_BAUD_250K,  250, "250 kbit/s"),
        new(PCAN_BAUD_125K,  125, "125 kbit/s"),
        new(PCAN_BAUD_100K,  100, "100 kbit/s"),
        new(PCAN_BAUD_50K,    50, "50 kbit/s"),
        new(PCAN_BAUD_20K,    20, "20 kbit/s"),
        new(PCAN_BAUD_10K,    10, "10 kbit/s"),
    ];

    public static int DefaultBitrate => 500;
    public static ushort DefaultBitrateCode => PCAN_BAUD_500K;

    /// <summary>
    /// Probes whether PCANBasic.dll is reachable. If false, the runtime
    /// driver is not installed — the UI surfaces a friendlier message.
    /// </summary>
    public static bool IsDriverAvailable()
    {
        try { _ = CAN_GetStatus(PCAN_NONEBUS); return true; }
        catch (DllNotFoundException) { return false; }
        catch { return true; }   // any other failure means DLL loaded
    }

    // ── Internal state ────────────────────────────────────────────────────
    private ushort _channel = PCAN_NONEBUS;
    private CancellationTokenSource? _cts;
    private Task? _readTask;

    // Frame reassembly — keep partial values until a fresh 0x100 closes
    // the snapshot. (0x100 is the heartbeat that signals "new sample".)
    private readonly object _asmLock = new();
    private BmsData _pending = new();
    private byte _seenMask;
    private const byte SEEN_PACK    = 0x01;
    private const byte SEEN_CELLS1  = 0x02;
    private const byte SEEN_CELLS2  = 0x04;
    private const byte SEEN_CELLS3  = 0x08;
    private const byte SEEN_CELLS4  = 0x10;
    private const byte SEEN_CELLS5  = 0x20;
    private const byte SEEN_TEMPS1  = 0x40;
    private const byte SEEN_TEMPS2  = 0x80;
    private const byte SEEN_ALL     = 0xFF;

    /// <summary>
    /// Briefly opens the channel and listens for up to <paramref name="timeoutMs"/>
    /// for an ID 0x100 frame. Returns true if at least one BMS pack frame
    /// is observed, confirming this is the expected device.
    /// </summary>
    public static bool Probe(ushort channel, ushort btr0btr1, int timeoutMs = 3000)
    {
        uint open;
        try { open = CAN_Initialize(channel, btr0btr1, 0, 0, 0); }
        catch (DllNotFoundException) { return false; }
        catch { return false; }
        if (open != PCAN_ERROR_OK) return false;

        try
        {
            var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                uint rc = CAN_Read(channel, out var msg, out _);
                if (rc == PCAN_ERROR_OK)
                {
                    if (msg.ID == 0x100 && msg.LEN >= 6) return true;
                }
                else if (rc == PCAN_ERROR_QRCVEMPTY)
                {
                    Thread.Sleep(20);
                }
                else
                {
                    Thread.Sleep(50);
                }
            }
            return false;
        }
        finally
        {
            try { CAN_Uninitialize(channel); } catch { }
        }
    }

    public bool Connect(ushort channel, ushort btr0btr1, int bitrateKbps, string displayName)
    {
        if (IsConnected) Disconnect();

        uint rc;
        try { rc = CAN_Initialize(channel, btr0btr1, 0, 0, 0); }
        catch (DllNotFoundException)
        {
            ErrorOccurred?.Invoke("PCANBasic.dll tidak ditemukan — silakan install driver PEAK PCAN-Basic.");
            return false;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Inisialisasi CAN gagal: {ex.Message}");
            return false;
        }

        if (rc != PCAN_ERROR_OK)
        {
            ErrorOccurred?.Invoke($"Inisialisasi CAN gagal (kode 0x{rc:X}).");
            return false;
        }

        _channel       = channel;
        Bitrate        = bitrateKbps;
        ChannelName    = displayName;
        FramesReceived = 0;
        ParseErrors    = 0;
        ResetAssembly();

        _cts = new CancellationTokenSource();
        _readTask = Task.Run(() => ReadLoop(_cts.Token));

        StatusChanged?.Invoke($"Connected — {displayName} @ {bitrateKbps} kbit/s");
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
        _channel = PCAN_NONEBUS;
        _cts?.Dispose();
        _cts = null;
        _readTask = null;
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
                ErrorOccurred?.Invoke($"CAN read error: {ex.Message}");
                Thread.Sleep(200);
                continue;
            }

            if (rc == PCAN_ERROR_QRCVEMPTY)
            {
                // Idle — sleep briefly to keep CPU usage near zero
                Thread.Sleep(2);
                continue;
            }

            if (rc != PCAN_ERROR_OK)
            {
                Thread.Sleep(20);
                continue;
            }

            HandleFrame(msg);
        }
    }

    private void HandleFrame(TPCANMsg msg)
    {
        if (msg.DATA is null || msg.LEN == 0) return;
        BmsData? completed = null;

        lock (_asmLock)
        {
            switch (msg.ID)
            {
                case 0x100:
                    // Heartbeat — a fresh pack frame closes the previous snapshot.
                    // If we accumulated a complete-ish set already, ship it before
                    // starting the new one.
                    if (_seenMask != 0 && (_seenMask & SEEN_PACK) != 0)
                        completed = SnapshotAndReset();

                    if (msg.LEN >= 6)
                    {
                        ushort vMv  = ReadU16LE(msg.DATA, 0);
                        short  iDa  = ReadI16LE(msg.DATA, 2);
                        byte   soc  = msg.DATA[4];
                        byte   stat = msg.DATA[5];
                        _pending.PackVoltage = vMv / 100.0;
                        _pending.Current     = iDa / 10.0;
                        _pending.Soc         = soc;
                        _pending.Status      = StatusToString(stat);
                        _seenMask |= SEEN_PACK;
                    }
                    break;

                case 0x101: ApplyCellGroup(msg, baseIndex: 0);  _seenMask |= SEEN_CELLS1; break;
                case 0x102: ApplyCellGroup(msg, baseIndex: 4);  _seenMask |= SEEN_CELLS2; break;
                case 0x103: ApplyCellGroup(msg, baseIndex: 8);  _seenMask |= SEEN_CELLS3; break;
                case 0x104: ApplyCellGroup(msg, baseIndex: 12); _seenMask |= SEEN_CELLS4; break;
                case 0x105: ApplyCellGroup(msg, baseIndex: 16); _seenMask |= SEEN_CELLS5; break;

                case 0x110:
                    int tCount = Math.Min(8, (int)msg.LEN);
                    for (int i = 0; i < tCount; i++)
                        _pending.Temps[i] = (sbyte)msg.DATA[i];
                    _seenMask |= SEEN_TEMPS1;
                    break;

                case 0x111:
                    if (msg.LEN >= 6)
                    {
                        _pending.Temps[8] = (sbyte)msg.DATA[0];
                        _pending.Temps[9] = (sbyte)msg.DATA[1];
                        uint bitmap = ReadU32LE(msg.DATA, 2);
                        for (int i = 0; i < 20; i++)
                            _pending.Balancing[i] = ((bitmap >> i) & 1) == 1;
                        _seenMask |= SEEN_TEMPS2;
                    }
                    break;

                default:
                    return;        // unknown ID — ignore silently
            }

            // Ship as soon as a full set is collected so the heartbeat
            // doesn't gate single-rate publishers (some firmwares only
            // emit 0x100 every Nth set).
            if (_seenMask == SEEN_ALL)
                completed = SnapshotAndReset();
        }

        if (completed is not null)
        {
            FramesReceived++;
            DataReceived?.Invoke(completed);
        }
    }

    private BmsData SnapshotAndReset()
    {
        var snap = new BmsData
        {
            PackVoltage = _pending.PackVoltage,
            Soc         = _pending.Soc,
            Current     = _pending.Current,
            Status      = _pending.Status,
        };
        Array.Copy(_pending.Cells,     snap.Cells,     20);
        Array.Copy(_pending.Temps,     snap.Temps,     10);
        Array.Copy(_pending.Balancing, snap.Balancing, 20);
        _seenMask = 0;
        return snap;
    }

    private void ResetAssembly()
    {
        lock (_asmLock)
        {
            _pending  = new BmsData();
            _seenMask = 0;
        }
    }

    private void ApplyCellGroup(TPCANMsg msg, int baseIndex)
    {
        // Four uint16 cells per frame, mV little-endian.
        for (int i = 0; i < 4 && baseIndex + i < 20; i++)
        {
            int off = i * 2;
            if (off + 1 >= msg.LEN) break;
            ushort mv = ReadU16LE(msg.DATA, off);
            _pending.Cells[baseIndex + i] = mv / 1000.0;
        }
    }

    private static ushort ReadU16LE(byte[] b, int i) => (ushort)(b[i] | (b[i + 1] << 8));
    private static short  ReadI16LE(byte[] b, int i) => (short) (b[i] | (b[i + 1] << 8));
    private static uint   ReadU32LE(byte[] b, int i) =>
        (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));

    private static string StatusToString(byte code) => code switch
    {
        0 => "idle",
        1 => "charging",
        2 => "discharging",
        3 => "fault",
        4 => "balancing",
        _ => "unknown",
    };

    public void Dispose() => Disconnect();
}
