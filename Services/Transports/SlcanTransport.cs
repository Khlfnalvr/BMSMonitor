using System.Globalization;
using System.IO.Ports;
using System.Text;
using BMSMonitor.Models;

namespace BMSMonitor.Services.Transports;

/// <summary>
/// USB-to-CAN adapter speaking the SLCAN / LAWICEL ASCII protocol over a
/// virtual COM port. Compatible with CANable, CANable Pro (slcan firmware),
/// Waveshare USB-CAN-A in SLCAN mode, USR-CANET200, and similar cheap
/// (~Rp 500 rb) bridges. The adapter does the actual CAN-bus signalling;
/// the PC just talks ASCII over USB-CDC.
///
/// Wire format (we read; commands we send between brackets):
///
///   tIIIDD…\r      Receive standard frame    III=3-hex-digit ID, D=DLC, …=DLC×2 hex
///   TIIIIIIIIDD…\r Receive extended frame    8-hex-digit ID
///   [SN\r]         Set bus speed             N = 0..8 (10k..1000k)
///   [O\r]          Open the bus              must follow Sn
///   [C\r]          Close the bus             sent on disconnect
///   [V\r]          Request version           used for probing
///
/// Host USB-CDC baud is hardcoded at 115 200 — this is the universal SLCAN
/// default; CANable Pro can also run at 1 Mbaud but auto-negotiates back
/// when 115 200 is opened.
/// </summary>
internal sealed class SlcanTransport : IBmsTransport
{
    private const int HostBaud = 115200;     // CDC-side USB baud (NOT the CAN bitrate)

    public event Action<BmsData>? DataReceived;
    public event Action<string>?  StatusChanged;
    public event Action<string>?  ErrorOccurred;

    public bool   IsConnected     => _port is { IsOpen: true };
    public string Channel         => _portName ?? "";
    public int    Bitrate         { get; private set; }     // CAN bitrate in bit/s
    public string BitrateText     => $"{Bitrate / 1000} kbit/s";
    public string ChannelName     { get; private set; } = "";
    public int    FramesReceived  { get; private set; }
    public int    ParseErrors     { get; private set; }

    public int    DefaultBitrateKbps => 500;
    public bool   IsAvailable        => TrySerialEnumerate();

    public CanChannel[] EnumerateChannels() => SerialPortHelper.EnumerateComChannels();

    // SLCAN bus speeds (the "Sn" command digit per LAWICEL spec).
    public CanBitrate[] GetBitrates() =>
    [
        new(HostBaud, 1000, "1 Mbit/s (CAN)") { SlcanCode = 8 },
        new(HostBaud,  800, "800 kbit/s (CAN)") { SlcanCode = 7 },
        new(HostBaud,  500, "500 kbit/s (CAN)") { SlcanCode = 6 },
        new(HostBaud,  250, "250 kbit/s (CAN)") { SlcanCode = 5 },
        new(HostBaud,  125, "125 kbit/s (CAN)") { SlcanCode = 4 },
        new(HostBaud,  100, "100 kbit/s (CAN)") { SlcanCode = 3 },
        new(HostBaud,   50,  "50 kbit/s (CAN)") { SlcanCode = 2 },
        new(HostBaud,   20,  "20 kbit/s (CAN)") { SlcanCode = 1 },
        new(HostBaud,   10,  "10 kbit/s (CAN)") { SlcanCode = 0 },
    ];

    // ── Internal state ────────────────────────────────────────────────────
    private SerialPort? _port;
    private string?     _portName;
    private CancellationTokenSource? _cts;
    private Task?       _readTask;
    private readonly StringBuilder _frameBuf  = new(64);
    private readonly CanFrameAssembler _asm   = new();

    public bool Probe(CanChannel channel, CanBitrate bitrate, int timeoutMs)
    {
        SerialPort? sp = null;
        try
        {
            sp = OpenAndConfigure(channel.PortName, bitrate);
        }
        catch { try { sp?.Dispose(); } catch { } return false; }

        try
        {
            var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            var sb     = new StringBuilder(64);
            var probe  = new CanFrameAssembler();   // local, separate from live one
            while (DateTime.Now < deadline)
            {
                int ch;
                try { ch = sp.ReadByte(); }
                catch (TimeoutException) { continue; }
                catch { return false; }

                if (ch < 0) continue;
                if (ch == '\r' || ch == 0x07)       // 0x07 = SLCAN nack/bell
                {
                    var line = sb.ToString();
                    sb.Clear();
                    if (TryParseFrame(line, out uint id, out byte[]? data, out int len))
                    {
                        // Any well-formed BMS frame (0x100..0x111) confirms wiring.
                        if (id >= 0x100 && id <= 0x111) return true;
                        // Otherwise keep accumulating until 0x100 arrives or timeout.
                        probe.HandleFrame(id, data!, len);
                    }
                }
                else
                {
                    sb.Append((char)ch);
                    if (sb.Length > 256) sb.Clear();
                }
            }
            return false;
        }
        finally
        {
            try { sp.Write("C\r"); } catch { }
            try { sp.Close(); }      catch { }
            try { sp.Dispose(); }    catch { }
        }
    }

    public bool Connect(CanChannel channel, CanBitrate bitrate)
    {
        if (IsConnected) Disconnect();

        try
        {
            _port = OpenAndConfigure(channel.PortName, bitrate);
        }
        catch (UnauthorizedAccessException)
        {
            ErrorOccurred?.Invoke($"Port {channel.PortName} sedang dipakai aplikasi lain.");
            _port?.Dispose(); _port = null;
            return false;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Gagal membuka {channel.PortName}: {ex.Message}");
            _port?.Dispose(); _port = null;
            return false;
        }

        _portName       = channel.PortName;
        Bitrate         = bitrate.Kbps * 1000;
        ChannelName     = channel.DisplayName;
        FramesReceived  = 0;
        ParseErrors     = 0;
        _frameBuf.Clear();
        _asm.Reset();

        _cts      = new CancellationTokenSource();
        _readTask = Task.Run(() => ReadLoop(_cts.Token));

        StatusChanged?.Invoke($"Connected — {channel.DisplayName} @ {bitrate.Kbps} kbit/s (SLCAN)");
        return true;
    }

