using System.Collections.ObjectModel;
using System.Text;
using BMSMonitor.Models;
#if __ANDROID__
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.OS;
using Java.Util;
#endif

namespace BMSMonitor.Services;

/// <summary>One pickable BLE device.</summary>
public record BluetoothDeviceInfo(string DeviceId, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// Android implementation of the BLE link (same public surface as the Windows
/// <c>Services/BluetoothService.cs</c>). Connects to an ESP32 master running the
/// Nordic UART Service (NUS), subscribes to the TX characteristic, splits the
/// byte stream on '\n' and parses each JSON line via
/// <see cref="SerialService.TryParseLine"/>.
/// </summary>
public sealed class BluetoothService : IDisposable
{
    // ── Public events ─────────────────────────────────────────────────────
    public event Action<BmsData>? DataReceived;
    public event Action<string>?  StatusChanged;
    public event Action<string>?  ErrorOccurred;
    public event Action?          DevicesChanged;

    // ── Live state ────────────────────────────────────────────────────────
    public bool   IsConnected     { get; private set; }
    public string DeviceId        => _deviceId ?? "";
    public string DeviceName      { get; private set; } = "";
    public int    FramesReceived  { get; private set; }
    public int    ParseErrors     { get; private set; }
    public bool   IsScanning      { get; private set; }

    /// <summary>Devices discovered by the current/last scan. UI-bindable.</summary>
    public ObservableCollection<BluetoothDeviceInfo> Devices { get; } = new();

    private string?                _deviceId;
    private readonly StringBuilder _lineBuf = new(512);
    private readonly object        _stateLock = new();
    private BmsData                _last = new();
    private DateTime               _lastParseErrorFired = DateTime.MinValue;
    private static readonly TimeSpan ParseErrorCooldown = TimeSpan.FromSeconds(5);

    private static LocalizationManager L => LocalizationManager.Instance;

#if __ANDROID__
    private static readonly UUID NusServiceUuid = UUID.FromString("6E400001-B5A3-F393-E0A9-E50E24DCCA9E")!;
    private static readonly UUID NusTxCharUuid  = UUID.FromString("6E400003-B5A3-F393-E0A9-E50E24DCCA9E")!;
    private static readonly UUID CccdUuid       = UUID.FromString("00002902-0000-1000-8000-00805f9b34fb")!;
    private static readonly TimeSpan ScanDuration    = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ConnectTimeout  = TimeSpan.FromSeconds(20);

    private BluetoothLeScanner? _scanner;
    private ScanCb?             _scanCb;
    private CancellationTokenSource? _scanTimer;
    private BluetoothGatt?      _gatt;
    private GattCb?             _gattCb;
    private TaskCompletionSource<bool>? _connectTcs;
    private string              _connectError = "";

    private static Context Ctx => Android.App.Application.Context;

    private static BluetoothAdapter? Adapter =>
        (Ctx.GetSystemService(Context.BluetoothService) as BluetoothManager)?.Adapter;

    private static string[] RequiredPermissions =>
        Build.VERSION.SdkInt >= BuildVersionCodes.S
            ? [Android.Manifest.Permission.BluetoothScan, Android.Manifest.Permission.BluetoothConnect]
            : [Android.Manifest.Permission.AccessFineLocation];

    private static async Task<bool> EnsurePermissionsAsync()
    {
        var act = BMSMonitor.Droid.MainActivity.Current;
        if (act is null) return false;
        return await act.RequestPermissionsAsync(RequiredPermissions);
    }

    private bool EnsureAdapterOn()
    {
        var adapter = Adapter;
        if (adapter is null)
        {
            ErrorOccurred?.Invoke(L.Format("Bt_ScanError", "Bluetooth LE not available"));
            return false;
        }
        if (!adapter.IsEnabled)
        {
            try
            {
                var intent = new Intent(BluetoothAdapter.ActionRequestEnable);
                BMSMonitor.Droid.MainActivity.Current?.StartActivity(intent);
            }
            catch { }
            ErrorOccurred?.Invoke(L.Format("Bt_ScanError", "Bluetooth is turned off"));
            return false;
        }
        return true;
    }

    // ── Discovery ─────────────────────────────────────────────────────────
    public async void StartScan()
    {
        StopScan();
        Devices.Clear();

        try
        {
            if (!await EnsurePermissionsAsync())
            {
                ErrorOccurred?.Invoke(L.Format("Bt_ScanError", "Bluetooth permission denied"));
                return;
            }
            if (!EnsureAdapterOn()) return;

            _scanner = Adapter!.BluetoothLeScanner;
            if (_scanner is null)
            {
                ErrorOccurred?.Invoke(L.Format("Bt_ScanError", "BLE scanner unavailable"));
                return;
            }

            _scanCb = new ScanCb(this);
            var settings = new ScanSettings.Builder()
                .SetScanMode(Android.Bluetooth.LE.ScanMode.LowLatency)!
                .Build();
            IsScanning = true;
            _scanner.StartScan(null, settings, _scanCb);

            var cts = new CancellationTokenSource();
            _scanTimer = cts;
            _ = Task.Delay(ScanDuration, cts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled) StopScan();
                DevicesChanged?.Invoke();
            }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            IsScanning = false;
            ErrorOccurred?.Invoke(L.Format("Bt_ScanError", ex.Message));
        }
    }

    public void StopScan()
    {
        try { _scanTimer?.Cancel(); } catch { }
        _scanTimer = null;
        if (_scanner is not null && _scanCb is not null)
        {
            try { _scanner.StopScan(_scanCb); } catch { }
        }
        _scanner = null;
        _scanCb = null;
        IsScanning = false;
    }

    private void OnDeviceFound(BluetoothDevice device, string? advertisedName)
    {
        var name = (advertisedName ?? SafeName(device) ?? "").Trim();
        if (name.Length == 0) return;
        var id = device.Address ?? "";
        if (id.Length == 0) return;

        lock (Devices)
        {
            for (int i = 0; i < Devices.Count; i++)
            {
                if (Devices[i].DeviceId != id) continue;
                if (Devices[i].DisplayName != name)
                {
                    Devices[i] = new BluetoothDeviceInfo(id, name);
                    DevicesChanged?.Invoke();
                }
                return;
            }
            Devices.Add(new BluetoothDeviceInfo(id, name));
        }
        DevicesChanged?.Invoke();
    }

    private static string? SafeName(BluetoothDevice d)
    {
        try { return d.Name; } catch { return null; }
    }

    private sealed class ScanCb(BluetoothService owner) : ScanCallback
    {
        public override void OnScanResult(ScanCallbackType callbackType, ScanResult? result)
        {
            if (result?.Device is { } dev)
                owner.OnDeviceFound(dev, result.ScanRecord?.DeviceName);
        }

        public override void OnBatchScanResults(IList<ScanResult>? results)
        {
            if (results is null) return;
            foreach (var r in results)
                if (r.Device is { } dev) owner.OnDeviceFound(dev, r.ScanRecord?.DeviceName);
        }

        public override void OnScanFailed(ScanFailure errorCode)
        {
            owner.IsScanning = false;
            owner.ErrorOccurred?.Invoke(L.Format("Bt_ScanError", errorCode.ToString()));
            owner.DevicesChanged?.Invoke();
        }
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────
    public async Task<bool> ConnectAsync(BluetoothDeviceInfo target)
    {
        if (IsConnected) Disconnect();
        StopScan();

        try
        {
            if (!await EnsurePermissionsAsync())
            {
                ErrorOccurred?.Invoke(L.Format("Bt_OpenFailed", target.DisplayName, "Bluetooth permission denied"));
                return false;
            }
            if (!EnsureAdapterOn()) return false;

            var device = Adapter!.GetRemoteDevice(target.DeviceId);
            if (device is null)
            {
                ErrorOccurred?.Invoke(L.Format("Bt_OpenFailed", target.DisplayName, "device not found"));
                return false;
            }

            _connectError = "";
            _connectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _gattCb = new GattCb(this);
            _gatt = device.ConnectGatt(Ctx, false, _gattCb, BluetoothTransports.Le);
            if (_gatt is null)
            {
                ErrorOccurred?.Invoke(L.Format("Bt_OpenFailed", target.DisplayName, "GATT connect failed"));
                return false;
            }

            var done = await Task.WhenAny(_connectTcs.Task, Task.Delay(ConnectTimeout));
            bool ok = done == _connectTcs.Task && _connectTcs.Task.Result;
            if (!ok)
            {
                string why = _connectError.Length > 0 ? _connectError : "timeout";
                ErrorOccurred?.Invoke(why switch
                {
                    "nus"       => L.Format("Bt_NoNusService", target.DisplayName),
                    "tx"        => L.Format("Bt_NoTxCharacteristic", target.DisplayName),
                    "subscribe" => L.Format("Bt_SubscribeFailed", target.DisplayName),
                    _           => L.Format("Bt_OpenFailed", target.DisplayName, why),
                });
                CleanupGatt();
                return false;
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(L.Format("Bt_OpenFailed", target.DisplayName, ex.Message));
            CleanupGatt();
            return false;
        }

        _deviceId      = target.DeviceId;
        DeviceName     = target.DisplayName;
        FramesReceived = 0;
        ParseErrors    = 0;
        lock (_stateLock) { _lineBuf.Clear(); _last = new BmsData(); }
        IsConnected    = true;

        StatusChanged?.Invoke(L.Format("Bt_StatusConnected", target.DisplayName));
        return true;
    }

    public void Disconnect()
    {
        CleanupGatt();
        StatusChanged?.Invoke(L.Get("Bt_StatusDisconnected"));
    }

    private void CleanupGatt()
    {
        IsConnected = false;
        var g = _gatt;
        _gatt = null;
        _gattCb = null;
        if (g is not null)
        {
            try { g.Disconnect(); } catch { }
            try { g.Close(); } catch { }
        }
        _deviceId  = null;
        DeviceName = "";
    }

    public void Dispose()
    {
        StopScan();
        Disconnect();
    }

    private void FailConnect(string reason)
    {
        _connectError = reason;
        _connectTcs?.TrySetResult(false);
    }

    private void OnLinkLost()
    {
        bool wasConnected = IsConnected;
        if (_connectTcs is { Task.IsCompleted: false })
        {
            FailConnect("connection lost");
            return;
        }
        if (!wasConnected) return;
        CleanupGatt();
        StatusChanged?.Invoke(L.Get("Bt_StatusDisconnected"));
    }

    private void SubscribeToTx(BluetoothGatt gatt)
    {
        var service = gatt.GetService(NusServiceUuid);
        if (service is null) { FailConnect("nus"); return; }

        var ch = service.GetCharacteristic(NusTxCharUuid);
        if (ch is null) { FailConnect("tx"); return; }

        if (!gatt.SetCharacteristicNotification(ch, true)) { FailConnect("subscribe"); return; }

        var cccd = ch.GetDescriptor(CccdUuid);
        if (cccd is null) { _connectTcs?.TrySetResult(true); return; }  // notify w/o CCCD

        bool notify = ch.Properties.HasFlag(GattProperty.Notify);
        var value = (notify
            ? BluetoothGattDescriptor.EnableNotificationValue
            : BluetoothGattDescriptor.EnableIndicationValue)!.ToArray();

        bool started;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            started = gatt.WriteDescriptor(cccd, value) == (int)CurrentBluetoothStatusCodes.Success;
        }
        else
        {
#pragma warning disable CA1422
            cccd.SetValue(value);
            started = gatt.WriteDescriptor(cccd);
#pragma warning restore CA1422
        }
        if (!started) FailConnect("subscribe");
    }

    private sealed class GattCb(BluetoothService owner) : BluetoothGattCallback
    {
        public override void OnConnectionStateChange(BluetoothGatt? gatt, GattStatus status, ProfileState newState)
        {
            if (gatt is null) return;
            if (newState == ProfileState.Connected && status == GattStatus.Success)
            {
                // Larger MTU = fewer fragments per JSON line. Services are
                // discovered once the MTU exchange completes (or fails).
                if (!gatt.RequestMtu(247)) gatt.DiscoverServices();
            }
            else if (newState == ProfileState.Disconnected || status != GattStatus.Success)
            {
                owner.OnLinkLost();
            }
        }

        public override void OnMtuChanged(BluetoothGatt? gatt, int mtu, GattStatus status)
        {
            gatt?.DiscoverServices();
        }

        public override void OnServicesDiscovered(BluetoothGatt? gatt, GattStatus status)
        {
            if (gatt is null) return;
            if (status != GattStatus.Success) { owner.FailConnect("nus"); return; }
            owner.SubscribeToTx(gatt);
        }

        public override void OnDescriptorWrite(BluetoothGatt? gatt, BluetoothGattDescriptor? descriptor, GattStatus status)
        {
            if (status == GattStatus.Success) owner._connectTcs?.TrySetResult(true);
            else owner.FailConnect("subscribe");
        }

        // API 33+
        public override void OnCharacteristicChanged(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, byte[] value)
        {
            owner.OnBytes(value);
        }

        // API < 33
#pragma warning disable CA1422, CS0672
        public override void OnCharacteristicChanged(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic)
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu) return;
            var v = characteristic?.GetValue();
            if (v is not null) owner.OnBytes(v);
        }
#pragma warning restore CA1422, CS0672
    }
