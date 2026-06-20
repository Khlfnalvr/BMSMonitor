// ─────────────────────────────────────────────────────────────────────────
//  BMS_ESP32_Slave_CAN
//
//  ESP32 "BMS slave" that GENERATES dummy BMS telemetry and broadcasts it on
//  a CAN bus through an MCP2551 transceiver. An ESP master (running
//  BMS_ESP32_Master_CAN) receives these frames and re-emits them as JSON over
//  USB serial for the BMSMonitor desktop app.
//
//  This is the CAN-bus version of BMS_ESP32_DummyFirmware: same simulation,
//  but the output goes out as CAN frames instead of serial/BLE JSON.
//
//  HARDWARE
//    ESP32  GPIO26 (CAN TX) ── TXD       ┐ MCP2551 ┌ CANH ── twisted ── CANH ┐ to
//    ESP32  GPIO27 (CAN RX) ◄─ level-shift─ RXD     ┘ (5 V!)   └ CANL ── pair    ── CANL ┘ master
//    ESP32  5V   ── MCP2551 VCC         (MCP2551 is a 5 V part — RXD swings to 5 V)
//    ESP32  GND  ── MCP2551 GND ── common GND with the master node
//    MCP2551 Rs  ── GND               (high-speed mode; floating/high = standby)
//    IMPORTANT: MCP2551 RXD output swings to VCC (5 V). Feeding it straight into
//    an ESP32 GPIO (3.3 V max) can damage the pin — put a level shifter or a
//    simple resistor divider on the RXD→GPIO27 line. TXD (ESP32 3.3 V → MCP2551)
//    is fine as-is.
//    120 Ω termination at BOTH ends of the bus (most breakouts have it onboard).
//
//  CAN: 500 kbit/s, standard 11-bit IDs. See the protocol table below — it
//  MUST match BMS_ESP32_Master_CAN.
//
//  BOARD: any ESP32 with the built-in TWAI controller (classic / S3 / C3 …).
//         No external libraries — the TWAI driver ships with the ESP32 core.
// ─────────────────────────────────────────────────────────────────────────

#include <Arduino.h>
#include <math.h>
#include "driver/twai.h"

// ── Pins / bus (match the master) ──────────────────────────────────────────
#define CAN_TX_PIN   GPIO_NUM_26
#define CAN_RX_PIN   GPIO_NUM_27
#define NUM_CELLS    20
#define NUM_TEMPS    10
#define TX_PERIOD_MS 500          // 2 Hz snapshot rate
#define DEBUG_PRINT  1            // 1 = echo a short status line on USB serial

// ── CAN protocol — keep identical on both nodes ────────────────────────────
//  0x100 PACK  (7 B): [0:1] Vpack u16 0.01V · [2:3] I i16 0.01A · [4:5] SoC u16 0.01% · [6] status
//  0x110..114  CELLS (8 B): 4 cells/frame, u16 mV  (0x110=cells0-3 … 0x114=cells16-19)
//  0x120..122  TEMPS: i16 0.1°C  (0x120=temps0-3 · 0x121=temps4-7 · 0x122=temps8-9)
//  0x130 BAL   (3 B): bitmask, bit i set => cell i balancing  (LAST frame of a snapshot)
#define CAN_ID_PACK   0x100
#define CAN_ID_CELLS0 0x110
#define CAN_ID_TEMPS0 0x120
#define CAN_ID_BAL    0x130

// status enum sent in the PACK frame
enum { ST_IDLE = 0, ST_CHARGING = 1, ST_DISCHARGING = 2 };

// ── Thresholds the simulation uses (e.g. to decide which cells balance) ─────
struct {
  float balStartMv = 20.0f;   // a cell balances when it sits this far above the pack minimum
  float hvw        = 4.10f;   // high-voltage point (top of the SoC->voltage map)
  float lvw        = 3.00f;   // low-voltage point  (bottom of the map)
  float maxCharge  = 20.0f;   // A
  float maxDischg  = 40.0f;   // A
} cfg;

