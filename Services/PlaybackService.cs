using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using BMSMonitor.Models;

namespace BMSMonitor.Services;

/// <summary>
/// Loads a BMS Monitor CSV log file and replays its frames through
/// the same ApplyData() pipeline used for live serial data.
/// </summary>
public sealed class PlaybackService
{
    // ── State ────────────────────────────────────────────────────────────
    private BmsData[] _frames     = [];
    private string[]  _timestamps = [];
    private Timer?    _timer;
    private int       _currentFrame;   // written only from timer or Seek (UI thread)

    public bool   IsLoaded    { get; private set; }
    public bool   IsPlaying   { get; private set; }
    public int    TotalFrames => _frames.Length;
    public int    CurrentFrame => _currentFrame;
    public string FileName    { get; private set; } = "";

    /// <summary>HH:mm:ss timestamp of the current frame.</summary>
    public string CurrentTimestamp =>
        IsLoaded && _currentFrame < _timestamps.Length
            ? _timestamps[_currentFrame]
            : "";

    /// <summary>Frames per second (1 = realtime, 2 = 2×, …).</summary>
    public double PlaybackSpeed { get; set; } = 1.0;

    // ── Events ───────────────────────────────────────────────────────────
    /// <summary>Fired on every frame advance (from thread-pool). Subscribe with dispatcher.</summary>
    public event Action<BmsData>? FrameChanged;
    /// <summary>Fired when load/play/pause/stop/unload state changes.</summary>
    public event Action? StateChanged;

    // ── Load / Unload ────────────────────────────────────────────────────
    /// <returns>null on success; error message on failure.</returns>
    public string? LoadFile(string path)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length < 2) return "File has no data rows.";

            var frames     = new List<BmsData>();
            var timestamps = new List<string>();

            for (int i = 1; i < lines.Length; i++)   // skip header
            {
                var row = lines[i].Split(',');
                if (row.Length < 55) continue;

                // Timestamp: store only HH:mm:ss
                var ts = row[0];
                timestamps.Add(ts.Length >= 19 ? ts.Substring(11, 8) : ts);

                // Pack-level
                if (!TryParse(row[1], out double v) ||
                    !TryParse(row[2], out double s) ||
                    !TryParse(row[3], out double c))
                    continue;

                var data = new BmsData
                {
                    PackVoltage = v,
                    Soc         = s,
                    Current     = c,
                    Status      = row[4]
                };

                // Cells
                bool ok = true;
                for (int j = 0; j < 20; j++)
                {
                    if (!TryParse(row[5 + j], out double cell)) { ok = false; break; }
                    data.Cells[j] = cell;
                }
                if (!ok) continue;

                // Balancing
                for (int j = 0; j < 20; j++)
                    data.Balancing[j] = row[25 + j] == "1";

                // Temperatures
                for (int j = 0; j < 10; j++)
                    if (TryParse(row[45 + j], out double t))
                        data.Temps[j] = t;

                frames.Add(data);
            }

            if (frames.Count == 0) return "No valid data rows found.";

            // Atomically replace state
            StopTimer();
            _frames       = [.. frames];
            _timestamps   = [.. timestamps];
            _currentFrame = 0;
            FileName      = Path.GetFileName(path);
            IsLoaded      = true;
            IsPlaying     = false;

            StateChanged?.Invoke();
            FrameChanged?.Invoke(_frames[0]);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public void Unload()
    {
        StopTimer();
        _frames       = [];
        _timestamps   = [];
        _currentFrame = 0;
        FileName      = "";
        IsLoaded      = false;
        IsPlaying     = false;
        StateChanged?.Invoke();
    }

    // ── Transport ────────────────────────────────────────────────────────
    public void Play()
    {
        if (!IsLoaded || IsPlaying) return;
        if (_currentFrame >= TotalFrames - 1) _currentFrame = 0;

        IsPlaying = true;
        var interval = TimeSpan.FromSeconds(1.0 / Math.Max(0.1, PlaybackSpeed));
        _timer = new Timer(OnTick, null, interval, interval);
        StateChanged?.Invoke();
    }

    public void Pause()
    {
        if (!IsPlaying) return;
        StopTimer();
        IsPlaying = false;
        StateChanged?.Invoke();
    }

    public void SeekTo(int frameIndex)
    {
        if (!IsLoaded) return;
        _currentFrame = Math.Clamp(frameIndex, 0, TotalFrames - 1);
        FrameChanged?.Invoke(_frames[_currentFrame]);
        StateChanged?.Invoke();
    }

    // ── Internals ────────────────────────────────────────────────────────
    private void OnTick(object? _)
    {
        if (!IsLoaded || !IsPlaying) return;

        if (_currentFrame >= TotalFrames - 1)
        {
            StopTimer();
            IsPlaying = false;
            StateChanged?.Invoke();
            return;
        }

        Interlocked.Increment(ref _currentFrame);
        FrameChanged?.Invoke(_frames[_currentFrame]);
        StateChanged?.Invoke();
    }

    private void StopTimer()
    {
        var t = Interlocked.Exchange(ref _timer, null);
        t?.Dispose();
    }

    private static bool TryParse(string s, out double value) =>
        double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
}
