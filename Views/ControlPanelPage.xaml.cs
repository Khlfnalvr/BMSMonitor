using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using BMSMonitor.Services;
using BMSMonitor.ViewModels;

namespace BMSMonitor.Views;

public sealed partial class ControlPanelPage : Page
{
    private MainViewModel ViewModel => App.ViewModel;
    private LocalizationManager Lang => App.Lang;

    private bool _initializingParams;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _counterTimer;

    public ControlPanelPage()
    {
        InitializeComponent();
        LoadConfig();
        PopulateBitrates();
        RefreshChannels();
        HookSerial();
        SyncConnectButton();
        LoadSerialParams();

        // Poll frame counters at 2 Hz — cheap and avoids wiring an event
        // for every received frame. Started/stopped with page lifecycle.
        _counterTimer = DispatcherQueue.CreateTimer();
        _counterTimer.Interval = TimeSpan.FromMilliseconds(500);
        _counterTimer.Tick += (_, _) => UpdateCounters();

        Loaded   += (_, _) => _counterTimer.Start();
        Unloaded += (_, _) => _counterTimer.Stop();
    }

    // ── Serial controls ───────────────────────────────────────────────────
    private void HookSerial()
    {
        ViewModel.Serial.StatusChanged += msg => DispatcherQueue.TryEnqueue(() =>
        {
            ConnStatusText.Text = msg;
            SyncConnectButton();
        });
        ViewModel.Serial.ErrorOccurred += msg => DispatcherQueue.TryEnqueue(() =>
        {
            // Suppress errors while auto-connect is scanning — expected when probing channels.
            if (!ViewModel.AutoConnect.IsSuspended) return;
            ShowFeedback(Lang.Fb_SerialError, msg, InfoBarSeverity.Error);
        });
        ViewModel.AutoConnect.Notification += msg => DispatcherQueue.TryEnqueue(() =>
            AutoConnectStatusText.Text = msg);
    }

    private void SyncConnectButton()
    {
        bool connected = ViewModel.Serial.IsConnected;
        ConnectBtn.Content        = connected ? Lang.Ctrl_Disconnect : Lang.Ctrl_Connect;
        ComboSerialPort.IsEnabled = !connected;
        ComboSerialBaud.IsEnabled = !connected;
        if (!connected) ConnStatusText.Text = Lang.Ctrl_NotConnected;

        // Auto-connect toggle reflects the service's suspended flag.
        _initializingParams = true;
        ToggleAutoConnect.IsOn = !ViewModel.AutoConnect.IsSuspended;
        _initializingParams = false;
    }

    private void PopulateBitrates()
    {
        ComboSerialBaud.Items.Clear();
        foreach (var b in ViewModel.Serial.Bitrates)
            ComboSerialBaud.Items.Add(new ComboBoxItem { Content = b.DisplayName, Tag = b });

        // Prefer the saved baud rate; fall back to default (115 200).
        int defBaud = ViewModel.Serial.DefaultBitrate;
        for (int i = 0; i < ComboSerialBaud.Items.Count; i++)
        {
            if (ComboSerialBaud.Items[i] is ComboBoxItem item &&
                item.Tag is SerialBaud br &&
                br.Baud == defBaud)
            {
                ComboSerialBaud.SelectedIndex = i;
                break;
            }
        }
        if (ComboSerialBaud.SelectedIndex < 0 && ComboSerialBaud.Items.Count > 0)
            ComboSerialBaud.SelectedIndex = 0;
    }

    private void RefreshChannels()
    {
        var current = (ComboSerialPort.SelectedItem as ComboBoxItem)?.Tag as SerialPortInfo;
        ComboSerialPort.Items.Clear();

        if (!ViewModel.Serial.IsDriverAvailable)
        {
            ComboSerialPort.PlaceholderText = Lang.Ctrl_PhNoPorts;
            return;
        }

        foreach (var ch in ViewModel.Serial.Channels)
            ComboSerialPort.Items.Add(new ComboBoxItem { Content = ch.DisplayName, Tag = ch });

        ComboSerialPort.PlaceholderText = Lang.Ctrl_PhScanning;

        if (current is not null)
        {
            for (int i = 0; i < ComboSerialPort.Items.Count; i++)
            {
                if (ComboSerialPort.Items[i] is ComboBoxItem it &&
                    it.Tag is SerialPortInfo c &&
                    string.Equals(c.PortName, current.PortName, StringComparison.OrdinalIgnoreCase))
                {
                    ComboSerialPort.SelectedIndex = i;
                    return;
                }
            }
        }
        if (ComboSerialPort.Items.Count > 0)
            ComboSerialPort.SelectedIndex = 0;
    }

    private void RefreshChannels_Click(object sender, RoutedEventArgs e) => RefreshChannels();

    private SerialBaud? GetSelectedBitrate() =>
        (ComboSerialBaud.SelectedItem as ComboBoxItem)?.Tag as SerialBaud;

