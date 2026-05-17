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

    private void PopulateBitrates()
    {
        ComboCanBitrate.Items.Clear();
        foreach (var b in CanBusService.Bitrates)
            ComboCanBitrate.Items.Add(new ComboBoxItem { Content = b.DisplayName, Tag = b });

        // Default to 500 kbit/s — typical BMS rate
        for (int i = 0; i < ComboCanBitrate.Items.Count; i++)
        {
            if (ComboCanBitrate.Items[i] is ComboBoxItem item &&
                item.Tag is CanBusService.CanBitrate br &&
                br.Kbps == CanBusService.DefaultBitrate)
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
        var current = (ComboCanChannel.SelectedItem as ComboBoxItem)?.Tag as CanBusService.CanChannel;
        ComboCanChannel.Items.Clear();

        if (!CanBusService.IsDriverAvailable())
        {
            ComboCanChannel.PlaceholderText = Lang.Ctrl_PhNoDriver;
            return;
        }

        foreach (var ch in CanBusService.Channels)
            ComboCanChannel.Items.Add(new ComboBoxItem { Content = ch.DisplayName, Tag = ch });

        ComboCanChannel.PlaceholderText = Lang.Ctrl_PhScanning;

        if (current is not null)
        {
            for (int i = 0; i < ComboCanChannel.Items.Count; i++)
            {
                if (ComboCanChannel.Items[i] is ComboBoxItem it &&
                    it.Tag is CanBusService.CanChannel c && c.Handle == current.Handle)
                {
                    ComboCanChannel.SelectedIndex = i;
                    return;
                }
            }
        }
        ComboCanChannel.SelectedIndex = 0;
    }

    private void RefreshChannels_Click(object sender, RoutedEventArgs e) => RefreshChannels();

    private (ushort btr, int kbps) GetSelectedBitrate()
    {
        if (ComboCanBitrate.SelectedItem is ComboBoxItem item &&
            item.Tag is CanBusService.CanBitrate br)
            return (br.Btr, br.Kbps);
        return (CanBusService.DefaultBitrateCode, CanBusService.DefaultBitrate);
    }

    private CanBusService.CanChannel? GetSelectedChannel()
    {
        if (ComboCanChannel.SelectedItem is ComboBoxItem item &&
            item.Tag is CanBusService.CanChannel ch)
            return ch;
        return null;
    }

    private void ConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CanBus.IsConnected)
        {
            ViewModel.AutoConnect.SuspendReconnect();
            ViewModel.CanBus.Disconnect();
            return;
        }

        var channel = GetSelectedChannel();
        if (channel is null)
        {
            FeedbackBar.Title    = Lang.Fb_SelectChannel;
            FeedbackBar.Message  = Lang.Fb_SelectChannelMsg;
            FeedbackBar.Severity = InfoBarSeverity.Warning;
            FeedbackBar.IsOpen   = true;
            return;
        }

        var (btr, kbps) = GetSelectedBitrate();
        ViewModel.AutoConnect.BitrateCode = btr;
        ViewModel.AutoConnect.BitrateKbps = kbps;
        ViewModel.AutoConnect.ResumeReconnect();
        ViewModel.CanBus.Connect(channel.Handle, btr, kbps, channel.DisplayName);
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
    }

    private void NbxReconnectInterval_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_initializingParams) return;
        if (!double.IsNaN(args.NewValue))
            ViewModel.AutoConnect.ReconnectIntervalSec = (int)args.NewValue;
    }

    private void NbxProbeTimeout_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_initializingParams) return;
        if (!double.IsNaN(args.NewValue))
            ViewModel.AutoConnect.ProbeTimeoutMs = (int)args.NewValue;
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
