using BMSMonitor.Models;

namespace BMSMonitor.Services.Transports;

/// <summary>
/// Reassembles the BMS snapshot from the eight classic-CAN frames the
/// firmware publishes. Shared by PCAN and SLCAN backends — they both
/// receive raw CAN frames; only the driver differs.
///
/// Protocol (little-endian, see CanBusService doc comment for details):
///
///   0x100   pack overview         (V, I, SOC, status, balancing count)
///   0x101 … 0x105  cell voltages  (4 × uint16 mV per frame)
///   0x110   temperatures 1..8     (int8 °C)
///   0x111   temperatures 9..10 + balancing bitmap
///
/// Call <see cref="HandleFrame"/> for each received CAN frame; returns the
/// completed snapshot when a full set has been observed, or null otherwise.
/// </summary>
internal sealed class CanFrameAssembler
{
    private const byte SEEN_PACK    = 0x01;
    private const byte SEEN_CELLS1  = 0x02;
    private const byte SEEN_CELLS2  = 0x04;
    private const byte SEEN_CELLS3  = 0x08;
    private const byte SEEN_CELLS4  = 0x10;
    private const byte SEEN_CELLS5  = 0x20;
    private const byte SEEN_TEMPS1  = 0x40;
    private const byte SEEN_TEMPS2  = 0x80;
    private const byte SEEN_ALL     = 0xFF;

    private readonly object _lock = new();
    private BmsData _pending = new();
    private byte    _seenMask;

    public void Reset()
    {
        lock (_lock)
        {
            _pending  = new BmsData();
            _seenMask = 0;
        }
    }

    /// <summary>
    /// Feeds one CAN frame into the assembler. Returns a complete snapshot
    /// when the eight-frame set is closed (either by SEEN_ALL or by a fresh
    /// 0x100 heartbeat arriving with a previous pack frame already seen).
    /// </summary>
    public BmsData? HandleFrame(uint id, byte[] data, int len)
    {
        if (data is null || len == 0) return null;
        BmsData? completed = null;

        lock (_lock)
        {
            switch (id)
            {
                case 0x100:
                    // Heartbeat — a fresh 0x100 closes the previous snapshot
                    // when we already saw one. Lets us ship even when later
                    // frames stop arriving (slow / dropped).
                    if (_seenMask != 0 && (_seenMask & SEEN_PACK) != 0)
                        completed = SnapshotAndReset();

                    if (len >= 6)
                    {
                        ushort vCv  = U16(data, 0);
                        short  iDa  = I16(data, 2);
                        byte   soc  = data[4];
                        byte   stat = data[5];
                        _pending.PackVoltage = vCv / 100.0;
                        _pending.Current     = iDa / 10.0;
                        _pending.Soc         = soc;
                        _pending.Status      = StatusToString(stat);
                        _seenMask |= SEEN_PACK;
                    }
                    break;

                case 0x101: ApplyCellGroup(data, len, 0);  _seenMask |= SEEN_CELLS1; break;
                case 0x102: ApplyCellGroup(data, len, 4);  _seenMask |= SEEN_CELLS2; break;
                case 0x103: ApplyCellGroup(data, len, 8);  _seenMask |= SEEN_CELLS3; break;
                case 0x104: ApplyCellGroup(data, len, 12); _seenMask |= SEEN_CELLS4; break;
                case 0x105: ApplyCellGroup(data, len, 16); _seenMask |= SEEN_CELLS5; break;

                case 0x110:
                    int tCount = Math.Min(8, len);
                    for (int i = 0; i < tCount; i++) _pending.Temps[i] = (sbyte)data[i];
                    _seenMask |= SEEN_TEMPS1;
                    break;

                case 0x111:
                    if (len >= 6)
                    {
                        _pending.Temps[8] = (sbyte)data[0];
                        _pending.Temps[9] = (sbyte)data[1];
                        uint bitmap = U32(data, 2);
                        for (int i = 0; i < 20; i++)
                            _pending.Balancing[i] = ((bitmap >> i) & 1) == 1;
                        _seenMask |= SEEN_TEMPS2;
                    }
                    break;

                default:
                    return null;     // unknown ID — ignore silently
            }

            // Ship as soon as the full set is in — single-rate firmwares may
            // not emit a fresh 0x100 between sets.
            if (_seenMask == SEEN_ALL)
                completed = SnapshotAndReset();
        }

        return completed;
    }

    private BmsData SnapshotAndReset()
    {
        var snap = new BmsData
        {
            PackVoltage = _pending.PackVoltage,
            Current     = _pending.Current,
            Soc         = _pending.Soc,
            Status      = _pending.Status,
        };
        Array.Copy(_pending.Cells,     snap.Cells,     20);
        Array.Copy(_pending.Temps,     snap.Temps,     10);
        Array.Copy(_pending.Balancing, snap.Balancing, 20);
        _seenMask = 0;
        return snap;
    }

    private void ApplyCellGroup(byte[] data, int len, int baseIndex)
    {
        for (int i = 0; i < 4 && baseIndex + i < 20; i++)
        {
            int off = i * 2;
            if (off + 1 >= len) break;
            ushort mv = U16(data, off);
            _pending.Cells[baseIndex + i] = mv / 1000.0;
        }
    }

    private static ushort U16(byte[] b, int i) => (ushort)(b[i] | (b[i + 1] << 8));
    private static short  I16(byte[] b, int i) => (short) (b[i] | (b[i + 1] << 8));
    private static uint   U32(byte[] b, int i) =>
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
}
