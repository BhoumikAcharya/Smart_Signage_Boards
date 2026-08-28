/*
 * ESP32 Enterprise Signage Controller — Firmware v4.0.0-p1
 * ========================================================
 * Target : PCB V3, ESP32 + LAN8720 + MCP23017 (10 MOSFETs) + 4x ACS712
 * Pairs with : RaspberryPi/Gateway/Pi.py v2.0.0
 * Contract   : ESP32/esp32_contract.md
 *
 * PHASE 1 · PART 1 — HARDCODED BENCH BUILD, NO BATTERY.
 *   The purpose of this build is bring-up: get a handful of nodes onto Ethernet,
 *   connected to the Pi/broker, executing commands and streaming telemetry so
 *   they can be left running and observed. Everything is baked into source.
 *
 *   - ALL config is a compile-time const: per-unit IP + register, sensor
 *     calibration (ZERO/SENS), the PSU divider ratio, and every threshold.
 *     There is no runtime calibration and nothing is persisted — you edit the
 *     constants below and reflash.
 *   - The auto-calibration machinery (serial c/g/v/m/p) is GONE. Calibration
 *     returns in Phase 2, writing derived values to NVS instead of RAM.
 *   - Battery is GONE this build. No battery pin, no divider, no percentage,
 *     nothing published. Battery logic is Phase 1 · Part 2.
 *   - Serial menu is reduced to three commands: h (help), i (node info),
 *     s (sensor stream).
 *
 * CALIBRATION HONESTY: the SENS/ZERO/divider values below are datasheet-nominal
 * placeholders, NOT measured on this board. Ethernet, MQTT, command execution
 * and the animation are unaffected — but the 4-state current health is only
 * approximate until Phase-2 auto-calibration derives per-board values. Good
 * enough to verify connectivity and functionality; not yet a trustworthy alarm.
 *
 * WHAT CARRIED OVER FROM v3.0.0 (unchanged, deliberate — see esp32_contract.md §8):
 *   - I2C on 4/13 (GPIO17 is the LAN8720 50MHz clock).
 *   - 4 ACS712 channels: current1/2 = LHS/RHS arrows (switched), current3/4 =
 *     static zones 1/2 (hardwired always-on, monitored, never switched).
 *   - Modes 0-6 plus the 10000-11023 raw MOSFET bitmask. Mode 4 = solid LEFT,
 *     5 = solid RIGHT, 6 = solid ALL.
 *   - 'state' topic = what the node is ACTUALLY executing, published only after a
 *     verified MOSFET write. MCP unreachable => "FAULT" => gateway reads 65535.
 *   - Validated command parse (strtol, not atoi): a corrupt payload is rejected,
 *     never silently decoded as 0 = "arrows off".
 *   - Animation runs unconditionally: the sign keeps animating through a broker
 *     or link outage (hold-last-command). No NVS yet, so a reboot comes up dark
 *     until the retained 'value' topic re-commands it.
 *   - Network-first boot: an MCP/I2C fault never blocks the PHY, so the node
 *     still comes ONLINE and reports the fault as 'state' = 65535.
 *   - MOSFET writes are read back and verified; thresholds are set for BOTH
 *     sides on every mode change.
 */

#include <ETH.h>
#include <PubSubClient.h>
#include <WiFi.h>
#include <Wire.h>
#include <Adafruit_MCP23X17.h>
#include <esp_task_wdt.h>
#include <freertos/FreeRTOS.h>
#include <freertos/task.h>
#include <freertos/queue.h>

// ========================================================
//  USER CONFIGURATION — CHANGE PER UNIT
// ========================================================
const char* mqtt_server_ip   = "192.168.1.10";     // Pi, ESP32-side static IP
const int   mqtt_port        = 1883;
const int   ASSIGNED_REGISTER = 40001;              // node 1 = 40001, node 2 = 40002 ...

IPAddress local_IP  (192, 168, 1, 101);             // must be unique per unit
IPAddress gateway_ip(192, 168, 1, 1);
IPAddress subnet    (255, 255, 255, 0);
IPAddress primaryDNS(8, 8, 8, 8);

// ========================================================
//  SENSOR CALIBRATION — HARDCODED (datasheet-nominal placeholders)
// ========================================================
/*
 * ACS712 SENSITIVITY by variant:      5A part = 0.185 V/A
 *                                    20A part = 0.100 V/A
 *                                    30A part = 0.066 V/A
 *
 * These are const in this build: there is no runtime calibration. They are
 * NOMINAL — the true per-board zero and gain are not measured until Phase-2
 * auto-calibration. Edit here and reflash to change them.
 *
 * Part choice (which sensitivity goes where):
 *   ARROWS  -> 5A part  (low current, needs the ADC resolution to catch one
 *              strip out: ~14 counts/strip vs ~7 on the 20A part).
 *   STATIC  -> 20A part (higher current, only needs ON vs FAIL_OPEN).
 * Flip the values to match the parts actually fitted.
 */
