// ─────────────────────────────────────────────────────────────────────────
//  BMS_UNO_Master_CAN
//
//  Arduino Uno "BMS master": receives BMS telemetry from a slave over CAN
//  (MCP2515 controller + SN65HVD230 transceiver) and re-emits each complete
//  snapshot as one JSON line over USB serial — the exact format BMSMonitor
//  parses. Same CAN protocol as the ESP32 sketches (interoperable).
//
//  WHY AN MCP2515?
//    The Uno has no built-in CAN controller; the SN65HVD230 is only a
//    transceiver. The MCP2515 (SPI) is the controller between them:
//      Uno --SPI--> MCP2515 --TXCAN/RXCAN--> SN65HVD230 --CANH/CANL--> bus
//
//  WIRING  (Uno ↔ MCP2515, SPI)
//    Uno D10 → MCP2515 CS        Uno D13 → MCP2515 SCK
//    Uno D11 → MCP2515 SI(MOSI)  Uno D12 ← MCP2515 SO(MISO)
//    USB → PC running BMSMonitor (Serial tab, 115200 baud).
//    See README_CAN.md for the SN65HVD230 (3.3 V) wiring / level-shift notes.
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
#define STALE_MS     3000

// Plain-text link logging. Keep 0 when BMSMonitor is connected (those lines
// would show up as parse errors). Set to 1 to watch the link in a serial
// monitor. The boot self-test always prints.
#define MASTER_DEBUG 0

// ── CAN protocol — identical to the slave ──────────────────────────────────
#define CAN_ID_PACK   0x100
#define CAN_ID_CELLS0 0x110       // 0x110..0x114
#define CAN_ID_TEMPS0 0x120       // 0x120..0x122
#define CAN_ID_BAL    0x130
#define SELFTEST_ID   0x552       // unique per node — used only by the boot self-test

enum { ST_IDLE = 0, ST_CHARGING = 1, ST_DISCHARGING = 2 };

// ── Latest decoded snapshot ────────────────────────────────────────────────
float    cells[NUM_CELLS];
float    temps[NUM_TEMPS];
float    vpack = 0, current = 0, soc = 0;
byte     status = ST_IDLE;
uint32_t balBits = 0;
bool     gotPack = false;
bool     linkUp = false;
uint32_t rxFrames = 0;
unsigned long lastFrameMs = 0;

MCP_CAN CAN0(MCP_CS_PIN);

// ── Little-endian unpack (AVR-safe: 16-bit unsigned intermediate) ──────────
uint16_t get_u16(byte* p) { return (uint16_t)(((unsigned int)p[1] << 8) | (unsigned int)p[0]); }
int16_t  get_i16(byte* p) { return (int16_t)get_u16(p); }

// ── Build the JSON line BMSMonitor expects and push it to USB serial ───────
// Uses Serial.print(float, n) — AVR's printf has no %f, so we print directly.
void emitJson() {
  if (!gotPack) return;

  const char* st = (status == ST_CHARGING) ? "charging"
                 : (status == ST_DISCHARGING) ? "discharging" : "idle";

  Serial.print(F("{\"v\":"));    Serial.print(vpack, 2);
  Serial.print(F(",\"i\":"));    Serial.print(current, 2);
  Serial.print(F(",\"soc\":"));  Serial.print(soc, 1);
  Serial.print(F(",\"st\":\"")); Serial.print(st);
  Serial.print(F("\",\"cells\":["));
  for (int i = 0; i < NUM_CELLS; i++) { if (i) Serial.print(','); Serial.print(cells[i], 3); }
  Serial.print(F("],\"temps\":["));
  for (int i = 0; i < NUM_TEMPS; i++) { if (i) Serial.print(','); Serial.print(temps[i], 1); }
  Serial.print(F("],\"bal\":["));
  bool first = true;
  for (int i = 0; i < NUM_CELLS; i++) {
    if (balBits & (1UL << i)) { if (!first) Serial.print(','); Serial.print(i); first = false; }
  }
  Serial.print(F("]}\n"));
}