#else
    // Desktop compile-check head: no BLE.
    public void StartScan() =>
        ErrorOccurred?.Invoke(L.Format("Bt_ScanError", "Bluetooth LE is not available on this platform"));
    public void StopScan() => IsScanning = false;
    public Task<bool> ConnectAsync(BluetoothDeviceInfo target) => Task.FromResult(false);
    public void Disconnect() => StatusChanged?.Invoke(L.Get("Bt_StatusDisconnected"));
    public void Dispose() { }
#endif

    // ── Read pipeline (identical to the Windows implementation) ──────────
    private void OnBytes(byte[] bytes)
    {
        try
        {
            lock (_stateLock)
            {
                for (int i = 0; i < bytes.Length; i++)
                {
                    byte b = bytes[i];
                    if (b == (byte)'\n')
                    {
                        var line = _lineBuf.ToString().Trim();
                        _lineBuf.Clear();
                        if (line.Length > 0) HandleLine(line);
                    }
                    else if (b != (byte)'\r')
                    {
                        _lineBuf.Append((char)b);
                        if (_lineBuf.Length > 8192) _lineBuf.Clear();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(L.Format("Bt_ReadError", ex.Message));
        }
    }

    private void HandleLine(string line)
    {
        if (!SerialService.TryParseLine(line, out var snap))
        {
            ParseErrors++;
            var now = DateTime.Now;
            if (now - _lastParseErrorFired > ParseErrorCooldown)
            {
                _lastParseErrorFired = now;
                string preview = line.Length > 60 ? line[..60] + "…" : line;
                ErrorOccurred?.Invoke(L.Format("Bt_ParseError", ParseErrors, preview));
            }
            return;
        }

        MergeInto(_last, snap);
        FramesReceived++;
        DataReceived?.Invoke(Clone(_last));
    }

    private static void MergeInto(BmsData dst, BmsData src)
    {
        dst.PackVoltage = src.PackVoltage;
        dst.Current     = src.Current;
        dst.Soc         = src.Soc;
        dst.Status      = src.Status;
        for (int k = 0; k < 20; k++) if (src.Cells[k] != 0) dst.Cells[k] = src.Cells[k];
        for (int k = 0; k < BmsData.TempCount; k++) if (src.Temps[k] != 0) dst.Temps[k] = src.Temps[k];
        for (int k = 0; k < 20; k++) dst.Balancing[k] = src.Balancing[k];
    }

    private static BmsData Clone(BmsData s)
    {
        var c = new BmsData
        {
            PackVoltage = s.PackVoltage,
            Current     = s.Current,
            Soc         = s.Soc,
            Status      = s.Status,
        };
        Array.Copy(s.Cells,     c.Cells,     20);
        Array.Copy(s.Temps,     c.Temps,     BmsData.TempCount);
        Array.Copy(s.Balancing, c.Balancing, 20);
        return c;
    }
}