const float SENS_LEFT = 0.185f, ZERO_LEFT = 2.500f;   // arrows  — 5A part assumed
const float SENS_RGHT = 0.185f, ZERO_RGHT = 2.500f;   // arrows  — 5A part assumed
const float SENS_STA1 = 0.100f, ZERO_STA1 = 2.500f;   // static  — 20A part assumed
const float SENS_STA2 = 0.100f, ZERO_STA2 = 2.500f;   // static  — 20A part assumed

// PSU voltage divider: Vbus = Vadc * ratio. Nominal placeholder — set from the
// fitted resistors / a multimeter reading and reflash.
const float PSU_DIV_RATIO = 5.30f;

/*
 * PSU FAIL DETECTION — hysteresis + debounce.
 *
 * 'power=FAIL' from an ONLINE node is this system's critical alarm: a node
 * running on battery reporting that its PSU died. False positives are expensive.
 *
 * HYSTERESIS: fail below FAIL_VOLTS, recover only above OK_VOLTS, hold in
 * between, so a supply sagging to exactly the threshold cannot chatter.
 * DEBOUNCE: require N consecutive agreeing reads before flipping, so a mains dip
 * or an inrush cannot publish a one-cycle FAIL. At 500 ms cadence, 3 reads ~1.5s.
 *
 * PLACEHOLDERS. They must sit below the normal supply voltage but ABOVE the
 * battery's loaded voltage, or the node reports FAIL while running fine on mains.
 * (The battery itself is not part of this build — that is Phase 1 · Part 2 — but
 * these thresholds already anticipate it.) Set from the real PSU and reflash.
 */
const float PSU_FAIL_VOLTS = 10.0f;   // below this => FAIL
const float PSU_OK_VOLTS   = 11.0f;   // above this => OK (must be > PSU_FAIL_VOLTS)
const int   PWR_DEBOUNCE_COUNT = 3;   // consecutive agreeing reads before flipping

// ========================================================
//  DISCREPANCY THRESHOLDS
// ========================================================
const float THRESH_PER_STRIP = 0.060f;  // min acceptable current for ONE strip
const float THRESHOLD_STATIC = 0.080f;  // static zones: load never changes

/*
 * When a side is expected OFF we still need to catch a stuck-on MOSFET, so it
 * gets a LOW threshold rather than a stale high one.
 *
 * ORDERING CONSTRAINT: NOISE_FLOOR must stay BELOW THRESH_OFF_DETECT. The noise
 * gate snaps small readings to zero; if it sat above the off-threshold it would
 * zero out exactly the currents FAIL_SHORT is meant to catch.
 */
const float THRESH_OFF_DETECT = 0.030f;  // expected-OFF side: above this => FAIL_SHORT
const float NOISE_FLOOR       = 0.020f;  // below this => treat as 0 A

const float ADC_REF = 3.3f;
const float ALPHA   = 0.15f;   // IIR low-pass

/*
 * MODE-CHANGE SETTLE — do not evaluate current states straight after a switch.
 *
 * The IIR filter is deliberately slow (~7 s to 90% of a step at ALPHA=0.15, 500ms
 * cadence) and the solid-ON threshold sits at 90% of expected, so without this
 * every off->on change would publish FAIL_OPEN for ~7 s before self-clearing.
 * Fix: SNAP the filter to the instantaneous reading on a mode change, and
 * suppress state evaluation for a short window while the load physically settles.
 */
const unsigned long MODE_SETTLE_MS = 1200;
volatile unsigned long lastModeChangeMs = 0;

// ========================================================
//  HARDWARE PIN MAP — finalized 2026-07-13
// ========================================================
#define I2C_SDA 4      // was 16
#define I2C_SCL 13     // was 17 — collided with ETH_CLOCK_GPIO17_OUT
#define MCP_ADDR 0x20
Adafruit_MCP23X17 mcp;

const int PIN_VOLT_PSU  = 36;
const int PIN_CURR_LEFT = 34;   // ACS712 #1  LHS arrows    (switched)
const int PIN_CURR_RGHT = 35;   // ACS712 #2  RHS arrows    (switched)
const int PIN_CURR_STA1 = 32;   // ACS712 #3  Static Zone 1 (always on)
const int PIN_CURR_STA2 = 33;   // ACS712 #4  Static Zone 2 (always on)
// NOTE: battery sense (was GPIO39) is intentionally absent — reintroduced in
// Phase 1 · Part 2 together with the battery logic.

