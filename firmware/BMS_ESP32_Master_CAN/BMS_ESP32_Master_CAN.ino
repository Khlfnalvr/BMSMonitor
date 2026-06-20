// ─────────────────────────────────────────────────────────────────────────
//  BMS_ESP32_Master_CAN
//
//  ESP32 "BMS master". Receives BMS telemetry from a slave node over a CAN
//  bus (via an MCP2551 transceiver) and re-emits each complete snapshot as
//  one JSON line over USB serial — in the exact format the BMSMonitor desktop
//  app parses (see SerialService.TryParseLine).
//
//  This is the filled-in version of firmware/esp32_bms_bridge.ino: the same
//  CAN pins / bitrate, with pollBmsSlaves() actually decoding the frames sent
//  by BMS_ESP32_Slave_CAN.
//
//  HARDWARE
//    ESP32  GPIO26 (CAN TX) ── TXD       ┐ MCP2551 ┌ CANH ── twisted ── CANH ┐ to
//    ESP32  GPIO27 (CAN RX) ◄─ level-shift─ RXD     ┘ (5 V!)   └ CANL ── pair    ── CANL ┘ slave
//    ESP32  5V   ── MCP2551 VCC         (MCP2551 is a 5 V part — RXD swings to 5 V)
//    ESP32  GND  ── MCP2551 GND ── common GND with the slave node
//    MCP2551 Rs  ── GND               (high-speed mode; floating/high = standby)
//    IMPORTANT: MCP2551 RXD output swings to VCC (5 V). Feeding it straight into
//    an ESP32 GPIO (3.3 V max) can damage the pin — put a level shifter or a
//    simple resistor divider on the RXD→GPIO27 line. TXD (ESP32 3.3 V → MCP2551)
//    is fine as-is.
//    120 Ω termination at BOTH ends of the bus.
//    USB ── PC running BMSMonitor (Serial tab, 115200 baud).
//
//  CAN: 500 kbit/s, standard 11-bit IDs. Protocol MUST match the slave.
//
//  BOARD: any ESP32 with the built-in TWAI controller. No external libraries.
// ─────────────────────────────────────────────────────────────────────────

#include <Arduino.h>
#include "driver/twai.h"

// ── Pins / serial (match the slave) ────────────────────────────────────────
#define CAN_TX_PIN   GPIO_NUM_26
#define CAN_RX_PIN   GPIO_NUM_33      // moved off GPIO27 — suspected damaged by earlier 5V RXD exposure
#define SERIAL_BAUD  115200       // must match BMSMonitor's selected baud
#define NUM_CELLS    20
#define NUM_TEMPS    10
#define STALE_MS     3000         // no slave frames for this long → link considered down

// Runtime link-state logging. Keep 0 when BMSMonitor is connected: those logs
// are plain text and would show up as parse errors in the app. Set to 1 to
// watch the CAN link in a serial monitor.
#define MASTER_DEBUG 1

// ── CAN protocol — identical to BMS_ESP32_Slave_CAN ────────────────────────
#define CAN_ID_PACK   0x100
#define CAN_ID_CELLS0 0x110       // 0x110..0x114
#define CAN_ID_TEMPS0 0x120       // 0x120..0x122
#define CAN_ID_BAL    0x130

enum { ST_IDLE = 0, ST_CHARGING = 1, ST_DISCHARGING = 2 };

// ── Latest decoded snapshot ────────────────────────────────────────────────
float    cells[NUM_CELLS] = {0};
float    temps[NUM_TEMPS] = {0};
float    vpack   = 0.0f;
float    current = 0.0f;
float    soc     = 0.0f;
uint8_t  status  = ST_IDLE;
uint32_t balBits = 0;
bool     gotPack = false;
bool     linkUp  = false;
uint32_t rxFrames = 0;          // total CAN frames seen (diagnostic)
unsigned long lastFrameMs = 0;

static char txbuf[640];

// ── Little-endian unpack helpers ───────────────────────────────────────────
static inline uint16_t get_u16(const uint8_t* p) { return (uint16_t)(p[0] | (p[1] << 8)); }
static inline int16_t  get_i16(const uint8_t* p) { return (int16_t)get_u16(p); }

// ── Build the JSON line BMSMonitor expects and push it to USB serial ───────
void emitJson() {
  if (!gotPack) return;

  const char* st = (status == ST_CHARGING)    ? "charging"
                 : (status == ST_DISCHARGING) ? "discharging"
                                              : "idle";

  int n = snprintf(txbuf, sizeof(txbuf),
                   "{\"v\":%.2f,\"i\":%.2f,\"soc\":%.1f,\"st\":\"%s\",\"cells\":[",
                   vpack, current, soc, st);
  for (int i = 0; i < NUM_CELLS; i++)
    n += snprintf(txbuf + n, sizeof(txbuf) - n, "%s%.3f", i ? "," : "", cells[i]);

  n += snprintf(txbuf + n, sizeof(txbuf) - n, "],\"temps\":[");
  for (int i = 0; i < NUM_TEMPS; i++)
    n += snprintf(txbuf + n, sizeof(txbuf) - n, "%s%.1f", i ? "," : "", temps[i]);

  n += snprintf(txbuf + n, sizeof(txbuf) - n, "],\"bal\":[");
  bool first = true;
  for (int i = 0; i < NUM_CELLS; i++) {
    if (balBits & (1u << i)) {
      n += snprintf(txbuf + n, sizeof(txbuf) - n, "%s%d", first ? "" : ",", i);
      first = false;
    }
  }
  snprintf(txbuf + n, sizeof(txbuf) - n, "]}\n");

  Serial.print(txbuf);
}