    private SerialPortInfo? GetSelectedChannel() =>
        (ComboSerialPort.SelectedItem as ComboBoxItem)?.Tag as SerialPortInfo;

    private void ConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Serial.IsConnected)
        {
            ViewModel.AutoConnect.SuspendReconnect();
            ViewModel.Serial.Disconnect();
            return;
        }

        var channel = GetSelectedChannel();
        var bitrate = GetSelectedBitrate();
        if (channel is null || bitrate is null)
        {
            ShowFeedback(Lang.Fb_SelectChannel, Lang.Fb_SelectChannelMsg, InfoBarSeverity.Warning);
            return;
        }

        ViewModel.AutoConnect.Baud = bitrate.Baud;
        ViewModel.AutoConnect.ResumeReconnect();
        ViewModel.Serial.Connect(channel, bitrate);
    }

    // ── Serial parameters ─────────────────────────────────────────────────
    private void LoadSerialParams()
    {
        _initializingParams = true;
        ToggleAutoConnect.IsOn    = !ViewModel.AutoConnect.IsSuspended;
        NbxReconnectInterval.Value = ViewModel.AutoConnect.ReconnectIntervalSec;
        NbxProbeTimeout.Value      = ViewModel.AutoConnect.ProbeTimeoutMs;
        _initializingParams = false;
    }

    private void ToggleAutoConnect_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializingParams) return;
        if (ToggleAutoConnect.IsOn) ViewModel.AutoConnect.ResumeReconnect();
        else                        ViewModel.AutoConnect.SuspendReconnect();
        ViewModel.SaveSettings();
    }

    private void NbxReconnectInterval_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_initializingParams) return;
        if (!double.IsNaN(args.NewValue))
        {
            ViewModel.AutoConnect.ReconnectIntervalSec = (int)args.NewValue;
            ViewModel.SaveSettings();
        }
    }

    private void NbxProbeTimeout_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_initializingParams) return;
        if (!double.IsNaN(args.NewValue))
        {
            ViewModel.AutoConnect.ProbeTimeoutMs = (int)args.NewValue;
            ViewModel.SaveSettings();
        }
    }

    private void UpdateCounters()
    {
        FramesReceivedText.Text = ViewModel.Serial.FramesReceived.ToString("N0");
        ParseErrorsText.Text    = ViewModel.Serial.ParseErrors.ToString("N0");
    }

    // ── Settings ──────────────────────────────────────────────────────────
    private void LoadConfig()
    {
        var cfg = ViewModel.Config;
        NbxCapacity.Value     = cfg.NominalCapacityAh;
        NbxOvervolt.Value     = cfg.OvervoltageThreshold;
        NbxHighVolt.Value     = cfg.HighVoltageWarning;
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
        cfg.HighVoltageWarning    = NbxHighVolt.Value;
        cfg.UndervoltageThreshold = NbxUndervolt.Value;
        cfg.LowVoltageWarning     = NbxLowVolt.Value;
        cfg.OverTempWarning       = NbxTempWarn.Value;
        cfg.OverTempCutoff        = NbxTempCutoff.Value;
        cfg.MaxChargeCurrent      = NbxMaxCharge.Value;
        cfg.MaxDischargeCurrent   = NbxMaxDischarge.Value;
        cfg.MaxDod                = NbxMaxDod.Value;
        cfg.BalancingStartDelta   = NbxBalStart.Value / 1000.0;
        cfg.BalancingStopDelta    = NbxBalStop.Value  / 1000.0;

        ViewModel.SaveSettings();

        ShowFeedback(Lang.Fb_SettingsApplied, Lang.Fb_SettingsAppliedMsg, InfoBarSeverity.Success);
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        NbxCapacity.Value     = 20;
        NbxOvervolt.Value     = 4.20;
        NbxHighVolt.Value     = 4.10;
        NbxUndervolt.Value    = 2.80;
        NbxLowVolt.Value      = 3.00;
        NbxTempWarn.Value     = 60;
        NbxTempCutoff.Value   = 70;
        NbxMaxCharge.Value    = 20;
        NbxMaxDischarge.Value = 40;
        NbxMaxDod.Value       = 80;
        NbxBalStart.Value     = 20;
        NbxBalStop.Value      = 5;

        ShowFeedback(Lang.Fb_DefaultsRestored, Lang.Fb_DefaultsRestoredMsg, InfoBarSeverity.Informational);
    }

    private void ShowFeedback(string title, string message, InfoBarSeverity severity)
    {
        FeedbackBar.Visibility = Visibility.Visible;
        FeedbackBar.Title      = title;
        FeedbackBar.Message    = message;
        FeedbackBar.Severity   = severity;
        FeedbackBar.IsOpen     = true;
    }

    private void FeedbackBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        FeedbackBar.Visibility = Visibility.Collapsed;
    }
}
