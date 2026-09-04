/*
 * Battery Calibration — bench tool  (Phase 1 · Part 2 groundwork)
 * ==============================================================
 * Target     : PCB V3, ESP32 battery sense on GPIO39 via a 100k/18k divider
 * Standalone : serial only. No Ethernet, no MQTT, no MCP — this is a bench
 *              instrument for characterising the LFP battery, nothing else.
 *
 * WHAT IT IS FOR
 *   1. Watch the ADC / battery voltage in different situations — charging, at
 *      rest, under LED load, and on discharge.
 *   2. Calibrate the divider ratio to THIS board. 100k/18k is nominally 6.556,
 *      but resistor tolerance shifts it; 'd' back-solves it from a multimeter
 *      reading.
 *   3. Capture the 0% / 50% / 100% threshold voltages as the pack discharges,
 *      to drop into the Part-2 firmware's percentage map.
 *
 * BATTERY: 4S LiFePO4, 12.8 V nominal. Its discharge curve is FLAT through the
 * middle, so voltage->% is inherently coarse — a two-segment map anchored on the
 * three measured voltages (0 / 50 / 100 %) is about as good as voltage alone
 * gets. For the numbers to transfer to the firmware:
 *   - Capture ON BATTERY (PSU disconnected) — with the charger connected the bus
 *     is pinned near 14 V and you never see the real discharge voltages.
 *   - Capture UNDER the node's real operating load (LEDs ride the battery), and
 *     let each point SETTLE a few seconds before stamping — LFP recovers slowly.
 *   - The 50% anchor is the hard one: LFP voltage barely moves mid-range, so
 *     you cannot eyeball 50% from voltage. Place it by TIME/Ah — discharge at a
 *     roughly steady load and stamp 'm' at about half the total runtime.
 *
 * VOLTAGE READ: analogReadMilliVolts() applies the ESP32's factory eFuse ADC
 * calibration internally — far better than raw/4095*3.3, which matters here
 * because the useful LFP signal is only tens of mV.
 *
 * DIVIDER: 100k (battery+ -> pin) / 18k (pin -> GND).
 *   Vadc = Vbatt * 18/118 = Vbatt / 6.556.  At 14.6 V -> 2.23 V (safe, linear).
 */

#include <Arduino.h>

// ---- Hardware ----
const int PIN_VOLT_BATT = 39;          // ADC1_CH3 — the old battery sense line

// ---- Calibration state (mutable: this tool exists to derive these) ----
float BATT_DIV_RATIO = 6.556f;         // (100+18)/18 nominal; refine with 'd'

float V0   = 0.0f;                     // battery volts at 0%   (empty)
float V50  = 0.0f;                     // battery volts at 50%
float V100 = 0.0f;                     // battery volts at 100% (full)
bool  haveV0 = false, haveV50 = false, haveV100 = false;

// ========================================================
//  SAMPLING
// ========================================================
int getMedianRaw(int pin) {
  int s[51];
  for (int i = 0; i < 51; i++) s[i] = analogRead(pin);
  for (int i = 1; i < 51; i++) {
    int key = s[i], j = i - 1;
    while (j >= 0 && s[j] > key) { s[j + 1] = s[j]; j--; }
    s[j + 1] = key;
  }
  return s[25];
}

uint32_t getMedianMv(int pin) {
  uint32_t s[51];
  for (int i = 0; i < 51; i++) s[i] = analogReadMilliVolts(pin);
  for (int i = 1; i < 51; i++) {
    uint32_t key = s[i]; int j = i - 1;
    while (j >= 0 && s[j] > key) { s[j + 1] = s[j]; j--; }
    s[j + 1] = key;
  }
  return s[25];
}

float readBatteryVolts() {
  return (getMedianMv(PIN_VOLT_BATT) / 1000.0f) * BATT_DIV_RATIO;
}

// ========================================================
//  TWO-SEGMENT PERCENTAGE MAP (0 / 50 / 100 anchors)
// ========================================================
// Two straight segments — 0..50 between V0 and V50, 50..100 between V50 and V100
// — so the 50% point can bend the map to fit LFP's flat middle far better than a
// single line. Returns -1 until all three anchors are set and increasing.
float batteryPercent(float v) {
  if (!(haveV0 && haveV50 && haveV100)) return -1.0f;
  if (!(V0 < V50 && V50 < V100))        return -1.0f;   // anchors must increase
  if (v >= V100) return 100.0f;
  if (v <= V0)   return 0.0f;
  if (v >= V50)  return 50.0f + 50.0f * (v - V50) / (V100 - V50);
  return 50.0f * (v - V0) / (V50 - V0);
}

// ========================================================
//  SERIAL
// ========================================================
// Blocking line read — fine here: standalone tool, no watchdog or MQTT to feed.
bool readLine(char* buf, size_t len, unsigned long timeoutMs) {
  size_t i = 0; unsigned long start = millis();
  while (millis() - start < timeoutMs) {
    while (Serial.available()) {
      char ch = Serial.read();
      if (ch == '\n' || ch == '\r') { if (i > 0) { buf[i] = '\0'; return true; } }
      else if (i < len - 1) buf[i++] = ch;
    }
    delay(2);
  }
  buf[0] = '\0'; return false;
}

