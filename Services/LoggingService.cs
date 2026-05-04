using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using BMSMonitor.Models;
using MiniExcelLibs;

namespace BMSMonitor.Services;

public class LoggingService
{
    // ── Private state ────────────────────────────────────────────────────
    private StreamWriter?   _writer;       // CSV / TSV — streaming
    private List<LogEntry>? _buffer;       // Excel / JSON — buffered until Stop()
    private DateTime        _startTime;
    private DateTime        _endTime;
    private LogFormat       _format;

    // Lightweight record that pairs a timestamp with each incoming frame
    private readonly record struct LogEntry(DateTime Timestamp, BmsData Data);

    // ── Public state ─────────────────────────────────────────────────────
    public bool     IsLogging   { get; private set; }
    public string?  FilePath    { get; private set; }
    public int      SampleCount { get; private set; }

    /// <summary>
    /// Live duration while recording; total session duration after Stop();
    /// zero when no session has been recorded yet.
    /// </summary>
    public TimeSpan Duration =>
        IsLogging       ? DateTime.Now  - _startTime :
        SampleCount > 0 ? _endTime      - _startTime :
                          TimeSpan.Zero;

    public event Action? StateChanged;

    // ── Statics ───────────────────────────────────────────────────────────
    public static string DefaultLogsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BMSLogs");

    public static string ExtensionFor(LogFormat fmt) => fmt switch
    {
        LogFormat.Tsv   => ".tsv",
        LogFormat.Excel => ".xlsx",
        LogFormat.Json  => ".json",
        _               => ".csv"
    };

    public static string GenerateFileName(LogFormat fmt = LogFormat.Csv) =>
        $"BMS_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}{ExtensionFor(fmt)}";

    // ── Session control ───────────────────────────────────────────────────
    public void Start(string filePath, LogFormat format = LogFormat.Csv)
    {
        if (IsLogging) return;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        _format     = format;
        FilePath    = filePath;
        _startTime  = DateTime.Now;
        _endTime    = default;
        SampleCount = 0;

        if (format is LogFormat.Csv or LogFormat.Tsv)
        {
            _writer = new StreamWriter(filePath, append: false, Encoding.UTF8);
            WriteTextHeader(format == LogFormat.Tsv ? '\t' : ',');
        }
        else
        {
            _buffer = new List<LogEntry>(4096);
        }

        IsLogging = true;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Stops the session. For Excel/JSON this finalises and writes the file —
    /// may take a moment for very large sessions.
    /// </summary>
    public void Stop()
    {
        if (!IsLogging) return;
        _endTime  = DateTime.Now;
        IsLogging = false;

        if (_format is LogFormat.Csv or LogFormat.Tsv)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
        else if (_format == LogFormat.Excel)
        {
            WriteExcel();
            _buffer = null;
        }
        else   // JSON
        {
            WriteJson();
            _buffer = null;
        }

        StateChanged?.Invoke();
    }

    public void Log(BmsData data)
    {
        if (!IsLogging) return;

        if (_format is LogFormat.Csv or LogFormat.Tsv)
        {
            char sep = _format == LogFormat.Tsv ? '\t' : ',';
            var  sb  = new StringBuilder();
            AppendRow(sb, sep, DateTime.Now, data);
            _writer!.WriteLine(sb.ToString());
        }
        else
        {
            _buffer!.Add(new LogEntry(DateTime.Now, data));
        }

        SampleCount++;
        StateChanged?.Invoke();
    }

    // ── Internal: text (CSV / TSV) ────────────────────────────────────────
    private void WriteTextHeader(char sep)
    {
        var sb = new StringBuilder();
        sb.Append("Timestamp");
        sb.Append($"{sep}PackVoltage_V{sep}SOC_pct{sep}Current_A{sep}Status");
        for (int i = 1; i <= 20; i++) sb.Append($"{sep}Cell{i}_V");
        for (int i = 1; i <= 20; i++) sb.Append($"{sep}Bal{i}");
        for (int i = 1; i <= 10; i++) sb.Append($"{sep}Temp{i}_C");
        _writer!.WriteLine(sb.ToString());
    }

    private static void AppendRow(StringBuilder sb, char sep, DateTime ts, BmsData d)
    {
        sb.Append(ts.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        sb.Append($"{sep}{d.PackVoltage:F3}{sep}{d.Soc:F2}{sep}{d.Current:F3}{sep}{d.Status}");
        for (int i = 0; i < 20; i++) sb.Append($"{sep}{d.Cells[i]:F4}");
        for (int i = 0; i < 20; i++) sb.Append($"{sep}{(d.Balancing[i] ? 1 : 0)}");
        for (int i = 0; i < 10; i++) sb.Append($"{sep}{d.Temps[i]:F2}");
    }

    // ── Internal: Excel ───────────────────────────────────────────────────
    private void WriteExcel()
    {
        var rows = new List<Dictionary<string, object>>(_buffer!.Count);

        foreach (var (ts, d) in _buffer!)
        {
            var row = new Dictionary<string, object>
            {
                ["Timestamp"]     = ts.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                ["PackVoltage_V"] = d.PackVoltage,
                ["SOC_pct"]       = d.Soc,
                ["Current_A"]     = d.Current,
                ["Status"]        = d.Status
            };
            for (int i = 1; i <= 20; i++) row[$"Cell{i}_V"] = d.Cells[i - 1];
            for (int i = 1; i <= 20; i++) row[$"Bal{i}"]    = d.Balancing[i - 1] ? 1 : 0;
            for (int i = 1; i <= 10; i++) row[$"Temp{i}_C"] = d.Temps[i - 1];
            rows.Add(row);
        }

        MiniExcel.SaveAs(FilePath!, rows, overwriteFile: true);
    }

    // ── Internal: JSON ────────────────────────────────────────────────────
    private void WriteJson()
    {
        var opts = new JsonSerializerOptions { WriteIndented = true };
        // Project each buffered entry to an anonymous object matching the schema
        var rows = System.Linq.Enumerable.Select(_buffer!, e => new
        {
            timestamp   = e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            packVoltage = e.Data.PackVoltage,
            soc         = e.Data.Soc,
            current     = e.Data.Current,
            status      = e.Data.Status,
            cells       = e.Data.Cells,
            balancing   = e.Data.Balancing,
            temps       = e.Data.Temps
        });
        var json = JsonSerializer.Serialize(rows, opts);
        File.WriteAllText(FilePath!, json, Encoding.UTF8);
    }
}
