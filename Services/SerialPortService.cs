using System.IO.Ports;
using System.Text;
using System.Text.Json;
using BMSMonitor.Models;

namespace BMSMonitor.Services;

/// <summary>
/// Reads line-delimited JSON frames from a UART/USB-CDC serial port and
/// converts them to <see cref="BmsData"/>. Protocol version 1.
/// </summary>
public class SerialPortService : IDisposable
{
    private SerialPort? _port;
    private CancellationTokenSource? _cts;
    private Task? _readTask;

    public event Action<BmsData>? DataReceived;
    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorOccurred;

    public bool IsConnected => _port?.IsOpen ?? false;
    public string? PortName { get; private set; }
    public int BaudRate { get; private set; }
    public int FramesReceived { get; private set; }
    public int ParseErrors { get; private set; }

    public static string[] GetAvailablePorts()
    {
        try { return SerialPort.GetPortNames().Distinct().OrderBy(p => p).ToArray(); }
        catch { return Array.Empty<string>(); }
    }

    public bool Connect(string portName, int baudRate = 115200)
    {
        if (IsConnected) Disconnect();
        try
        {
            _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
            {
                ReadTimeout  = 2000,
                WriteTimeout = 1000,
                NewLine      = "\n",
                Encoding     = Encoding.UTF8,
                DtrEnable    = true,
                RtsEnable    = true
            };
            _port.Open();
            PortName = portName;
            BaudRate = baudRate;
            FramesReceived = 0;
            ParseErrors = 0;

            _cts = new CancellationTokenSource();
            _readTask = Task.Run(() => ReadLoop(_cts.Token));

            StatusChanged?.Invoke($"Connected — {portName} @ {baudRate} 8N1");
            return true;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Failed to open {portName}: {ex.Message}");
            _port = null;
            return false;
        }
    }

    public void Disconnect()
    {
        try { _cts?.Cancel(); } catch { }
        try { _readTask?.Wait(1500); } catch { }
        try { if (_port?.IsOpen == true) _port.Close(); } catch { }
        try { _port?.Dispose(); } catch { }
        _port = null;
        _cts?.Dispose();
        _cts = null;
        _readTask = null;
        StatusChanged?.Invoke("Disconnected");
    }

    private void ReadLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _port is { IsOpen: true })
        {
            string? line = null;
            try { line = _port.ReadLine(); }
            catch (TimeoutException) { continue; }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"Read error: {ex.Message}");
                Thread.Sleep(200);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line)) continue;
            line = line.Trim();
            if (line.Length == 0 || line[0] != '{') continue;  // skip non-JSON noise

            var data = TryParseFrame(line);
            if (data != null)
            {
                FramesReceived++;
                DataReceived?.Invoke(data);
            }
            else
            {
                ParseErrors++;
            }
        }
    }

    private static BmsData? TryParseFrame(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("v", out var vEl) && vEl.ValueKind == JsonValueKind.Number)
            {
                int ver = vEl.GetInt32();
                if (ver != 1) return null;   // unknown protocol version
            }

            var data = new BmsData();

            if (root.TryGetProperty("cells", out var cellsEl) && cellsEl.ValueKind == JsonValueKind.Array)
            {
                int n = Math.Min(20, cellsEl.GetArrayLength());
                for (int i = 0; i < n; i++) data.Cells[i] = cellsEl[i].GetDouble();
            }

            if (root.TryGetProperty("temps", out var tempsEl) && tempsEl.ValueKind == JsonValueKind.Array)
            {
                int n = Math.Min(10, tempsEl.GetArrayLength());
                for (int i = 0; i < n; i++) data.Temps[i] = tempsEl[i].GetDouble();
            }

            if (root.TryGetProperty("soc",     out var socEl)) data.Soc         = socEl.GetDouble();
            if (root.TryGetProperty("current", out var curEl)) data.Current     = curEl.GetDouble();
            if (root.TryGetProperty("pack_v",  out var pvEl))  data.PackVoltage = pvEl.GetDouble();
            if (root.TryGetProperty("status",  out var stEl))  data.Status      = stEl.GetString() ?? "idle";

            if (root.TryGetProperty("bal", out var balEl) && balEl.ValueKind == JsonValueKind.Array)
            {
                int n = Math.Min(20, balEl.GetArrayLength());
                for (int i = 0; i < n; i++)
                    data.Balancing[i] = balEl[i].ValueKind switch
                    {
                        JsonValueKind.Number => balEl[i].GetInt32() != 0,
                        JsonValueKind.True => true,
                        _ => false
                    };
            }

            return data;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => Disconnect();
}