void printMenu() {
  Serial.println(F("\n=============================================="));
  Serial.println(F("  BATTERY CALIBRATION  —  GPIO39, 100k/18k"));
  Serial.println(F("=============================================="));
  Serial.println(F("  h -> this menu"));
  Serial.println(F("  r -> read once (raw ADC, pin mV, Vbatt, %)"));
  Serial.println(F("  s -> toggle 1 Hz stream"));
  Serial.println(F("  d -> calibrate divider ratio (enter multimeter Vbatt)"));
  Serial.println(F("  e -> mark CURRENT voltage as 0%   (empty)"));
  Serial.println(F("  m -> mark CURRENT voltage as 50%  (mid)"));
  Serial.println(F("  f -> mark CURRENT voltage as 100% (full)"));
  Serial.println(F("  p -> print paste-ready block"));
  Serial.println(F("==============================================\n"));
}

void readOnce() {
  int raw = getMedianRaw(PIN_VOLT_BATT);
  uint32_t mv = getMedianMv(PIN_VOLT_BATT);
  float vbatt = (mv / 1000.0f) * BATT_DIV_RATIO;
  float pct = batteryPercent(vbatt);
  Serial.printf("  rawADC=%4d   pin=%4u mV   Vbatt=%.3f V   ratio=%.4f   ",
                raw, mv, vbatt, BATT_DIV_RATIO);
  if (pct < 0) Serial.println(F("%=--- (set e/m/f)"));
  else         Serial.printf("%%=%.0f\n", pct);
}

void calibrateDivider() {
  char buf[24];
  Serial.print(F("\n[CAL] Measure the battery with a multimeter, type the volts: "));
  if (!readLine(buf, sizeof(buf), 30000) || !buf[0]) { Serial.println(F("\n[CAL] Skipped.")); return; }
  float vtrue = atof(buf);
  uint32_t mv = getMedianMv(PIN_VOLT_BATT);
  float vadc = mv / 1000.0f;
  if (vtrue > 0 && vadc > 0.01f) {
    BATT_DIV_RATIO = vtrue / vadc;
    Serial.printf("\n[CAL] pin=%.4f V, true=%.3f V  ->  BATT_DIV_RATIO = %.4f\n",
                  vadc, vtrue, BATT_DIV_RATIO);
  } else Serial.println(F("\n[CAL] Bad input or no signal — unchanged."));
}

void markAnchor(char which) {
  float v = readBatteryVolts();
  const char* name;
  switch (which) {
    case 'e': V0   = v; haveV0   = true; name = "0%   (V0)";   break;
    case 'm': V50  = v; haveV50  = true; name = "50%  (V50)";  break;
    case 'f': V100 = v; haveV100 = true; name = "100% (V100)"; break;
    default: return;
  }
  Serial.printf("[CAL] Marked %s = %.3f V\n", name, v);
  if (haveV0 && haveV50 && haveV100 && !(V0 < V50 && V50 < V100))
    Serial.println(F("[CAL] WARNING: anchors not increasing (need V0 < V50 < V100) — re-capture."));
}

void printBlock() {
  Serial.println(F("\n// ---- PASTE INTO PART-2 FIRMWARE ----"));
  Serial.printf("const float BATT_DIV_RATIO = %.4ff;\n", BATT_DIV_RATIO);
  Serial.printf("const float BATT_V0   = %.3ff;   // 0%%\n",   V0);
  Serial.printf("const float BATT_V50  = %.3ff;   // 50%%\n",  V50);
  Serial.printf("const float BATT_V100 = %.3ff;   // 100%%\n", V100);
  Serial.println(F("// ------------------------------------"));
  if (!(haveV0 && haveV50 && haveV100))
    Serial.println(F("// NOTE: not all anchors captured yet (e / m / f)."));
}

bool stream = false;
unsigned long lastStream = 0;

void handleSerial() {
  if (!Serial.available()) return;
  char c = Serial.read();
  while (Serial.available() && (Serial.peek() == '\n' || Serial.peek() == '\r')) Serial.read();
  switch (c) {
    case 'h': printMenu();        break;
    case 'r': readOnce();         break;
    case 's': stream = !stream;
              Serial.printf(">> Stream %s\n", stream ? "ON" : "OFF"); break;
    case 'd': calibrateDivider(); break;
    case 'e': case 'm': case 'f': markAnchor(c); break;
    case 'p': printBlock();       break;
    default: break;
  }
}

// ========================================================
//  SETUP & LOOP
// ========================================================
void setup() {
  Serial.begin(115200);
  delay(300);
  analogReadResolution(12);
  analogSetAttenuation(ADC_11db);
  pinMode(PIN_VOLT_BATT, INPUT);
  Serial.println(F("\nBattery calibration tool ready (GPIO39, 100k/18k divider)."));
  printMenu();
}

void loop() {
  handleSerial();
  if (stream && millis() - lastStream >= 1000) {
    lastStream = millis();
    readOnce();
  }
}
