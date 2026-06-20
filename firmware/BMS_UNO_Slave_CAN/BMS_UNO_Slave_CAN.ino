// ─────────────────────────────────────────────────────────────────────────
//  BMS_UNO_Slave_CAN
//
//  Arduino Uno "BMS slave": generates dummy BMS telemetry and broadcasts it
//  on a CAN bus, so the Uno master (BMS_UNO_Master_CAN) — or an ESP32 master
//  — can forward it to BMSMonitor. Same CAN protocol as the ESP32 sketches, so
//  Uno and ESP32 nodes interoperate on one bus.
//
//  WHY AN MCP2515?
//    The Uno (ATmega328P) has NO built-in CAN controller. The SN65HVD230 is
//    only a transceiver, so it can't talk to the Uno on its own. You need an
//    MCP2515 CAN controller (SPI) between them:
//
//      Uno --SPI--> MCP2515 --TXCAN/RXCAN--> SN65HVD230 --CANH/CANL--> bus
//
//  WIRING  (Uno ↔ MCP2515, SPI)
//    Uno D10 → MCP2515 CS        Uno D13 → MCP2515 SCK
//    Uno D11 → MCP2515 SI(MOSI)  Uno D12 ← MCP2515 SO(MISO)
//    (MCP2515 INT not used — we poll.)
//
//  TRANSCEIVER (MCP2515 ↔ SN65HVD230)
//    MCP2515 TXCAN → SN65HVD230 D (TXD)   MCP2515 RXCAN → SN65HVD230 R (RXD)
//    SN65HVD230 is a 3.3 V part. Easiest is a standard MCP2515 module (it has
//    a 5 V transceiver onboard) wired straight to the Uno at 5 V. To use the
//    SN65HVD230 specifically, run the MCP2515 + SN65HVD230 at 3.3 V and level-
//    shift the SPI lines from the 5 V Uno (see README_CAN.md).
//    120 Ω termination at both bus ends; common GND between nodes.
//
//  LIBRARY: "MCP_CAN" by coryjfowler (Library Manager). No other deps.
//  CAN: 500 kbit/s, standard 11-bit IDs.
// ─────────────────────────────────────────────────────────────────────────

#include <SPI.h>
#include <mcp_can.h>

// ── Board / module config ───────────────────────────────────────────────────
#define MCP_CS_PIN   10
#define MCP_CLOCK    MCP_8MHZ     // crystal on YOUR module: 8 MHz → MCP_8MHZ, 16 MHz → MCP_16MHZ
#define NUM_CELLS    20
#define NUM_TEMPS    10
#define TX_PERIOD_MS 500
#define DEBUG_PRINT  1

// ── CAN protocol — identical to the ESP32 / Uno master ─────────────────────
#define CAN_ID_PACK   0x100       // PACK : Vpack/I/SoC/status
#define CAN_ID_CELLS0 0x110       // 0x110..0x114 : 4 cells/frame (u16 mV)
#define CAN_ID_TEMPS0 0x120       // 0x120..0x122 : i16 0.1 °C
#define CAN_ID_BAL    0x130       // bitmask, LAST frame of a snapshot
#define SELFTEST_ID   0x551       // unique per node — used only by the boot self-test

enum { ST_IDLE = 0, ST_CHARGING = 1, ST_DISCHARGING = 2 };

// Thresholds the simulation uses (e.g. which cells balance).
struct {
  float balStartMv = 20.0;
  float hvw = 4.10, lvw = 3.00, maxCharge = 20, maxDischg = 40;
} cfg;

// ── Simulation state ────────────────────────────────────────────────────────
float cells[NUM_CELLS];
float temps[NUM_TEMPS];
float soc = 50.0;
int   socDir = +1;
float current = 0.0;
unsigned long lastTx = 0;
bool  linkUp = false;

MCP_CAN CAN0(MCP_CS_PIN);

// ── Little-endian pack helpers ─────────────────────────────────────────────
void put_u16(byte* p, uint16_t v) { p[0] = v & 0xFF; p[1] = (v >> 8) & 0xFF; }
void put_i16(byte* p, int16_t  v) { put_u16(p, (uint16_t)v); }

// sendMsgBuf returns CAN_OK once the frame is acknowledged on the bus; with no
// master present it times out (no ACK) — we use that as the link indicator.
bool canSend(unsigned long id, byte* data, byte len) {
  return CAN0.sendMsgBuf(id, 0 /*standard*/, len, data) == CAN_OK;
}

