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
        PopulateTransports();
        PopulateBitrates();
        RefreshChannels();
        HookCanBus();
        SyncConnectButton();
        LoadCanParams();

        // Poll frame counters at 2 Hz — cheap and avoids wiring an event
        // for every received frame. Started/stopped with page lifecycle.
        _counterTimer = DispatcherQueue.CreateTimer();
        _counterTimer.Interval = TimeSpan.FromMilliseconds(500);
        _counterTimer.Tick += (_, _) => UpdateCounters();

        Loaded   += (_, _) => _counterTimer.Start();
        Unloaded += (_, _) => _counterTimer.Stop();
    }

    // ── CAN bus controls ──────────────────────────────────────────────────
    private void HookCanBus()
    {
        ViewModel.CanBus.StatusChanged += msg => DispatcherQueue.TryEnqueue(() =>
        {
            ConnStatusText.Text = msg;
            SyncConnectButton();
        });
        ViewModel.CanBus.ErrorOccurred += msg => DispatcherQueue.TryEnqueue(() =>
        {
            // Suppress errors while auto-connect is scanning — expected when probing channels.
            if (!ViewModel.AutoConnect.IsSuspended) return;
            FeedbackBar.Title    = Lang.Fb_CanError;
            FeedbackBar.Message  = msg;
            FeedbackBar.Severity = InfoBarSeverity.Error;
            FeedbackBar.IsOpen   = true;
        });
        ViewModel.AutoConnect.Notification += msg => DispatcherQueue.TryEnqueue(() =>
            AutoConnectStatusText.Text = msg);
    }

    private void SyncConnectButton()
    {
        bool connected = ViewModel.CanBus.IsConnected;
        ConnectBtn.Content        = connected ? Lang.Ctrl_Disconnect : Lang.Ctrl_Connect;
        ComboCanChannel.IsEnabled = !connected;
        ComboCanBitrate.IsEnabled = !connected;
        if (!connected) ConnStatusText.Text = Lang.Ctrl_NotConnected;

        // Auto-connect toggle reflects the service's suspended flag.
        _initializingParams = true;
        ToggleAutoConnect.IsOn = !ViewModel.AutoConnect.IsSuspended;
        _initializingParams = false;
    }

    private void PopulateTransports()
    {
        _initializingParams = true;
        ComboTransport.Items.Clear();
        AddTransport(TransportMode.EspSerial, Lang.Ctrl_TransportEsp);
        AddTransport(TransportMode.Slcan,     Lang.Ctrl_TransportSlcan);
        AddTransport(TransportMode.Pcan,      Lang.Ctrl_TransportPcan);

        // Preselect the live mode
        for (int i = 0; i < ComboTransport.Items.Count; i++)
        {
            if (ComboTransport.Items[i] is ComboBoxItem item &&
                item.Tag is TransportMode tm && tm == ViewModel.CanBus.Mode)
            {
                ComboTransport.SelectedIndex = i;
                break;
            }
        }
        if (ComboTransport.SelectedIndex < 0) ComboTransport.SelectedIndex = 0;
        _initializingParams = false;

        void AddTransport(TransportMode mode, string label) =>
            ComboTransport.Items.Add(new ComboBoxItem { Content = label, Tag = mode });
    }

    private void ComboTransport_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingParams) return;
        if (ComboTransport.SelectedItem is not ComboBoxItem item ||
            item.Tag is not TransportMode mode) return;
        if (mode == ViewModel.CanBus.Mode) return;

        // Switching mode disconnects the current backend (the service does
        // this internally). Suspend auto-connect so the user has to click
        // Connect deliberately after picking the new mode.
        ViewModel.AutoConnect.SuspendReconnect();
        ViewModel.CanBus.Mode = mode;

        // The new backend exposes different channels & bitrates — repopulate.
        PopulateBitrates();
        RefreshChannels();
        SyncConnectButton();

        ViewModel.SaveSettings();
    }

    private void PopulateBitrates()
    {
        ComboCanBitrate.Items.Clear();
        foreach (var b in ViewModel.CanBus.Bitrates)
            ComboCanBitrate.Items.Add(new ComboBoxItem { Content = b.DisplayName, Tag = b });

        // Prefer the backend's default; fall back to the first entry.
        int defKbps = ViewModel.CanBus.DefaultBitrate;
        for (int i = 0; i < ComboCanBitrate.Items.Count; i++)
        {
            if (ComboCanBitrate.Items[i] is ComboBoxItem item &&
                item.Tag is CanBitrate br &&
                br.Kbps == defKbps)
            {
                ComboCanBitrate.SelectedIndex = i;
                break;
            }
        }
        if (ComboCanBitrate.SelectedIndex < 0 && ComboCanBitrate.Items.Count > 0)
            ComboCanBitrate.SelectedIndex = 0;
    }

    private void RefreshChannels()
    {
        var current = (ComboCanChannel.SelectedItem as ComboBoxItem)?.Tag as CanChannel;
        ComboCanChannel.Items.Clear();

        if (!ViewModel.CanBus.IsDriverAvailable)
        {
            ComboCanChannel.PlaceholderText = Lang.Ctrl_PhNoDriver;
            return;
        }

        foreach (var ch in ViewModel.CanBus.Channels)
            ComboCanChannel.Items.Add(new ComboBoxItem { Content = ch.DisplayName, Tag = ch });

        ComboCanChannel.PlaceholderText = Lang.Ctrl_PhScanning;

        if (current is not null)
        {
            for (int i = 0; i < ComboCanChannel.Items.Count; i++)
            {
                if (ComboCanChannel.Items[i] is ComboBoxItem it &&
                    it.Tag is CanChannel c &&
                    string.Equals(c.PortName, current.PortName, StringComparison.OrdinalIgnoreCase))
                {
                    ComboCanChannel.SelectedIndex = i;
                    return;
                }
            }
        }
        if (ComboCanChannel.Items.Count > 0)
            ComboCanChannel.SelectedIndex = 0;
    }

    private void RefreshChannels_Click(object sender, RoutedEventArgs e) => RefreshChannels();

    private CanBitrate? GetSelectedBitrate() =>
        (ComboCanBitrate.SelectedItem as ComboBoxItem)?.Tag as CanBitrate;

    private CanChannel? GetSelectedChannel() =>
        (ComboCanChannel.SelectedItem as ComboBoxItem)?.Tag as CanChannel;

    private void ConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CanBus.IsConnected)
        {
            ViewModel.AutoConnect.SuspendReconnect();
            ViewModel.CanBus.Disconnect();
            return;
        }

        var channel = GetSelectedChannel();
        var bitrate = GetSelectedBitrate();
        if (channel is null || bitrate is null)
        {
            FeedbackBar.Title    = Lang.Fb_SelectChannel;
            FeedbackBar.Message  = Lang.Fb_SelectChannelMsg;
            FeedbackBar.Severity = InfoBarSeverity.Warning;
            FeedbackBar.IsOpen   = true;
            return;
        }

        ViewModel.AutoConnect.BitrateKbps = bitrate.Kbps;
        ViewModel.AutoConnect.ResumeReconnect();
        ViewModel.CanBus.Connect(channel, bitrate);
    }

    // ── CAN bus parameters ────────────────────────────────────────────────
    private void LoadCanParams()
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
        FramesReceivedText.Text = ViewModel.CanBus.FramesReceived.ToString("N0");
        ParseErrorsText.Text    = ViewModel.CanBus.ParseErrors.ToString("N0");
    }

    // ── Settings ──────────────────────────────────────────────────────────
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

        ViewModel.SaveSettings();

        FeedbackBar.Title    = Lang.Fb_SettingsApplied;
        FeedbackBar.Message  = Lang.Fb_SettingsAppliedMsg;
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

        FeedbackBar.Title    = Lang.Fb_DefaultsRestored;
        FeedbackBar.Message  = Lang.Fb_DefaultsRestoredMsg;
        FeedbackBar.Severity = InfoBarSeverity.Informational;
        FeedbackBar.IsOpen   = true;
    }
}