// ── Simulation state ───────────────────────────────────────────────────────
float cells[NUM_CELLS];
float temps[NUM_TEMPS];
float soc     = 50.0f;
int   socDir  = +1;
float current = 0.0f;
unsigned long lastTx = 0;
bool  linkUp  = false;          // runtime link state (frames being ACKed?)

// ── Little-endian pack helpers ─────────────────────────────────────────────
static inline void put_u16(uint8_t* p, uint16_t v) { p[0] = v & 0xFF; p[1] = v >> 8; }
static inline void put_i16(uint8_t* p, int16_t  v) { put_u16(p, (uint16_t)v); }

// ── CAN TX ─────────────────────────────────────────────────────────────────
bool canSend(uint32_t id, const uint8_t* data, uint8_t len) {
  twai_message_t m = {};          // zero-inits flags (extd=0, rtr=0 → standard data frame)
  m.identifier       = id;
  m.ss               = 1;         // single-shot: attempt once, don't auto-retransmit. On a bus
                                  // where nobody ACKs, endless retransmits drive the controller
                                  // bus-off and underflow the TWAI tx counter → the
                                  // "twai_handle_tx_buffer_frame ... tx_msg_count >= 0" panic.
  m.data_length_code = len;
  for (uint8_t i = 0; i < len; i++) m.data[i] = data[i];
  return twai_transmit(&m, pdMS_TO_TICKS(10)) == ESP_OK;
}

// Encode the current simulation state into 10 CAN frames; BAL is sent last so
// the master can treat it as the end-of-snapshot marker.
void sendSnapshot() {
  uint8_t d[8];

  // PACK
  float vsum = 0.0f;
  for (int i = 0; i < NUM_CELLS; i++) vsum += cells[i];
  uint8_t status = (current >  0.05f) ? ST_CHARGING
                 : (current < -0.05f) ? ST_DISCHARGING
                                      : ST_IDLE;
  put_u16(d + 0, (uint16_t)lroundf(vsum    * 100.0f));   // 0.01 V
  put_i16(d + 2, (int16_t) lroundf(current * 100.0f));   // 0.01 A (signed)
  put_u16(d + 4, (uint16_t)lroundf(soc     * 100.0f));   // 0.01 %
  d[6] = status;
  canSend(CAN_ID_PACK, d, 7);

  // CELLS — 5 frames × 4 cells (u16 mV)
  for (int f = 0; f < 5; f++) {
    for (int k = 0; k < 4; k++)
      put_u16(d + k * 2, (uint16_t)lroundf(cells[f * 4 + k] * 1000.0f));
    canSend(CAN_ID_CELLS0 + f, d, 8);
  }

  // TEMPS — 2 frames × 4 + 1 frame × 2 (i16 0.1 °C)
  for (int f = 0; f < 3; f++) {
    int count = (f < 2) ? 4 : 2;
    for (int k = 0; k < count; k++)
      put_i16(d + k * 2, (int16_t)lroundf(temps[f * 4 + k] * 10.0f));
    canSend(CAN_ID_TEMPS0 + f, d, count * 2);
  }

  // BAL — cells more than balStartMv above the pack minimum (3-byte bitmask)
  float vmin = cells[0];
  for (int i = 1; i < NUM_CELLS; i++) if (cells[i] < vmin) vmin = cells[i];
  uint32_t bits = 0;
  for (int i = 0; i < NUM_CELLS; i++)
    if ((cells[i] - vmin) * 1000.0f >= cfg.balStartMv) bits |= (1u << i);
  d[0] = bits & 0xFF; d[1] = (bits >> 8) & 0xFF; d[2] = (bits >> 16) & 0xFF;
  canSend(CAN_ID_BAL, d, 3);

#if DEBUG_PRINT
  Serial.printf("[slave] tx  soc=%.1f%%  v=%.2fV  i=%.2fA  st=%u  bal=0x%05X\n",
                soc, vsum, current, status, bits);
#endif
}

