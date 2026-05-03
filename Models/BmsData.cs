namespace BMSMonitor.Models;

public enum CellState { Normal, Low, Undervoltage, Overvoltage }

public class CellStatus
{
    public int Index { get; set; }
    public double Voltage { get; set; }
    public CellState State { get; set; }
    public bool IsBalancing { get; set; }
}

public class BmsData
{
    public double[] Cells { get; set; } = new double[20];
    public double[] Temps { get; set; } = new double[10];
    public double Soc { get; set; }
    public double Current { get; set; }
    public double PackVoltage { get; set; }
    public string Status { get; set; } = "idle";
    public bool[] Balancing { get; set; } = new bool[20];
}

public class BmsConfig
{
    public double MaxDod { get; set; } = 80;
    public double MaxChargeCurrent { get; set; } = 20;
    public double MaxDischargeCurrent { get; set; } = 40;
    public double OvervoltageThreshold { get; set; } = 4.20;
    public double UndervoltageThreshold { get; set; } = 2.80;
    public double LowVoltageWarning { get; set; } = 3.00;
    public double OverTempWarning { get; set; } = 60;
    public double OverTempCutoff { get; set; } = 70;
    public double BalancingStartDelta { get; set; } = 0.020;
    public double BalancingStopDelta { get; set; } = 0.005;
}
