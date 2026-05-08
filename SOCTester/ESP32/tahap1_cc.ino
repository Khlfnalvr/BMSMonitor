/*
 * TAHAP I — Coulomb Counting (CC)
 * NMC 21700 Single Cell | LG INR21700 M50LT
 *
 * Hardware (sesuai dokumen metode_testing_baterai):
 *   GPIO34 → Titik tengah R1(68kΩ)–R2(33kΩ) → V_cell
 *   GPIO35 → Titik tengah Ra(10kΩ)–Rb(10kΩ) → Output ACS712
 *   VIN    → Vcc ACS712 (5V dari USB ESP32)
 *
 * Formula ADC:
 *   V_cell = Vadc_34 × (101/33)           [R1+R2=101k, R2=33k]
 *   I_amp  = ((Vadc_35 × 2.0) − 2.5) / 0.185   [divider 0.5, ACS712 5A]
 *
 * Output: JSON per baris, 1 Hz — kompatibel dengan SOCTester PC app.
 * Baris yang tidak diawali '{' diabaikan oleh app (pesan debug aman).
 *
 * Format JSON:
 *   {"v":1,"soc":<pct>,"pack_v":<V>,"current":<A>,"status":"<str>","q_mah":<mAh>}
 *   current < 0 → discharge (konvensi app: positif = charging)
 *
 * Tujuan: Ukur Q_aktual sel → gunakan sebagai Q_ref Tahap III
 */

// ===== PIN =====
#define PIN_VOLTAGE   34
#define PIN_CURRENT   35

// ===== PARAMETER SEL =====
#define Q_REF_MAH       5000.0f   // Update dengan Q_aktual setelah Tahap I selesai
#define SOC_INIT        100.0f
#define CUTOFF_V        2.5f
#define CUTOFF_WARN_V   2.6f

// ===== ADC / DIVIDER =====
#define VREF            3.3f
#define ADC_MAX         4095.0f
// Voltage divider V_cell: R1=68k, R2=33k → ratio = 33/101
#define VCELL_RATIO     (101.0f / 33.0f)    // Vadc × ini = V_cell
// Divider ACS output: Ra=Rb=10k → ratio = 0.5 → balik ×2
// ACS712-5A: Voffset=2.5V @ 0A (powered 5V), sens=0.185 V/A
#define ACS_VCC         5.0f
#define ACS_VOFFSET     2.5f                // Vcc/2 saat supply 5V
#define ACS_SENS        0.185f              // V/A (5A variant)
#define CURRENT_DEADBAND 0.05f             // A — abaikan noise < 50mA

// ===== SAMPLING =====
#define SAMPLE_COUNT          64
#define SAMPLE_INTERVAL_MS   100    // ms antar coulomb update
#define PRINT_INTERVAL_MS   1000    // ms antar JSON output (1 Hz)

// ===== VARIABEL =====
float acs_voffset_adc = 0.0f;   // Vout_ACS terbaca saat I=0 (setelah koreksi divider)
float soc        = SOC_INIT;
float q_total    = 0.0f;        // mAh terakumulasi
bool  done       = false;

// Simpan nilai terakhir untuk heartbeat setelah cutoff
float last_V     = 0.0f;
float last_I     = 0.0f;

unsigned long t_sample = 0;
unsigned long t_print  = 0;

// ===== ADC AVERAGING =====
float readADC_avg(uint8_t pin) {
  long sum = 0;
  for (int i = 0; i < SAMPLE_COUNT; i++) {
    sum += analogRead(pin);
    delayMicroseconds(100);
  }
  return (float)sum / SAMPLE_COUNT;
}

// ===== BACA TEGANGAN SEL (V) =====
float readVcell() {
  float raw = readADC_avg(PIN_VOLTAGE);
  return (raw / ADC_MAX) * VREF * VCELL_RATIO;
}

// ===== BACA ARUS (A) =====
// Mengembalikan nilai positif saja (discharge); tanda diberikan di printFrame().
float readCurrent() {
  float raw   = readADC_avg(PIN_CURRENT);
  float vadc  = (raw / ADC_MAX) * VREF;
  float vacs  = vadc * 2.0f;                       // balik divider Ra/Rb 0.5
  float I = (vacs - acs_voffset_adc) / ACS_SENS;
  I = fabsf(I);
  if (I < CURRENT_DEADBAND) I = 0.0f;
  return I;
}

