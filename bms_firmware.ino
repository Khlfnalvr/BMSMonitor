/*
 * TAHAP I — Coulomb Counting (CC) + OCV Correction
 * NMC 21700 Single Cell | LG INR21700 M50LT
 *
 * Hardware:
 *   GPIO34 → Titik tengah R1(11kΩ)–R2(11kΩ) → V_cell
 *   GPIO35 → Titik tengah Ra(11kΩ)–Rb(11kΩ) → Output ACS712
 *   VIN    → Vcc ACS712 (5V dari USB ESP32)
 *
 * Formula ADC (ADC_6db, range 0–2.2V):
 *   V_cell = Vadc_34 × (22/11) = Vadc × 2.0
 *   I_amp  = ((Vadc_35 × 2.0) − Voffset) / 0.066   [ACS712 30A]
 *
 * OCV Correction Logic:
 *   - SOC diupdate dari OCV jika idle ≥ 30 menit
 *   - "Idle" = |I| < CURRENT_DEADBAND
 *   - Glitch diabaikan: arus sesaat < 30 detik tidak mereset timer idle
 *   - Saat startup: OCV dibaca langsung untuk SOC awal
 *
 * Output: JSON 1Hz — SOCTester app compatible
 */

// ===== PIN =====
#define PIN_VOLTAGE   34
#define PIN_CURRENT   35

// ===== PARAMETER SEL =====
#define Q_REF_MAH         4229.2f
#define CUTOFF_V          2.5f
#define CUTOFF_WARN_V     2.6f

// ===== ADC / DIVIDER =====
// Voltage divider semua channel: R1=11k (ke sumber), R2=11k (ke GND) → ratio = 22/11 = 2.0
// ADC_6db: range 0–2.2V → V_adc_max = 4.2 × 0.5 = 2.1V (zona linear)
#define VCELL_RATIO       (22.0f / 11.0f)
// ACS712-30A: Voffset=2.5V @ 0A (powered 5V), sens=0.066 V/A
#define ACS_VOFFSET       2.5f
#define ACS_SENS          0.066f
#define CURRENT_DEADBAND  0.10f

// ===== SAMPLING =====
#define SAMPLE_COUNT          64
#define SAMPLE_INTERVAL_MS   100
#define PRINT_INTERVAL_MS   1000

// ===== OCV CORRECTION CONFIG =====
#define IDLE_REQUIRED_MS      (30UL * 60UL * 1000UL)
#define GLITCH_TOLERANCE_MS   (30UL * 1000UL)

// ===== LUT OCV-SOC (101 titik, index = %SOC) =====
static const float OCV_TABLE[101] = {
  2.8309f, 3.0218f, 3.1279f, 3.2132f, 3.2767f, 3.3218f, 3.3542f, 3.3748f,
  3.3853f, 3.3940f, 3.4024f, 3.4111f, 3.4210f, 3.4327f, 3.4458f, 3.4595f,
  3.4731f, 3.4863f, 3.4987f, 3.5102f, 3.5211f, 3.5319f, 3.5426f, 3.5528f,
  3.5610f, 3.5678f, 3.5747f, 3.5814f, 3.5876f, 3.5934f, 3.5991f, 3.6047f,
  3.6103f, 3.6158f, 3.6213f, 3.6267f, 3.6322f, 3.6377f, 3.6433f, 3.6490f,
  3.6547f, 3.6608f, 3.6672f, 3.6739f, 3.6808f, 3.6883f, 3.6964f, 3.7049f,
  3.7139f, 3.7237f, 3.7341f, 3.7451f, 3.7564f, 3.7683f, 3.7820f, 3.7979f,
  3.8145f, 3.8309f, 3.8440f, 3.8539f, 3.8628f, 3.8712f, 3.8793f, 3.8871f,
  3.8944f, 3.9014f, 3.9081f, 3.9149f, 3.9217f, 3.9286f, 3.9360f, 3.9439f,
  3.9524f, 3.9616f, 3.9716f, 3.9823f, 3.9937f, 4.0056f, 4.0177f, 4.0296f,
  4.0410f, 4.0515f, 4.0609f, 4.0690f, 4.0755f, 4.0804f, 4.0837f, 4.0865f,
  4.0891f, 4.0914f, 4.0937f, 4.0961f, 4.0989f, 4.1021f, 4.1058f, 4.1101f,
  4.1154f, 4.1222f, 4.1305f, 4.1443f, 4.1846f
};