// ── Decode one received CAN frame into the snapshot ────────────────────────
void decodeFrame(const twai_message_t& m) {
  const uint8_t* d = m.data;
  lastFrameMs = millis();

  if (m.identifier == CAN_ID_PACK && m.data_length_code >= 7) {
    vpack   = get_u16(d + 0) / 100.0f;
    current = get_i16(d + 2) / 100.0f;
    soc     = get_u16(d + 4) / 100.0f;
    status  = d[6];
    gotPack = true;
  }
  else if (m.identifier >= CAN_ID_CELLS0 && m.identifier <= CAN_ID_CELLS0 + 4) {
    int base  = (m.identifier - CAN_ID_CELLS0) * 4;
    int count = m.data_length_code / 2;
    for (int k = 0; k < count && base + k < NUM_CELLS; k++)
      cells[base + k] = get_u16(d + k * 2) / 1000.0f;
  }
  else if (m.identifier >= CAN_ID_TEMPS0 && m.identifier <= CAN_ID_TEMPS0 + 2) {
    int base  = (m.identifier - CAN_ID_TEMPS0) * 4;
    int count = m.data_length_code / 2;
    for (int k = 0; k < count && base + k < NUM_TEMPS; k++)
      temps[base + k] = get_i16(d + k * 2) / 10.0f;
  }
  else if (m.identifier == CAN_ID_BAL && m.data_length_code >= 3) {
    balBits = (uint32_t)d[0] | ((uint32_t)d[1] << 8) | ((uint32_t)d[2] << 16);
    // BAL is the last frame of a snapshot → the set is now complete: emit it.
    emitJson();
  }
}

// ── Drain everything waiting in the TWAI RX queue ──────────────────────────
void pollBmsSlaves() {
  twai_message_t m;
  while (twai_receive(&m, 0) == ESP_OK) {
    rxFrames++;
#if MASTER_DEBUG
    Serial.printf("[master] rx id=0x%03X dlc=%u\n", (unsigned)m.identifier, m.data_length_code);
#endif
    if (!m.rtr) decodeFrame(m);
  }
}

// ── Bus health: recover automatically from a bus-off condition ─────────────
void serviceBus() {
  twai_status_info_t s;
  if (twai_get_status_info(&s) != ESP_OK) return;
  if (s.state == TWAI_STATE_BUS_OFF) {
    twai_clear_transmit_queue();
    twai_initiate_recovery();
  } else if (s.state == TWAI_STATE_STOPPED) {
    twai_start();
  }
}

bool canStartNormal() {
  twai_general_config_t g = TWAI_GENERAL_CONFIG_DEFAULT(CAN_TX_PIN, CAN_RX_PIN, TWAI_MODE_NORMAL);
  g.rx_queue_len = 32;            // a full snapshot is 10 frames — give RX headroom
  twai_timing_config_t  t = TWAI_TIMING_CONFIG_500KBITS();
  twai_filter_config_t  f = TWAI_FILTER_CONFIG_ACCEPT_ALL();
  return twai_driver_install(&g, &t, &f) == ESP_OK && twai_start() == ESP_OK;
}

// Runtime link state: are we still receiving frames from the slave? Only prints
// on a state change, and only when MASTER_DEBUG is on (plain text would
// otherwise pollute the JSON stream BMSMonitor reads).
void reportHealth() {
#if MASTER_DEBUG
  bool nowUp = gotPack && (millis() - lastFrameMs < STALE_MS);
  if (nowUp != linkUp) {
    linkUp = nowUp;
    Serial.println(linkUp
      ? "[master] CAN link UP — receiving frames from slave."
      : "[master] CAN link DOWN — no CAN data (slave offline or wiring).");
  }

  // Every 2 s, dump the bus state + counters. If bus=RUNNING but rx stays 0
  // with no errors, frames simply aren't reaching this node (wiring / the slave
  // isn't transmitting). High txErr/rxErr or BUS_OFF ⇒ bad wiring/termination.
  static unsigned long lastTick = 0;
  if (millis() - lastTick >= 2000) {
    lastTick = millis();
    twai_status_info_t s;
    if (twai_get_status_info(&s) == ESP_OK) {
      const char* st = (s.state == TWAI_STATE_RUNNING)    ? "RUNNING"
                     : (s.state == TWAI_STATE_BUS_OFF)    ? "BUS_OFF"
                     : (s.state == TWAI_STATE_RECOVERING) ? "RECOVERING"
                                                          : "STOPPED";
      Serial.printf("[master] bus=%s rx=%lu txErr=%u rxErr=%u rxQ=%u\n",
                    st, (unsigned long)rxFrames, s.tx_error_counter, s.rx_error_counter, s.msgs_to_rx);
    }
  }
#endif
}

// ── Arduino entry points ───────────────────────────────────────────────────
void setup() {
  Serial.begin(SERIAL_BAUD);

  if (canStartNormal())
    Serial.println("[master] TWAI running @ 500 kbit/s — waiting for slave frames.");
  else
    Serial.println("[master] ERROR: TWAI failed to start.");
}

void loop() {
  serviceBus();
  pollBmsSlaves();
  reportHealth();
}
