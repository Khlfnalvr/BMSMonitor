// ─────────────────────────────────────────────────────────────────────────
//  BMS_ESP32_DummyFirmware
//
//  Dummy ESP32 "BMS master" for testing the BMSMonitor desktop app.
//
//  WHAT IT DOES
//    1. Emits one simulated telemetry JSON snapshot per line over BOTH:
//         • USB serial (the COM port BMSMonitor opens at 115200 baud)
//         • BLE Nordic UART Service (NUS) — notify char 6E400003-…
//       The format matches exactly what BMSMonitor's parser expects
//       (see SerialService.TryParseLine in the desktop app).
//
//    2. Receives parameter / threshold edits coming back from the host over:
//         • USB serial RX (just type/send a line ending in '\n')
//         • BLE NUS RX char 6E400002-… (host -> device WRITE)
//       and applies them live to the simulation, then echoes the active
//       config back as an ack frame.
//
//  WIRE FORMAT — telemetry (device -> host), one object per line, '\n' end:
//
//    {"v":74.12,"i":-2.50,"soc":78.0,"st":"discharging",
//     "cells":[3.682, …20 values…],
//     "temps":[28.0, …10 values…],
//     "bal":[0,5,12]}
//
//    Fields the host understands: v, i, soc, st, cells[], temps[], bal[].
//    Missing fields keep their previous value on the host side.
//
//  WIRE FORMAT — config (host -> device), one JSON object per line, '\n' end:
//
//    {"cap":20,"dod":80,"mcc":20,"mdc":40,
//     "ovp":4.20,"hvw":4.10,"uvp":2.80,"lvw":3.00,
//     "otw":60,"otc":70,"bsd":20,"bpd":5}
//
//    You can send any subset — only the keys present are updated. The long
//    BmsConfig property names (e.g. "OvervoltageThreshold") are also accepted
//    as aliases. Send {"cmd":"get"} to dump the current config.
//
//  KEY -> BmsConfig mapping:
//    cap = NominalCapacityAh        dod = MaxDod
//    mcc = MaxChargeCurrent         mdc = MaxDischargeCurrent
//    ovp = OvervoltageThreshold     hvw = HighVoltageWarning
//    uvp = UndervoltageThreshold    lvw = LowVoltageWarning
//    otw = OverTempWarning          otc = OverTempCutoff
//    bsd = BalancingStart delta mV  bpd = BalancingStop delta mV
//
//  BOARD: any ESP32 (classic / S3 / C3 …) on the Arduino-ESP32 core.
//         No external libraries required — BLE ships with the core.
//         Set USE_BLE to 0 below if you only want the USB-serial path
//         (smaller build, no Bluetooth).
// ─────────────────────────────────────────────────────────────────────────

#include <Arduino.h>
#include <ctype.h>

// ── Compile-time options ───────────────────────────────────────────────────
#define USE_BLE             1          // 1 = also advertise BLE NUS, 0 = serial only
#define SERIAL_BAUD         115200     // must match BMSMonitor's selected baud
#define DEVICE_NAME         "BMS-ESP32"
#define NUM_CELLS           20         // host accepts up to 20
#define NUM_TEMPS           10         // host accepts up to 10
#define TELEMETRY_PERIOD_MS 500        // 2 Hz — comfortable for the host

// ── Active configuration (mirrors the app's BmsConfig) ─────────────────────
struct BmsConfig {
  float capacityAh          = 20.0f;   // cap
  float maxDod              = 80.0f;   // dod  (%)
  float maxChargeCurrent    = 20.0f;   // mcc  (A)
  float maxDischargeCurrent = 40.0f;   // mdc  (A)
  float ovp                 = 4.20f;   // ovp  overvoltage cutoff (V)
  float hvw                 = 4.10f;   // hvw  high-voltage warning (V)
  float uvp                 = 2.80f;   // uvp  undervoltage cutoff (V)
  float lvw                 = 3.00f;   // lvw  low-voltage warning (V)
  float overTempWarn        = 60.0f;   // otw  (°C)
  float overTempCutoff      = 70.0f;   // otc  (°C)
  float balStartMv          = 20.0f;   // bsd  start balancing above this delta (mV)
  float balStopMv           = 5.0f;    // bpd  stop balancing below this delta (mV)
};
BmsConfig cfg;

// ── Simulation state ───────────────────────────────────────────────────────
float cells[NUM_CELLS];
float temps[NUM_TEMPS];
float soc      = 50.0f;     // %
int   socDir   = +1;        // +1 charging, -1 discharging
float current  = 0.0f;      // A (+ charge / - discharge)

