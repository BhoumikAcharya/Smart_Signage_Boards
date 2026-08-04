/*
 * ESP32 Enterprise Signage Controller — HARDWARE BRING-UP / TESTING BUILD
 * -----------------------------------------------------------------------
 * Purpose: Validate the V3 board hardware over Serial WITHOUT needing the
 *          Ethernet / MQTT / Modbus stack up. Answers "what is working?".
 *
 * Exercises:
 *   - MCP23017 I2C expander on the FINALIZED pins (SDA=4, SCL=13)
 *   - All 10 MOSFET channels (Port A0-A4 = LHS, Port B0-B4 = RHS)
 *   - 4x ACS712 current sensors (live, using production calibration math):
 *       LHS, RHS, Static Load 1, Static Load 2
 *   - PSU + Battery voltage dividers (raw ADC + estimated volts)
 *
 * NOTE: This is the TESTING build. The networked production firmware lives
 *       in ../Firmware/. Failsafe + battery-enable are decided later.
 */

#include <Wire.h>
#include <Adafruit_MCP23X17.h>

// --- FINALIZED I2C PINS (per PCB_V3/Connections.md) ---
// Changed from 16/17 -> 4/13 to free GPIO17 for the Ethernet clock.
#define I2C_SDA 4
#define I2C_SCL 13
#define MCP_ADDR 0x20
Adafruit_MCP23X17 mcp;

// --- ANALOG SENSORS (ADC1 only) ---
const int PIN_VOLT_PSU  = 36; // PSU voltage divider
const int PIN_VOLT_BATT = 39; // Battery voltage divider
const int PIN_CURR_LEFT = 34; // ACS712 #1 (LHS)
const int PIN_CURR_RGHT = 35; // ACS712 #2 (RHS)
const int PIN_CURR_STA1 = 32; // ACS712 #3 (Static Load 1 / Always ON)
const int PIN_CURR_STA2 = 33; // ACS712 #4 (Static Load 2 / Always ON)

// --- SENSOR CALIBRATION (mirror production so we validate the same math) ---
const float ADC_REF   = 3.3;
const float SENS_LEFT = 0.146, ZERO_LEFT = 2.400;
const float SENS_RGHT = 0.146, ZERO_RGHT = 2.400;
const float SENS_STA1 = 0.146, ZERO_STA1 = 2.400;
const float SENS_STA2 = 0.146, ZERO_STA2 = 2.400;

// Voltage-divider ratios (Connections.md steps 14.5V bus -> <=3.0V).
// These are PLACEHOLDERS — measure with a multimeter and correct on the bench.
const float PSU_DIV_RATIO  = 5.30; // Vbus = Vadc * PSU_DIV_RATIO
const float BATT_DIV_RATIO = 5.30; // Vbatt = Vadc * BATT_DIV_RATIO

// --- STATE ---
uint8_t portA = 0x00; // LHS bitmask (bits 0-4)
uint8_t portB = 0x00; // RHS bitmask (bits 0-4)
bool telemetryStream = false;
unsigned long lastTelemetry = 0;

// Animation frames reused from production for the chase self-test
const uint8_t chaseFrames[5] = {0x07, 0x0E, 0x1C, 0x19, 0x13};

// ========================================================
// HELPERS
// ========================================================

int getMedianADC(int pin) {
  int s[51];
  for (int i = 0; i < 51; i++) s[i] = analogRead(pin);
  for (int i = 1; i < 51; i++) {
    int key = s[i], j = i - 1;
    while (j >= 0 && s[j] > key) { s[j + 1] = s[j]; j--; }
    s[j + 1] = key;
  }
  return s[25];
}

float readCurrent(int pin, float zero, float sens) {
  return fabs((((getMedianADC(pin) / 4095.0) * ADC_REF) - zero) / sens);
}

void pushPorts() {
  mcp.writeGPIOA(portA);
  mcp.writeGPIOB(portB);
}

void printBits(uint8_t v) {
  for (int b = 4; b >= 0; b--) Serial.print((v >> b) & 1);
}