void sendSnapshot() {
  byte d[8];
  bool allOk = true;

  // PACK
  float vsum = 0;
  for (int i = 0; i < NUM_CELLS; i++) vsum += cells[i];
  byte status = (current > 0.05) ? ST_CHARGING : (current < -0.05) ? ST_DISCHARGING : ST_IDLE;
  put_u16(d + 0, (uint16_t)lround(vsum    * 100.0));
  put_i16(d + 2, (int16_t) lround(current * 100.0));
  put_u16(d + 4, (uint16_t)lround(soc     * 100.0));
  d[6] = status;
  allOk &= canSend(CAN_ID_PACK, d, 7);

  // CELLS — 5 frames × 4 cells (u16 mV)
  for (int f = 0; f < 5; f++) {
    for (int k = 0; k < 4; k++)
      put_u16(d + k * 2, (uint16_t)lround(cells[f * 4 + k] * 1000.0));
    allOk &= canSend(CAN_ID_CELLS0 + f, d, 8);
  }

  // TEMPS — 2 frames × 4 + 1 frame × 2 (i16 0.1 °C)
  for (int f = 0; f < 3; f++) {
    int count = (f < 2) ? 4 : 2;
    for (int k = 0; k < count; k++)
      put_i16(d + k * 2, (int16_t)lround(temps[f * 4 + k] * 10.0));
    allOk &= canSend(CAN_ID_TEMPS0 + f, d, count * 2);
  }

  // BAL — cells more than balStartMv above the pack minimum (3-byte bitmask)
  float vmin = cells[0];
  for (int i = 1; i < NUM_CELLS; i++) if (cells[i] < vmin) vmin = cells[i];
  uint32_t bits = 0;
  for (int i = 0; i < NUM_CELLS; i++)
    if ((cells[i] - vmin) * 1000.0 >= cfg.balStartMv) bits |= (1UL << i);
  d[0] = bits & 0xFF; d[1] = (bits >> 8) & 0xFF; d[2] = (bits >> 16) & 0xFF;
  allOk &= canSend(CAN_ID_BAL, d, 3);

#if DEBUG_PRINT
  Serial.print(F("[slave] tx soc=")); Serial.print(soc, 1);
  Serial.print(F(" v="));            Serial.print(vsum, 2);
  Serial.print(F(" i="));            Serial.print(current, 2);
  Serial.print(F(" bal=0x"));        Serial.println(bits, HEX);
#endif

  if (allOk != linkUp) {
    linkUp = allOk;
#if DEBUG_PRINT
    Serial.println(linkUp
      ? F("[slave] CAN link UP — frames are being ACKed by the master.")
      : F("[slave] CAN link DOWN — no ACK (master offline or wiring)."));
#endif
  }
}

// ── Simulation: advance one step ───────────────────────────────────────────
void simulateStep() {
  soc += socDir * 0.5;
  if (soc >= 100.0) { soc = 100.0; socDir = -1; }
  if (soc <=  20.0) { soc =  20.0; socDir = +1; }

  float base = cfg.lvw + (soc / 100.0) * (cfg.hvw - cfg.lvw);
  for (int i = 0; i < NUM_CELLS; i++) {
    float spread = i * 0.002;
    float jitter = (float)random(-15, 16) / 1000.0;
    cells[i] = base + spread + jitter;
  }
  for (int i = 0; i < NUM_TEMPS; i++)
    temps[i] = 25.0 + (soc / 100.0) * 15.0 + (float)random(-10, 11) / 10.0;

  current = (socDir > 0) ? cfg.maxCharge * 0.5 : -cfg.maxDischg * 0.5;
}

// ── Boot self-test: is the MCP2515 reachable & working? ────────────────────
// begin() succeeds only if SPI reaches the MCP2515. Internal LOOPBACK mode
// then sends a frame to itself (no bus / no transceiver involved) to confirm
// the controller's TX+RX path. NOTE: this verifies the MCP2515, not the
// SN65HVD230 — the transceiver is confirmed at runtime by the link state.
bool canSelfTest() {
  if (CAN0.begin(MCP_ANY, CAN_500KBPS, MCP_CLOCK) != CAN_OK) return false;
  if (CAN0.setMode(MCP_LOOPBACK) != MCP2515_OK) return false;

  byte tx[1] = { 0xA5 };
  if (CAN0.sendMsgBuf(SELFTEST_ID, 0, 1, tx) != CAN_OK) return false;

  unsigned long deadline = millis() + 250;
  while (millis() < deadline) {
    if (CAN0.checkReceive() == CAN_MSGAVAIL) {
      unsigned long rxId = 0; byte len = 0; byte buf[8];
      if (CAN0.readMsgBuf(&rxId, &len, buf) == CAN_OK && (rxId & 0x7FF) == SELFTEST_ID)
        return true;
    }
  }
  return false;
}

// ── Arduino entry points ───────────────────────────────────────────────────
void setup() {
  Serial.begin(115200);
  randomSeed(analogRead(A0));

  for (int i = 0; i < NUM_CELLS; i++) cells[i] = 3.7;
  for (int i = 0; i < NUM_TEMPS; i++) temps[i] = 25.0;

  Serial.println(F("[slave] Checking MCP2515 CAN controller (SPI)..."));
  bool ok = false;
  for (int attempt = 1; attempt <= 10 && !ok; attempt++) {
    ok = canSelfTest();
    if (!ok) {
      Serial.print(F("[slave]   not detected (try ")); Serial.print(attempt);
      Serial.println(F("/10) — check CS/SCK/MOSI/MISO, power, crystal (MCP_8MHZ vs MCP_16MHZ)."));
      delay(700);
    }
  }
  Serial.println(ok
    ? F("[slave] MCP2515 detected — controller OK.")
    : F("[slave] WARNING: MCP2515 not confirmed; starting anyway."));

  if (CAN0.begin(MCP_ANY, CAN_500KBPS, MCP_CLOCK) == CAN_OK && CAN0.setMode(MCP_NORMAL) == MCP2515_OK)
    Serial.println(F("[slave] MCP2515 running @ 500 kbit/s (NORMAL)."));
  else
    Serial.println(F("[slave] ERROR: MCP2515 failed to start."));
}

void loop() {
  unsigned long now = millis();
  if (now - lastTx >= TX_PERIOD_MS) {
    lastTx = now;
    simulateStep();
    sendSnapshot();
  }
}