// ── Buffers ────────────────────────────────────────────────────────────────
static char    txbuf[640];          // outgoing telemetry / ack line
static String  serialRxBuf;         // accumulates USB-serial inbound until '\n'
static volatile bool g_echoConfig = false;  // set when config changes / on get
unsigned long lastSend = 0;

// ───────────────────────────────────────────────────────────────────────────
//  BLE (Nordic UART Service)
// ───────────────────────────────────────────────────────────────────────────
#if USE_BLE
#include <BLEDevice.h>
#include <BLEServer.h>
#include <BLEUtils.h>
#include <BLE2902.h>

#define NUS_SERVICE_UUID "6E400001-B5A3-F393-E0A9-E50E24DCCA9E"
#define NUS_RX_UUID      "6E400002-B5A3-F393-E0A9-E50E24DCCA9E"  // host -> device (WRITE)
#define NUS_TX_UUID      "6E400003-B5A3-F393-E0A9-E50E24DCCA9E"  // device -> host (NOTIFY)

static BLECharacteristic* txChar = nullptr;
static volatile bool      bleConnected = false;
static String             bleRxBuf;           // accumulates BLE inbound until '\n'

// Forward decl — defined below.
void handleInboundLine(String line);

class ServerCallbacks : public BLEServerCallbacks {
  void onConnect(BLEServer*) override    { bleConnected = true; }
  void onDisconnect(BLEServer*) override { bleConnected = false; BLEDevice::startAdvertising(); }
};

class RxCallbacks : public BLECharacteristicCallbacks {
  void onWrite(BLECharacteristic* c) override {
    uint8_t* data = c->getData();
    size_t   len  = c->getLength();
    for (size_t i = 0; i < len; i++) {
      char ch = (char)data[i];
      if (ch == '\n')      { handleInboundLine(bleRxBuf); bleRxBuf = ""; }
      else if (ch != '\r') { bleRxBuf += ch; if (bleRxBuf.length() > 1024) bleRxBuf = ""; }
    }
    // A host may write a whole command without a trailing newline; flush it.
    if (bleRxBuf.length() > 0) { handleInboundLine(bleRxBuf); bleRxBuf = ""; }
  }
};

void setupBle() {
  BLEDevice::init(DEVICE_NAME);
  BLEServer* server = BLEDevice::createServer();
  server->setCallbacks(new ServerCallbacks());

  BLEService* svc = server->createService(NUS_SERVICE_UUID);

  txChar = svc->createCharacteristic(NUS_TX_UUID, BLECharacteristic::PROPERTY_NOTIFY);
  txChar->addDescriptor(new BLE2902());

  BLECharacteristic* rxChar = svc->createCharacteristic(
      NUS_RX_UUID,
      BLECharacteristic::PROPERTY_WRITE | BLECharacteristic::PROPERTY_WRITE_NR);
  rxChar->setCallbacks(new RxCallbacks());

  svc->start();

  BLEAdvertising* adv = BLEDevice::getAdvertising();
  adv->addServiceUUID(NUS_SERVICE_UUID);
  adv->setScanResponse(true);
  BLEDevice::startAdvertising();
}

// Notify in <=20-byte chunks so it survives the default 23-byte MTU; the host
// reassembles on '\n', so chunk boundaries don't matter.
void bleNotify(const char* s, size_t len) {
  if (!bleConnected || txChar == nullptr) return;
  const size_t CHUNK = 20;
  size_t off = 0;
  while (off < len) {
    size_t c = (len - off < CHUNK) ? (len - off) : CHUNK;
    txChar->setValue((uint8_t*)(s + off), c);
    txChar->notify();
    off += c;
    delay(3);   // brief gap to avoid flooding the controller's tx queue
  }
}
#endif  // USE_BLE

// ───────────────────────────────────────────────────────────────────────────
//  Output: send one line to every active transport
// ───────────────────────────────────────────────────────────────────────────
void sendLine(const char* s) {
  Serial.print(s);                 // s already ends in '\n'
#if USE_BLE
  bleNotify(s, strlen(s));
#endif
}