#define ETH_PHY_ADDR  1
#define ETH_PHY_POWER -1
#define ETH_PHY_MDC   23
#define ETH_PHY_MDIO  18
#define ETH_PHY_TYPE  ETH_PHY_LAN8720
#define ETH_CLK_MODE  ETH_CLOCK_GPIO17_OUT

/*
 * MCP23017 HEALTH PATTERN — how we detect a dead I2C bus.
 *
 * Port A5-A7 and B5-B7 are unused and unconnected. We drive a fixed pattern
 * into them and read it back with every verify. Without this, a dead bus reads
 * as 0x00, which MATCHES a legitimate "all MOSFETs off" write — so mode 0 could
 * never be verified. The pattern guarantees a non-zero expected value in every
 * mode, so a silent bus always fails the check.
 */
const uint8_t MCP_HEALTH_PATTERN = 0xA0;   // bits 7 and 5 of the unused nibble

// ========================================================
//  MQTT TOPICS
// ========================================================
char topic_cmd[64], topic_status[64], topic_power[64], topic_state[64];
char topic_curr1[64], topic_curr2[64], topic_curr3[64], topic_curr4[64];
const char* topic_scan = "metro/signage/scan";

WiFiClient ethClient;
PubSubClient client(ethClient);
bool eth_connected = false;

// ========================================================
//  STATE
// ========================================================
volatile int  commandedValue = 0;    // last VALID command received
volatile int  activeCommand  = 0;    // what we have actually, verifiably executed
volatile bool mcpHealthy     = false;

volatile bool  expectLeftOn  = false;
volatile bool  expectRightOn = false;
volatile float dynamicThreshLeft  = THRESH_OFF_DETECT;
volatile float dynamicThreshRight = THRESH_OFF_DETECT;

uint8_t lastPortA = 0x00, lastPortB = 0x00;   // intent, for re-apply after recovery

struct NodeStateMsg {
  bool power_ok;
  const char* state_left;
  const char* state_right;
  const char* state_sta1;
  const char* state_sta2;
};
QueueHandle_t sensorQueue;
NodeStateMsg networkState = {true, "OFF", "OFF", "OFF", "OFF"};
volatile bool haveSensorData = false;  // written on core 0, read on core 1

/*
 * Chase 1 (wrapping), 2-LED arrow, direction A4 -> A0. Five frames, repeating.
 * Bits shown 4->0:  11000, 01100, 00110, 00011, 10001. The last is the WRAP
 * frame — the arrow breaks across the seam (A4 + A0 lit together). That is
 * intentional and defines Chase 1; it is what keeps the motion continuous with
 * no jump-back. Do not "fix" it.
 * Reference + rationale: Workbench/Firmware_Test/led_test_2/CHASE_ANIMATION_SPEC.md.
 */
const uint8_t chaseFrames[5] = {0x18, 0x0C, 0x06, 0x03, 0x11};
const int numFrames = 5;
const unsigned long FRAME_MS = 250;   // 5 frames x 250 ms = 1.25 s per sweep

// A named handler, not a lambda: WiFi.onEvent() is overloaded on three different
// callback signatures and a lambda can make the overload ambiguous.
void eth_event_handler(arduino_event_id_t event) {
  if (event == ARDUINO_EVENT_ETH_GOT_IP)            eth_connected = true;
  else if (event == ARDUINO_EVENT_ETH_DISCONNECTED) eth_connected = false;
}

// ========================================================
//  MCP23017 — WRITE, VERIFY, RECOVER
// ========================================================

/*
 * Write both ports and read them back. Returns false if the readback disagrees.
 *
 * Readback proves the MCP LATCHED what we intended. It says nothing about
 * whether the MOSFET conducted or the LED lit — that is what the ACS712
 * channels are for. Two independent layers:
 *     readback        -> I2C / MCP / firmware faults
 *     current sensing -> MOSFET / wiring / LED faults
 * Neither alone covers the path.
 */
bool writePortsVerified(uint8_t a, uint8_t b) {
  uint8_t wantA = a | MCP_HEALTH_PATTERN;
  uint8_t wantB = b | MCP_HEALTH_PATTERN;

  mcp.writeGPIOA(wantA);
  mcp.writeGPIOB(wantB);

  uint8_t gotA = mcp.readGPIOA();
  uint8_t gotB = mcp.readGPIOB();

  return (gotA == wantA && gotB == wantB);
}

void setMcpHealth(bool healthy);   // fwd

// Non-blocking re-init attempt. Called from loop() whenever the MCP is unhealthy.
void ensureMcpHealthy() {
  if (mcpHealthy) return;

  static unsigned long lastTry = 0;
  if (millis() - lastTry < 2000) return;
  lastTry = millis();

  Wire.begin(I2C_SDA, I2C_SCL);
  if (!mcp.begin_I2C(MCP_ADDR, &Wire)) return;

  for (int i = 0; i < 16; i++) mcp.pinMode(i, OUTPUT);

  // Re-apply the intent we were holding, so recovery restores the sign by itself.
  if (writePortsVerified(lastPortA, lastPortB)) {
    Serial.println(F("[MCP] Recovered. Output state re-applied."));
    setMcpHealth(true);
  }
}

