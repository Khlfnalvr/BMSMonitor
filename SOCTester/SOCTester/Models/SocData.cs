namespace SOCTester.Models;

/// <summary>
/// One frame of SOC-related data received from the device.
/// </summary>
public class SocData
{
    public double Soc         { get; set; }   // % from device (0-100)
    public double PackVoltage { get; set; }   // V
    public double Current     { get; set; }   // A (positive = charging, negative = discharging)
    public string Status      { get; set; } = "idle";
}

/// <summary>
/// Tunable battery parameters used by the on-host SOC estimator.
/// </summary>
public class SocConfig
{
    public double NominalCapacityAh { get; set; } = 20.0;   // Ah
    public double NominalPackVoltage { get; set; } = 72.0;  // V (e.g. 20S × 3.6 V)
    public int    CellCount          { get; set; } = 20;
    public double InitialSoc         { get; set; } = 100.0; // %
    public double FullVoltage        { get; set; } = 84.0;  // V at 100% SOC (20S × 4.20)
    public double EmptyVoltage       { get; set; } = 60.0;  // V at 0%   SOC (20S × 3.00)
    public double CurrentZeroOffset  { get; set; } = 0.0;   // A
    public SocAlgorithm Algorithm    { get; set; } = SocAlgorithm.Hybrid;
}

public enum SocAlgorithm
{
    DeviceReported,   // Use SOC reported by device
    VoltageBased,     // Estimate from pack voltage curve
    CoulombCounting,  // Integrate current
    Hybrid            // Voltage-anchor with coulomb tracking between
}
