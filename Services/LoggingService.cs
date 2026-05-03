using System.IO;
using System.Text;
using BMSMonitor.Models;

namespace BMSMonitor.Services;

public class LoggingService
{
    private StreamWriter? _writer;
    private DateTime _startTime;

    public bool IsLogging   { get; private set; }
    public string? FilePath { get; private set; }
    public int SampleCount  { get; private set; }

    public TimeSpan Duration =>
        IsLogging ? DateTime.Now - _startTime : TimeSpan.Zero;

    public event Action? StateChanged;

    public static string DefaultLogsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BMSLogs");

    public static string GenerateFileName() =>
        $"BMS_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv";

    public void Start(string filePath)
    {
        if (IsLogging) return;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        _writer = new StreamWriter(filePath, append: false, Encoding.UTF8);
        WriteHeader();
        FilePath    = filePath;
        _startTime  = DateTime.Now;
        SampleCount = 0;
        IsLogging   = true;
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        if (!IsLogging) return;
        _writer?.Flush();
        _writer?.Dispose();
        _writer   = null;
        IsLogging = false;
        StateChanged?.Invoke();
    }

    public void Log(BmsData data)
    {
        if (!IsLogging || _writer == null) return;

        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        sb.Append($",{data.PackVoltage:F3},{data.Soc:F2},{data.Current:F3},{data.Status}");
        for (int i = 0; i < 20; i++) sb.Append($",{data.Cells[i]:F4}");
        for (int i = 0; i < 20; i++) sb.Append($",{(data.Balancing[i] ? 1 : 0)}");
        for (int i = 0; i < 10; i++) sb.Append($",{data.Temps[i]:F2}");

        _writer.WriteLine(sb.ToString());
        SampleCount++;
        StateChanged?.Invoke();
    }

    private void WriteHeader()
    {
        var sb = new StringBuilder();
        sb.Append("Timestamp,PackVoltage_V,SOC_pct,Current_A,Status");
        for (int i = 1; i <= 20; i++) sb.Append($",Cell{i}_V");
        for (int i = 1; i <= 20; i++) sb.Append($",Bal{i}");
        for (int i = 1; i <= 10; i++) sb.Append($",Temp{i}_C");
        _writer!.WriteLine(sb.ToString());
    }
}