// ========================================================
//  TELEMETRY
// ========================================================

void publishState() {
  if (!client.connected()) return;

  /*
   * Two very different reasons 'state' can diverge from the command, and they
   * get different payloads on purpose:
   *
   *   Command rejected  -> we are still faithfully running the PREVIOUS mode and
   *                        we know it. Publish that real number.
   *   MCP unreachable   -> we do NOT know what the outputs are doing. Publish a
   *                        non-numeric payload; the gateway maps it to 65535.
   *
   * Why not reuse the last known number in the second case: an operator could
   * clear the alarm by accident. They see the mismatch, command the value that
   * happens to match the stale number, gateway sees commanded == actual, the
   * flag clears and the screen goes green — while the sign is still dark. 65535
   * can never equal a valid command, so the fault cannot be hidden by commanding
   * anything. It clears only when the hardware is fixed.
   */
  if (!mcpHealthy) {
    client.publish(topic_state, "FAULT", true);
    return;
  }
  char buf[12];
  snprintf(buf, sizeof(buf), "%d", activeCommand);
  client.publish(topic_state, buf, true);
}

void setMcpHealth(bool healthy) {
  if (mcpHealthy == healthy) return;
  mcpHealthy = healthy;
  if (!healthy) Serial.println(F("[MCP] UNHEALTHY — outputs unverifiable, publishing FAULT."));
  publishState();
}

void publish_state_msg(NodeStateMsg msg) {
  if (!client.connected()) return;

  char onlineMsg[40];
  snprintf(onlineMsg, sizeof(onlineMsg), "ONLINE:%s", ETH.localIP().toString().c_str());
  client.publish(topic_status, onlineMsg, true);
  client.publish(topic_power, msg.power_ok ? "OK" : "FAIL", true);

  // Suppress current states until the first real sensor cycle has run, so the
  // gateway never sees a startup default that looks like a reading.
  if (haveSensorData) {
    client.publish(topic_curr1, msg.state_left,  true);
    client.publish(topic_curr2, msg.state_right, true);
    client.publish(topic_curr3, msg.state_sta1,  true);
    client.publish(topic_curr4, msg.state_sta2,  true);
  }

  publishState();

  // battery_pct is NOT published in this build — no battery logic yet (Phase 1 ·
  // Part 2). The gateway maps a missing value to 65535 = unknown, never 0%.
}

// ========================================================
//  COMMAND DECODE
// ========================================================

bool isValidCommand(long v) {
  if (v >= 0 && v <= 6) return true;
  if (v >= 10000 && v <= 11023) return true;
  return false;
}

void callback(char* topic, byte* payload, unsigned int length) {
  char msgBuffer[16];
  unsigned int copyLength = (length < sizeof(msgBuffer) - 1) ? length : (sizeof(msgBuffer) - 1);
  memcpy(msgBuffer, payload, copyLength);
  msgBuffer[copyLength] = '\0';

  if (strcmp(topic, topic_scan) == 0 && strcmp(msgBuffer, "PING") == 0) {
    publish_state_msg(networkState);
    return;
  }

  /*
   * Validated parse. atoi() returns 0 for unparseable input — and 0 is a valid
   * command meaning "arrows off", so a corrupt payload would blank the sign with
   * no error anywhere. strtol lets us tell "the number zero" from "not a number".
   */
  char* endp = nullptr;
  long v = strtol(msgBuffer, &endp, 10);

  if (endp == msgBuffer || *endp != '\0') {
    Serial.printf("[CMD] REJECTED malformed payload: '%s' (still running %d)\n",
                  msgBuffer, activeCommand);
    publishState();          // re-assert actual, so the gateway sees the divergence
    return;
  }
  if (!isValidCommand(v)) {
    Serial.printf("[CMD] REJECTED out-of-range: %ld (still running %d)\n",
                  v, activeCommand);
    publishState();
    return;
  }

  commandedValue = (int)v;
  Serial.printf("[CMD] Accepted: %d\n", commandedValue);
}

int countActiveStrips(uint8_t portMask) {
  int count = 0;
  portMask &= 0x1F;                       // ignore the health pattern bits
  while (portMask) { count += portMask & 1; portMask >>= 1; }
  return count;
}

/*
 * Set the discrepancy threshold for BOTH sides on every mode change.
 *
 * Only ever setting the side turning ON leaves the other side carrying the
 * previous mode's threshold. Modes 4 and 5 are the trap: one side goes solid
 * while the other must be expected OFF.
 */
