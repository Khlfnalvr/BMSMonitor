namespace BMSMonitor.Models;

/// <summary>One row in the live data stream table (10 most recent frames).</summary>
public sealed class LogRow
{
    public string Timestamp   { get; init; } = "";
    public string Soc         { get; init; } = "";   // %
    public string PackVoltage { get; init; } = "";   // V
    public string Current     { get; init; } = "";   // A (+/-)
    public string Status      { get; init; } = "";
    public string MinCell     { get; init; } = "";   // V
    public string MaxCell     { get; init; } = "";   // V
    public string Delta       { get; init; } = "";   // mV
    public string Balancing   { get; init; } = "";   // count
}
