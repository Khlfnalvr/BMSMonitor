using System.ComponentModel;
using System.IO;
using System.Globalization;

namespace BMSMonitor.Services;

/// <summary>
/// Singleton localization manager. Changing <see cref="CurrentLanguage"/> raises
/// PropertyChanged("") so every {x:Bind Lang.XYZ, Mode=OneWay} binding refreshes.
/// Supported: id · ms · en · nl · zh
/// </summary>
public sealed class LocalizationManager : INotifyPropertyChanged
{
    // ── Singleton ─────────────────────────────────────────────────────────
    public static LocalizationManager Instance { get; } = new();
    private LocalizationManager() { _currentLang = LoadOrDetect(); }

    // ── INotifyPropertyChanged ────────────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    // ── Language state ────────────────────────────────────────────────────
    private string _currentLang;

    public string CurrentLanguage
    {
        get => _currentLang;
        set
        {
            if (_currentLang == value) return;
            _currentLang = value;
            Save(value);
            Notify();
        }
    }

    public static readonly string[] SupportedLanguages = ["id", "ms", "en", "nl", "zh"];
    public static readonly string[] LanguageLabels     = ["Indonesia", "Malay", "English", "Nederlands", "中文"];

    // ── Persistence ───────────────────────────────────────────────────────
    private static readonly string _settingsFile =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BMSMonitor", "language.txt");

    private static string LoadOrDetect()
    {
        try
        {
            if (File.Exists(_settingsFile))
            {
                var saved = File.ReadAllText(_settingsFile).Trim();
                if (Array.IndexOf(SupportedLanguages, saved) >= 0) return saved;
            }
        }
        catch { /* ignore */ }

        // Auto-detect from system locale
        var culture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return culture switch
        {
            "id" => "id",
            "ms" => "ms",
            "nl" => "nl",
            "zh" => "zh",
            _    => "en",
        };
    }