void setThresholds(int nLeft, int nRight) {
  if (nLeft > 0) {
    expectLeftOn = true;
    dynamicThreshLeft = THRESH_PER_STRIP * (nLeft - 0.5f);
  } else {
    expectLeftOn = false;
    dynamicThreshLeft = THRESH_OFF_DETECT;
  }
  if (nRight > 0) {
    expectRightOn = true;
    dynamicThreshRight = THRESH_PER_STRIP * (nRight - 0.5f);
  } else {
    expectRightOn = false;
    dynamicThreshRight = THRESH_OFF_DETECT;
  }
}

/*
 * The animation state machine.
 *
 * Runs UNCONDITIONALLY from loop() — not gated on MQTT, not gated on MCP health.
 * Two separate jobs: bookkeeping (counting time, tracking which of the 5 frames
 * is next) and sending (pushing it over I2C). If the network drops we must keep
 * animating, per the hold-last-command failsafe. If the MCP is dead the send
 * fails harmlessly but the bookkeeping keeps ticking, so the moment I2C recovers
 * the next frame goes out and the sign resumes on its own.
 */
void runAnimationStateMachine() {
  static unsigned long previousMillis = 0;
  static int currentFrame = 0;
  static int lastExecutedCommand = -1;

  unsigned long now = millis();
  int cmd = commandedValue;
  bool modeChanged = (cmd != lastExecutedCommand);

  uint8_t portA = 0x00, portB = 0x00;
  bool doWrite = false;

  if (cmd >= 0 && cmd <= 6) {
    bool chasing = (cmd == 1 || cmd == 2 || cmd == 3);

    if (modeChanged || (chasing && now - previousMillis >= FRAME_MS)) {
      if (chasing) previousMillis = now;
      doWrite = true;

      switch (cmd) {
        case 0: portA = 0x00;                    portB = 0x00;                    break;
        case 1: portA = chaseFrames[currentFrame]; portB = 0x00;                  break;
        case 2: portA = 0x00;                    portB = chaseFrames[currentFrame]; break;
        case 3: portA = chaseFrames[currentFrame]; portB = chaseFrames[currentFrame]; break;
        case 4: portA = 0x1F;                    portB = 0x00;                    break;  // LEFT only
        case 5: portA = 0x00;                    portB = 0x1F;                    break;  // RIGHT only
        case 6: portA = 0x1F;                    portB = 0x1F;                    break;  // ALL
      }

      if (modeChanged) {
        switch (cmd) {
          case 0: setThresholds(0, 0); break;
          case 1: setThresholds(2, 0); break;   // chase lights 2 strips per frame
          case 2: setThresholds(0, 2); break;
          case 3: setThresholds(2, 2); break;
          case 4: setThresholds(5, 0); break;
          case 5: setThresholds(0, 5); break;
          case 6: setThresholds(5, 5); break;
        }
      }
      if (chasing) currentFrame = (currentFrame + 1) % numFrames;
    }
  }
  else if (cmd >= 10000 && cmd <= 11023) {
    if (modeChanged) {
      int bitmask = cmd - 10000;
      portA = bitmask & 0x1F;
      portB = (bitmask >> 5) & 0x1F;
      doWrite = true;
      setThresholds(countActiveStrips(portA), countActiveStrips(portB));
    }
  }

  if (!doWrite) return;

  lastPortA = portA;
  lastPortB = portB;

  /*
   * If the MCP is already known-bad, do the bookkeeping but skip the write.
   * Retrying a dead I2C bus every loop iteration would stall on timeouts
   * thousands of times a second for no benefit — ensureMcpHealthy() owns
   * recovery on a 2 s cadence and re-applies lastPortA/B when it succeeds.
   * Frame counting continues regardless, so the sign resumes by itself.
   */
  if (!mcpHealthy) {
    lastExecutedCommand = cmd;
    return;
  }

  if (!writePortsVerified(portA, portB)) {
    setMcpHealth(false);
    return;                       // do NOT advance activeCommand — we did not do it
  }

  lastExecutedCommand = cmd;

  // Publish 'state' only AFTER a verified write. Never optimistically.
  if (activeCommand != cmd) {
    activeCommand = cmd;
    // Stamp the MODE change only — not each chase frame. A chase holds 2 strips
    // lit throughout, so the load is steady and needs no settle window.
    lastModeChangeMs = millis();
    publishState();
  }
}

// ========================================================
//  ADC + DISCREPANCY LOGIC
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

float adcVolts(int pin) { return (getMedianADC(pin) / 4095.0f) * ADC_REF; }

float readCurrent(int pin, float zero, float sens) {
  return fabs((adcVolts(pin) - zero) / sens);
}

