# BMS_ESP32_DummyFirmware

Dummy ESP32 firmware that pretends to be the BMS master, so you can test the
**BMSMonitor** desktop app without real hardware.

It does two things:

1. **Sends** simulated telemetry in the exact format BMSMonitor parses, over
   **both** USB serial and BLE (Nordic UART Service).
2. **Receives** parameter/threshold edits back from the host and applies them
   live (over serial RX and the BLE NUS RX characteristic).

## Flashing

- **Board:** any ESP32 (classic / S3 / C3 …), Arduino-ESP32 core installed.
- **Libraries:** none — BLE ships with the core.
- Open `BMS_ESP32_DummyFirmware.ino` in Arduino IDE, pick your board + port,
  Upload. (Set `USE_BLE 0` near the top if you only want the USB path.)

## Connect from BMSMonitor

- **USB:** Control Panel → Serial tab → pick the COM port → 115200 baud →
  Connect. You should see live cells/temps/SoC and the frame counter climbing.
- **BLE:** Control Panel → Bluetooth tab → Scan → pick **BMS-ESP32** → Connect.

## Telemetry format (device → host)

One JSON object per line, `\n`-terminated:

```json
{"v":74.12,"i":-2.50,"soc":78.0,"st":"discharging","cells":[3.682, …20…],"temps":[28.0, …10…],"bal":[0,5,12]}
```

`i` is negative while discharging. `st` is one of `idle` / `charging` /
`discharging`. `bal` lists the indices of cells currently balancing.

## Config format (host → device)

Send a JSON line (any subset of keys) ending in `\n`:

```json
{"ovp":4.25,"uvp":2.70,"bsd":15}
```

| key | BmsConfig field        | unit |
|-----|------------------------|------|
| cap | NominalCapacityAh      | Ah   |
| dod | MaxDod                 | %    |
| mcc | MaxChargeCurrent       | A    |
| mdc | MaxDischargeCurrent    | A    |
| ovp | OvervoltageThreshold   | V    |
| hvw | HighVoltageWarning     | V    |
| uvp | UndervoltageThreshold  | V    |
| lvw | LowVoltageWarning      | V    |
| otw | OverTempWarning        | °C   |
| otc | OverTempCutoff         | °C   |
| bsd | balancing **start** Δ  | mV   |
| bpd | balancing **stop** Δ   | mV   |

The full property names (e.g. `"OvervoltageThreshold"`) are accepted as
aliases. Send `{"cmd":"get"}` to dump the active config. After any change the
device echoes an ack line: `{"ack":"cfg", …}` (valid JSON, ignored by the
host's telemetry parser).

## ⚠️ The app does not send config yet

In the current BMSMonitor build, the Control Panel **Apply** button only saves
settings locally (`ViewModel.SaveSettings()`); it never writes to the serial /
BLE link. So the receive path above is ready on the device, but nothing in the
app transmits to it today.

To test the receive path **now**, send a config line manually:

- **Serial:** any terminal (Arduino Serial Monitor with line ending = Newline,
  PuTTY, `pyserial`, etc.) at 115200 — type `{"ovp":4.25}` and send.
- **BLE:** a generic BLE app (nRF Connect) — write to char `6E400002-…`.

Watch the `{"ack":"cfg",…}` echo come back to confirm it applied.

If you want, the app's **Apply** button can be wired to actually push this
config over the active transport — ask and it can be added.
