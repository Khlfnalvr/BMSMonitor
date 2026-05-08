using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SOCTester.Models;
using SOCTester.ViewModels;

namespace SOCTester.Views;

public sealed partial class ControlPanelPage : Page
{
    private static readonly int[] BaudRates = { 9600, 19200, 57600, 115200, 230400, 460800 };

    private MainViewModel ViewModel => App.ViewModel;

    public ControlPanelPage()
    {
        InitializeComponent();
        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        RefreshPorts();
        LoadConfig();
        UpdateConnectionUi();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewModel.ConnectionStatus) or nameof(ViewModel.IsConnected))
            UpdateConnectionUi();
    }

    // ── Port enumeration ──────────────────────────────────────────────────────

    private void RefreshPorts()
    {
        string? current = PortBox.SelectedItem as string;
        var ports = SOCTester.Services.SerialPortService.GetAvailablePorts();
        PortBox.ItemsSource = ports;

        if (current is not null)
        {
            int idx = Array.IndexOf(ports, current);
            if (idx >= 0) PortBox.SelectedIndex = idx;
        }
        if (PortBox.SelectedIndex < 0 && ports.Length > 0)
            PortBox.SelectedIndex = 0;
    }

    // ── Config loading ─────────────────────────────────────────────────────────

    private void LoadConfig()
    {
        var cfg = ViewModel.Config;
        CapacityBox.Value     = cfg.NominalCapacityAh;
        CellCountBox.Value    = cfg.CellCount;
        FullVoltageBox.Value  = cfg.FullVoltage;
        EmptyVoltageBox.Value = cfg.EmptyVoltage;
        InitialSocBox.Value   = cfg.InitialSoc;
        OffsetBox.Value       = cfg.CurrentZeroOffset;

        AlgDevice.IsChecked  = cfg.Algorithm == SocAlgorithm.DeviceReported;
        AlgVoltage.IsChecked = cfg.Algorithm == SocAlgorithm.VoltageBased;
        AlgCoulomb.IsChecked = cfg.Algorithm == SocAlgorithm.CoulombCounting;
        AlgHybrid.IsChecked  = cfg.Algorithm == SocAlgorithm.Hybrid;
    }

    // ── Connection UI sync ────────────────────────────────────────────────────

    private void UpdateConnectionUi()
    {
        bool connected     = ViewModel.IsConnected;
        ConnectBtn.Content = connected ? "Disconnect" : "Connect";
        StatusText.Text    = ViewModel.ConnectionStatus;
        PortBox.IsEnabled  = !connected;
        BaudBox.IsEnabled  = !connected;
    }

    // ── Event handlers ─────────────────────────────────────────────────────────

    private void RefreshBtn_Click(object sender, RoutedEventArgs e)
        => RefreshPorts();

    private void ConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsConnected)
        {
            ViewModel.Serial.Disconnect();
        }
        else
        {
            string? port = PortBox.SelectedItem as string;
            if (port is null) { StatusText.Text = "No COM port selected."; return; }
            int baud = BaudRates[Math.Clamp(BaudBox.SelectedIndex, 0, BaudRates.Length - 1)];
            ViewModel.Serial.Connect(port, baud);
        }
    }

    private void ApplyBtn_Click(object sender, RoutedEventArgs e)
    {
        var cfg = ViewModel.Config;
        cfg.NominalCapacityAh = CapacityBox.Value;
        cfg.CellCount         = (int)CellCountBox.Value;
        cfg.FullVoltage       = FullVoltageBox.Value;
        cfg.EmptyVoltage      = EmptyVoltageBox.Value;
        cfg.InitialSoc        = InitialSocBox.Value;
        cfg.CurrentZeroOffset = OffsetBox.Value;

        cfg.Algorithm =
            AlgDevice.IsChecked  == true ? SocAlgorithm.DeviceReported  :
            AlgVoltage.IsChecked == true ? SocAlgorithm.VoltageBased     :
            AlgCoulomb.IsChecked == true ? SocAlgorithm.CoulombCounting  :
                                           SocAlgorithm.Hybrid;

        StatusText.Text = "Settings applied.";
    }

    private void ResetCoulombBtn_Click(object sender, RoutedEventArgs e)
        => ViewModel.ResetCoulombCounter(InitialSocBox.Value);

    private void DefaultsBtn_Click(object sender, RoutedEventArgs e)
    {
        CapacityBox.Value     = 20.0;
        CellCountBox.Value    = 20;
        FullVoltageBox.Value  = 84.0;
        EmptyVoltageBox.Value = 60.0;
        InitialSocBox.Value   = 100.0;
        OffsetBox.Value       = 0.0;
        AlgHybrid.IsChecked   = true;
    }
}