// Spelling is load-bearing: the gateway maps these four strings to 1/0/2/3 and
// anything else to 99.
const char* getDiscrepancyState(bool expectedOn, float actual, float threshold) {
  if (expectedOn  && actual >= threshold) return "ON";
  if (!expectedOn && actual <  threshold) return "OFF";
  if (expectedOn  && actual <  threshold) return "FAIL_OPEN";
  return "FAIL_SHORT";
}

void SensorTask(void* parameter) {
  esp_task_wdt_add(NULL);
  NodeStateMsg st = {true, "OFF", "OFF", "OFF", "OFF"};
  float f1 = 0, f2 = 0;   // filtered STATIC currents; arrows are intent-echo, unmeasured
  unsigned long lastRead = 0;

  bool pwrCandidate = true;      // debounce: the value we are counting towards
  int  pwrCandidateCount = 0;
  unsigned long seenModeChange = 0;

  for (;;) {
    esp_task_wdt_reset();

    if (millis() - lastRead >= 500) {
      lastRead = millis();
      bool changed = false;

      // ---- PSU: hysteresis band, then debounce ----
      float psuV = adcVolts(PIN_VOLT_PSU) * PSU_DIV_RATIO;
      bool rawPwr;
      if      (psuV <= PSU_FAIL_VOLTS) rawPwr = false;
      else if (psuV >= PSU_OK_VOLTS)   rawPwr = true;
      else                             rawPwr = st.power_ok;   // in the band: hold

      if (rawPwr != st.power_ok) {
        if (rawPwr == pwrCandidate) pwrCandidateCount++;
        else { pwrCandidate = rawPwr; pwrCandidateCount = 1; }

        if (pwrCandidateCount >= PWR_DEBOUNCE_COUNT) {
          st.power_ok = rawPwr;
          pwrCandidateCount = 0;
          changed = true;
          Serial.printf("[PWR] %s  (%.2f V)\n", rawPwr ? "OK" : "FAIL", psuV);
        }
      } else {
        pwrCandidateCount = 0;    // reading agrees with committed state; reset
      }

      // ---- Static-zone currents (arrows are intent-echo, not measured) ----
      float c1 = readCurrent(PIN_CURR_STA1, ZERO_STA1, SENS_STA1);
      float c2 = readCurrent(PIN_CURR_STA2, ZERO_STA2, SENS_STA2);

      // Snap the filters past a step WE caused, rather than ramping through it.
      unsigned long mc = lastModeChangeMs;
      if (mc != seenModeChange) {
        seenModeChange = mc;
        f1 = c1; f2 = c2;
      } else {
        f1 = (c1 < NOISE_FLOOR) ? 0 : (c1 * ALPHA) + (f1 * (1 - ALPHA));
        f2 = (c2 < NOISE_FLOOR) ? 0 : (c2 * ALPHA) + (f2 * (1 - ALPHA));
      }

      /*
       * ARROWS — intent-echo, evaluated every cycle. This is NOT a measurement,
       * so it needs no filtering and no settle window.
       *
       * At 2-strip chase current the 5A ACS712's signal (~40 ADC counts) sits
       * inside its own noise, so a measured 4-state verdict is unreliable and
       * false-alarms both ways (FAIL_SHORT when off, FAIL_OPEN when chasing). We
       * therefore report only what this side was COMMANDED to do — "ON"/"OFF",
       * never FAIL_*. Genuine arrow health is deferred to the Phase-3 scheduled
       * all-on self-test, where 5 lit strips (~5x the current) clear the noise.
       */
      const char* sl = expectLeftOn  ? "ON" : "OFF";
      const char* sr = expectRightOn ? "ON" : "OFF";
      if (strcmp(st.state_left, sl) || strcmp(st.state_right, sr)) {
        st.state_left = sl; st.state_right = sr;
        changed = true;
      }

      // Static zones ARE measured (they draw enough current to be reliable), but
      // their verdict is held while the load physically settles after a mode
      // change — an arrow side switching briefly perturbs the shared supply.
      // Hardwired always-on: forever expected ON. Only ON and FAIL_OPEN are legal.
      if (millis() - mc >= MODE_SETTLE_MS) {
        const char* s1 = getDiscrepancyState(true, f1, THRESHOLD_STATIC);
        const char* s2 = getDiscrepancyState(true, f2, THRESHOLD_STATIC);
        if (strcmp(st.state_sta1, s1) || strcmp(st.state_sta2, s2)) {
          st.state_sta1 = s1; st.state_sta2 = s2;
          changed = true;
        }
      }

      if (!haveSensorData) { haveSensorData = true; changed = true; }
      if (changed) xQueueOverwrite(sensorQueue, &st);
    }
    vTaskDelay(pdMS_TO_TICKS(10));
  }
}