void printMenu() {
  Serial.println(F("\n=========================================="));
  Serial.println(F("  ESP32 V3 HARDWARE BRING-UP TEST"));
  Serial.println(F("=========================================="));
  Serial.println(F("MOSFETs (0-9 toggles a channel):"));
  Serial.println(F("  0-4 -> Port A (LHS strips A0-A4)"));
  Serial.println(F("  5-9 -> Port B (RHS strips B0-B4)"));
  Serial.println(F("Patterns:"));
  Serial.println(F("  a -> ALL MOSFETs ON"));
  Serial.println(F("  x -> ALL MOSFETs OFF"));
  Serial.println(F("  c -> run CHASE on BOTH sides (5 cycles)"));
  Serial.println(F("  L -> LEFT-side chase for 10 seconds (uppercase)"));
  Serial.println(F("  R -> RIGHT-side chase for 10 seconds (uppercase)"));
  Serial.println(F("  D -> DEMO loop: LEFT 10s -> RIGHT 10s -> OFF 3s (repeats)"));
  Serial.println(F("Sensors:"));
  Serial.println(F("  r -> read all sensors ONCE"));
  Serial.println(F("  s -> toggle continuous sensor STREAM (1 Hz)"));
  Serial.println(F("  v -> read PSU + Battery voltages ONCE"));
  Serial.println(F("  h -> show this menu"));
  Serial.println(F("==========================================\n"));
}

// ========================================================
// TEST ROUTINES
// ========================================================

void toggleChannel(int idx) {   // idx 0-4 = A, 5-9 = B
  if (idx < 5) portA ^= (1 << idx);
  else         portB ^= (1 << (idx - 5));
  pushPorts();
  Serial.print(F(">> Port A[")); printBits(portA);
  Serial.print(F("]  Port B[")); printBits(portB);
  Serial.println(F("]"));
}

void runChase() {
  Serial.println(F(">> Running chase animation..."));
  for (int cycle = 0; cycle < 5; cycle++) {
    for (int f = 0; f < 5; f++) {
      mcp.writeGPIOA(chaseFrames[f]);
      mcp.writeGPIOB(chaseFrames[f]);
      delay(300);
    }
  }
  pushPorts(); // restore prior state
  Serial.println(F(">> Chase done, state restored."));
}

// Blocking wait that aborts early the moment a Serial byte arrives.
// Returns true if interrupted (a byte is waiting), false if the full time elapsed.
bool delayOrAbort(unsigned long ms) {
  unsigned long start = millis();
  while (millis() - start < ms) {
    if (Serial.available() > 0) return true;
    delay(1);
  }
  return false;
}

// Animate the chase on ONE side for durationMs. left=true -> Port A (LHS),
// left=false -> Port B (RHS). Does NOT touch the other side. Returns true if
// aborted early by Serial input.
bool chaseSide(bool left, unsigned long durationMs) {
  unsigned long start = millis();
  int f = 0;
  while (millis() - start < durationMs) {
    if (left) mcp.writeGPIOA(chaseFrames[f]);
    else      mcp.writeGPIOB(chaseFrames[f]);
    f = (f + 1) % 5;
    if (delayOrAbort(120)) return true;
  }
  return false;
}

// One-shot single-side chase for the 'L' / 'R' menu keys: runs 10 s (or until a
// key is pressed), then restores the prior manual state on both sides.
void runChaseSide(bool left) {
  Serial.printf(">> %s chase (10s)... press any key to stop early\n", left ? "LEFT" : "RIGHT");
  while (Serial.available() > 0) Serial.read(); // flush the command's trailing newline
  chaseSide(left, 10000);
  while (Serial.available() > 0) Serial.read(); // consume an early-stop key if any
  pushPorts();                                  // restore prior manual state
  Serial.printf(">> %s chase done, state restored.\n", left ? "LEFT" : "RIGHT");
}

// Continuous DEMO: LEFT 10s -> RIGHT 10s -> ALL OFF 3s, repeating until any key
// is pressed. Only the active side animates during each phase.
void runDemo() {
  Serial.println(F(">> DEMO started: LEFT 10s -> RIGHT 10s -> OFF 3s (repeats)."));
  Serial.println(F("   Press any key to stop."));
  while (Serial.available() > 0) Serial.read(); // flush the command's trailing newline

  while (true) {
    Serial.println(F("   [DEMO] LEFT chase (10s)"));
    mcp.writeGPIOB(0x00);                 // hold RHS off
    if (chaseSide(true, 10000)) break;

    Serial.println(F("   [DEMO] RIGHT chase (10s)"));
    mcp.writeGPIOA(0x00);                 // hold LHS off
    if (chaseSide(false, 10000)) break;

    Serial.println(F("   [DEMO] ALL OFF (3s)"));
    mcp.writeGPIOA(0x00); mcp.writeGPIOB(0x00);
    if (delayOrAbort(3000)) break;
  }

  while (Serial.available() > 0) Serial.read(); // consume the stop key
  pushPorts();                                  // restore prior manual state
  Serial.println(F(">> DEMO stopped, state restored."));
}