    private static void Save(string lang)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
            File.WriteAllText(_settingsFile, lang);
        }
        catch { /* ignore */ }
    }

    // ── String lookup ─────────────────────────────────────────────────────
    private string T(string key)
    {
        if (_strings.TryGetValue(_currentLang, out var d) && d.TryGetValue(key, out var v)) return v;
        if (_strings.TryGetValue("en",         out var e) && e.TryGetValue(key, out var f)) return f;
        return key;
    }

    // ── Navigation ────────────────────────────────────────────────────────
    public string Nav_Dashboard    => T(nameof(Nav_Dashboard));
    public string Nav_CellView     => T(nameof(Nav_CellView));
    public string Nav_ControlPanel => T(nameof(Nav_ControlPanel));
    public string Nav_Logging      => T(nameof(Nav_Logging));
    public string Nav_Playback     => T(nameof(Nav_Playback));

    // ── Theme toggle ──────────────────────────────────────────────────────
    public string Ui_Dark           => T(nameof(Ui_Dark));
    public string Ui_Light          => T(nameof(Ui_Light));
    public string Ui_SwitchToLight  => T(nameof(Ui_SwitchToLight));
    public string Ui_SwitchToDark   => T(nameof(Ui_SwitchToDark));
    public string Ui_ChangeLanguage => T(nameof(Ui_ChangeLanguage));

    // ── Caption-bar CAN picker ────────────────────────────────────────────
    public string Ui_CanConnection  => T(nameof(Ui_CanConnection));
    public string Ui_CanQuickAccess => T(nameof(Ui_CanQuickAccess));

    // ── Common ────────────────────────────────────────────────────────────
    public string Com_Min    => T(nameof(Com_Min));
    public string Com_Max    => T(nameof(Com_Max));
    public string Com_Avg    => T(nameof(Com_Avg));
    public string Com_Delta  => T(nameof(Com_Delta));
    public string Com_Status => T(nameof(Com_Status));

    // ── Dashboard ─────────────────────────────────────────────────────────
    public string Dash_SecPackOverview  => T(nameof(Dash_SecPackOverview));
    public string Dash_PackVoltage      => T(nameof(Dash_PackVoltage));
    public string Dash_PackNominal      => T(nameof(Dash_PackNominal));
    public string Dash_StateOfCharge    => T(nameof(Dash_StateOfCharge));
    public string Dash_Remaining        => T(nameof(Dash_Remaining));
    public string Dash_RemainingSub     => T(nameof(Dash_RemainingSub));
    public string Dash_Current          => T(nameof(Dash_Current));
    public string Dash_CurrentSub       => T(nameof(Dash_CurrentSub));
    public string Dash_PackConfig       => T(nameof(Dash_PackConfig));
    public string Dash_SecSocHistory    => T(nameof(Dash_SecSocHistory));
    public string Dash_TimeAgo          => T(nameof(Dash_TimeAgo));
    public string Dash_Now              => T(nameof(Dash_Now));
    public string Dash_NowArrow         => T(nameof(Dash_NowArrow));
    public string Dash_SecViHistory     => T(nameof(Dash_SecViHistory));
    public string Dash_SaveChart        => T(nameof(Dash_SaveChart));
    public string Dash_SecTempHistory   => T(nameof(Dash_SecTempHistory));
    public string Dash_TempC            => T(nameof(Dash_TempC));
    public string Dash_TempF            => T(nameof(Dash_TempF));
    public string Dash_VoltageV         => T(nameof(Dash_VoltageV));
    public string Dash_CurrentA         => T(nameof(Dash_CurrentA));
    public string Dash_SecCellSummary   => T(nameof(Dash_SecCellSummary));
    public string Dash_TempSensors      => T(nameof(Dash_TempSensors));
    public string Dash_BalancingStatus  => T(nameof(Dash_BalancingStatus));
    public string Dash_ActiveCells      => T(nameof(Dash_ActiveCells));
    public string Dash_CellDelta        => T(nameof(Dash_CellDelta));
    public string Dash_Method           => T(nameof(Dash_Method));
    public string Dash_ActiveMethod     => T(nameof(Dash_ActiveMethod));

    // ── Cell View ─────────────────────────────────────────────────────────
    public string Cell_SecVoltageSummary => T(nameof(Cell_SecVoltageSummary));
    public string Cell_SecCellGrid       => T(nameof(Cell_SecCellGrid));
    public string Cell_DeltaLabel        => T(nameof(Cell_DeltaLabel));
    public string Cell_Normal            => T(nameof(Cell_Normal));
    public string Cell_Low               => T(nameof(Cell_Low));
    public string Cell_Undervoltage      => T(nameof(Cell_Undervoltage));
    public string Cell_Overvoltage       => T(nameof(Cell_Overvoltage));
    public string Cell_Balancing         => T(nameof(Cell_Balancing));
    public string Cell_SecNtcReadings    => T(nameof(Cell_SecNtcReadings));
    public string Cell_Thresholds        => T(nameof(Cell_Thresholds));
    public string Cell_ThreshWarn        => T(nameof(Cell_ThreshWarn));
    public string Cell_ThreshCutoff      => T(nameof(Cell_ThreshCutoff));
    public string Cell_Legend            => T(nameof(Cell_Legend));
    public string Cell_NormalDesc        => T(nameof(Cell_NormalDesc));
    public string Cell_WarnDesc          => T(nameof(Cell_WarnDesc));
    public string Cell_CutoffDesc        => T(nameof(Cell_CutoffDesc));

    // ── Control Panel ─────────────────────────────────────────────────────
    public string Ctrl_SecCan          => T(nameof(Ctrl_SecCan));
    public string Ctrl_CanChannel            => T(nameof(Ctrl_CanChannel));
    public string Ctrl_PhScanning         => T(nameof(Ctrl_PhScanning));
    public string Ctrl_PhNoPorts          => T(nameof(Ctrl_PhNoPorts));
    public string Ctrl_PhNoDriver         => T(nameof(Ctrl_PhNoDriver));
    public string Ctrl_Refresh            => T(nameof(Ctrl_Refresh));
    public string Ctrl_Connect            => T(nameof(Ctrl_Connect));
    public string Ctrl_Disconnect         => T(nameof(Ctrl_Disconnect));
    public string Ctrl_CanBitrate           => T(nameof(Ctrl_CanBitrate));
    public string Ctrl_ConnStatus         => T(nameof(Ctrl_ConnStatus));
    public string Ctrl_NotConnected       => T(nameof(Ctrl_NotConnected));
    public string Ctrl_AutoConnectStatus  => T(nameof(Ctrl_AutoConnectStatus));
    public string Ctrl_SecCapacity        => T(nameof(Ctrl_SecCapacity));
    public string Ctrl_NominalCapacity    => T(nameof(Ctrl_NominalCapacity));
    public string Ctrl_CapacityHint       => T(nameof(Ctrl_CapacityHint));
    public string Ctrl_SecProtection      => T(nameof(Ctrl_SecProtection));
    public string Ctrl_OvervoltCutoff     => T(nameof(Ctrl_OvervoltCutoff));
    public string Ctrl_UnderVoltCutoff    => T(nameof(Ctrl_UnderVoltCutoff));
    public string Ctrl_LowVoltWarn        => T(nameof(Ctrl_LowVoltWarn));
    public string Ctrl_OverTempWarn       => T(nameof(Ctrl_OverTempWarn));
    public string Ctrl_OverTempCutoff     => T(nameof(Ctrl_OverTempCutoff));
    public string Ctrl_SecCurrentLimits   => T(nameof(Ctrl_SecCurrentLimits));
    public string Ctrl_MaxCharge          => T(nameof(Ctrl_MaxCharge));
    public string Ctrl_MaxDischarge       => T(nameof(Ctrl_MaxDischarge));
    public string Ctrl_MaxDod             => T(nameof(Ctrl_MaxDod));
    public string Ctrl_SecBalancing       => T(nameof(Ctrl_SecBalancing));
    public string Ctrl_StartDelta         => T(nameof(Ctrl_StartDelta));
    public string Ctrl_StopDelta          => T(nameof(Ctrl_StopDelta));
    public string Ctrl_ResetDefaults      => T(nameof(Ctrl_ResetDefaults));
    public string Ctrl_ApplySettings      => T(nameof(Ctrl_ApplySettings));

    // ── Control Panel — advanced CAN parameters ───────────────────────────
    public string Ctrl_SecCanAdvanced     => T(nameof(Ctrl_SecCanAdvanced));
    public string Ctrl_AutoConnect        => T(nameof(Ctrl_AutoConnect));
    public string Ctrl_AutoConnectHint    => T(nameof(Ctrl_AutoConnectHint));
    public string Ctrl_ReconnectInterval  => T(nameof(Ctrl_ReconnectInterval));
    public string Ctrl_ProbeTimeout       => T(nameof(Ctrl_ProbeTimeout));
    public string Ctrl_FramesReceived     => T(nameof(Ctrl_FramesReceived));
    public string Ctrl_ParseErrors        => T(nameof(Ctrl_ParseErrors));

    // ── Feedback messages ─────────────────────────────────────────────────
    public string Fb_CanError          => T(nameof(Fb_CanError));
    public string Fb_SelectChannel           => T(nameof(Fb_SelectChannel));
    public string Fb_SelectChannelMsg        => T(nameof(Fb_SelectChannelMsg));
    public string Fb_SettingsApplied      => T(nameof(Fb_SettingsApplied));
    public string Fb_SettingsAppliedMsg   => T(nameof(Fb_SettingsAppliedMsg));
    public string Fb_DefaultsRestored     => T(nameof(Fb_DefaultsRestored));
    public string Fb_DefaultsRestoredMsg  => T(nameof(Fb_DefaultsRestoredMsg));

    // ── Logging ───────────────────────────────────────────────────────────
    public string Log_SecStatus          => T(nameof(Log_SecStatus));
    public string Log_State              => T(nameof(Log_State));
    public string Log_Samples            => T(nameof(Log_Samples));
    public string Log_Duration           => T(nameof(Log_Duration));
    public string Log_SecFileSettings    => T(nameof(Log_SecFileSettings));
    public string Log_Folder             => T(nameof(Log_Folder));
    public string Log_Browse             => T(nameof(Log_Browse));
    public string Log_Format             => T(nameof(Log_Format));
    public string Log_Filename           => T(nameof(Log_Filename));
    public string Log_PhAutoFilename     => T(nameof(Log_PhAutoFilename));
    public string Log_SecColumnSettings  => T(nameof(Log_SecColumnSettings));
    public string Log_ColumnHint         => T(nameof(Log_ColumnHint));
    public string Log_SelectAll          => T(nameof(Log_SelectAll));
    public string Log_DeselectAll        => T(nameof(Log_DeselectAll));
    public string Log_ResetDefault       => T(nameof(Log_ResetDefault));
    public string Log_Group              => T(nameof(Log_Group));
    public string Log_CellVoltages       => T(nameof(Log_CellVoltages));
    public string Log_Balancing          => T(nameof(Log_Balancing));
    public string Log_Temperatures       => T(nameof(Log_Temperatures));
    public string Log_SecControls        => T(nameof(Log_SecControls));
    public string Log_ConnectHint        => T(nameof(Log_ConnectHint));
    public string Log_RecordingHint      => T(nameof(Log_RecordingHint));
    public string Log_ReadyHint          => T(nameof(Log_ReadyHint));
    public string Log_StartLogging       => T(nameof(Log_StartLogging));
    public string Log_StopLogging        => T(nameof(Log_StopLogging));
    public string Log_OpenFolder         => T(nameof(Log_OpenFolder));
    public string Log_SecDataFormat      => T(nameof(Log_SecDataFormat));
    public string Log_DataDesc1          => T(nameof(Log_DataDesc1));
    public string Log_DataDesc2          => T(nameof(Log_DataDesc2));
    public string Log_DataDesc3          => T(nameof(Log_DataDesc3));
    public string Log_SecLiveData        => T(nameof(Log_SecLiveData));
    public string Log_HdrTimestamp       => T(nameof(Log_HdrTimestamp));
    public string Log_HdrSoc             => T(nameof(Log_HdrSoc));
    public string Log_HdrPackV           => T(nameof(Log_HdrPackV));
    public string Log_HdrCurrentA        => T(nameof(Log_HdrCurrentA));
    public string Log_HdrStatus          => T(nameof(Log_HdrStatus));
    public string Log_HdrMinCell         => T(nameof(Log_HdrMinCell));
    public string Log_HdrMaxCell         => T(nameof(Log_HdrMaxCell));
    public string Log_HdrDeltaMv         => T(nameof(Log_HdrDeltaMv));
    public string Log_HdrBalCells        => T(nameof(Log_HdrBalCells));
    public string Log_NoData             => T(nameof(Log_NoData));
    public string Log_Idle               => T(nameof(Log_Idle));
    public string Log_Logging            => T(nameof(Log_Logging));

    // ── Playback ──────────────────────────────────────────────────────────
    public string Pb_SecLoadFile    => T(nameof(Pb_SecLoadFile));
    public string Pb_NoFileLoaded   => T(nameof(Pb_NoFileLoaded));
    public string Pb_Browse         => T(nameof(Pb_Browse));
    public string Pb_Unload         => T(nameof(Pb_Unload));
    public string Pb_LoadStatus     => T(nameof(Pb_LoadStatus));
    public string Pb_SecFileInfo    => T(nameof(Pb_SecFileInfo));
    public string Pb_Frames         => T(nameof(Pb_Frames));
    public string Pb_EstDuration    => T(nameof(Pb_EstDuration));
    public string Pb_PlaybackSpeed  => T(nameof(Pb_PlaybackSpeed));
    public string Pb_SecHowToUse    => T(nameof(Pb_SecHowToUse));
    public string Pb_HowToUse1      => T(nameof(Pb_HowToUse1));
    public string Pb_HowToUse2      => T(nameof(Pb_HowToUse2));

    // ══════════════════════════════════════════════════════════════════════
    // Translation table
    // ══════════════════════════════════════════════════════════════════════
    private static readonly Dictionary<string, Dictionary<string, string>> _strings = new()
    {
        // ── ENGLISH ──────────────────────────────────────────────────────
        ["en"] = new()
        {
            [nameof(Nav_Dashboard)]    = "Dashboard",
            [nameof(Nav_CellView)]     = "Cell View",
            [nameof(Nav_ControlPanel)] = "Control Panel",
            [nameof(Nav_Logging)]      = "Logging",
            [nameof(Nav_Playback)]     = "Playback",

            [nameof(Ui_Dark)]          = "DARK",
            [nameof(Ui_Light)]         = "LIGHT",
            [nameof(Ui_SwitchToLight)] = "Switch to Light mode",
            [nameof(Ui_SwitchToDark)]  = "Switch to Dark mode",
            [nameof(Ui_ChangeLanguage)] = "Change language",
            [nameof(Ui_CanConnection)]  = "CAN Connection",
            [nameof(Ui_CanQuickAccess)] = "Quick CAN bus access",

            [nameof(Com_Min)]    = "MIN",
            [nameof(Com_Max)]    = "MAX",
            [nameof(Com_Avg)]    = "AVG",
            [nameof(Com_Delta)]  = "DELTA",
            [nameof(Com_Status)] = "STATUS",

            [nameof(Dash_SecPackOverview)]  = "PACK OVERVIEW",
            [nameof(Dash_PackVoltage)]      = "PACK VOLTAGE",
            [nameof(Dash_PackNominal)]      = "72V nominal",
            [nameof(Dash_StateOfCharge)]    = "STATE OF CHARGE",
            [nameof(Dash_Remaining)]        = "REMAINING",
            [nameof(Dash_RemainingSub)]     = "based on SOC × capacity",
            [nameof(Dash_Current)]          = "CURRENT",
            [nameof(Dash_CurrentSub)]       = "+ = charging  − = discharging",
            [nameof(Dash_PackConfig)]       = "20S4P NMC",
            [nameof(Dash_SecSocHistory)]    = "SOC HISTORY",
            [nameof(Dash_TimeAgo)]          = "← 2 min ago",
            [nameof(Dash_Now)]              = "now",
            [nameof(Dash_NowArrow)]         = "now →",
            [nameof(Dash_SecViHistory)]     = "VOLTAGE / CURRENT HISTORY",
            [nameof(Dash_VoltageV)]         = "Voltage (V)",
            [nameof(Dash_CurrentA)]         = "Current (A)",
            [nameof(Dash_SecCellSummary)]   = "CELL VOLTAGE SUMMARY",
            [nameof(Dash_TempSensors)]      = "TEMPERATURE SENSORS",
            [nameof(Dash_BalancingStatus)]  = "BALANCING STATUS",
            [nameof(Dash_ActiveCells)]      = "Active Cells",
            [nameof(Dash_CellDelta)]        = "Cell Delta",
            [nameof(Dash_Method)]           = "Method",
            [nameof(Dash_ActiveMethod)]     = "Active (LTC8584)",
            [nameof(Dash_SaveChart)]        = "Save chart as PNG",
            [nameof(Dash_SecTempHistory)]   = "TEMPERATURE HISTORY",
            [nameof(Dash_TempC)]            = "°C",
            [nameof(Dash_TempF)]            = "°F",

            [nameof(Cell_SecVoltageSummary)] = "VOLTAGE SUMMARY",
            [nameof(Cell_SecCellGrid)]       = "CELL GRID — 20S4P",
            [nameof(Cell_DeltaLabel)]        = "Δ DELTA",
            [nameof(Cell_Normal)]            = "Normal",
            [nameof(Cell_Low)]               = "Low",
            [nameof(Cell_Undervoltage)]      = "Undervoltage",
            [nameof(Cell_Overvoltage)]       = "Overvoltage",
            [nameof(Cell_Balancing)]         = "Balancing",
            [nameof(Cell_SecNtcReadings)]    = "NTC THERMISTOR READINGS",
            [nameof(Cell_Thresholds)]        = "THRESHOLDS",
            [nameof(Cell_ThreshWarn)]        = "Warning :  60 °C",
            [nameof(Cell_ThreshCutoff)]      = "Cutoff  :  70 °C",
            [nameof(Cell_Legend)]            = "LEGEND",
            [nameof(Cell_NormalDesc)]        = "Normal  (below 60°C)",
            [nameof(Cell_WarnDesc)]          = "Warning (60 – 70°C)",
            [nameof(Cell_CutoffDesc)]        = "Cutoff  (above 70°C)",

            [nameof(Ctrl_SecCan)]            = "CAN BUS CONNECTION",
            [nameof(Ctrl_CanChannel)]        = "CAN Channel",
            [nameof(Ctrl_PhScanning)]        = "Scanning channels…",
            [nameof(Ctrl_PhNoPorts)]         = "No CAN channels detected",
            [nameof(Ctrl_PhNoDriver)]        = "PCAN-Basic driver not installed",
            [nameof(Ctrl_Refresh)]           = "Refresh",
            [nameof(Ctrl_Connect)]           = "Connect",
            [nameof(Ctrl_Disconnect)]        = "Disconnect",
            [nameof(Ctrl_CanBitrate)]        = "Bitrate",
            [nameof(Ctrl_ConnStatus)]        = "Status",
            [nameof(Ctrl_NotConnected)]      = "Not connected",
            [nameof(Ctrl_AutoConnectStatus)] = "Auto-connect active — waiting for BMS CAN frames…",
            [nameof(Ctrl_SecCapacity)]       = "BATTERY CAPACITY",
            [nameof(Ctrl_NominalCapacity)]   = "Nominal Capacity",
            [nameof(Ctrl_CapacityHint)]      = "Used to calculate remaining capacity (mAh) on dashboard.",
            [nameof(Ctrl_SecProtection)]     = "PROTECTION THRESHOLDS",
            [nameof(Ctrl_OvervoltCutoff)]    = "Overvoltage Cutoff",
            [nameof(Ctrl_UnderVoltCutoff)]   = "Undervoltage Cutoff",
            [nameof(Ctrl_LowVoltWarn)]       = "Low Voltage Warning",
            [nameof(Ctrl_OverTempWarn)]      = "Over-Temp Warning",
            [nameof(Ctrl_OverTempCutoff)]    = "Over-Temp Cutoff",
            [nameof(Ctrl_SecCurrentLimits)]  = "CURRENT LIMITS",
            [nameof(Ctrl_MaxCharge)]         = "Max Charge Current",
            [nameof(Ctrl_MaxDischarge)]      = "Max Discharge Current",
            [nameof(Ctrl_MaxDod)]            = "Max DoD",
            [nameof(Ctrl_SecBalancing)]      = "ACTIVE BALANCING (LTC8584)",
            [nameof(Ctrl_StartDelta)]        = "Start Delta",
            [nameof(Ctrl_StopDelta)]         = "Stop Delta",
            [nameof(Ctrl_ResetDefaults)]     = "Reset to Defaults",
            [nameof(Ctrl_ApplySettings)]     = "Apply Settings",

            [nameof(Ctrl_SecCanAdvanced)]    = "CAN BUS PARAMETERS",
            [nameof(Ctrl_AutoConnect)]       = "Auto-Connect",
            [nameof(Ctrl_AutoConnectHint)]   = "Automatically scan PCAN-USB channels and lock onto the first one that broadcasts BMS heartbeat frames.",
            [nameof(Ctrl_ReconnectInterval)] = "Reconnect Interval",
            [nameof(Ctrl_ProbeTimeout)]      = "Probe Timeout",
            [nameof(Ctrl_FramesReceived)]    = "Frames received",
            [nameof(Ctrl_ParseErrors)]       = "Parse errors",

            [nameof(Fb_CanError)]            = "CAN error",
            [nameof(Fb_SelectChannel)]       = "Select a channel",
            [nameof(Fb_SelectChannelMsg)]    = "Pick a CAN channel from the dropdown first.",
            [nameof(Fb_SettingsApplied)]     = "Settings applied",
            [nameof(Fb_SettingsAppliedMsg)]  = "New thresholds are active.",
            [nameof(Fb_DefaultsRestored)]    = "Defaults restored",
            [nameof(Fb_DefaultsRestoredMsg)] = "Values reset — click Apply to activate.",

            [nameof(Log_SecStatus)]         = "LOGGING STATUS",
            [nameof(Log_State)]             = "STATE",
            [nameof(Log_Samples)]           = "SAMPLES",
            [nameof(Log_Duration)]          = "DURATION",
            [nameof(Log_SecFileSettings)]   = "FILE SETTINGS",
            [nameof(Log_Folder)]            = "Folder",
            [nameof(Log_Browse)]            = "Browse…",
            [nameof(Log_Format)]            = "Format",
            [nameof(Log_Filename)]          = "Filename",
            [nameof(Log_PhAutoFilename)]    = "auto-generated if left blank",
            [nameof(Log_SecColumnSettings)] = "COLUMN SETTINGS",
            [nameof(Log_ColumnHint)]        = "Choose columns to log and drag ≡ to reorder. Locked while logging is active.",
            [nameof(Log_SelectAll)]         = "Select All",
            [nameof(Log_DeselectAll)]       = "Deselect All",
            [nameof(Log_ResetDefault)]      = "Reset Default",
            [nameof(Log_Group)]             = "Group:",
            [nameof(Log_CellVoltages)]      = "Cell Voltages",
            [nameof(Log_Balancing)]         = "Balancing",
            [nameof(Log_Temperatures)]      = "Temperatures",
            [nameof(Log_SecControls)]       = "CONTROLS",
            [nameof(Log_ConnectHint)]       = "Connect to the ESP32 first, then start logging.",
            [nameof(Log_RecordingHint)]     = "Recording in progress. Stop to close / write the file.",
            [nameof(Log_ReadyHint)]         = "Ready. Press Start Logging to begin recording.",
            [nameof(Log_StartLogging)]      = "Start Logging",
            [nameof(Log_StopLogging)]       = "Stop Logging",
            [nameof(Log_OpenFolder)]        = "Open Folder",
            [nameof(Log_SecDataFormat)]     = "DATA FORMAT",
            [nameof(Log_DataDesc1)]         = "Each row = one data frame received from the ESP32 (∼1 Hz).",
            [nameof(Log_DataDesc2)]         = "Fields: Timestamp · PackVoltage_V · SOC_pct · Current_A · Status · Cell1_V … Cell20_V · Bal1 … Bal20 · Temp1_C … Temp10_C",
            [nameof(Log_DataDesc3)]         = "CSV / TSV: streamed to disk every frame.  Excel / JSON: buffered in memory and written when you press Stop.",
            [nameof(Log_SecLiveData)]       = "LIVE DATA (LAST 20)",
            [nameof(Log_HdrTimestamp)]      = "TIMESTAMP",
            [nameof(Log_HdrSoc)]            = "SOC %",
            [nameof(Log_HdrPackV)]          = "PACK V",
            [nameof(Log_HdrCurrentA)]       = "CURRENT A",
            [nameof(Log_HdrStatus)]         = "STATUS",
            [nameof(Log_HdrMinCell)]        = "MIN CELL V",
            [nameof(Log_HdrMaxCell)]        = "MAX CELL V",
            [nameof(Log_HdrDeltaMv)]        = "DELTA mV",
            [nameof(Log_HdrBalCells)]       = "BAL CELLS",
            [nameof(Log_NoData)]            = "No data yet — connect the ESP32 to see the data stream.",
            [nameof(Log_Idle)]              = "Idle",
            [nameof(Log_Logging)]           = "Logging",

            [nameof(Pb_SecLoadFile)]   = "LOAD FILE",
            [nameof(Pb_NoFileLoaded)]  = "No file loaded",
            [nameof(Pb_Browse)]        = "Browse…",
            [nameof(Pb_Unload)]        = "Unload",
            [nameof(Pb_LoadStatus)]    = "Browse and open a BMS Monitor CSV log file (.csv).",
            [nameof(Pb_SecFileInfo)]   = "FILE INFO",
            [nameof(Pb_Frames)]        = "FRAMES",
            [nameof(Pb_EstDuration)]   = "ESTIMATED DURATION",
            [nameof(Pb_PlaybackSpeed)] = "PLAYBACK SPEED",
            [nameof(Pb_SecHowToUse)]   = "HOW TO USE",
            [nameof(Pb_HowToUse1)]     = "Browse and load a CSV file above, then use the playback bar that appears at the bottom of the window.",
            [nameof(Pb_HowToUse2)]     = "While playing, all pages (Dashboard, Cell View, etc.) update in real-time with the recorded data. Logging is paused automatically during playback. Click ✕ in the playback bar or press Unload to return to live mode.",
        },

        // ── INDONESIA ─────────────────────────────────────────────────────
        ["id"] = new()
        {
            [nameof(Nav_Dashboard)]    = "Dashboard",
            [nameof(Nav_CellView)]     = "Tampilan Sel",
            [nameof(Nav_ControlPanel)] = "Panel Kontrol",
            [nameof(Nav_Logging)]      = "Logging",
            [nameof(Nav_Playback)]     = "Putar Ulang",

            [nameof(Ui_Dark)]          = "GELAP",
            [nameof(Ui_Light)]         = "TERANG",
            [nameof(Ui_SwitchToLight)] = "Beralih ke Mode Terang",
            [nameof(Ui_SwitchToDark)]  = "Beralih ke Mode Gelap",
            [nameof(Ui_ChangeLanguage)] = "Ubah bahasa",
            [nameof(Ui_CanConnection)]  = "Koneksi CAN",
            [nameof(Ui_CanQuickAccess)] = "Akses cepat CAN bus",

            [nameof(Com_Min)]    = "MIN",
            [nameof(Com_Max)]    = "MAKS",
            [nameof(Com_Avg)]    = "RATA",
            [nameof(Com_Delta)]  = "DELTA",
            [nameof(Com_Status)] = "STATUS",

            [nameof(Dash_SecPackOverview)]  = "IKHTISAR PACK",
            [nameof(Dash_PackVoltage)]      = "TEGANGAN PACK",
            [nameof(Dash_PackNominal)]      = "72V nominal",
            [nameof(Dash_StateOfCharge)]    = "STATUS PENGISIAN",
            [nameof(Dash_Remaining)]        = "SISA",
            [nameof(Dash_RemainingSub)]     = "berdasarkan SOC × kapasitas",
            [nameof(Dash_Current)]          = "ARUS",
            [nameof(Dash_CurrentSub)]       = "+ = mengisi  − = mengosongkan",
            [nameof(Dash_PackConfig)]       = "20S4P NMC",
            [nameof(Dash_SecSocHistory)]    = "RIWAYAT SOC",
            [nameof(Dash_TimeAgo)]          = "← 2 mnt lalu",
            [nameof(Dash_Now)]              = "sekarang",
            [nameof(Dash_NowArrow)]         = "sekarang →",
            [nameof(Dash_SecViHistory)]     = "RIWAYAT TEGANGAN / ARUS",
            [nameof(Dash_VoltageV)]         = "Tegangan (V)",
            [nameof(Dash_CurrentA)]         = "Arus (A)",
            [nameof(Dash_SecCellSummary)]   = "RINGKASAN TEGANGAN SEL",
            [nameof(Dash_TempSensors)]      = "SENSOR SUHU",
            [nameof(Dash_BalancingStatus)]  = "STATUS PENYEIMBANGAN",
            [nameof(Dash_ActiveCells)]      = "Sel Aktif",
            [nameof(Dash_CellDelta)]        = "Delta Sel",
            [nameof(Dash_Method)]           = "Metode",
            [nameof(Dash_ActiveMethod)]     = "Aktif (LTC8584)",
            [nameof(Dash_SaveChart)]        = "Simpan grafik sebagai PNG",
            [nameof(Dash_SecTempHistory)]   = "RIWAYAT SUHU",
            [nameof(Dash_TempC)]            = "°C",
            [nameof(Dash_TempF)]            = "°F",

            [nameof(Cell_SecVoltageSummary)] = "RINGKASAN TEGANGAN",
            [nameof(Cell_SecCellGrid)]       = "GRID SEL — 20S4P",
            [nameof(Cell_DeltaLabel)]        = "Δ DELTA",
            [nameof(Cell_Normal)]            = "Normal",
            [nameof(Cell_Low)]               = "Rendah",
            [nameof(Cell_Undervoltage)]      = "Tegangan Rendah",
            [nameof(Cell_Overvoltage)]       = "Tegangan Tinggi",
            [nameof(Cell_Balancing)]         = "Penyeimbangan",
            [nameof(Cell_SecNtcReadings)]    = "PEMBACAAN TERMISTOR NTC",
            [nameof(Cell_Thresholds)]        = "AMBANG BATAS",
            [nameof(Cell_ThreshWarn)]        = "Peringatan :  60 °C",
            [nameof(Cell_ThreshCutoff)]      = "Pemutus  :  70 °C",
            [nameof(Cell_Legend)]            = "LEGENDA",
            [nameof(Cell_NormalDesc)]        = "Normal  (di bawah 60°C)",
            [nameof(Cell_WarnDesc)]          = "Peringatan (60 – 70°C)",
            [nameof(Cell_CutoffDesc)]        = "Pemutus  (di atas 70°C)",

            [nameof(Ctrl_SecCan)]            = "KONEKSI CAN BUS",
            [nameof(Ctrl_CanChannel)]        = "Channel CAN",
            [nameof(Ctrl_PhScanning)]        = "Memindai channel…",
            [nameof(Ctrl_PhNoPorts)]         = "Tidak ada channel CAN terdeteksi",
            [nameof(Ctrl_PhNoDriver)]        = "Driver PCAN-Basic belum terinstal",
            [nameof(Ctrl_Refresh)]           = "Perbarui",
            [nameof(Ctrl_Connect)]           = "Hubungkan",
            [nameof(Ctrl_Disconnect)]        = "Putuskan",
            [nameof(Ctrl_CanBitrate)]        = "Bitrate",
            [nameof(Ctrl_ConnStatus)]        = "Status",
            [nameof(Ctrl_NotConnected)]      = "Tidak terhubung",
            [nameof(Ctrl_AutoConnectStatus)] = "Auto-connect aktif — menunggu frame CAN BMS…",
            [nameof(Ctrl_SecCapacity)]       = "KAPASITAS BATERAI",
            [nameof(Ctrl_NominalCapacity)]   = "Kapasitas Nominal",
            [nameof(Ctrl_CapacityHint)]      = "Digunakan untuk menghitung kapasitas sisa (mAh) di dashboard.",
            [nameof(Ctrl_SecProtection)]     = "AMBANG PERLINDUNGAN",
            [nameof(Ctrl_OvervoltCutoff)]    = "Pemutus Tegangan Tinggi",
            [nameof(Ctrl_UnderVoltCutoff)]   = "Pemutus Tegangan Rendah",
            [nameof(Ctrl_LowVoltWarn)]       = "Peringatan Tegangan Rendah",
            [nameof(Ctrl_OverTempWarn)]      = "Peringatan Suhu Tinggi",
            [nameof(Ctrl_OverTempCutoff)]    = "Pemutus Suhu Tinggi",
            [nameof(Ctrl_SecCurrentLimits)]  = "BATAS ARUS",
            [nameof(Ctrl_MaxCharge)]         = "Arus Pengisian Maks",
            [nameof(Ctrl_MaxDischarge)]      = "Arus Pengosongan Maks",
            [nameof(Ctrl_MaxDod)]            = "DoD Maks",
            [nameof(Ctrl_SecBalancing)]      = "PENYEIMBANGAN AKTIF (LTC8584)",
            [nameof(Ctrl_StartDelta)]        = "Delta Mulai",
            [nameof(Ctrl_StopDelta)]         = "Delta Berhenti",
            [nameof(Ctrl_ResetDefaults)]     = "Reset ke Default",
            [nameof(Ctrl_ApplySettings)]     = "Terapkan Pengaturan",

            [nameof(Ctrl_SecCanAdvanced)]    = "PARAMETER CAN BUS",
            [nameof(Ctrl_AutoConnect)]       = "Auto-Hubungkan",
            [nameof(Ctrl_AutoConnectHint)]   = "Memindai channel PCAN-USB secara otomatis dan terhubung ke yang mengirim frame heartbeat BMS.",
            [nameof(Ctrl_ReconnectInterval)] = "Interval Pindai",
            [nameof(Ctrl_ProbeTimeout)]      = "Timeout Verifikasi",
            [nameof(Ctrl_FramesReceived)]    = "Frame diterima",
            [nameof(Ctrl_ParseErrors)]       = "Kesalahan parsing",

            [nameof(Fb_CanError)]            = "Kesalahan CAN",
            [nameof(Fb_SelectChannel)]       = "Pilih channel",
            [nameof(Fb_SelectChannelMsg)]    = "Pilih channel CAN dari dropdown terlebih dahulu.",
            [nameof(Fb_SettingsApplied)]     = "Pengaturan diterapkan",
            [nameof(Fb_SettingsAppliedMsg)]  = "Ambang batas baru aktif.",
            [nameof(Fb_DefaultsRestored)]    = "Default dipulihkan",
            [nameof(Fb_DefaultsRestoredMsg)] = "Nilai direset — klik Terapkan untuk mengaktifkan.",

            [nameof(Log_SecStatus)]         = "STATUS LOGGING",
            [nameof(Log_State)]             = "STATUS",
            [nameof(Log_Samples)]           = "SAMPEL",
            [nameof(Log_Duration)]          = "DURASI",
            [nameof(Log_SecFileSettings)]   = "PENGATURAN FILE",
            [nameof(Log_Folder)]            = "Folder",
            [nameof(Log_Browse)]            = "Telusuri…",
            [nameof(Log_Format)]            = "Format",
            [nameof(Log_Filename)]          = "Nama File",
            [nameof(Log_PhAutoFilename)]    = "dibuat otomatis jika kosong",
            [nameof(Log_SecColumnSettings)] = "PENGATURAN KOLOM",
            [nameof(Log_ColumnHint)]        = "Pilih kolom yang ingin di-log dan atur urutannya dengan drag ≡. Dikunci saat logging aktif.",
            [nameof(Log_SelectAll)]         = "Pilih Semua",
            [nameof(Log_DeselectAll)]       = "Hapus Semua",
            [nameof(Log_ResetDefault)]      = "Reset Default",
            [nameof(Log_Group)]             = "Grup:",
            [nameof(Log_CellVoltages)]      = "Cell Voltages",
            [nameof(Log_Balancing)]         = "Balancing",
            [nameof(Log_Temperatures)]      = "Temperatures",
            [nameof(Log_SecControls)]       = "KONTROL",
            [nameof(Log_ConnectHint)]       = "Hubungkan ke ESP32 terlebih dahulu, lalu mulai logging.",
            [nameof(Log_RecordingHint)]     = "Perekaman sedang berlangsung. Hentikan untuk menutup / menulis file.",
            [nameof(Log_ReadyHint)]         = "Siap. Tekan Mulai Logging untuk mulai merekam.",
            [nameof(Log_StartLogging)]      = "Mulai Logging",
            [nameof(Log_StopLogging)]       = "Hentikan Logging",
            [nameof(Log_OpenFolder)]        = "Buka Folder",
            [nameof(Log_SecDataFormat)]     = "FORMAT DATA",
            [nameof(Log_DataDesc1)]         = "Setiap baris = satu frame data yang diterima dari ESP32 (∼1 Hz).",
            [nameof(Log_DataDesc2)]         = "Kolom: Timestamp · PackVoltage_V · SOC_pct · Current_A · Status · Cell1_V … Cell20_V · Bal1 … Bal20 · Temp1_C … Temp10_C",
            [nameof(Log_DataDesc3)]         = "CSV / TSV: langsung ditulis setiap frame.  Excel / JSON: disimpan di memori dan ditulis saat Anda menekan Stop.",
            [nameof(Log_SecLiveData)]       = "DATA LANGSUNG (20 TERBARU)",
            [nameof(Log_HdrTimestamp)]      = "TIMESTAMP",
            [nameof(Log_HdrSoc)]            = "SOC %",
            [nameof(Log_HdrPackV)]          = "PACK V",
            [nameof(Log_HdrCurrentA)]       = "ARUS A",
            [nameof(Log_HdrStatus)]         = "STATUS",
            [nameof(Log_HdrMinCell)]        = "MIN SEL V",
            [nameof(Log_HdrMaxCell)]        = "MAKS SEL V",
            [nameof(Log_HdrDeltaMv)]        = "DELTA mV",
            [nameof(Log_HdrBalCells)]       = "BAL CELLS",
            [nameof(Log_NoData)]            = "Belum ada data — hubungkan ESP32 untuk melihat data stream.",
            [nameof(Log_Idle)]              = "Diam",
            [nameof(Log_Logging)]           = "Merekam",

            [nameof(Pb_SecLoadFile)]   = "MUAT FILE",
            [nameof(Pb_NoFileLoaded)]  = "Tidak ada file yang dimuat",
            [nameof(Pb_Browse)]        = "Telusuri…",
            [nameof(Pb_Unload)]        = "Hapus",
            [nameof(Pb_LoadStatus)]    = "Telusuri dan buka file log CSV BMS Monitor (.csv).",
            [nameof(Pb_SecFileInfo)]   = "INFO FILE",
            [nameof(Pb_Frames)]        = "BINGKAI",
            [nameof(Pb_EstDuration)]   = "PERKIRAAN DURASI",
            [nameof(Pb_PlaybackSpeed)] = "KECEPATAN PUTAR",
            [nameof(Pb_SecHowToUse)]   = "CARA MENGGUNAKAN",
            [nameof(Pb_HowToUse1)]     = "Telusuri dan muat file CSV di atas, lalu gunakan bilah putar ulang yang muncul di bagian bawah jendela.",
            [nameof(Pb_HowToUse2)]     = "Saat diputar, semua halaman (Dashboard, Tampilan Sel, dll.) diperbarui secara real-time. Logging dijeda otomatis saat playback. Klik ✕ di bilah putar ulang atau tekan Hapus untuk kembali ke mode live.",
        },

        // ── MALAY ─────────────────────────────────────────────────────────
        ["ms"] = new()
        {
            [nameof(Nav_Dashboard)]    = "Dashboard",
            [nameof(Nav_CellView)]     = "Paparan Sel",
            [nameof(Nav_ControlPanel)] = "Panel Kawalan",
            [nameof(Nav_Logging)]      = "Pembalakan",
            [nameof(Nav_Playback)]     = "Main Semula",

            [nameof(Ui_Dark)]          = "GELAP",
            [nameof(Ui_Light)]         = "CERAH",
            [nameof(Ui_SwitchToLight)] = "Tukar ke Mod Cerah",
            [nameof(Ui_SwitchToDark)]  = "Tukar ke Mod Gelap",
            [nameof(Ui_ChangeLanguage)] = "Tukar bahasa",
            [nameof(Ui_CanConnection)]  = "Sambungan CAN",
            [nameof(Ui_CanQuickAccess)] = "Akses pantas CAN bus",

            [nameof(Com_Min)]    = "MIN",
            [nameof(Com_Max)]    = "MAKS",
            [nameof(Com_Avg)]    = "PURATA",
            [nameof(Com_Delta)]  = "DELTA",
            [nameof(Com_Status)] = "STATUS",

            [nameof(Dash_SecPackOverview)]  = "RINGKASAN PACK",
            [nameof(Dash_PackVoltage)]      = "VOLTAN PACK",
            [nameof(Dash_PackNominal)]      = "72V nominal",
            [nameof(Dash_StateOfCharge)]    = "STATUS CAS",
            [nameof(Dash_Remaining)]        = "SISA",
            [nameof(Dash_RemainingSub)]     = "berdasarkan SOC × kapasiti",
            [nameof(Dash_Current)]          = "ARUS",
            [nameof(Dash_CurrentSub)]       = "+ = mengecas  − = nyahcas",
            [nameof(Dash_PackConfig)]       = "20S4P NMC",
            [nameof(Dash_SecSocHistory)]    = "SEJARAH SOC",
            [nameof(Dash_TimeAgo)]          = "← 2 min lalu",
            [nameof(Dash_Now)]              = "sekarang",
            [nameof(Dash_NowArrow)]         = "sekarang →",
            [nameof(Dash_SecViHistory)]     = "SEJARAH VOLTAN / ARUS",
            [nameof(Dash_VoltageV)]         = "Voltan (V)",
            [nameof(Dash_CurrentA)]         = "Arus (A)",
            [nameof(Dash_SecCellSummary)]   = "RINGKASAN VOLTAN SEL",
            [nameof(Dash_TempSensors)]      = "PENDERIA SUHU",
            [nameof(Dash_BalancingStatus)]  = "STATUS IMBANGAN",
            [nameof(Dash_ActiveCells)]      = "Sel Aktif",
            [nameof(Dash_CellDelta)]        = "Delta Sel",
            [nameof(Dash_Method)]           = "Kaedah",
            [nameof(Dash_ActiveMethod)]     = "Aktif (LTC8584)",
            [nameof(Dash_SaveChart)]        = "Simpan graf sebagai PNG",
            [nameof(Dash_SecTempHistory)]   = "SEJARAH SUHU",
            [nameof(Dash_TempC)]            = "°C",
            [nameof(Dash_TempF)]            = "°F",

            [nameof(Cell_SecVoltageSummary)] = "RINGKASAN VOLTAN",
            [nameof(Cell_SecCellGrid)]       = "GRID SEL — 20S4P",
            [nameof(Cell_DeltaLabel)]        = "Δ DELTA",
            [nameof(Cell_Normal)]            = "Normal",
            [nameof(Cell_Low)]               = "Rendah",
            [nameof(Cell_Undervoltage)]      = "Voltan Rendah",
            [nameof(Cell_Overvoltage)]       = "Voltan Tinggi",
            [nameof(Cell_Balancing)]         = "Pengimbangan",
            [nameof(Cell_SecNtcReadings)]    = "BACAAN TERMISTOR NTC",
            [nameof(Cell_Thresholds)]        = "AMBANG",
            [nameof(Cell_ThreshWarn)]        = "Amaran :  60 °C",
            [nameof(Cell_ThreshCutoff)]      = "Pemutus  :  70 °C",
            [nameof(Cell_Legend)]            = "LEGENDA",
            [nameof(Cell_NormalDesc)]        = "Normal  (di bawah 60°C)",
            [nameof(Cell_WarnDesc)]          = "Amaran (60 – 70°C)",
            [nameof(Cell_CutoffDesc)]        = "Pemutus  (di atas 70°C)",

            [nameof(Ctrl_SecCan)]            = "SAMBUNGAN CAN BUS",
            [nameof(Ctrl_CanChannel)]        = "Saluran CAN",
            [nameof(Ctrl_PhScanning)]        = "Mengimbas saluran…",
            [nameof(Ctrl_PhNoPorts)]         = "Tiada saluran CAN dikesan",
            [nameof(Ctrl_PhNoDriver)]        = "Pemacu PCAN-Basic belum dipasang",
            [nameof(Ctrl_Refresh)]           = "Muat Semula",
            [nameof(Ctrl_Connect)]           = "Sambungkan",
            [nameof(Ctrl_Disconnect)]        = "Putuskan",
            [nameof(Ctrl_CanBitrate)]        = "Kadar Bit",
            [nameof(Ctrl_ConnStatus)]        = "Status",
            [nameof(Ctrl_NotConnected)]      = "Tidak disambungkan",
            [nameof(Ctrl_AutoConnectStatus)] = "Auto-sambung aktif — menunggu bingkai CAN BMS…",
            [nameof(Ctrl_SecCapacity)]       = "KAPASITI BATERI",
            [nameof(Ctrl_NominalCapacity)]   = "Kapasiti Nominal",
            [nameof(Ctrl_CapacityHint)]      = "Digunakan untuk mengira kapasiti sisa (mAh) di papan pemuka.",
            [nameof(Ctrl_SecProtection)]     = "AMBANG PERLINDUNGAN",
            [nameof(Ctrl_OvervoltCutoff)]    = "Pemutus Voltan Tinggi",
            [nameof(Ctrl_UnderVoltCutoff)]   = "Pemutus Voltan Rendah",
            [nameof(Ctrl_LowVoltWarn)]       = "Amaran Voltan Rendah",
            [nameof(Ctrl_OverTempWarn)]      = "Amaran Suhu Tinggi",
            [nameof(Ctrl_OverTempCutoff)]    = "Pemutus Suhu Tinggi",
            [nameof(Ctrl_SecCurrentLimits)]  = "HAD ARUS",
            [nameof(Ctrl_MaxCharge)]         = "Arus Pengecasan Maks",
            [nameof(Ctrl_MaxDischarge)]      = "Arus Nyahcas Maks",
            [nameof(Ctrl_MaxDod)]            = "DoD Maks",
            [nameof(Ctrl_SecBalancing)]      = "PENGIMBANGAN AKTIF (LTC8584)",
            [nameof(Ctrl_StartDelta)]        = "Delta Mula",
            [nameof(Ctrl_StopDelta)]         = "Delta Berhenti",
            [nameof(Ctrl_ResetDefaults)]     = "Tetapkan Semula",
            [nameof(Ctrl_ApplySettings)]     = "Guna Tetapan",

            [nameof(Ctrl_SecCanAdvanced)]    = "PARAMETER CAN BUS",
            [nameof(Ctrl_AutoConnect)]       = "Auto-Sambung",
            [nameof(Ctrl_AutoConnectHint)]   = "Mengimbas saluran PCAN-USB secara automatik dan menyambung ke saluran yang menghantar bingkai heartbeat BMS.",
            [nameof(Ctrl_ReconnectInterval)] = "Selang Imbasan",
            [nameof(Ctrl_ProbeTimeout)]      = "Tamat Masa Pemeriksaan",
            [nameof(Ctrl_FramesReceived)]    = "Bingkai diterima",
            [nameof(Ctrl_ParseErrors)]       = "Ralat penghuraian",

            [nameof(Fb_CanError)]            = "Ralat CAN",
            [nameof(Fb_SelectChannel)]       = "Pilih saluran",
            [nameof(Fb_SelectChannelMsg)]    = "Pilih saluran CAN dari senarai juntai bawah terlebih dahulu.",
            [nameof(Fb_SettingsApplied)]     = "Tetapan digunakan",
            [nameof(Fb_SettingsAppliedMsg)]  = "Ambang baru adalah aktif.",
            [nameof(Fb_DefaultsRestored)]    = "Tetapan asal dipulihkan",
            [nameof(Fb_DefaultsRestoredMsg)] = "Nilai diset semula — klik Guna untuk mengaktifkan.",

            [nameof(Log_SecStatus)]         = "STATUS PEMBALAKAN",
            [nameof(Log_State)]             = "STATUS",
            [nameof(Log_Samples)]           = "SAMPEL",
            [nameof(Log_Duration)]          = "TEMPOH",
            [nameof(Log_SecFileSettings)]   = "TETAPAN FAIL",
            [nameof(Log_Folder)]            = "Folder",
            [nameof(Log_Browse)]            = "Semak Imbas…",
            [nameof(Log_Format)]            = "Format",
            [nameof(Log_Filename)]          = "Nama Fail",
            [nameof(Log_PhAutoFilename)]    = "dijana secara automatik jika kosong",
            [nameof(Log_SecColumnSettings)] = "TETAPAN LAJUR",
            [nameof(Log_ColumnHint)]        = "Pilih lajur untuk dilog dan susun semula dengan seret ≡. Dikunci semasa pembalakan aktif.",
            [nameof(Log_SelectAll)]         = "Pilih Semua",
            [nameof(Log_DeselectAll)]       = "Nyahpilih Semua",
            [nameof(Log_ResetDefault)]      = "Tetapkan Semula",
            [nameof(Log_Group)]             = "Kumpulan:",
            [nameof(Log_CellVoltages)]      = "Voltan Sel",
            [nameof(Log_Balancing)]         = "Pengimbangan",
            [nameof(Log_Temperatures)]      = "Suhu",
            [nameof(Log_SecControls)]       = "KAWALAN",
            [nameof(Log_ConnectHint)]       = "Sambung ke ESP32 terlebih dahulu, kemudian mulakan pembalakan.",
            [nameof(Log_RecordingHint)]     = "Rakaman sedang berlangsung. Hentikan untuk menutup / menulis fail.",
            [nameof(Log_ReadyHint)]         = "Sedia. Tekan Mula Pembalakan untuk mula merakam.",
            [nameof(Log_StartLogging)]      = "Mula Pembalakan",
            [nameof(Log_StopLogging)]       = "Hentikan Pembalakan",
            [nameof(Log_OpenFolder)]        = "Buka Folder",
            [nameof(Log_SecDataFormat)]     = "FORMAT DATA",
            [nameof(Log_DataDesc1)]         = "Setiap baris = satu bingkai data yang diterima dari ESP32 (∼1 Hz).",
            [nameof(Log_DataDesc2)]         = "Kolom: Timestamp · PackVoltage_V · SOC_pct · Current_A · Status · Cell1_V … Cell20_V · Bal1 … Bal20 · Temp1_C … Temp10_C",
            [nameof(Log_DataDesc3)]         = "CSV / TSV: disalirkan ke cakera setiap bingkai.  Excel / JSON: ditimbal dalam memori dan ditulis apabila anda tekan Stop.",
            [nameof(Log_SecLiveData)]       = "DATA LANGSUNG (20 TERBARU)",
            [nameof(Log_HdrTimestamp)]      = "TIMESTAMP",
            [nameof(Log_HdrSoc)]            = "SOC %",
            [nameof(Log_HdrPackV)]          = "PACK V",
            [nameof(Log_HdrCurrentA)]       = "ARUS A",
            [nameof(Log_HdrStatus)]         = "STATUS",
            [nameof(Log_HdrMinCell)]        = "MIN SEL V",
            [nameof(Log_HdrMaxCell)]        = "MAKS SEL V",
            [nameof(Log_HdrDeltaMv)]        = "DELTA mV",
            [nameof(Log_HdrBalCells)]       = "SEL SEIMBANG",
            [nameof(Log_NoData)]            = "Tiada data lagi — sambungkan ESP32 untuk melihat aliran data.",
            [nameof(Log_Idle)]              = "Diam",
            [nameof(Log_Logging)]           = "Merakam",

            [nameof(Pb_SecLoadFile)]   = "MUAT FAIL",
            [nameof(Pb_NoFileLoaded)]  = "Tiada fail dimuatkan",
            [nameof(Pb_Browse)]        = "Semak Imbas…",
            [nameof(Pb_Unload)]        = "Buang",
            [nameof(Pb_LoadStatus)]    = "Semak imbas dan buka fail log CSV BMS Monitor (.csv).",
            [nameof(Pb_SecFileInfo)]   = "INFO FAIL",
            [nameof(Pb_Frames)]        = "BINGKAI",
            [nameof(Pb_EstDuration)]   = "ANGGARAN TEMPOH",
            [nameof(Pb_PlaybackSpeed)] = "KELAJUAN MAIN SEMULA",
            [nameof(Pb_SecHowToUse)]   = "CARA MENGGUNAKAN",
            [nameof(Pb_HowToUse1)]     = "Semak imbas dan muat fail CSV di atas, kemudian gunakan bar main semula yang muncul di bahagian bawah tetingkap.",
            [nameof(Pb_HowToUse2)]     = "Semasa dimainkan, semua halaman dikemas kini secara masa nyata. Pembalakan dijeda secara automatik semasa main balik. Klik ✕ di bar main semula atau tekan Buang untuk kembali ke mod langsung.",
        },

        // ── NEDERLANDS ────────────────────────────────────────────────────
        ["nl"] = new()
        {
            [nameof(Nav_Dashboard)]    = "Dashboard",
            [nameof(Nav_CellView)]     = "Celweergave",
            [nameof(Nav_ControlPanel)] = "Configuratiescherm",
            [nameof(Nav_Logging)]      = "Logboek",
            [nameof(Nav_Playback)]     = "Afspelen",

            [nameof(Ui_Dark)]          = "DONKER",
            [nameof(Ui_Light)]         = "LICHT",
            [nameof(Ui_SwitchToLight)] = "Overschakelen naar lichte modus",
            [nameof(Ui_SwitchToDark)]  = "Overschakelen naar donkere modus",
            [nameof(Ui_ChangeLanguage)] = "Taal wijzigen",
            [nameof(Ui_CanConnection)]  = "CAN-verbinding",
            [nameof(Ui_CanQuickAccess)] = "Snelle toegang tot CAN-bus",

            [nameof(Com_Min)]    = "MIN",
            [nameof(Com_Max)]    = "MAX",
            [nameof(Com_Avg)]    = "GEM",
            [nameof(Com_Delta)]  = "DELTA",
            [nameof(Com_Status)] = "STATUS",

            [nameof(Dash_SecPackOverview)]  = "PACK OVERZICHT",
            [nameof(Dash_PackVoltage)]      = "PACK SPANNING",
            [nameof(Dash_PackNominal)]      = "72V nominaal",
            [nameof(Dash_StateOfCharge)]    = "LAADTOESTAND",
            [nameof(Dash_Remaining)]        = "RESTEREND",
            [nameof(Dash_RemainingSub)]     = "gebaseerd op SOC × capaciteit",
            [nameof(Dash_Current)]          = "STROOM",
            [nameof(Dash_CurrentSub)]       = "+ = laden  − = ontladen",
            [nameof(Dash_PackConfig)]       = "20S4P NMC",
            [nameof(Dash_SecSocHistory)]    = "SOC GESCHIEDENIS",
            [nameof(Dash_TimeAgo)]          = "← 2 min geleden",
            [nameof(Dash_Now)]              = "nu",
            [nameof(Dash_NowArrow)]         = "nu →",
            [nameof(Dash_SecViHistory)]     = "SPANNING / STROOM GESCHIEDENIS",
            [nameof(Dash_VoltageV)]         = "Spanning (V)",
            [nameof(Dash_CurrentA)]         = "Stroom (A)",
            [nameof(Dash_SecCellSummary)]   = "CEL SPANNING OVERZICHT",
            [nameof(Dash_TempSensors)]      = "TEMPERATUURSENSOREN",
            [nameof(Dash_BalancingStatus)]  = "BALANCEERSTATUS",
            [nameof(Dash_ActiveCells)]      = "Actieve Cellen",
            [nameof(Dash_CellDelta)]        = "Cel Delta",
            [nameof(Dash_Method)]           = "Methode",
            [nameof(Dash_ActiveMethod)]     = "Actief (LTC8584)",
            [nameof(Dash_SaveChart)]        = "Grafiek opslaan als PNG",
            [nameof(Dash_SecTempHistory)]   = "TEMPERATUUR GESCHIEDENIS",
            [nameof(Dash_TempC)]            = "°C",
            [nameof(Dash_TempF)]            = "°F",

            [nameof(Cell_SecVoltageSummary)] = "SPANNING OVERZICHT",
            [nameof(Cell_SecCellGrid)]       = "CEL RASTER — 20S4P",
            [nameof(Cell_DeltaLabel)]        = "Δ DELTA",
            [nameof(Cell_Normal)]            = "Normaal",
            [nameof(Cell_Low)]               = "Laag",
            [nameof(Cell_Undervoltage)]      = "Onderspanning",
            [nameof(Cell_Overvoltage)]       = "Overspanning",
            [nameof(Cell_Balancing)]         = "Balanceren",
            [nameof(Cell_SecNtcReadings)]    = "NTC THERMISTOR METINGEN",
            [nameof(Cell_Thresholds)]        = "DREMPELWAARDEN",
            [nameof(Cell_ThreshWarn)]        = "Waarschuwing :  60 °C",
            [nameof(Cell_ThreshCutoff)]      = "Beveiliging  :  70 °C",
            [nameof(Cell_Legend)]            = "LEGENDA",
            [nameof(Cell_NormalDesc)]        = "Normaal  (onder 60°C)",
            [nameof(Cell_WarnDesc)]          = "Waarschuwing (60 – 70°C)",
            [nameof(Cell_CutoffDesc)]        = "Beveiliging  (boven 70°C)",

            [nameof(Ctrl_SecCan)]            = "CAN-BUS VERBINDING",
            [nameof(Ctrl_CanChannel)]        = "CAN-kanaal",
            [nameof(Ctrl_PhScanning)]        = "Kanalen scannen…",
            [nameof(Ctrl_PhNoPorts)]         = "Geen CAN-kanalen gevonden",
            [nameof(Ctrl_PhNoDriver)]        = "PCAN-Basic-stuurprogramma niet geïnstalleerd",
            [nameof(Ctrl_Refresh)]           = "Vernieuwen",
            [nameof(Ctrl_Connect)]           = "Verbinden",
            [nameof(Ctrl_Disconnect)]        = "Verbreken",
            [nameof(Ctrl_CanBitrate)]        = "Bitrate",
            [nameof(Ctrl_ConnStatus)]        = "Status",
            [nameof(Ctrl_NotConnected)]      = "Niet verbonden",
            [nameof(Ctrl_AutoConnectStatus)] = "Automatisch verbinden actief — wacht op BMS CAN-frames…",
            [nameof(Ctrl_SecCapacity)]       = "BATTERIJCAPACITEIT",
            [nameof(Ctrl_NominalCapacity)]   = "Nominale Capaciteit",
            [nameof(Ctrl_CapacityHint)]      = "Gebruikt om de resterende capaciteit (mAh) op het dashboard te berekenen.",
            [nameof(Ctrl_SecProtection)]     = "BESCHERMINGSDREMPELS",
            [nameof(Ctrl_OvervoltCutoff)]    = "Overspanningsbeveiliging",
            [nameof(Ctrl_UnderVoltCutoff)]   = "Onderspanningsbeveiliging",
            [nameof(Ctrl_LowVoltWarn)]       = "Laagspanningswaarschuwing",
            [nameof(Ctrl_OverTempWarn)]      = "Temperatuurwaarschuwing",
            [nameof(Ctrl_OverTempCutoff)]    = "Temperatuurbeveiliging",
            [nameof(Ctrl_SecCurrentLimits)]  = "STROOMLIMIETEN",
            [nameof(Ctrl_MaxCharge)]         = "Max. Laadstroom",
            [nameof(Ctrl_MaxDischarge)]      = "Max. Ontlaadstroom",
            [nameof(Ctrl_MaxDod)]            = "Max. DoD",
            [nameof(Ctrl_SecBalancing)]      = "ACTIEF BALANCEREN (LTC8584)",
            [nameof(Ctrl_StartDelta)]        = "Start Delta",
            [nameof(Ctrl_StopDelta)]         = "Stop Delta",
            [nameof(Ctrl_ResetDefaults)]     = "Standaard herstellen",
            [nameof(Ctrl_ApplySettings)]     = "Instellingen toepassen",

            [nameof(Ctrl_SecCanAdvanced)]    = "CAN-BUS PARAMETERS",
            [nameof(Ctrl_AutoConnect)]       = "Automatisch verbinden",
            [nameof(Ctrl_AutoConnectHint)]   = "Scan automatisch PCAN-USB-kanalen en maak verbinding met het kanaal dat BMS-heartbeat-frames uitzendt.",
            [nameof(Ctrl_ReconnectInterval)] = "Scan-interval",
            [nameof(Ctrl_ProbeTimeout)]      = "Detectie-timeout",
            [nameof(Ctrl_FramesReceived)]    = "Frames ontvangen",
            [nameof(Ctrl_ParseErrors)]       = "Parse-fouten",

            [nameof(Fb_CanError)]            = "CAN-fout",
            [nameof(Fb_SelectChannel)]       = "Selecteer een kanaal",
            [nameof(Fb_SelectChannelMsg)]    = "Selecteer eerst een CAN-kanaal uit de vervolgkeuzelijst.",
            [nameof(Fb_SettingsApplied)]     = "Instellingen toegepast",
            [nameof(Fb_SettingsAppliedMsg)]  = "Nieuwe drempelwaarden zijn actief.",
            [nameof(Fb_DefaultsRestored)]    = "Standaard hersteld",
            [nameof(Fb_DefaultsRestoredMsg)] = "Waarden gereset — klik Toepassen om te activeren.",

            [nameof(Log_SecStatus)]         = "LOGBOEKSTATUS",
            [nameof(Log_State)]             = "STATUS",
            [nameof(Log_Samples)]           = "MONSTERS",
            [nameof(Log_Duration)]          = "DUUR",
            [nameof(Log_SecFileSettings)]   = "BESTANDSINSTELLINGEN",
            [nameof(Log_Folder)]            = "Map",
            [nameof(Log_Browse)]            = "Bladeren…",
            [nameof(Log_Format)]            = "Formaat",
            [nameof(Log_Filename)]          = "Bestandsnaam",
            [nameof(Log_PhAutoFilename)]    = "automatisch gegenereerd als leeg",
            [nameof(Log_SecColumnSettings)] = "KOLOMINSTELLINGEN",
            [nameof(Log_ColumnHint)]        = "Kies kolommen om te loggen en sleep ≡ om te herordenen. Vergrendeld tijdens actief loggen.",
            [nameof(Log_SelectAll)]         = "Alles selecteren",
            [nameof(Log_DeselectAll)]       = "Alles deselecteren",
            [nameof(Log_ResetDefault)]      = "Standaard herstellen",
            [nameof(Log_Group)]             = "Groep:",
            [nameof(Log_CellVoltages)]      = "Celspanning",
            [nameof(Log_Balancing)]         = "Balanceren",
            [nameof(Log_Temperatures)]      = "Temperaturen",
            [nameof(Log_SecControls)]       = "BEDIENINGSMIDDELEN",
            [nameof(Log_ConnectHint)]       = "Verbind eerst met de ESP32 en start dan met loggen.",
            [nameof(Log_RecordingHint)]     = "Opname bezig. Stop om het bestand te sluiten / schrijven.",
            [nameof(Log_ReadyHint)]         = "Klaar. Druk op Loggen starten om te beginnen.",
            [nameof(Log_StartLogging)]      = "Loggen starten",
            [nameof(Log_StopLogging)]       = "Loggen stoppen",
            [nameof(Log_OpenFolder)]        = "Map openen",
            [nameof(Log_SecDataFormat)]     = "GEGEVENSFORMAAT",
            [nameof(Log_DataDesc1)]         = "Elke rij = één gegevensframe ontvangen van de ESP32 (∼1 Hz).",
            [nameof(Log_DataDesc2)]         = "Velden: Timestamp · PackVoltage_V · SOC_pct · Current_A · Status · Cell1_V … Cell20_V · Bal1 … Bal20 · Temp1_C … Temp10_C",
            [nameof(Log_DataDesc3)]         = "CSV / TSV: elk frame naar schijf gestreamd.  Excel / JSON: gebufferd in geheugen en geschreven wanneer u op Stoppen drukt.",
            [nameof(Log_SecLiveData)]       = "LIVE DATA (LAATSTE 20)",
            [nameof(Log_HdrTimestamp)]      = "TIJDSTEMPEL",
            [nameof(Log_HdrSoc)]            = "SOC %",
            [nameof(Log_HdrPackV)]          = "PACK V",
            [nameof(Log_HdrCurrentA)]       = "STROOM A",
            [nameof(Log_HdrStatus)]         = "STATUS",
            [nameof(Log_HdrMinCell)]        = "MIN CEL V",
            [nameof(Log_HdrMaxCell)]        = "MAX CEL V",
            [nameof(Log_HdrDeltaMv)]        = "DELTA mV",
            [nameof(Log_HdrBalCells)]       = "BAL CELLEN",
            [nameof(Log_NoData)]            = "Nog geen data — verbind de ESP32 om de datastroom te zien.",
            [nameof(Log_Idle)]              = "Inactief",
            [nameof(Log_Logging)]           = "Loggen",

            [nameof(Pb_SecLoadFile)]   = "BESTAND LADEN",
            [nameof(Pb_NoFileLoaded)]  = "Geen bestand geladen",
            [nameof(Pb_Browse)]        = "Bladeren…",
            [nameof(Pb_Unload)]        = "Verwijderen",
            [nameof(Pb_LoadStatus)]    = "Blader naar en open een BMS Monitor CSV-logbestand (.csv).",
            [nameof(Pb_SecFileInfo)]   = "BESTANDSINFO",
            [nameof(Pb_Frames)]        = "FRAMES",
            [nameof(Pb_EstDuration)]   = "GESCHATTE DUUR",
            [nameof(Pb_PlaybackSpeed)] = "AFSPEELSNELHEID",
            [nameof(Pb_SecHowToUse)]   = "HOE TE GEBRUIKEN",
            [nameof(Pb_HowToUse1)]     = "Blader en laad een CSV-bestand hierboven, gebruik dan de afspeelbalk die onderaan het venster verschijnt.",
            [nameof(Pb_HowToUse2)]     = "Tijdens afspelen worden alle pagina's real-time bijgewerkt met de opgenomen data. Loggen wordt automatisch gepauzeerd. Klik ✕ in de afspeelbalk of druk op Verwijderen om terug te keren naar de live modus.",
        },

        // ── CHINESE (Simplified) ──────────────────────────────────────────
        ["zh"] = new()
        {
            [nameof(Nav_Dashboard)]    = "仪表盘",
            [nameof(Nav_CellView)]     = "电池格",
            [nameof(Nav_ControlPanel)] = "控制面板",
            [nameof(Nav_Logging)]      = "数据记录",
            [nameof(Nav_Playback)]     = "回放",

            [nameof(Ui_Dark)]          = "深色",
            [nameof(Ui_Light)]         = "浅色",
            [nameof(Ui_SwitchToLight)] = "切换到浅色模式",
            [nameof(Ui_SwitchToDark)]  = "切换到深色模式",
            [nameof(Ui_ChangeLanguage)] = "更改语言",
            [nameof(Ui_CanConnection)]  = "CAN 连接",
            [nameof(Ui_CanQuickAccess)] = "CAN 总线快速访问",

            [nameof(Com_Min)]    = "最低",
            [nameof(Com_Max)]    = "最高",
            [nameof(Com_Avg)]    = "均值",
            [nameof(Com_Delta)]  = "差值",
            [nameof(Com_Status)] = "状态",

            [nameof(Dash_SecPackOverview)]  = "电池组概览",
            [nameof(Dash_PackVoltage)]      = "组电压",
            [nameof(Dash_PackNominal)]      = "72V 额定",
            [nameof(Dash_StateOfCharge)]    = "电量状态",
            [nameof(Dash_Remaining)]        = "剩余",
            [nameof(Dash_RemainingSub)]     = "基于 SOC × 容量",
            [nameof(Dash_Current)]          = "电流",
            [nameof(Dash_CurrentSub)]       = "+ = 充电  − = 放电",
            [nameof(Dash_PackConfig)]       = "20S4P NMC",
            [nameof(Dash_SecSocHistory)]    = "SOC 历史",
            [nameof(Dash_TimeAgo)]          = "← 2 分钟前",
            [nameof(Dash_Now)]              = "现在",
            [nameof(Dash_NowArrow)]         = "现在 →",
            [nameof(Dash_SecViHistory)]     = "电压 / 电流历史",
            [nameof(Dash_VoltageV)]         = "电压 (V)",
            [nameof(Dash_CurrentA)]         = "电流 (A)",
            [nameof(Dash_SecCellSummary)]   = "电池格电压摘要",
            [nameof(Dash_TempSensors)]      = "温度传感器",
            [nameof(Dash_BalancingStatus)]  = "均衡状态",
            [nameof(Dash_ActiveCells)]      = "活跃单元",
            [nameof(Dash_CellDelta)]        = "电压差",
            [nameof(Dash_Method)]           = "方法",
            [nameof(Dash_ActiveMethod)]     = "主动 (LTC8584)",
            [nameof(Dash_SaveChart)]        = "保存图表为 PNG",
            [nameof(Dash_SecTempHistory)]   = "温度历史",
            [nameof(Dash_TempC)]            = "°C",
            [nameof(Dash_TempF)]            = "°F",

            [nameof(Cell_SecVoltageSummary)] = "电压摘要",
            [nameof(Cell_SecCellGrid)]       = "电池格阵列 — 20S4P",
            [nameof(Cell_DeltaLabel)]        = "Δ 差值",
            [nameof(Cell_Normal)]            = "正常",
            [nameof(Cell_Low)]               = "偏低",
            [nameof(Cell_Undervoltage)]      = "欠压",
            [nameof(Cell_Overvoltage)]       = "过压",
            [nameof(Cell_Balancing)]         = "均衡中",
            [nameof(Cell_SecNtcReadings)]    = "NTC 热敏电阔读数",
            [nameof(Cell_Thresholds)]        = "阈值",
            [nameof(Cell_ThreshWarn)]        = "警告 :  60 °C",
            [nameof(Cell_ThreshCutoff)]      = "断路 :  70 °C",
            [nameof(Cell_Legend)]            = "图例",
            [nameof(Cell_NormalDesc)]        = "正常  (低于 60°C)",
            [nameof(Cell_WarnDesc)]          = "警告 (60 – 70°C)",
            [nameof(Cell_CutoffDesc)]        = "断路 (高于 70°C)",

            [nameof(Ctrl_SecCan)]            = "CAN 总线连接",
            [nameof(Ctrl_CanChannel)]        = "CAN 通道",
            [nameof(Ctrl_PhScanning)]        = "正在扫描通道…",
            [nameof(Ctrl_PhNoPorts)]         = "未检测到 CAN 通道",
            [nameof(Ctrl_PhNoDriver)]        = "未安装 PCAN-Basic 驱动",
            [nameof(Ctrl_Refresh)]           = "刷新",
            [nameof(Ctrl_Connect)]           = "连接",
            [nameof(Ctrl_Disconnect)]        = "断开",
            [nameof(Ctrl_CanBitrate)]        = "比特率",
            [nameof(Ctrl_ConnStatus)]        = "状态",
            [nameof(Ctrl_NotConnected)]      = "未连接",
            [nameof(Ctrl_AutoConnectStatus)] = "自动连接已启动 — 等待 BMS CAN 帧…",
            [nameof(Ctrl_SecCapacity)]       = "电池容量",
            [nameof(Ctrl_NominalCapacity)]   = "额定容量",
            [nameof(Ctrl_CapacityHint)]      = "用于计算仪表盘上的剩余容量 (mAh)。",
            [nameof(Ctrl_SecProtection)]     = "保护阈值",
            [nameof(Ctrl_OvervoltCutoff)]    = "过压截止",
            [nameof(Ctrl_UnderVoltCutoff)]   = "欠压截止",
            [nameof(Ctrl_LowVoltWarn)]       = "低压警告",
            [nameof(Ctrl_OverTempWarn)]      = "过温警告",
            [nameof(Ctrl_OverTempCutoff)]    = "过温截止",
            [nameof(Ctrl_SecCurrentLimits)]  = "电流限制",
            [nameof(Ctrl_MaxCharge)]         = "最大充电电流",
            [nameof(Ctrl_MaxDischarge)]      = "最大放电电流",
            [nameof(Ctrl_MaxDod)]            = "最大放电深度",
            [nameof(Ctrl_SecBalancing)]      = "主动均衡 (LTC8584)",
            [nameof(Ctrl_StartDelta)]        = "启动差值",
            [nameof(Ctrl_StopDelta)]         = "停止差值",
            [nameof(Ctrl_ResetDefaults)]     = "恢复默认",
            [nameof(Ctrl_ApplySettings)]     = "应用设置",

            [nameof(Ctrl_SecCanAdvanced)]    = "CAN 总线参数",
            [nameof(Ctrl_AutoConnect)]       = "自动连接",
            [nameof(Ctrl_AutoConnectHint)]   = "自动扫描 PCAN-USB 通道并连接到广播 BMS 心跳帧的通道。",
            [nameof(Ctrl_ReconnectInterval)] = "扫描间隔",
            [nameof(Ctrl_ProbeTimeout)]      = "探测超时",
            [nameof(Ctrl_FramesReceived)]    = "已接收帧数",
            [nameof(Ctrl_ParseErrors)]       = "解析错误",

            [nameof(Fb_CanError)]            = "CAN 错误",
            [nameof(Fb_SelectChannel)]       = "选择通道",
            [nameof(Fb_SelectChannelMsg)]    = "请先从下拉列表中选择一个 CAN 通道。",
            [nameof(Fb_SettingsApplied)]     = "设置已应用",
            [nameof(Fb_SettingsAppliedMsg)]  = "新阈值已生效。",
            [nameof(Fb_DefaultsRestored)]    = "已恢复默认",
            [nameof(Fb_DefaultsRestoredMsg)] = "值已重置 — 点击应用以生效。",

            [nameof(Log_SecStatus)]         = "记录状态",
            [nameof(Log_State)]             = "状态",
            [nameof(Log_Samples)]           = "样本",
            [nameof(Log_Duration)]          = "时长",
            [nameof(Log_SecFileSettings)]   = "文件设置",
            [nameof(Log_Folder)]            = "文件夹",
            [nameof(Log_Browse)]            = "浏览…",
            [nameof(Log_Format)]            = "格式",
            [nameof(Log_Filename)]          = "文件名",
            [nameof(Log_PhAutoFilename)]    = "留空则自动生成",
            [nameof(Log_SecColumnSettings)] = "列设置",
            [nameof(Log_ColumnHint)]        = "选择要记录的列，拖动 ≡ 重新排序。记录时锁定。",
            [nameof(Log_SelectAll)]         = "全选",
            [nameof(Log_DeselectAll)]       = "取消全选",
            [nameof(Log_ResetDefault)]      = "重置默认",
            [nameof(Log_Group)]             = "组别:",
            [nameof(Log_CellVoltages)]      = "电池格电压",
            [nameof(Log_Balancing)]         = "均衡",
            [nameof(Log_Temperatures)]      = "温度",
            [nameof(Log_SecControls)]       = "控制",
            [nameof(Log_ConnectHint)]       = "请先连接 ESP32，然后开始记录。",
            [nameof(Log_RecordingHint)]     = "正在记录。停止以关闭/写入文件。",
            [nameof(Log_ReadyHint)]         = "就绪。按下开始记录以开始录制。",
            [nameof(Log_StartLogging)]      = "开始记录",
            [nameof(Log_StopLogging)]       = "停止记录",
            [nameof(Log_OpenFolder)]        = "打开文件夹",
            [nameof(Log_SecDataFormat)]     = "数据格式",
            [nameof(Log_DataDesc1)]         = "每行 = 从 ESP32 接收的一个数据帧 (∼1 Hz)。",
            [nameof(Log_DataDesc2)]         = "字段: Timestamp · PackVoltage_V · SOC_pct · Current_A · Status · Cell1_V … Cell20_V · Bal1 … Bal20 · Temp1_C … Temp10_C",
            [nameof(Log_DataDesc3)]         = "CSV / TSV: 每帧流式写入磁盘。Excel / JSON: 在内存中缓冲，按停止时写入。",
            [nameof(Log_SecLiveData)]       = "实时数据 (最新 20 条)",
            [nameof(Log_HdrTimestamp)]      = "时间戳",
            [nameof(Log_HdrSoc)]            = "SOC %",
            [nameof(Log_HdrPackV)]          = "组电压",
            [nameof(Log_HdrCurrentA)]       = "电流 A",
            [nameof(Log_HdrStatus)]         = "状态",
            [nameof(Log_HdrMinCell)]        = "最低格压",
            [nameof(Log_HdrMaxCell)]        = "最高格压",
            [nameof(Log_HdrDeltaMv)]        = "差值 mV",
            [nameof(Log_HdrBalCells)]       = "均衡格",
            [nameof(Log_NoData)]            = "暂无数据 — 连接 ESP32 以查看数据流。",
            [nameof(Log_Idle)]              = "空闲",
            [nameof(Log_Logging)]           = "记录中",

            [nameof(Pb_SecLoadFile)]   = "加载文件",
            [nameof(Pb_NoFileLoaded)]  = "未加载文件",
            [nameof(Pb_Browse)]        = "浏览…",
            [nameof(Pb_Unload)]        = "卸载",
            [nameof(Pb_LoadStatus)]    = "浏览并打开 BMS Monitor CSV 日志文件 (.csv)。",
            [nameof(Pb_SecFileInfo)]   = "文件信息",
            [nameof(Pb_Frames)]        = "帧数",
            [nameof(Pb_EstDuration)]   = "估计时长",
            [nameof(Pb_PlaybackSpeed)] = "播放速度",
            [nameof(Pb_SecHowToUse)]   = "使用方法",
            [nameof(Pb_HowToUse1)]     = "浏览并加载上方的 CSV 文件，然后使用窗口底部出现的播放栏。",
            [nameof(Pb_HowToUse2)]     = "播放时，所有页面将实时更新录制数据。播放期间日志记录自动暂停。点击播放栏中的 ✕ 或按卸载返回实时模式。",
        },
    };
}