// ========================================================
//  SERIAL MENU — bench observation only (h / i / s)
// ========================================================

void printMenu() {
  Serial.println(F("\n=============================================="));
  Serial.printf ("  SIGNAGE NODE v4.0.0-p1  —  register %d\n", ASSIGNED_REGISTER);
  Serial.println(F("=============================================="));
  Serial.println(F("  h -> this menu"));
  Serial.println(F("  i -> node info (net, MCP health, active mode)"));
  Serial.println(F("  s -> toggle sensor STREAM (1 Hz, raw + volts + amps)"));
  Serial.println(F("==============================================\n"));
}

void readSensorsVerbose() {
  int rl = getMedianADC(PIN_CURR_LEFT), rr = getMedianADC(PIN_CURR_RGHT);
  int r1 = getMedianADC(PIN_CURR_STA1), r2 = getMedianADC(PIN_CURR_STA2);
  int rp = getMedianADC(PIN_VOLT_PSU);

  float vl = (rl / 4095.0f) * ADC_REF, vr = (rr / 4095.0f) * ADC_REF;
  float v1 = (r1 / 4095.0f) * ADC_REF, v2 = (r2 / 4095.0f) * ADC_REF;

  // Arrows: raw ADC/volts/amps still shown for bench observation, but the STATE
  // column is the published intent-echo (see SensorTask) — never a FAIL verdict.
  Serial.println(F("\n  CH     rawADC   volts    amps    thresh   state"));
  Serial.printf("  LEFT   %5d   %.4f  %.4f  %.4f  %s (intent)\n", rl, vl,
                fabs((vl - ZERO_LEFT) / SENS_LEFT), dynamicThreshLeft,
                expectLeftOn ? "ON" : "OFF");
  Serial.printf("  RGHT   %5d   %.4f  %.4f  %.4f  %s (intent)\n", rr, vr,
                fabs((vr - ZERO_RGHT) / SENS_RGHT), dynamicThreshRight,
                expectRightOn ? "ON" : "OFF");
  Serial.printf("  STA1   %5d   %.4f  %.4f  %.4f  %s\n", r1, v1,
                fabs((v1 - ZERO_STA1) / SENS_STA1), THRESHOLD_STATIC,
                getDiscrepancyState(true, fabs((v1 - ZERO_STA1) / SENS_STA1), THRESHOLD_STATIC));
  Serial.printf("  STA2   %5d   %.4f  %.4f  %.4f  %s\n", r2, v2,
                fabs((v2 - ZERO_STA2) / SENS_STA2), THRESHOLD_STATIC,
                getDiscrepancyState(true, fabs((v2 - ZERO_STA2) / SENS_STA2), THRESHOLD_STATIC));
  Serial.printf("  PSU    %5d   %.4f  ->  %.2f V\n", rp, (rp / 4095.0f) * ADC_REF,
                (rp / 4095.0f) * ADC_REF * PSU_DIV_RATIO);
}

void printInfo() {
  Serial.println(F("\n---------------- NODE INFO ----------------"));
  Serial.printf("  Register      : %d\n", ASSIGNED_REGISTER);
  Serial.printf("  Ethernet      : %s\n", eth_connected ? ETH.localIP().toString().c_str() : "DOWN");
  Serial.printf("  MQTT          : %s (%s:%d)\n", client.connected() ? "connected" : "DISCONNECTED",
                mqtt_server_ip, mqtt_port);
  Serial.printf("  MCP23017      : %s\n", mcpHealthy ? "healthy" : "UNHEALTHY");
  Serial.printf("  Commanded     : %d\n", commandedValue);
  char act[24];
  if (!mcpHealthy) snprintf(act, sizeof(act), "UNKNOWN (65535)");
  else             snprintf(act, sizeof(act), "%d", activeCommand);
  Serial.printf("  Actually doing: %s\n", act);
  Serial.printf("  Ports  A=0x%02X  B=0x%02X\n", lastPortA, lastPortB);
  Serial.println(F("-------------------------------------------"));
}

bool sensorStream = false;
unsigned long lastStream = 0;

