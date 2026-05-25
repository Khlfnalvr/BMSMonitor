using System.Linq;
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
        HookBluetooth();
        RestoreLastBtDevice();
        SyncConnectButton();
        SyncBtButtons();
        LoadSerialParams();

        // Default to the Serial tab on first show. SelectorBar.SelectedItem
        // is null on open, which would leave both panels visible — pick the
        // first item explicitly.
        ConnTabsCtrl.SelectedItem = CtrlTabSerial;
        ApplyConnTab();

        // Poll frame counters at 2 Hz — cheap and avoids wiring an event
        // for every received frame. Started/stopped with page lifecycle.
        _counterTimer = DispatcherQueue.CreateTimer();
        _counterTimer.Interval = TimeSpan.FromMilliseconds(500);
        _counterTimer.Tick += (_, _) => UpdateCounters();

        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Lang.PropertyChanged += OnLanguageChanged;
        _counterTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Lang.PropertyChanged -= OnLanguageChanged;
        _counterTimer.Stop();
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Bindings.Update();
        RefreshChannels();
        SyncConnectButton();
        SyncBtButtons();
        AutoConnectStatusText.Text = Lang.Ctrl_AutoConnectStatus;
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
        ConnStatusText.Text = connected
            ? Lang.Format("Serial_StatusConnected", ViewModel.Serial.ChannelName, ViewModel.Serial.Bitrate)
            : Lang.Ctrl_NotConnected;

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
        // Sum frame counters across whichever transport is active. Either
        // service zeroes its own counter on (re)connect, so this stays
        // accurate per-session regardless of which one is in use.
        int frames = ViewModel.Serial.FramesReceived + ViewModel.Bluetooth.FramesReceived;
        int errors = ViewModel.Serial.ParseErrors    + ViewModel.Bluetooth.ParseErrors;
        FramesReceivedText.Text = frames.ToString("N0");
        ParseErrorsText.Text    = errors.ToString("N0");
    }

    // ── Connection tab switch ─────────────────────────────────────────────
    private void ConnTabsCtrl_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        ApplyConnTab();
    }

    private void ApplyConnTab()
    {
        if (SerialPanel is null || BluetoothPanel is null) return;
        bool serial = ConnTabsCtrl.SelectedItem == CtrlTabSerial || ConnTabsCtrl.SelectedItem is null;

        // Opacity-toggle (instead of Visibility) keeps the hidden panel
        // in the layout tree so the shared Grid cell stays sized to the
        // taller (Serial) panel. IsHitTestVisible blocks pointer input on
        // the invisible side. (StackPanel/Panel doesn't expose IsEnabled —
        // that's on Control, so we can't disable the whole subtree here;
        // pointer hit-testing is enough to make the hidden panel inert.)
        SerialPanel.Opacity          = serial ? 1 : 0;
        SerialPanel.IsHitTestVisible = serial;

        BluetoothPanel.Opacity          = serial ? 0 : 1;
        BluetoothPanel.IsHitTestVisible = !serial;
    }

    // ── Bluetooth controls ────────────────────────────────────────────────
    private void HookBluetooth()
    {
        ViewModel.Bluetooth.StatusChanged += msg => DispatcherQueue.TryEnqueue(() =>
        {
            BtStatusText.Text = msg;
            SyncBtButtons();
        });
        ViewModel.Bluetooth.ErrorOccurred += msg => DispatcherQueue.TryEnqueue(() =>
            ShowFeedback(Lang.Get("Alert_SerialErrorTitle"), msg, InfoBarSeverity.Error));
        ViewModel.Bluetooth.DevicesChanged += () => DispatcherQueue.TryEnqueue(RefreshBtDevices);
    }

    private void RestoreLastBtDevice()
    {
        var saved = AppSettingsService.Load();
        if (string.IsNullOrWhiteSpace(saved.LastBluetoothDeviceId)) return;

        // Seed the dropdown with the remembered device so the user can press
        // Connect without first running a scan. A live scan refreshes the
        // entry if the device shows up under a different display name.
        var entry = new BluetoothDeviceInfo(saved.LastBluetoothDeviceId,
            string.IsNullOrWhiteSpace(saved.LastBluetoothDeviceName)
                ? saved.LastBluetoothDeviceId
                : saved.LastBluetoothDeviceName);
        if (!ViewModel.Bluetooth.Devices.Any(d => d.DeviceId == entry.DeviceId))
            ViewModel.Bluetooth.Devices.Add(entry);
        RefreshBtDevices();
    }

    private void RefreshBtDevices()
    {
        var current = (ComboBtDevice.SelectedItem as ComboBoxItem)?.Tag as BluetoothDeviceInfo;
        ComboBtDevice.Items.Clear();
        foreach (var d in ViewModel.Bluetooth.Devices)
            ComboBtDevice.Items.Add(new ComboBoxItem { Content = d.DisplayName, Tag = d });

        var preferredId = current?.DeviceId
                          ?? AppSettingsService.Load().LastBluetoothDeviceId;

        if (!string.IsNullOrWhiteSpace(preferredId))
        {
            for (int i = 0; i < ComboBtDevice.Items.Count; i++)
            {
                if (ComboBtDevice.Items[i] is ComboBoxItem it &&
                    it.Tag is BluetoothDeviceInfo c &&
                    c.DeviceId == preferredId)
                {
                    ComboBtDevice.SelectedIndex = i;
                    return;
                }
            }
        }
        if (ComboBtDevice.SelectedIndex < 0 && ComboBtDevice.Items.Count > 0)
            ComboBtDevice.SelectedIndex = 0;
    }

    private void SyncBtButtons()
    {
        bool connected = ViewModel.Bluetooth.IsConnected;
        bool scanning  = ViewModel.Bluetooth.IsScanning;
        BtConnectBtn.Content = connected ? Lang.Ctrl_BtDisconnect : Lang.Ctrl_BtConnect;
        BtScanBtn.Content    = scanning  ? Lang.Ctrl_BtStopScan   : Lang.Ctrl_BtScan;
        ComboBtDevice.IsEnabled = !connected;
        BtStatusText.Text = connected
            ? Lang.Format("Bt_StatusConnected", ViewModel.Bluetooth.DeviceName)
            : Lang.Ctrl_NotConnected;
    }

    private void BtScanBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Bluetooth.IsScanning)
        {
            ViewModel.Bluetooth.StopScan();
        }
        else
        {
            ViewModel.Bluetooth.StartScan();
        }
        SyncBtButtons();
    }

    private async void BtConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Bluetooth.IsConnected)
        {
            ViewModel.Bluetooth.Disconnect();
            return;
        }

        var device = (ComboBtDevice.SelectedItem as ComboBoxItem)?.Tag as BluetoothDeviceInfo;
        if (device is null)
        {
            ShowFeedback(Lang.Get("Bt_FbSelect"), Lang.Get("Bt_FbSelectMsg"), InfoBarSeverity.Warning);
            return;
        }

        // Pause the USB scanner — once BT is the active source the AutoConnect
        // poll would otherwise keep hunting for COM ports in the background.
        ViewModel.AutoConnect.SuspendReconnect();
        // If a USB session was already up, drop it so the source label stays honest.
        if (ViewModel.Serial.IsConnected) ViewModel.Serial.Disconnect();

        bool ok = await ViewModel.Bluetooth.ConnectAsync(device);
        if (ok)
        {
            var settings = AppSettingsService.Load();
            settings.LastBluetoothDeviceId   = device.DeviceId;
            settings.LastBluetoothDeviceName = device.DisplayName;
            AppSettingsService.Save(settings);
        }
        SyncBtButtons();
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