// ===== OUTPUT JSON — satu baris, diakhiri \n =====
// current_sign: -1.0 untuk discharge, +1.0 untuk charge
void printFrame(float V, float I, float soc_pct, float q_mah, const char* status) {
  // Konvensi app: current negatif = discharge
  Serial.printf(
    "{\"v\":1,\"soc\":%.2f,\"pack_v\":%.4f,\"current\":%.4f,\"status\":\"%s\",\"q_mah\":%.2f}\n",
    soc_pct, V, -I, status, q_mah
  );
}

// ===== KALIBRASI OFFSET ACS712 =====
void calibrateOffset() {
  Serial.println("[CAL] Kalibrasi offset — pastikan load tester BELUM terhubung...");
  delay(3000);
  long sum = 0;
  for (int i = 0; i < 512; i++) {
    sum += analogRead(PIN_CURRENT);
    delayMicroseconds(100);
  }
  float vadc_avg = ((float)sum / 512.0f / ADC_MAX) * VREF;
  acs_voffset_adc = vadc_avg * 2.0f;   // Vout_ACS @ I=0 (sudah koreksi divider ×2)
  Serial.printf("[CAL] Voffset ACS (Vout_ACS @ I=0) = %.4f V (teori: %.1f V)\n",
                acs_voffset_adc, ACS_VOFFSET);
}

void setup() {
  Serial.begin(115200);
  analogReadResolution(12);
  analogSetPinAttenuation(PIN_VOLTAGE, ADC_11db);
  analogSetPinAttenuation(PIN_CURRENT, ADC_11db);

  Serial.println();
  Serial.println("=== TAHAP I — COULOMB COUNTING ===");
  Serial.println("Sel    : NMC 21700 (~5Ah)");
  Serial.println("Discharge: 0.5C = 2.5A CC, cutoff 2.5V");
  Serial.println("Output : JSON 1Hz (SOCTester app compatible)");
  Serial.println("[Baris tanpa '{' diabaikan oleh app — pesan debug aman]");
  Serial.println();

  calibrateOffset();

  float v_ocv = readVcell();
  last_V = v_ocv;
  Serial.printf("[OCV] V_OCV awal = %.4f V → SOC awal = 100%%\n\n", v_ocv);
  Serial.println("[INFO] Hubungkan load tester, lalu pantau log JSON di bawah ini:");

  t_sample = millis();
  t_print  = millis();
}

void loop() {
  unsigned long now = millis();

  // ── Setelah cutoff: kirim heartbeat "cutoff" setiap 1 Hz ──────────────────
  if (done) {
    if (now - t_print >= PRINT_INTERVAL_MS) {
      t_print = now;
      printFrame(last_V, 0.0f, soc, q_total, "cutoff");
    }
    return;
  }

  // ── Coulomb update setiap SAMPLE_INTERVAL_MS ──────────────────────────────
  if (now - t_sample >= SAMPLE_INTERVAL_MS) {
    float I = readCurrent();
    float V = readVcell();

    unsigned long now2 = millis();
    float dt_h = (float)(now2 - t_sample) / 3600000.0f;
    t_sample = now2;

    float dQ = I * 1000.0f * dt_h;   // mAh (I positif = discharge)
    q_total += dQ;
    soc = SOC_INIT - (q_total / Q_REF_MAH * 100.0f);
    soc = constrain(soc, 0.0f, 100.0f);

    last_V = V;
    last_I = I;

    // ── Print JSON 1 Hz ───────────────────────────────────────────────────
    if (now2 - t_print >= PRINT_INTERVAL_MS) {
      t_print = now2;

      const char* status = "discharging";
      if (I < CURRENT_DEADBAND)  status = "idle";
      if (V <= CUTOFF_WARN_V)    status = "warn_low_v";   // mendekati cutoff

      printFrame(V, I, soc, q_total, status);
    }

    // ── Cek cutoff ────────────────────────────────────────────────────────
    if (V <= CUTOFF_V) {
      done = true;
      // Kirim frame terakhir dengan status "cutoff" langsung
      printFrame(V, 0.0f, soc, q_total, "cutoff");

      // Pesan ringkasan (diabaikan app, terlihat di Serial Monitor)
      Serial.println();
      Serial.println("=== TAHAP I SELESAI ===");
      Serial.printf("Q_aktual terukur : %.2f mAh\n", q_total);
      Serial.printf("Durasi discharge : %.1f menit\n", now / 60000.0f);
      Serial.println();
      Serial.println(">> Catat Q_aktual di atas sebagai Q_REF untuk Tahap III.");
      Serial.println(">> Update #define Q_REF_MAH di firmware Tahap III.");
    }
  }
}
