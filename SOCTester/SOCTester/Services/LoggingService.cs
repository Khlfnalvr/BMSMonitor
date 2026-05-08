using System.IO;
using System.Text;

namespace SOCTester.Services;

public class LoggingService
{
    private StreamWriter? _writer;
    private DateTime _startTime;

    public bool   IsLogging   { get; private set; }
    public string? FilePath   { get; private set; }
    public int    SampleCount { get; private set; }

    public TimeSpan Duration =>
        IsLogging ? DateTime.Now - _startTime : TimeSpan.Zero;

    public event Action? StateChanged;

    public static string DefaultLogsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SOCLogs");

    public static string GenerateFileName() =>
        $"SOC_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv";

    public void Start(string filePath)
    {
        if (IsLogging) return;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        _writer = new StreamWriter(filePath, append: false, Encoding.UTF8);
        _writer.WriteLine(
            "Timestamp,SOC_pct,SOC_voltage_pct,SOC_coulomb_pct,PackVoltage_V," +
            "Current_A,Power_W,CoulombCount_Ah,EnergyDelivered_Wh,Status");
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

    public void Log(
        double socFromDevice, double socVoltage, double socCoulomb,
        double packVoltage, double current, double power,
        double coulombCountAh, double energyWh, string status)
    {
        if (!IsLogging || _writer == null) return;

        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        sb.Append($",{socFromDevice:F2},{socVoltage:F2},{socCoulomb:F2}");
        sb.Append($",{packVoltage:F3},{current:F3},{power:F2}");
        sb.Append($",{coulombCountAh:F4},{energyWh:F2},{status}");
        _writer.WriteLine(sb.ToString());
        SampleCount++;
        StateChanged?.Invoke();
    }
}