// ───────────────────────────────────────────────────────────────────────────
//  Minimal JSON helper — pull a numeric value for a flat "key":number pair.
//  Good enough for the flat config command; avoids any external dependency.
// ───────────────────────────────────────────────────────────────────────────
bool jsonGetNumber(const String& src, const char* key, double& out) {
  String token = "\"";
  token += key;
  token += "\"";
  int k = src.indexOf(token);
  if (k < 0) return false;

  int i = k + token.length();
  while (i < (int)src.length() && (src[i] == ' ' || src[i] == '\t')) i++;
  if (i >= (int)src.length() || src[i] != ':') return false;   // not a key, it's a value
  i++;
  while (i < (int)src.length() && (src[i] == ' ' || src[i] == '\t')) i++;

  int start = i;
  if (i < (int)src.length() && (src[i] == '+' || src[i] == '-')) i++;
  bool any = false;
  while (i < (int)src.length()) {
    char ch = src[i];
    if (isdigit((unsigned char)ch) || ch == '.' || ch == 'e' || ch == 'E' || ch == '+' || ch == '-') {
      i++; any = true;
    } else break;
  }
  if (!any) return false;
  out = src.substring(start, i).toDouble();
  return true;
}

// Try a short key first, then an optional long alias.
bool getCfg(const String& line, double& v, const char* shortKey, const char* longKey = nullptr) {
  if (jsonGetNumber(line, shortKey, v)) return true;
  if (longKey && jsonGetNumber(line, longKey, v)) return true;
  return false;
}

// ───────────────────────────────────────────────────────────────────────────
//  Inbound: apply a config command line from the host
// ───────────────────────────────────────────────────────────────────────────
void handleInboundLine(String line) {
  line.trim();
  if (line.length() == 0) return;

  // {"cmd":"get"} -> just echo current config.
  if (line.indexOf("\"get\"") >= 0) { g_echoConfig = true; return; }

  bool changed = false;
  double v;
  if (getCfg(line, v, "cap", "NominalCapacityAh"))      { cfg.capacityAh          = (float)v; changed = true; }
  if (getCfg(line, v, "dod", "MaxDod"))                 { cfg.maxDod              = (float)v; changed = true; }
  if (getCfg(line, v, "mcc", "MaxChargeCurrent"))       { cfg.maxChargeCurrent    = (float)v; changed = true; }
  if (getCfg(line, v, "mdc", "MaxDischargeCurrent"))    { cfg.maxDischargeCurrent = (float)v; changed = true; }
  if (getCfg(line, v, "ovp", "OvervoltageThreshold"))   { cfg.ovp                 = (float)v; changed = true; }
  if (getCfg(line, v, "hvw", "HighVoltageWarning"))     { cfg.hvw                 = (float)v; changed = true; }
  if (getCfg(line, v, "uvp", "UndervoltageThreshold"))  { cfg.uvp                 = (float)v; changed = true; }
  if (getCfg(line, v, "lvw", "LowVoltageWarning"))      { cfg.lvw                 = (float)v; changed = true; }
  if (getCfg(line, v, "otw", "OverTempWarning"))        { cfg.overTempWarn        = (float)v; changed = true; }
  if (getCfg(line, v, "otc", "OverTempCutoff"))         { cfg.overTempCutoff      = (float)v; changed = true; }
  if (getCfg(line, v, "bsd", "BalancingStartDeltaMv"))  { cfg.balStartMv          = (float)v; changed = true; }
  if (getCfg(line, v, "bpd", "BalancingStopDeltaMv"))   { cfg.balStopMv           = (float)v; changed = true; }

  if (changed) g_echoConfig = true;   // ack with the now-active config
}

// Echo the live config back as a JSON line. It is valid JSON, so the host
// parser accepts it harmlessly (no telemetry keys -> no state change).
void sendConfigEcho() {
  snprintf(txbuf, sizeof(txbuf),
    "{\"ack\":\"cfg\",\"cap\":%.1f,\"dod\":%.1f,\"mcc\":%.1f,\"mdc\":%.1f,"
    "\"ovp\":%.3f,\"hvw\":%.3f,\"uvp\":%.3f,\"lvw\":%.3f,"
    "\"otw\":%.1f,\"otc\":%.1f,\"bsd\":%.1f,\"bpd\":%.1f}\n",
    cfg.capacityAh, cfg.maxDod, cfg.maxChargeCurrent, cfg.maxDischargeCurrent,
    cfg.ovp, cfg.hvw, cfg.uvp, cfg.lvw,
    cfg.overTempWarn, cfg.overTempCutoff, cfg.balStartMv, cfg.balStopMv);
  sendLine(txbuf);
}