// ── Decode one received CAN frame into the snapshot ────────────────────────
void decodeFrame(unsigned long id, byte len, byte* d) {
  lastFrameMs = millis();

  if (id == CAN_ID_PACK && len >= 7) {
    vpack   = get_u16(d + 0) / 100.0;
    current = get_i16(d + 2) / 100.0;
    soc     = get_u16(d + 4) / 100.0;
    status  = d[6];
    gotPack = true;
  }
  else if (id >= CAN_ID_CELLS0 && id <= CAN_ID_CELLS0 + 4) {
    int base = (id - CAN_ID_CELLS0) * 4;
    int count = len / 2;
    for (int k = 0; k < count && base + k < NUM_CELLS; k++)
      cells[base + k] = get_u16(d + k * 2) / 1000.0;
  }
  else if (id >= CAN_ID_TEMPS0 && id <= CAN_ID_TEMPS0 + 2) {
    int base = (id - CAN_ID_TEMPS0) * 4;
    int count = len / 2;
    for (int k = 0; k < count && base + k < NUM_TEMPS; k++)
      temps[base + k] = get_i16(d + k * 2) / 10.0;
  }
  else if (id == CAN_ID_BAL && len >= 3) {
    balBits = (uint32_t)d[0] | ((uint32_t)d[1] << 8) | ((uint32_t)d[2] << 16);
    emitJson();                 // BAL is last in a snapshot → set is complete
  }
}

// ── Drain everything waiting in the MCP2515 RX buffers ─────────────────────
void pollBmsSlaves() {
  while (CAN0.checkReceive() == CAN_MSGAVAIL) {
    unsigned long rxId = 0; byte len = 0; byte buf[8];
    if (CAN0.readMsgBuf(&rxId, &len, buf) == CAN_OK) {
      rxFrames++;
      unsigned long id = rxId & 0x7FF;   // drop coryjfowler's ext/rtr flag bits
#if MASTER_DEBUG
      Serial.print(F("[master] rx id=0x")); Serial.print(id, HEX);
      Serial.print(F(" len=")); Serial.println(len);
#endif
      decodeFrame(id, len, buf);
    }
  }
}

// ── Boot self-test: is the MCP2515 reachable & working? ────────────────────
// (Verifies the MCP2515 controller via SPI + internal loopback. The SN65HVD230
// transceiver is confirmed at runtime by the link state, since loopback mode
// bypasses the bus.)
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

void reportHealth() {
#if MASTER_DEBUG
  bool nowUp = gotPack && (millis() - lastFrameMs < STALE_MS);
  if (nowUp != linkUp) {
    linkUp = nowUp;
    Serial.println(linkUp
      ? F("[master] CAN link UP — receiving frames from slave.")
      : F("[master] CAN link DOWN — no CAN data (slave offline or wiring)."));
  }
  static unsigned long lastTick = 0;
  if (millis() - lastTick >= 2000) {
    lastTick = millis();
    Serial.print(F("[master] rx=")); Serial.print(rxFrames);
    Serial.print(F(" eflg=0x"));     Serial.println(CAN0.getError(), HEX);
  }
#endif
}

// ── Arduino entry points ───────────────────────────────────────────────────
void setup() {
  Serial.begin(115200);

  Serial.println(F("[master] Checking MCP2515 CAN controller (SPI)..."));
  bool ok = false;
  for (int attempt = 1; attempt <= 10 && !ok; attempt++) {
    ok = canSelfTest();
    if (!ok) {
      Serial.print(F("[master]   not detected (try ")); Serial.print(attempt);
      Serial.println(F("/10) — check CS/SCK/MOSI/MISO, power, crystal (MCP_8MHZ vs MCP_16MHZ)."));
      delay(700);
    }
  }
  Serial.println(ok
    ? F("[master] MCP2515 detected — controller OK.")
    : F("[master] WARNING: MCP2515 not confirmed; starting anyway."));

  if (CAN0.begin(MCP_ANY, CAN_500KBPS, MCP_CLOCK) == CAN_OK && CAN0.setMode(MCP_NORMAL) == MCP2515_OK)
    Serial.println(F("[master] MCP2515 running @ 500 kbit/s — waiting for slave frames."));
  else
    Serial.println(F("[master] ERROR: MCP2515 failed to start."));
}

void loop() {
  pollBmsSlaves();
  reportHealth();
}