void handleSerial() {
  if (!Serial.available()) return;
  char c = Serial.read();
  while (Serial.available() && (Serial.peek() == '\n' || Serial.peek() == '\r')) Serial.read();

  switch (c) {
    case 'h': printMenu();          break;
    case 'i': printInfo();          break;
    case 's': sensorStream = !sensorStream;
              Serial.printf(">> Sensor stream %s\n", sensorStream ? "ON" : "OFF"); break;
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
  pinMode(PIN_VOLT_PSU, INPUT);
  pinMode(PIN_CURR_LEFT, INPUT); pinMode(PIN_CURR_RGHT, INPUT);
  pinMode(PIN_CURR_STA1, INPUT); pinMode(PIN_CURR_STA2, INPUT);

  snprintf(topic_cmd,    sizeof(topic_cmd),    "metro/signage/register/%d/value",       ASSIGNED_REGISTER);
  snprintf(topic_status, sizeof(topic_status), "metro/signage/register/%d/status",      ASSIGNED_REGISTER);
  snprintf(topic_power,  sizeof(topic_power),  "metro/signage/register/%d/power",       ASSIGNED_REGISTER);
  snprintf(topic_state,  sizeof(topic_state),  "metro/signage/register/%d/state",       ASSIGNED_REGISTER);
  snprintf(topic_curr1,  sizeof(topic_curr1),  "metro/signage/register/%d/current1",    ASSIGNED_REGISTER);
  snprintf(topic_curr2,  sizeof(topic_curr2),  "metro/signage/register/%d/current2",    ASSIGNED_REGISTER);
  snprintf(topic_curr3,  sizeof(topic_curr3),  "metro/signage/register/%d/current3",    ASSIGNED_REGISTER);
  snprintf(topic_curr4,  sizeof(topic_curr4),  "metro/signage/register/%d/current4",    ASSIGNED_REGISTER);

  /*
   * NETWORK FIRST — deliberately before the MCP23017.
   *
   * Initialising the MCP first and doing while(1) on failure means an I2C fault
   * blocks the PHY: MQTT never connects, the LWT never registers (it is part of
   * client.connect), and the node goes mute, indistinguishable from an unplugged
   * cable. Network-first, the node always gets online and reports what it can,
   * and an MCP fault surfaces as 'state' = 65535 on an ONLINE node.
   */
  WiFi.onEvent(eth_event_handler);
  ETH.begin(ETH_PHY_TYPE, ETH_PHY_ADDR, ETH_PHY_MDC, ETH_PHY_MDIO, ETH_PHY_POWER, ETH_CLK_MODE);
  ETH.config(local_IP, gateway_ip, subnet, primaryDNS);
  client.setServer(mqtt_server_ip, mqtt_port);
  client.setCallback(callback);

  // MCP second, non-fatal.
  Wire.begin(I2C_SDA, I2C_SCL);
  Serial.print(F("\nProbing MCP23017 @0x20 on SDA=4/SCL=13 ... "));
  if (mcp.begin_I2C(MCP_ADDR, &Wire)) {
    for (int i = 0; i < 16; i++) mcp.pinMode(i, OUTPUT);
    mcpHealthy = writePortsVerified(0x00, 0x00);
    Serial.println(mcpHealthy ? F("OK") : F("FOUND BUT READBACK FAILED"));
  } else {
    Serial.println(F("NOT FOUND — continuing anyway, will retry in background."));
    Serial.println(F("Check: SDA=GPIO4, SCL=GPIO13, 4.7k pull-ups, RESET->3V3, A0/A1/A2->GND."));
  }

  sensorQueue = xQueueCreate(1, sizeof(NodeStateMsg));

  esp_task_wdt_config_t wdt_config = {
    .timeout_ms = 15000,
    .idle_core_mask = (1 << portNUM_PROCESSORS) - 1,
    .trigger_panic = true
  };
  esp_task_wdt_init(&wdt_config);
  esp_task_wdt_add(NULL);

  xTaskCreatePinnedToCore(SensorTask, "SensorTask", 10000, NULL, 1, NULL, 0);

  printMenu();
}

void loop() {
  esp_task_wdt_reset();

  handleSerial();
  ensureMcpHealthy();

  // Unconditional: the sign must keep animating through a broker or link outage.
  // NVS persistence of the last command is deferred to Phase 2 — on reboot the
  // node comes up at 0 and waits for the retained 'value' topic to re-command it.
  runAnimationStateMachine();

  if (sensorStream && millis() - lastStream >= 1000) {
    lastStream = millis();
    readSensorsVerbose();
  }

  if (eth_connected) {
    if (!client.connected()) {
      static unsigned long lastReconnect = 0;
      if (millis() - lastReconnect > 5000) {
        lastReconnect = millis();
        char clientId[40];
        snprintf(clientId, sizeof(clientId), "ESP32-%s", ETH.macAddress().c_str());
        // LWT: retained OFFLINE on .../status, QoS 1. This is what lets the
        // gateway scrub a node that drops without a clean disconnect.
        if (client.connect(clientId, topic_status, 1, true, "OFFLINE")) {
          client.subscribe(topic_cmd, 1);
          client.subscribe(topic_scan, 0);
          publish_state_msg(networkState);
        }
      }
    } else {
      client.loop();
      NodeStateMsg tmp;
      if (xQueueReceive(sensorQueue, &tmp, 0) == pdTRUE) {
        networkState = tmp;
        publish_state_msg(networkState);
      }
    }
  }
}