// ===== VARIABEL GLOBAL =====
float acs_voffset_adc = 0.0f;
float soc      = 0.0f;
float q_total  = 0.0f;
bool  done     = false;
float last_V   = 0.0f;
float last_I   = 0.0f;

// --- OCV correction state ---
unsigned long idle_start_ms   = 0;
bool          in_idle         = false;
unsigned long glitch_start_ms = 0;
bool          in_glitch       = false;
bool          ocv_corrected   = false;

unsigned long t_sample = 0;
unsigned long t_print  = 0;

// ===== FUNGSI: OCV → SOC (interpolasi linear) =====
float ocvToSOC(float ocv) {
  if (ocv <= OCV_TABLE[0])   return 0.0f;
  if (ocv >= OCV_TABLE[100]) return 100.0f;
  int lo = 0, hi = 100;
  while (hi - lo > 1) {
    int mid = (lo + hi) / 2;
    if (OCV_TABLE[mid] <= ocv) lo = mid;
    else                        hi = mid;
  }
  float t = (ocv - OCV_TABLE[lo]) / (OCV_TABLE[hi] - OCV_TABLE[lo]);
  return (float)lo + t;
}

// ===== ADC AVERAGING (mV, pakai kalibrasi factory ESP32) =====
float readADC_mV(uint8_t pin) {
  long sum = 0;
  for (int i = 0; i < SAMPLE_COUNT; i++) {
    sum += analogReadMilliVolts(pin);
    delayMicroseconds(100);
  }
  return (float)sum / SAMPLE_COUNT;
}

// ===== BACA TEGANGAN SEL =====
float readVcell() {
  float vmv = readADC_mV(PIN_VOLTAGE);
  return (vmv / 1000.0f) * VCELL_RATIO;
}

// ===== BACA ARUS =====
float readCurrent() {
  float vmv  = readADC_mV(PIN_CURRENT);
  float vacs = (vmv / 1000.0f) * VCELL_RATIO;
  float I    = (vacs - acs_voffset_adc) / ACS_SENS;
  I = fabsf(I);
  if (I < CURRENT_DEADBAND) I = 0.0f;
  return I;
}

// ===== OUTPUT JSON =====
void printFrame(float V, float I, float soc_pct, float q_mah, const char* status) {
  Serial.printf(
    "{\"v\":1,\"soc\":%.2f,\"pack_v\":%.4f,\"current\":%.4f,\"status\":\"%s\",\"q_mah\":%.2f}\n",
    soc_pct, V, -I, status, q_mah
  );
}

// ===== UPDATE IDLE / GLITCH STATE =====
bool updateIdleState(float I, unsigned long now) {
  bool is_idle_now = (I < CURRENT_DEADBAND);

  if (is_idle_now) {
    if (in_glitch) in_glitch = false;
    if (!in_idle) {
      in_idle       = true;
      idle_start_ms = now;
      ocv_corrected = false;
    }
  } else {
    if (in_idle) {
      if (!in_glitch) {
        in_glitch       = true;
        glitch_start_ms = now;
      } else if ((now - glitch_start_ms) >= GLITCH_TOLERANCE_MS) {
        in_idle       = false;
        in_glitch     = false;
        ocv_corrected = false;
      }
    }
  }

  if (in_idle && !in_glitch && !ocv_corrected) {
    if ((now - idle_start_ms) >= IDLE_REQUIRED_MS) {
      return true;
    }
  }
  return false;
}