// ───────────────────────────────────────────────────────────────────────────
//  Simulation: advance one step (called every TELEMETRY_PERIOD_MS)
// ───────────────────────────────────────────────────────────────────────────
void simulateStep() {
  // Ramp SoC up then down between 20% and 100% to exercise charge/discharge.
  soc += socDir * 0.5f;                       // ~1%/s at 2 Hz
  if (soc >= 100.0f) { soc = 100.0f; socDir = -1; }
  if (soc <=  20.0f) { soc =  20.0f; socDir = +1; }

  // Map SoC to a plausible per-cell base voltage between lvw-ish and hvw.
  float base = cfg.lvw + (soc / 100.0f) * (cfg.hvw - cfg.lvw);

  // Slight per-cell spread (so balancing actually triggers) + random jitter.
  for (int i = 0; i < NUM_CELLS; i++) {
    float spread = i * 0.002f;                       // up to ~38 mV across the pack
    float jitter = (float)random(-15, 16) / 1000.0f; // ±15 mV
    cells[i] = base + spread + jitter;
  }

  // Temperatures: warmer at higher SoC, with noise.
  for (int i = 0; i < NUM_TEMPS; i++) {
    temps[i] = 25.0f + (soc / 100.0f) * 15.0f + (float)random(-10, 11) / 10.0f;
  }

  // Current sign follows charge/discharge direction; magnitude ~half the limit.
  current = (socDir > 0) ?  cfg.maxChargeCurrent    * 0.5f
                         : -cfg.maxDischargeCurrent * 0.5f;
}

// Build the telemetry JSON line into txbuf.
void buildTelemetry() {
  float vpack = 0.0f;
  float vmin  = cells[0];
  for (int i = 0; i < NUM_CELLS; i++) { vpack += cells[i]; if (cells[i] < vmin) vmin = cells[i]; }

  const char* st = (current >  0.05f) ? "charging"
                 : (current < -0.05f) ? "discharging"
                                      : "idle";

  int n = snprintf(txbuf, sizeof(txbuf),
                   "{\"v\":%.2f,\"i\":%.2f,\"soc\":%.1f,\"st\":\"%s\",\"cells\":[",
                   vpack, current, soc, st);

  for (int i = 0; i < NUM_CELLS; i++)
    n += snprintf(txbuf + n, sizeof(txbuf) - n, "%s%.3f", i ? "," : "", cells[i]);

  n += snprintf(txbuf + n, sizeof(txbuf) - n, "],\"temps\":[");
  for (int i = 0; i < NUM_TEMPS; i++)
    n += snprintf(txbuf + n, sizeof(txbuf) - n, "%s%.1f", i ? "," : "", temps[i]);

  // Balancing: cells whose delta above the pack minimum exceeds the start
  // threshold (in mV) are reported as balancing.
  n += snprintf(txbuf + n, sizeof(txbuf) - n, "],\"bal\":[");
  bool first = true;
  for (int i = 0; i < NUM_CELLS; i++) {
    if ((cells[i] - vmin) * 1000.0f >= cfg.balStartMv) {
      n += snprintf(txbuf + n, sizeof(txbuf) - n, "%s%d", first ? "" : ",", i);
      first = false;
    }
  }
  snprintf(txbuf + n, sizeof(txbuf) - n, "]}\n");
}

// ───────────────────────────────────────────────────────────────────────────
//  Arduino entry points
// ───────────────────────────────────────────────────────────────────────────
void setup() {
  Serial.begin(SERIAL_BAUD);
  randomSeed(esp_random());

  for (int i = 0; i < NUM_CELLS; i++) cells[i] = 3.7f;
  for (int i = 0; i < NUM_TEMPS; i++) temps[i] = 25.0f;

#if USE_BLE
  setupBle();
#endif
}

void loop() {
  // 1) Drain USB-serial inbound, split on '\n'.
  while (Serial.available()) {
    char ch = (char)Serial.read();
    if (ch == '\n')      { handleInboundLine(serialRxBuf); serialRxBuf = ""; }
    else if (ch != '\r') { serialRxBuf += ch; if (serialRxBuf.length() > 1024) serialRxBuf = ""; }
  }

  // 2) Config ack/echo (set from either transport's inbound handler).
  if (g_echoConfig) { g_echoConfig = false; sendConfigEcho(); }

  // 3) Periodic telemetry.
  unsigned long now = millis();
  if (now - lastSend >= TELEMETRY_PERIOD_MS) {
    lastSend = now;
    simulateStep();
    buildTelemetry();
    sendLine(txbuf);
  }
}