// ── Simulation: advance one step ───────────────────────────────────────────
void simulateStep() {
  soc += socDir * 0.5f;                       // ~1 %/s at 2 Hz
  if (soc >= 100.0f) { soc = 100.0f; socDir = -1; }
  if (soc <=  20.0f) { soc =  20.0f; socDir = +1; }

  float base = cfg.lvw + (soc / 100.0f) * (cfg.hvw - cfg.lvw);
  for (int i = 0; i < NUM_CELLS; i++) {
    float spread = i * 0.002f;                       // up to ~38 mV across the pack
    float jitter = (float)random(-15, 16) / 1000.0f; // ±15 mV
    cells[i] = base + spread + jitter;
  }
  for (int i = 0; i < NUM_TEMPS; i++)
    temps[i] = 25.0f + (soc / 100.0f) * 15.0f + (float)random(-10, 11) / 10.0f;

  current = (socDir > 0) ?  cfg.maxCharge  * 0.5f
                         : -cfg.maxDischg  * 0.5f;
}

// ── Bus health: recover automatically from a bus-off condition ─────────────
void serviceBus() {
  twai_status_info_t s;
  if (twai_get_status_info(&s) != ESP_OK) return;
  if (s.state == TWAI_STATE_BUS_OFF) {
    twai_clear_transmit_queue();          // drop pending TX so recovery accounting stays sane
    twai_initiate_recovery();
  } else if (s.state == TWAI_STATE_STOPPED) {
    twai_start();
  }
}

bool canStartNormal() {
  twai_general_config_t g = TWAI_GENERAL_CONFIG_DEFAULT(CAN_TX_PIN, CAN_RX_PIN, TWAI_MODE_NORMAL);
  twai_timing_config_t  t = TWAI_TIMING_CONFIG_500KBITS();
  twai_filter_config_t  f = TWAI_FILTER_CONFIG_ACCEPT_ALL();
  return twai_driver_install(&g, &t, &f) == ESP_OK && twai_start() == ESP_OK;
}

// Runtime link state: our frames need an ACK from the master. No ACK ⇒ the TX
// error counter climbs. Only prints on a state change.
void reportHealth() {
#if DEBUG_PRINT
  twai_status_info_t s;
  if (twai_get_status_info(&s) != ESP_OK) return;
  bool nowUp = (s.state == TWAI_STATE_RUNNING) && (s.tx_error_counter < 96);
  if (nowUp != linkUp) {
    linkUp = nowUp;
    Serial.println(linkUp
      ? "[slave] CAN link UP — frames are being ACKed by the master."
      : "[slave] CAN link DOWN — no ACK (master offline or wiring). auto-retrying...");
  }
#endif
}

// ── Arduino entry points ───────────────────────────────────────────────────
void setup() {
  Serial.begin(115200);
  randomSeed(esp_random());

  for (int i = 0; i < NUM_CELLS; i++) cells[i] = 3.7f;
  for (int i = 0; i < NUM_TEMPS; i++) temps[i] = 25.0f;

  if (canStartNormal())
    Serial.println("[slave] TWAI running @ 500 kbit/s (NORMAL mode).");
  else
    Serial.println("[slave] ERROR: TWAI failed to start.");
}

void loop() {
  serviceBus();
  reportHealth();

  unsigned long now = millis();
  if (now - lastTx >= TX_PERIOD_MS) {
    lastTx = now;
    simulateStep();
    // Only transmit while the controller is actually on-bus; sending during
    // recovery/bus-off is what stresses the TX path.
    twai_status_info_t s;
    if (twai_get_status_info(&s) == ESP_OK && s.state == TWAI_STATE_RUNNING)
      sendSnapshot();
  }
}
