using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using BMSMonitor.Services;
using BMSMonitor.ViewModels;

namespace BMSMonitor.Views;

public sealed partial class ControlPanelPage : Page
{
    private MainViewModel ViewModel => App.ViewModel;
    private LocalizationManager Lang => App.Lang;

    private bool _langComboInitializing;

    public ControlPanelPage()
    {
        InitializeComponent();
        LoadConfig();
        RefreshPorts();
        HookSerial();
        SyncConnectButton();
        InitLangCombo();
    }

    // ── Language selector ─────────────────────────────────────────────────
    private void InitLangCombo()
    {
        _langComboInitializing = true;
        int idx = Array.IndexOf(LocalizationManager.SupportedLanguages, Lang.CurrentLanguage);
        LangCombo.SelectedIndex = idx >= 0 ? idx : 2; // default English
        _langComboInitializing = false;
    }

    private void LangCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_langComboInitializing) return;
        if (LangCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            Lang.CurrentLanguage = tag;
            // Re-sync button text that is set in code-behind
            SyncConnectButton();
        }
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
            // Suppress errors while auto-connect is scanning — expected when probing ports.
            if (!ViewModel.AutoConnect.IsSuspended) return;
            FeedbackBar.Title    = Lang.Fb_SerialError;
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
        ConnectBtn.Content  = connected ? Lang.Ctrl_Disconnect : Lang.Ctrl_Connect;
        ComboCOM.IsEnabled  = !connected;
        ComboBaud.IsEnabled = !connected;
        if (!connected) ConnStatusText.Text = Lang.Ctrl_NotConnected;
    }

    private void RefreshPorts()
    {
        var current = ComboCOM.SelectedItem as string;
        ComboCOM.Items.Clear();

        var ports = SerialPortService.GetAvailablePorts();
        foreach (var p in ports) ComboCOM.Items.Add(p);

        if (ports.Length == 0)
        {
            ComboCOM.PlaceholderText = Lang.Ctrl_PhNoPorts;
            return;
        }

        ComboCOM.PlaceholderText = Lang.Ctrl_PhScanning;
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
            ViewModel.AutoConnect.SuspendReconnect();
            ViewModel.Serial.Disconnect();
            return;
        }

        if (ComboCOM.SelectedItem is not string port)
        {
            FeedbackBar.Title    = Lang.Fb_SelectPort;
            FeedbackBar.Message  = Lang.Fb_SelectPortMsg;
            FeedbackBar.Severity = InfoBarSeverity.Warning;
            FeedbackBar.IsOpen   = true;
            return;
        }

        ViewModel.AutoConnect.BaudRate = GetSelectedBaud();
        ViewModel.AutoConnect.ResumeReconnect();
        ViewModel.Serial.Connect(port, GetSelectedBaud());
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
