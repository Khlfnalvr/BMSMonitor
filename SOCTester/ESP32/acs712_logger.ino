// ============================================================
// ACS712 Battery Logger - ESP32
// Output: JSON 1 Hz — kompatibel dengan SOCTester PC app
//
// Wiring:
//   ACS712 VOUT -> voltage divider (R1=33k, R2=66k) -> GPIO34
//   ACS712 VCC  -> 5V (VUSB)
//   ACS712 GND  -> GND ESP32
//
// Format JSON per baris:
//   {"v":1,"soc":<pct>,"pack_v":0.0,"current":<A>,"status":"<str>"}
//   current negatif = discharge (konvensi app)
//
// CATATAN: Karena tidak ada sensor tegangan, pack_v = 0.0.
//   Di app SOCTester pilih algorithm: Coulomb Counting
//   (Voltage-based & Hybrid membutuhkan pack_v yang valid)
// ============================================================

#define PIN_CURRENT   34       // ADC input dari voltage divider
#define R1            33.0f    // kOhm
#define R2            66.0f    // kOhm (2x 33k seri)
#define SENSITIVITY   0.066f   // V/A  → ACS712-30A
                               // ganti: 0.185 (5A), 0.100 (20A), 0.066 (30A)
#define SAMPLES       64       // oversampling untuk noise reduction
#define INTERVAL_MS  1000      // 1 Hz — sinkron dengan app

// CURRENT_SIGN:
//   +1.0 → arus discharge terbaca positif dari ACS712
//   -1.0 → arus discharge terbaca negatif dari ACS712 (flip orientasi sensor)
// Cek di Serial Monitor: saat discharge, pastikan "current" di JSON negatif.
#define CURRENT_SIGN  (-1.0f)

// Kapasitas baterai
const float CAPACITY_AH = 4.0f;   // Ah — sesuaikan dengan sel yang di-test

// Runtime
float OFFSET_V        = 2.5f;
float charge_used_ah  = 0.0f;
float SOC             = 100.0f;
unsigned long lastSOC_ms  = 0;
unsigned long lastPrint   = 0;

// ===== ADC AVERAGING =====
float readADC_avg() {
  long sum = 0;
  for (int i = 0; i < SAMPLES; i++) {
    sum += analogRead(PIN_CURRENT);
  }
  return (float)sum / SAMPLES;
}

// ===== BACA ARUS (A) =====
// Tanda mengikuti CURRENT_SIGN: negatif = discharge (konvensi app)
float readCurrent() {
  float raw   = readADC_avg();
  float v_adc = raw * 3.3f / 4095.0f;
  float v_out = v_adc * (R1 + R2) / R2;          // undo voltage divider
  float I     = (v_out - OFFSET_V) / SENSITIVITY; // A, signed dari ACS712
  I *= CURRENT_SIGN;                               // sesuaikan arah
  if (fabsf(I) < 0.05f) I = 0.0f;                // deadband 50mA
  return I;
}

// ===== UPDATE SOC (Coulomb Counting) =====
void updateSOC(float I, unsigned long now_ms) {
  if (lastSOC_ms == 0) { lastSOC_ms = now_ms; return; }
  float dt_h       = (now_ms - lastSOC_ms) / 3600000.0f;
  // fabsf: discharge (negatif) tetap mengurangi SOC
  charge_used_ah  += fabsf(I) * dt_h;
  SOC              = 100.0f * (1.0f - charge_used_ah / CAPACITY_AH);
  SOC              = constrain(SOC, 0.0f, 100.0f);
  lastSOC_ms       = now_ms;
}

// ===== STATUS STRING =====
const char* getStatus(float I) {
  if (fabsf(I) < 0.05f) return "idle";
  if (I < 0.0f)          return "discharging";   // negatif = discharge
  return "charging";
}

// ===== PRINT JSON FRAME =====
void printFrame(float I) {
  // pack_v = 0.0 karena tidak ada sensor tegangan
  // q_mah  = extra field, diabaikan app tapi berguna di Serial Monitor
  Serial.printf(
    "{\"v\":1,\"soc\":%.2f,\"pack_v\":0.0,\"current\":%.4f,\"status\":\"%s\",\"q_mah\":%.3f}\n",
    SOC, I, getStatus(I), charge_used_ah * 1000.0f
  );
}

void setup() {
  Serial.begin(115200);
  analogReadResolution(12);
  analogSetAttenuation(ADC_11db);

  Serial.println();
  Serial.println("=== ACS712 Battery Logger ===");
  Serial.println("Output : JSON 1Hz (SOCTester app compatible)");
  Serial.println("[Baris tanpa '{' diabaikan oleh app]");
  Serial.println();
  Serial.println("[CAL] Kalibrasi offset (pastikan arus = 0A)...");
  delay(2000);

  // Auto-kalibrasi offset saat startup
  float sum = 0;
  for (int i = 0; i < 256; i++) {
    sum += analogRead(PIN_CURRENT);
    delay(2);
  }
  float raw_avg = sum / 256.0f;
  float v_adc   = raw_avg * 3.3f / 4095.0f;
  OFFSET_V      = v_adc * (R1 + R2) / R2;   // Vout_ACS @ I=0

  Serial.printf("[CAL] Offset = %.4f V (teori: %.1f V)\n", OFFSET_V, 2.5f);
  Serial.println();
  Serial.println("[INFO] Hubungkan beban, lalu buka SOCTester app.");
  Serial.println("[INFO] Pilih algorithm: Coulomb Counting (pack_v = 0)");
  Serial.println("[INFO] Cek tanda arus: discharge harus negatif di app.");
  Serial.println("[INFO]   Jika positif, ganti CURRENT_SIGN menjadi +1.0");
  Serial.println();

  lastPrint  = millis();
  lastSOC_ms = millis();
}

void loop() {
  unsigned long now = millis();

  float I = readCurrent();
  updateSOC(I, now);

  if (now - lastPrint >= INTERVAL_MS) {
    lastPrint = now;
    printFrame(I);
  }
}