// ===== KALIBRASI OFFSET =====
void calibrateOffset() {
  Serial.println("[CAL] Kalibrasi offset — pastikan load tester BELUM terhubung...");
  delay(3000);
  long sum = 0;
  for (int i = 0; i < 512; i++) {
    sum += analogReadMilliVolts(PIN_CURRENT);
    delayMicroseconds(100);
  }
  float vmv_avg   = (float)sum / 512.0f;
  acs_voffset_adc = (vmv_avg / 1000.0f) * VCELL_RATIO;
  Serial.printf("[CAL] Voffset ACS (Vout_ACS @ I=0) = %.4f V (teori: %.1f V)\n",
                acs_voffset_adc, ACS_VOFFSET);
}

// ===== SETUP =====
void setup() {
  Serial.begin(115200);
  analogReadResolution(12);
  analogSetPinAttenuation(PIN_VOLTAGE, ADC_6db);
  analogSetPinAttenuation(PIN_CURRENT, ADC_6db);

  Serial.println();
  Serial.println("=== TAHAP I — CC + OCV CORRECTION ===");
  Serial.println("Sel    : NMC 21700 | LG M50LT");
  Serial.println("Sensor : ACS712 30A");
  Serial.println("Divider: 11k/11k (semua channel) | ADC_6db");
  Serial.println("OCV    : koreksi setelah idle 30 menit");
  Serial.println();

  calibrateOffset();

  float v_ocv = readVcell();
  soc         = constrain(ocvToSOC(v_ocv), 0.0f, 100.0f);
  q_total     = (100.0f - soc) / 100.0f * Q_REF_MAH;
  last_V      = v_ocv;

  Serial.printf("[OCV_INIT] V_OCV = %.4f V → SOC awal = %.1f%%\n\n", v_ocv, soc);
  Serial.println("[INFO] Hubungkan load tester, pantau log JSON:");

  t_sample = millis();
  t_print  = millis();
}

// ===== LOOP =====
void loop() {
  unsigned long now = millis();

  if (done) {
    if (now - t_print >= PRINT_INTERVAL_MS) {
      t_print = now;
      printFrame(last_V, 0.0f, soc, q_total, "cutoff");
    }
    return;
  }

  if (now - t_sample >= SAMPLE_INTERVAL_MS) {
    float I = readCurrent();
    float V = readVcell();

    unsigned long now2 = millis();
    float dt_h = (float)(now2 - t_sample) / 3600000.0f;
    t_sample = now2;

    if (I >= CURRENT_DEADBAND) {
      float dQ  = I * 1000.0f * dt_h;
      q_total  += dQ;
      soc       = 100.0f - (q_total / Q_REF_MAH * 100.0f);
      soc       = constrain(soc, 0.0f, 100.0f);
    }

    bool trigger_ocv = updateIdleState(I, now2);
    if (trigger_ocv) {
      float soc_before = soc;
      soc           = constrain(ocvToSOC(V), 0.0f, 100.0f);
      q_total       = (100.0f - soc) / 100.0f * Q_REF_MAH;
      ocv_corrected = true;
      Serial.printf("[OCV_CORR] SOC: %.2f%% → %.2f%% (V=%.4fV)\n", soc_before, soc, V);
    }

    last_V = V;
    last_I = I;

    if (now2 - t_print >= PRINT_INTERVAL_MS) {
      t_print = now2;
      const char* status = "discharging";
      if (I < CURRENT_DEADBAND) status = "idle";
      if (V <= CUTOFF_WARN_V)   status = "warn_low_v";
      printFrame(V, I, soc, q_total, status);
    }

    if (V <= CUTOFF_V) {
      done = true;
      printFrame(V, 0.0f, soc, q_total, "cutoff");
      Serial.println();
      Serial.println("=== TAHAP I SELESAI ===");
      Serial.printf("Q_aktual : %.2f mAh\n", q_total);
      Serial.printf("Durasi   : %.1f menit\n", now / 60000.0f);
      Serial.println(">> Update Q_REF_MAH di Tahap III.");
    }
  }
}
