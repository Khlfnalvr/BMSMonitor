namespace SOCTester.Models;

/// <summary>One row in the live data stream table (10 most recent frames).</summary>
public sealed class LogRow
{
    public string Timestamp    { get; init; } = "";
    public string Soc          { get; init; } = "";   // device-reported %
    public string SocVoltage   { get; init; } = "";   // voltage-based %
    public string SocCoulomb   { get; init; } = "";   // coulomb-counted %
    public string PackVoltage  { get; init; } = "";   // V
    public string Current      { get; init; } = "";   // A (+/-)
    public string Power        { get; init; } = "";   // W
    public string CoulombCount { get; init; } = "";   // Ah
    public string Energy       { get; init; } = "";   // Wh
    public string Status       { get; init; } = "";
}