    public void Disconnect()
    {
        try { _cts?.Cancel(); } catch { }
        try { _readTask?.Wait(1500); } catch { }
        if (_port is not null)
        {
            // Politely tell the adapter to close the bus before we hang up.
            try { if (_port.IsOpen) _port.Write("C\r"); } catch { }
            try { if (_port.IsOpen) _port.Close(); }     catch { }
            try { _port.Dispose(); }                     catch { }
        }
        _port      = null;
        _portName  = null;
        _cts?.Dispose();
        _cts       = null;
        _readTask  = null;
        StatusChanged?.Invoke("Disconnected");
    }

    /// <summary>
    /// Opens the COM port at 115 200 (the SLCAN host-side default), sends
    /// C-S-O to reset the adapter to the requested CAN bitrate, and returns
    /// the opened port. Caller is responsible for Dispose on failure.
    /// </summary>
    private static SerialPort OpenAndConfigure(string portName, CanBitrate br)
    {
        var sp = new SerialPort(portName, HostBaud, Parity.None, 8, StopBits.One)
        {
            ReadTimeout  = 250,
            WriteTimeout = 250,
            NewLine      = "\r",
            Encoding     = Encoding.ASCII,
            DtrEnable    = true,
            RtsEnable    = true,
        };
        sp.Open();

        int sCode = br.SlcanCode >= 0 ? br.SlcanCode : 6;   // default S6 = 500 kbit/s
        // Close → set speed → open. Adapter ignores extra C if already closed.
        sp.Write("C\r");
        Thread.Sleep(10);
        sp.Write($"S{sCode}\r");
        Thread.Sleep(10);
        sp.Write("O\r");
        Thread.Sleep(20);
        return sp;
    }

    private void ReadLoop(CancellationToken ct)
    {
        var sp = _port;
        if (sp is null) return;
        var buf = new byte[1024];

        while (!ct.IsCancellationRequested && sp.IsOpen)
        {
            int n;
            try { n = sp.Read(buf, 0, buf.Length); }
            catch (TimeoutException) { continue; }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                ErrorOccurred?.Invoke($"Serial read error: {ex.Message}");
                StatusChanged?.Invoke("Disconnected");
                break;
            }
            catch { break; }

            if (n <= 0) { Thread.Sleep(2); continue; }

            for (int i = 0; i < n; i++)
            {
                byte b = buf[i];
                if (b == (byte)'\r' || b == 0x07)
                {
                    var line = _frameBuf.ToString();
                    _frameBuf.Clear();
                    HandleFrameLine(line);
                }
                else
                {
                    _frameBuf.Append((char)b);
                    if (_frameBuf.Length > 256) _frameBuf.Clear();
                }
            }
        }
    }

    private void HandleFrameLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        if (!TryParseFrame(line, out uint id, out byte[]? data, out int len))
        {
            // V/N/F/Z replies and per-command ACKs aren't frames — only count
            // *frame-looking* lines (start with t/T/r/R) as parse errors.
            if (line[0] == 't' || line[0] == 'T' || line[0] == 'r' || line[0] == 'R')
                ParseErrors++;
            return;
        }

        var snap = _asm.HandleFrame(id, data!, len);
        if (snap is not null)
        {
            FramesReceived++;
            DataReceived?.Invoke(snap);
        }
    }

    /// <summary>
    /// Parses an SLCAN receive line. Returns true on a well-formed t/T (data)
    /// frame; r/R (remote) frames are recognised but data is empty.
    ///
    ///   t IIIID[D…]      standard data    3-hex ID
    ///   T IIIIIIIIDD…    extended data    8-hex ID
    ///   r IIIID          standard remote  no data
    ///   R IIIIIIIIDl     extended remote  no data
    /// </summary>
    private static bool TryParseFrame(string line, out uint id, out byte[]? data, out int len)
    {
        id = 0; data = null; len = 0;
        if (line.Length < 5) return false;

        char kind = line[0];
        bool isExt    = kind == 'T' || kind == 'R';
        bool isRemote = kind == 'r' || kind == 'R';
        if (!isExt && !(kind == 't' || kind == 'r')) return false;

        int idLen = isExt ? 8 : 3;
        if (line.Length < 1 + idLen + 1) return false;

        if (!uint.TryParse(line.AsSpan(1, idLen),
                           NumberStyles.HexNumber,
                           CultureInfo.InvariantCulture,
                           out id))
            return false;

        char dlcCh = line[1 + idLen];
        if (dlcCh < '0' || dlcCh > '8') return false;
        len = dlcCh - '0';

        data = new byte[8];
        if (isRemote) return true;        // remote frames carry no payload

        int dataStart = 1 + idLen + 1;
        if (line.Length < dataStart + len * 2) return false;

        for (int i = 0; i < len; i++)
        {
            if (!byte.TryParse(line.AsSpan(dataStart + i * 2, 2),
                               NumberStyles.HexNumber,
                               CultureInfo.InvariantCulture,
                               out var b))
                return false;
            data[i] = b;
        }
        return true;
    }

    private static bool TrySerialEnumerate()
    {
        try { _ = SerialPort.GetPortNames(); return true; }
        catch { return false; }
    }

    public void Dispose() => Disconnect();
}