void readSensors() {
  float l  = readCurrent(PIN_CURR_LEFT, ZERO_LEFT, SENS_LEFT);
  float r  = readCurrent(PIN_CURR_RGHT, ZERO_RGHT, SENS_RGHT);
  float s1 = readCurrent(PIN_CURR_STA1, ZERO_STA1, SENS_STA1);
  float s2 = readCurrent(PIN_CURR_STA2, ZERO_STA2, SENS_STA2);
  Serial.printf(">> CURRENT  LHS: %.3f A | RHS: %.3f A | Static1: %.3f A | Static2: %.3f A\n",
                l, r, s1, s2);
}

void readVoltages() {
  int psuRaw  = getMedianADC(PIN_VOLT_PSU);
  int battRaw = getMedianADC(PIN_VOLT_BATT);
  float psuV  = (psuRaw  / 4095.0) * ADC_REF * PSU_DIV_RATIO;
  float battV = (battRaw / 4095.0) * ADC_REF * BATT_DIV_RATIO;
  Serial.printf(">> PSU  raw:%4d  ~%.2f V  |  BATT raw:%4d  ~%.2f V\n",
                psuRaw, psuV, battRaw, battV);
  Serial.println(F("   (divider ratios are placeholders — verify with a multimeter)"));
}

// ========================================================
// SETUP & LOOP
// ========================================================

void setup() {
  Serial.begin(115200);
  delay(500);

  // ADC config
  analogReadResolution(12);
  analogSetAttenuation(ADC_11db);
  pinMode(PIN_VOLT_PSU, INPUT);  pinMode(PIN_VOLT_BATT, INPUT);
  pinMode(PIN_CURR_LEFT, INPUT); pinMode(PIN_CURR_RGHT, INPUT);
  pinMode(PIN_CURR_STA1, INPUT); pinMode(PIN_CURR_STA2, INPUT);

  // I2C + MCP23017 on the corrected pins
  Wire.begin(I2C_SDA, I2C_SCL);
  Serial.print(F("\nProbing MCP23017 @0x20 on SDA=4/SCL=13 ... "));
  if (!mcp.begin_I2C(MCP_ADDR, &Wire)) {
    Serial.println(F("NOT FOUND!"));
    Serial.println(F("Check: SDA=GPIO4, SCL=GPIO13, 4.7k pull-ups, RESET->3V3, A0/A1/A2->GND."));
    while (1) delay(1000);
  }
  Serial.println(F("OK"));

  for (int i = 0; i < 16; i++) mcp.pinMode(i, OUTPUT);
  portA = 0x00; portB = 0x00;
  pushPorts();

  printMenu();
}

void loop() {
  // Handle Serial commands
  if (Serial.available() > 0) {
    char c = Serial.read();
    if (c >= '0' && c <= '9')      toggleChannel(c - '0');
    else if (c == 'a')            { portA = 0x1F; portB = 0x1F; pushPorts(); Serial.println(F(">> ALL ON")); }
    else if (c == 'x')            { portA = 0x00; portB = 0x00; pushPorts(); Serial.println(F(">> ALL OFF")); }
    else if (c == 'c')             runChase();
    else if (c == 'L')             runChaseSide(true);
    else if (c == 'R')             runChaseSide(false);
    else if (c == 'D')             runDemo();
    else if (c == 'r')             readSensors();
    else if (c == 'v')             readVoltages();
    else if (c == 's')            { telemetryStream = !telemetryStream;
                                     Serial.printf(">> Sensor stream %s\n", telemetryStream ? "ON" : "OFF"); }
    else if (c == 'h')             printMenu();
    // swallow trailing newline/whitespace
    while (Serial.available() > 0 && (Serial.peek() == '\n' || Serial.peek() == '\r' || Serial.peek() == ' ')) Serial.read();
  }

  // Continuous telemetry stream
  if (telemetryStream && millis() - lastTelemetry >= 1000) {
    lastTelemetry = millis();
    readSensors();
  }
}
