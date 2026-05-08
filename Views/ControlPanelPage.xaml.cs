using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using BMSMonitor.Services;
using BMSMonitor.ViewModels;

namespace BMSMonitor.Views;

public sealed partial class ControlPanelPage : Page
{
    private MainViewModel ViewModel => App.ViewModel;

    public ControlPanelPage()
    {
        InitializeComponent();
        LoadConfig();
        RefreshPorts();
        HookSerial();
        SyncConnectButton();
    }

    private void HookSerial()
    {
        ViewModel.Serial.StatusChanged += msg => DispatcherQueue.TryEnqueue(() =>
        {
            ConnStatusText.Text = msg;
            SyncConnectButton();
        });
        ViewModel.Serial.ErrorOccurred += msg => DispatcherQueue.TryEnqueue(() =>
        {
            // Suppress "Failed to open COMx" errors while auto-connect is scanning —
            // those are expected when probing non-BMS ports.
            if (!ViewModel.AutoConnect.IsSuspended) return;
            FeedbackBar.Title    = "Serial error";
            FeedbackBar.Message  = msg;
            FeedbackBar.Severity = InfoBarSeverity.Error;
            FeedbackBar.IsOpen   = true;
        });
        ViewModel.AutoConnect.Notification += msg => DispatcherQueue.TryEnqueue(() =>
            AutoConnectStatusText.Text = msg);
    }

    private void SyncConnectButton()
    {
        bool connected = ViewModel.Serial.IsConnected;
        ConnectBtn.Content   = connected ? "Disconnect" : "Connect";
        ComboCOM.IsEnabled   = !connected;
        ComboBaud.IsEnabled  = !connected;
        if (!connected) ConnStatusText.Text = "Not connected";
    }

    // ── Serial controls ──────────────────────────────────────────────
    private void RefreshPorts()
    {
        var current = ComboCOM.SelectedItem as string;
        ComboCOM.Items.Clear();

        var ports = SerialPortService.GetAvailablePorts();
        foreach (var p in ports) ComboCOM.Items.Add(p);

        if (ports.Length == 0)
        {
            ComboCOM.PlaceholderText = "No COM ports detected";
            return;
        }

        ComboCOM.PlaceholderText = "Select port…";
        if (current != null && ports.Contains(current))
            ComboCOM.SelectedItem = current;
        else
            ComboCOM.SelectedIndex = 0;
    }

    private void RefreshPorts_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private int GetSelectedBaud()
    {
        if (ComboBaud.SelectedItem is ComboBoxItem item &&
            item.Content is string s && int.TryParse(s, out var b))
            return b;
        return 115200;
    }

    private void ConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Serial.IsConnected)
        {
            // Manual disconnect → suspend auto-connect so we don't immediately reconnect
            ViewModel.AutoConnect.SuspendReconnect();
            ViewModel.Serial.Disconnect();
            return;
        }

        if (ComboCOM.SelectedItem is not string port)
        {
            FeedbackBar.Title    = "Select a port";
            FeedbackBar.Message  = "Pick a COM port from the dropdown first.";
            FeedbackBar.Severity = InfoBarSeverity.Warning;
            FeedbackBar.IsOpen   = true;
            return;
        }

        // Manual connect → resume auto-connect (so future unplug will auto-reconnect)
        ViewModel.AutoConnect.BaudRate = GetSelectedBaud();
        ViewModel.AutoConnect.ResumeReconnect();
        ViewModel.Serial.Connect(port, GetSelectedBaud());
    }

    // ── Settings ─────────────────────────────────────────────────────
    private void LoadConfig()
    {
        var cfg = ViewModel.Config;
        NbxCapacity.Value     = cfg.NominalCapacityAh;
        NbxOvervolt.Value     = cfg.OvervoltageThreshold;
        NbxUndervolt.Value    = cfg.UndervoltageThreshold;
        NbxLowVolt.Value      = cfg.LowVoltageWarning;
        NbxTempWarn.Value     = cfg.OverTempWarning;
        NbxTempCutoff.Value   = cfg.OverTempCutoff;
        NbxMaxCharge.Value    = cfg.MaxChargeCurrent;
        NbxMaxDischarge.Value = cfg.MaxDischargeCurrent;
        NbxMaxDod.Value       = cfg.MaxDod;
        NbxBalStart.Value     = cfg.BalancingStartDelta * 1000;
        NbxBalStop.Value      = cfg.BalancingStopDelta  * 1000;
    }

    private void ApplySettings_Click(object sender, RoutedEventArgs e)
    {
        var cfg = ViewModel.Config;
        cfg.NominalCapacityAh     = NbxCapacity.Value;
        cfg.OvervoltageThreshold  = NbxOvervolt.Value;
        cfg.UndervoltageThreshold = NbxUndervolt.Value;
        cfg.LowVoltageWarning     = NbxLowVolt.Value;
        cfg.OverTempWarning       = NbxTempWarn.Value;
        cfg.OverTempCutoff        = NbxTempCutoff.Value;
        cfg.MaxChargeCurrent      = NbxMaxCharge.Value;
        cfg.MaxDischargeCurrent   = NbxMaxDischarge.Value;
        cfg.MaxDod                = NbxMaxDod.Value;
        cfg.BalancingStartDelta   = NbxBalStart.Value / 1000.0;
        cfg.BalancingStopDelta    = NbxBalStop.Value  / 1000.0;

        FeedbackBar.Title    = "Settings applied";
        FeedbackBar.Message  = "New thresholds are active.";
        FeedbackBar.Severity = InfoBarSeverity.Success;
        FeedbackBar.IsOpen   = true;
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        NbxCapacity.Value     = 20;
        NbxOvervolt.Value     = 4.20;
        NbxUndervolt.Value    = 2.80;
        NbxLowVolt.Value      = 3.00;
        NbxTempWarn.Value     = 60;
        NbxTempCutoff.Value   = 70;
        NbxMaxCharge.Value    = 20;
        NbxMaxDischarge.Value = 40;
        NbxMaxDod.Value       = 80;
        NbxBalStart.Value     = 20;
        NbxBalStop.Value      = 5;

        FeedbackBar.Title    = "Defaults restored";
        FeedbackBar.Message  = "Values reset — click Apply to activate.";
        FeedbackBar.Severity = InfoBarSeverity.Informational;
        FeedbackBar.IsOpen   = true;
    }
}
