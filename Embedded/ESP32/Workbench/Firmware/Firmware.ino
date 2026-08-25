/*
 * ESP32 Enterprise Signage Controller — Firmware v3.0.0
 * =====================================================
 * Target : PCB V3, ESP32 + LAN8720 + MCP23017 (10 MOSFETs) + 4x ACS712
 * Pairs with : RaspberryPi/Gateway/Pi.py v2.0.0
 * Contract   : ESP32/esp32_contract.md
 *
 * THIS IS THE BENCH / BRING-UP ROUND (6 nodes).
 *   - Calibration constants live at the top of this file and are hand-editable.
 *   - The 'c' / 'g' / 'v' serial commands derive them automatically and print a
 *     paste-ready block. NVS persistence is DEFERRED — you calibrate, paste,
 *     reflash. When NVS lands, the same routines write to flash instead and the
 *     reflash step disappears. Nothing else has to change.
 *
 * WHAT CHANGED FROM v2.0 (all deliberate, see esp32_contract.md §8):
 *   1.  I2C moved 16/17 -> 4/13. GPIO17 is the LAN8720 50MHz clock.
 *   2.  4th ACS712 added (GPIO33, Static Zone 2) + 'current4' topic.
 *   3.  Modes 4/5/6 added. MODE 4 CHANGED MEANING: was solid-ON-all, now
 *       solid-ON-LEFT. Old solid-ON-all is now mode 6. Clear retained 'value'
 *       topics on the broker before flashing or a stale retained 4 lights the
 *       wrong zone.
 *   4.  'state' topic added — what this node is ACTUALLY executing, published
 *       only AFTER the MOSFET write is verified. Gateway exposes it at diag
 *       register +7 and flags commanded != actual.
 *   5.  Command parsing is validated. v2.0 used atoi(), so a corrupt payload
 *       became 0 == "arrows off" and silently blanked the sign.
 *   6.  Animation no longer stops when MQTT drops. v2.0 only called the state
 *       machine inside the connected branch, so a broker outage froze the chase
 *       mid-frame — directly contradicting the hold-last-command requirement.
 *   7.  MCP23017 failure no longer bricks the node. v2.0 did while(1) BEFORE
 *       ETH.begin(), so an I2C fault meant no network, no LWT, no telemetry —
 *       indistinguishable from a dead board. Boot order is now network-first
 *       and the MCP retries forever in the background.
 *   8.  MOSFET writes are read back and verified. writeGPIOA() returns void, so
 *       v2.0 could not tell a successful write from a failed one and would have
 *       reported a healthy 'state' while the sign was dark.
 *   9.  Thresholds are now set for BOTH sides on every mode change. v2.0 left
 *       stale thresholds on the side that turned off, which MASKED a real
 *       FAIL_SHORT (a shorted MOSFET read as OFF).
 *   10. Serial menu for bench work (h/s/r/c/g/v/m/p/i).
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
const char* mqtt_server_ip   = "192.168.1.10";
const int   mqtt_port        = 1883;
const int   ASSIGNED_REGISTER = 40001;              // node 1 = 40001, node 2 = 40002 ...

IPAddress local_IP  (192, 168, 1, 101);             // must be unique per unit
IPAddress gateway_ip(192, 168, 1, 1);
IPAddress subnet    (255, 255, 255, 0);
IPAddress primaryDNS(8, 8, 8, 8);

// ========================================================
//  EXPECTED LOAD CURRENTS — reference for auto-calibration
// ========================================================
/*
 * Auto-calibration ('g') derives each channel's SENSITIVITY by driving a known
 * load and comparing the measured voltage against these numbers. Fill them in
 * from the strip specs (V x A/m x length) before running 'g'.
 *
 * !! READ THIS BEFORE TRUSTING THE RESULT !!
 * Calibrating gain against an ASSUMED current forces the sensor to agree with
 * that assumption. If the real strips draw something else, the sensor is now
 * calibrated to call the wrong current "normal" and will never alarm on it.
 * The derived value is therefore sanity-checked against the datasheet below and
 * a warning is printed on a large deviation. With 6 bench nodes: calibrate all
 * six and compare. Tight agreement => the assumption is sound. Scatter => the
 * strips, the sensors or the wiring genuinely vary. Find that here, not in a
 * station.
 */
float EXPECTED_ARROW_PER_STRIP = 0.000f;   // A, ONE arrow strip lit     <-- FILL IN
float EXPECTED_STATIC_1        = 0.000f;   // A, static zone 1 total     <-- FILL IN
float EXPECTED_STATIC_2        = 0.000f;   // A, static zone 2 total     <-- FILL IN

// ========================================================
//  SENSOR CALIBRATION — hand-editable, overwritten by 'c'/'g'/'m'
// ========================================================
/*
 * ACS712 SENSITIVITY by variant:      5A part = 0.185 V/A
 *                                    20A part = 0.100 V/A
 *                                    30A part = 0.066 V/A
 *
 * v2.0 shipped with 0.146, which matches NO standard variant. Treat every value
 * here as a placeholder until 'g' has been run on the actual board.
 *
 * RESOLUTION NOTE — this drives which part goes where:
 *   One strip at THRESH_PER_STRIP (0.060 A) produces
 *       20A part: 6.0 mV  ~= 7 ADC counts   <-- marginal vs ADC noise
 *        5A part: 11.1 mV ~= 14 ADC counts  <-- roughly 2x the margin
 *   Detecting a whole side failing is fine either way (3 strips ~= 22 counts on
 *   the 20A part). It is the SINGLE-strip-out case that is at risk. So:
 *       ARROWS  -> prefer the 5A part  (low current, needs the resolution)
 *       STATIC  -> 20A part is fine    (higher current, only needs ON/FAIL_OPEN)
 *   Defaults below follow that. Flip them to match the parts actually fitted.
 */
float SENS_LEFT = 0.185f, ZERO_LEFT = 2.500f;   // arrows  — 5A part assumed
float SENS_RGHT = 0.185f, ZERO_RGHT = 2.500f;   // arrows  — 5A part assumed
float SENS_STA1 = 0.100f, ZERO_STA1 = 2.500f;   // static  — 20A part assumed
float SENS_STA2 = 0.100f, ZERO_STA2 = 2.500f;   // static  — 20A part assumed

// Datasheet reference used ONLY to sanity-check the derived gain.
float DS_SENS_LEFT = 0.185f;   // 5A=0.185 | 20A=0.100 | 30A=0.066
float DS_SENS_RGHT = 0.185f;   // 5A=0.185 | 20A=0.100 | 30A=0.066
float DS_SENS_STA1 = 0.100f;   // 5A=0.185 | 20A=0.100 | 30A=0.066
float DS_SENS_STA2 = 0.100f;   // 5A=0.185 | 20A=0.100 | 30A=0.066
const float SENS_TOLERANCE = 0.20f;   // warn if derived deviates >20%

// Voltage dividers. PLACEHOLDERS — set with 'v' against a multimeter reading.
float PSU_DIV_RATIO  = 5.30f;   // Vbus  = Vadc * ratio
float BATT_DIV_RATIO = 5.30f;   // Vbatt = Vadc * ratio  (battery stubbed, see below)

/*
 * PSU FAIL DETECTION — hysteresis + debounce.
 *
 * 'power=FAIL' from an ONLINE node is this system's critical alarm: a node
 * running on battery reporting that its PSU died. False positives are therefore
 * expensive — cry wolf a few times and the control room learns to ignore it.
 *
 * HYSTERESIS: a single edge makes a supply sagging to exactly the threshold
 * chatter OK/FAIL forever. Fail below FAIL_VOLTS, recover only above OK_VOLTS,
 * hold state in between.
 *
 * DEBOUNCE: require N consecutive agreeing reads before flipping, so a mains dip
 * or the inrush from a large load switching cannot publish a one-cycle FAIL.
 * At the 500 ms sensor cadence, 3 reads = 1.5 s.
 *
 * BOTH THRESHOLDS ARE PLACEHOLDERS. They must sit below the normal supply
 * voltage but ABOVE the battery's loaded voltage — otherwise the node reports
 * FAIL while running perfectly on mains. Set them from the real PSU and battery
 * chemistry on the bench.
 */
float PSU_FAIL_VOLTS = 10.0f;         // below this => FAIL
float PSU_OK_VOLTS   = 11.0f;         // above this => OK (must be > PSU_FAIL_VOLTS)
const int PWR_DEBOUNCE_COUNT = 3;     // consecutive agreeing reads before flipping

// ========================================================
//  DISCREPANCY THRESHOLDS
// ========================================================
const float THRESH_PER_STRIP = 0.060f;  // min acceptable current for ONE strip
const float THRESHOLD_STATIC = 0.080f;  // static zones: load never changes

/*
 * When a side is expected OFF we still need to catch a stuck-on MOSFET, so it
 * gets a LOW threshold rather than a stale high one. v2.0 left the previous
 * mode's threshold in place, which meant a shorted MOSFET drawing three strips
 * worth of current still read below it and reported OFF — a real fault silently
 * suppressed.
 *
 * ORDERING CONSTRAINT: NOISE_FLOOR must stay BELOW THRESH_OFF_DETECT. The noise
 * gate snaps small readings to zero; if it sat above the off-threshold it would
 * zero out exactly the currents FAIL_SHORT is meant to catch, re-creating the
 * bug by a different route.
 */
const float THRESH_OFF_DETECT = 0.030f;  // expected-OFF side: above this => FAIL_SHORT
const float NOISE_FLOOR       = 0.020f;  // below this => treat as 0 A

const float ADC_REF = 3.3f;
const float ALPHA   = 0.15f;   // IIR low-pass

/*
 * MODE-CHANGE SETTLE — do not evaluate current states straight after a switch.
 *
 * The IIR filter is deliberately slow. At ALPHA=0.15 and a 500 ms cadence it
 * needs roughly 14 samples (~7 s) to climb to 90% of a step change — and the
 * solid-ON threshold sits at exactly 90% of expected (4.5 of 5 strips). So
 * without this, EVERY mode change from off to on would publish FAIL_OPEN for
 * about seven seconds before self-clearing. Every command would raise a false
 * critical alarm.
 *
 * Two-part fix: the filter is SNAPPED to the instantaneous reading on a mode
 * change (the filter exists to smooth noise, not to smooth a step we ourselves
 * caused), and state evaluation is suppressed for a short window while the load
 * physically settles.
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
const int PIN_VOLT_BATT = 39;   // battery stubbed this round
const int PIN_CURR_LEFT = 34;   // ACS712 #1  LHS arrows   (switched)
const int PIN_CURR_RGHT = 35;   // ACS712 #2  RHS arrows   (switched)
const int PIN_CURR_STA1 = 32;   // ACS712 #3  Static Zone 1 (always on)
const int PIN_CURR_STA2 = 33;   // ACS712 #4  Static Zone 2 (always on)

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
volatile bool calibrationActive = false;

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

const uint8_t chaseFrames[5] = {0x07, 0x0E, 0x1C, 0x19, 0x13};
const int numFrames = 5;
const unsigned long FRAME_MS = 300;

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
   *   or calibrating       non-numeric payload; the gateway maps it to 65535.
   *
   * Why not reuse the last known number in the second case: an operator could
   * clear the alarm by accident. They see the mismatch, try commanding the value
   * that happens to match the stale number, gateway sees commanded == actual,
   * the flag clears and the screen goes green — while the sign is still dark.
   * 65535 can never equal a valid command, so the fault cannot be hidden by
   * commanding anything. It clears only when the hardware is fixed.
   */
  if (!mcpHealthy || calibrationActive) {
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

  // Suppress current states until the first real sensor cycle has run, and while
  // calibrating (we are deliberately switching loads; the readings are garbage
  // relative to the commanded mode and would alarm a live control room).
  if (haveSensorData && !calibrationActive) {
    client.publish(topic_curr1, msg.state_left,  true);
    client.publish(topic_curr2, msg.state_right, true);
    client.publish(topic_curr3, msg.state_sta1,  true);
    client.publish(topic_curr4, msg.state_sta2,  true);
  }

  publishState();

  // battery_pct is deliberately NOT published. The gateway maps a missing value
  // to 65535 = unknown. Publishing 0 would read as a genuinely flat battery.
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
   * Validated parse. v2.0 used atoi(), which returns 0 for unparseable input —
   * and 0 is a perfectly valid command meaning "arrows off". A corrupt payload
   * therefore blanked the sign with no error anywhere. strtol lets us tell
   * "the number zero" from "not a number".
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
 * v2.0 only ever set the side that was turning ON, leaving the other side
 * carrying the previous mode's threshold. Modes 4 and 5 are the trap: one side
 * goes solid while the other must be expected OFF.
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
 * Two separate jobs happen here: bookkeeping (counting time, tracking which of
 * the 5 frames is next) and sending (pushing it over I2C). If the network drops
 * we must keep animating, per the hold-last-command failsafe. If the MCP is
 * dead the send fails harmlessly but the bookkeeping keeps ticking, so the
 * moment I2C recovers the next frame goes out and the sign resumes on its own.
 */
void runAnimationStateMachine() {
  static unsigned long previousMillis = 0;
  static int currentFrame = 0;
  static int lastExecutedCommand = -1;

  if (calibrationActive) return;   // calibration owns the outputs

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
        case 4: portA = 0x1F;                    portB = 0x00;                    break;  // LEFT only (was all-on in v2.0)
        case 5: portA = 0x00;                    portB = 0x1F;                    break;
        case 6: portA = 0x1F;                    portB = 0x1F;                    break;  // the old mode 4
      }

      if (modeChanged) {
        switch (cmd) {
          case 0: setThresholds(0, 0); break;
          case 1: setThresholds(3, 0); break;
          case 2: setThresholds(0, 3); break;
          case 3: setThresholds(3, 3); break;
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
    // Stamp the MODE change only — not each chase frame. A chase holds 3 strips
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
  float fl = 0, fr = 0, f1 = 0, f2 = 0;
  unsigned long lastRead = 0;

  bool pwrCandidate = true;      // debounce: the value we are counting towards
  int  pwrCandidateCount = 0;
  unsigned long seenModeChange = 0;

  for (;;) {
    esp_task_wdt_reset();

    if (!calibrationActive && millis() - lastRead >= 500) {
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

      // ---- Currents ----
      float cl = readCurrent(PIN_CURR_LEFT, ZERO_LEFT, SENS_LEFT);
      float cr = readCurrent(PIN_CURR_RGHT, ZERO_RGHT, SENS_RGHT);
      float c1 = readCurrent(PIN_CURR_STA1, ZERO_STA1, SENS_STA1);
      float c2 = readCurrent(PIN_CURR_STA2, ZERO_STA2, SENS_STA2);

      // Snap the filters past a step WE caused, rather than ramping through it.
      unsigned long mc = lastModeChangeMs;
      if (mc != seenModeChange) {
        seenModeChange = mc;
        fl = cl; fr = cr; f1 = c1; f2 = c2;
      } else {
        fl = (cl < NOISE_FLOOR) ? 0 : (cl * ALPHA) + (fl * (1 - ALPHA));
        fr = (cr < NOISE_FLOOR) ? 0 : (cr * ALPHA) + (fr * (1 - ALPHA));
        f1 = (c1 < NOISE_FLOOR) ? 0 : (c1 * ALPHA) + (f1 * (1 - ALPHA));
        f2 = (c2 < NOISE_FLOOR) ? 0 : (c2 * ALPHA) + (f2 * (1 - ALPHA));
      }

      // Still physically settling — publish nothing about the currents yet.
      if (millis() - mc < MODE_SETTLE_MS) {
        if (changed) xQueueOverwrite(sensorQueue, &st);
        vTaskDelay(pdMS_TO_TICKS(10));
        continue;
      }

      const char* sl = getDiscrepancyState(expectLeftOn,  fl, dynamicThreshLeft);
      const char* sr = getDiscrepancyState(expectRightOn, fr, dynamicThreshRight);
      // Static zones are hardwired always-on: forever expected ON, fixed threshold.
      // Their only legal states are ON and FAIL_OPEN.
      const char* s1 = getDiscrepancyState(true, f1, THRESHOLD_STATIC);
      const char* s2 = getDiscrepancyState(true, f2, THRESHOLD_STATIC);

      if (strcmp(st.state_left, sl) || strcmp(st.state_right, sr) ||
          strcmp(st.state_sta1, s1) || strcmp(st.state_sta2, s2)) {
        st.state_left = sl; st.state_right = sr;
        st.state_sta1 = s1; st.state_sta2 = s2;
        changed = true;
      }

      if (!haveSensorData) { haveSensorData = true; changed = true; }
      if (changed) xQueueOverwrite(sensorQueue, &st);
    }
    vTaskDelay(pdMS_TO_TICKS(10));
  }
}

// ========================================================
//  SERIAL MENU — bench / commissioning
// ========================================================

void printMenu() {
  Serial.println(F("\n=============================================="));
  Serial.printf ("  SIGNAGE NODE v3.0.0  —  register %d\n", ASSIGNED_REGISTER);
  Serial.println(F("=============================================="));
  Serial.println(F("  h -> this menu"));
  Serial.println(F("  i -> node info (net, MCP health, active mode)"));
  Serial.println(F("  r -> read all sensors ONCE"));
  Serial.println(F("  s -> toggle sensor STREAM (1 Hz, raw + volts + amps)"));
  Serial.println(F("  c -> auto-calibrate ZERO points (arrows only)"));
  Serial.println(F("  g -> auto-calibrate GAIN from expected currents"));
  Serial.println(F("  v -> calibrate voltage dividers (needs a multimeter)"));
  Serial.println(F("  m -> manually set one channel's zero/sens"));
  Serial.println(F("  p -> print paste-ready calibration block"));
  Serial.println(F("==============================================\n"));
}

/*
 * Serial line read that keeps the system alive while it waits. Anything that
 * blocks in this firmware MUST pump the watchdog and the MQTT client — a bare
 * delay() loop would drop the broker session on keepalive and then trip the 15 s
 * watchdog into a panic reboot. (This is why the Firmware_Test menu handlers
 * cannot simply be copied across: they use blocking delay().)
 */
bool readSerialLine(char* buf, size_t len, unsigned long timeoutMs) {
  size_t i = 0;
  unsigned long start = millis();
  while (millis() - start < timeoutMs) {
    esp_task_wdt_reset();
    if (client.connected()) client.loop();
    while (Serial.available()) {
      char ch = Serial.read();
      if (ch == '\n' || ch == '\r') {
        if (i > 0) { buf[i] = '\0'; return true; }
      } else if (i < len - 1) {
        buf[i++] = ch;
      }
    }
    delay(2);
  }
  buf[0] = '\0';
  return false;
}

void calSettle(unsigned long ms) {
  unsigned long start = millis();
  while (millis() - start < ms) {
    esp_task_wdt_reset();
    if (client.connected()) client.loop();
    delay(5);
  }
}

void enterCalMode() {
  calibrationActive = true;
  publishState();     // -> FAULT -> gateway reads 65535
  Serial.println(F("\n[CAL] Entering calibration. Node stays ONLINE; 'state' now reports"));
  Serial.println(F("      65535 so SCADA shows an un-clearable mismatch for the duration."));
}

void exitCalMode() {
  calibrationActive = false;
  writePortsVerified(lastPortA, lastPortB);   // restore the commanded output
  publishState();
  Serial.println(F("[CAL] Done. Output state restored, telemetry resumed.\n"));
}

void checkDerivedSens(const char* name, float derived, float datasheet) {
  float dev = (datasheet > 0) ? (derived - datasheet) / datasheet : 0;
  Serial.printf("[CAL] %-6s derived SENS = %.4f V/A  (datasheet %.3f, %+.1f%%)  %s\n",
                name, derived, datasheet, dev * 100.0f,
                (fabs(dev) > SENS_TOLERANCE) ? "** CHECK LOAD/WIRING **" : "OK");
}

/*
 * 'c' — ZERO POINT.
 * Fully automatic for the arrows: switch the MOSFETs off, average the ADC.
 *
 * NOT possible for the static zones. They are hardwired always-on with no
 * MOSFET, so while the board is powered there is ALWAYS current through those
 * two sensors and a true zero can never be observed. That leaves one
 * measurement against two unknowns (zero and gain) — mathematically
 * underdetermined, no unique solution. So one has to come from outside, and the
 * best available estimate is the arrows' measured zero: same part, same 5 V
 * rail, same board, same temperature, same ADC. Far better than the datasheet's
 * nominal 2.5 V.
 */
void calibrateZero() {
  enterCalMode();
  Serial.println(F("[CAL] All MOSFETs OFF, settling 2 s..."));
  writePortsVerified(0x00, 0x00);
  calSettle(2000);

  ZERO_LEFT = adcVolts(PIN_CURR_LEFT);
  ZERO_RGHT = adcVolts(PIN_CURR_RGHT);
  float borrowed = (ZERO_LEFT + ZERO_RGHT) / 2.0f;
  ZERO_STA1 = borrowed;
  ZERO_STA2 = borrowed;

  Serial.printf("[CAL] ZERO_LEFT = %.4f V   (measured)\n", ZERO_LEFT);
  Serial.printf("[CAL] ZERO_RGHT = %.4f V   (measured)\n", ZERO_RGHT);
  Serial.printf("[CAL] ZERO_STA1 = %.4f V   (BORROWED from arrows — see note)\n", ZERO_STA1);
  Serial.printf("[CAL] ZERO_STA2 = %.4f V   (BORROWED from arrows — see note)\n", ZERO_STA2);
  Serial.println(F("[CAL] Static zones cannot be zeroed in place (no MOSFET to switch)."));
  Serial.println(F("      For a true static zero, calibrate before the strips are wired."));

  exitCalMode();
}

/*
 * 'g' — GAIN, derived from the EXPECTED_* constants.
 * Drives each side solid ON and solves sens = (Vmeasured - Vzero) / Iexpected.
 */
void calibrateGain() {
  if (EXPECTED_ARROW_PER_STRIP <= 0 || EXPECTED_STATIC_1 <= 0 || EXPECTED_STATIC_2 <= 0) {
    Serial.println(F("[CAL] ABORT: EXPECTED_* currents are still 0.000."));
    Serial.println(F("      Fill them in at the top of the file first."));
    return;
  }

  enterCalMode();

  float iArrow = EXPECTED_ARROW_PER_STRIP * 5.0f;   // solid ON = 5 strips

  Serial.println(F("[CAL] LEFT solid ON, settling 2 s..."));
  writePortsVerified(0x1F, 0x00);
  calSettle(2000);
  SENS_LEFT = (adcVolts(PIN_CURR_LEFT) - ZERO_LEFT) / iArrow;

  Serial.println(F("[CAL] RIGHT solid ON, settling 2 s..."));
  writePortsVerified(0x00, 0x1F);
  calSettle(2000);
  SENS_RGHT = (adcVolts(PIN_CURR_RGHT) - ZERO_RGHT) / iArrow;

  writePortsVerified(0x00, 0x00);
  calSettle(1000);
  SENS_STA1 = (adcVolts(PIN_CURR_STA1) - ZERO_STA1) / EXPECTED_STATIC_1;
  SENS_STA2 = (adcVolts(PIN_CURR_STA2) - ZERO_STA2) / EXPECTED_STATIC_2;

  Serial.println();
  checkDerivedSens("LEFT", SENS_LEFT, DS_SENS_LEFT);
  checkDerivedSens("RGHT", SENS_RGHT, DS_SENS_RGHT);
  checkDerivedSens("STA1", SENS_STA1, DS_SENS_STA1);
  checkDerivedSens("STA2", SENS_STA2, DS_SENS_STA2);
  Serial.println(F("[CAL] Reminder: gain derived from ASSUMED current. Run all 6 bench"));
  Serial.println(F("      nodes and compare — tight agreement validates the assumption."));

  exitCalMode();
}

void calibrateDividers() {
  char buf[24];

  Serial.println(F("\n[CAL] Divider ratio cannot be self-derived — the node can only read a"));
  Serial.println(F("      voltage, never the true bus voltage. Measure it and type it in."));

  Serial.print(F("[CAL] Measured PSU bus volts (blank to skip): "));
  if (readSerialLine(buf, sizeof(buf), 30000) && buf[0]) {
    float actual = atof(buf);
    float vadc = adcVolts(PIN_VOLT_PSU);
    if (actual > 0 && vadc > 0.01f) {
      PSU_DIV_RATIO = actual / vadc;
      Serial.printf("\n[CAL] PSU_DIV_RATIO = %.4f  (ADC saw %.4f V)\n", PSU_DIV_RATIO, vadc);
    } else Serial.println(F("\n[CAL] Skipped — bad input or no signal."));
  } else Serial.println(F("\n[CAL] Skipped."));

  Serial.print(F("[CAL] Measured BATTERY volts (blank to skip): "));
  if (readSerialLine(buf, sizeof(buf), 30000) && buf[0]) {
    float actual = atof(buf);
    float vadc = adcVolts(PIN_VOLT_BATT);
    if (actual > 0 && vadc > 0.01f) {
      BATT_DIV_RATIO = actual / vadc;
      Serial.printf("\n[CAL] BATT_DIV_RATIO = %.4f  (ADC saw %.4f V)\n", BATT_DIV_RATIO, vadc);
    } else Serial.println(F("\n[CAL] Skipped — bad input or no signal."));
  } else Serial.println(F("\n[CAL] Skipped."));
}

void manualCalibrate() {
  char buf[24];
  Serial.print(F("\n[MAN] Channel (1=LEFT 2=RGHT 3=STA1 4=STA2): "));
  if (!readSerialLine(buf, sizeof(buf), 20000)) { Serial.println(F("\n[MAN] Timeout.")); return; }
  int ch = atoi(buf);
  if (ch < 1 || ch > 4) { Serial.println(F("\n[MAN] Bad channel.")); return; }

  Serial.print(F("\n[MAN] ZERO volts (blank keeps current): "));
  bool gotZ = readSerialLine(buf, sizeof(buf), 20000) && buf[0];
  float z = gotZ ? atof(buf) : 0;

  Serial.print(F("\n[MAN] SENS V/A (blank keeps current): "));
  char buf2[24];
  bool gotS = readSerialLine(buf2, sizeof(buf2), 20000) && buf2[0];
  float s = gotS ? atof(buf2) : 0;

  switch (ch) {
    case 1: if (gotZ) ZERO_LEFT = z; if (gotS && s > 0) SENS_LEFT = s; break;
    case 2: if (gotZ) ZERO_RGHT = z; if (gotS && s > 0) SENS_RGHT = s; break;
    case 3: if (gotZ) ZERO_STA1 = z; if (gotS && s > 0) SENS_STA1 = s; break;
    case 4: if (gotZ) ZERO_STA2 = z; if (gotS && s > 0) SENS_STA2 = s; break;
  }
  Serial.println(F("\n[MAN] Applied (RAM only — press 'p' and paste to make it stick)."));
}

/*
 * 'p' — paste-ready block. NVS is deferred this round, so calibration survives a
 * reboot only by being pasted back into the source and reflashed. When NVS
 * lands, these same routines write to flash and this step goes away.
 */
void printCalBlock() {
  Serial.println(F("\n// ---- PASTE INTO THE CALIBRATION BLOCK ----"));
  Serial.printf("float SENS_LEFT = %.4ff, ZERO_LEFT = %.4ff;\n", SENS_LEFT, ZERO_LEFT);
  Serial.printf("float SENS_RGHT = %.4ff, ZERO_RGHT = %.4ff;\n", SENS_RGHT, ZERO_RGHT);
  Serial.printf("float SENS_STA1 = %.4ff, ZERO_STA1 = %.4ff;\n", SENS_STA1, ZERO_STA1);
  Serial.printf("float SENS_STA2 = %.4ff, ZERO_STA2 = %.4ff;\n", SENS_STA2, ZERO_STA2);
  Serial.printf("float PSU_DIV_RATIO  = %.4ff;\n", PSU_DIV_RATIO);
  Serial.printf("float BATT_DIV_RATIO = %.4ff;\n", BATT_DIV_RATIO);
  Serial.println(F("// ------------------------------------------"));
  // CSV for cross-comparing the 6 bench nodes in a spreadsheet.
  Serial.printf("CSV,%d,%.4f,%.4f,%.4f,%.4f,%.4f,%.4f,%.4f,%.4f\n",
                ASSIGNED_REGISTER, ZERO_LEFT, SENS_LEFT, ZERO_RGHT, SENS_RGHT,
                ZERO_STA1, SENS_STA1, ZERO_STA2, SENS_STA2);
}

void readSensorsVerbose() {
  int rl = getMedianADC(PIN_CURR_LEFT), rr = getMedianADC(PIN_CURR_RGHT);
  int r1 = getMedianADC(PIN_CURR_STA1), r2 = getMedianADC(PIN_CURR_STA2);
  int rp = getMedianADC(PIN_VOLT_PSU),  rb = getMedianADC(PIN_VOLT_BATT);

  float vl = (rl / 4095.0f) * ADC_REF, vr = (rr / 4095.0f) * ADC_REF;
  float v1 = (r1 / 4095.0f) * ADC_REF, v2 = (r2 / 4095.0f) * ADC_REF;

  Serial.println(F("\n  CH     rawADC   volts    amps    thresh   state"));
  Serial.printf("  LEFT   %5d   %.4f  %.4f  %.4f  %s\n", rl, vl,
                fabs((vl - ZERO_LEFT) / SENS_LEFT), dynamicThreshLeft,
                getDiscrepancyState(expectLeftOn, fabs((vl - ZERO_LEFT) / SENS_LEFT), dynamicThreshLeft));
  Serial.printf("  RGHT   %5d   %.4f  %.4f  %.4f  %s\n", rr, vr,
                fabs((vr - ZERO_RGHT) / SENS_RGHT), dynamicThreshRight,
                getDiscrepancyState(expectRightOn, fabs((vr - ZERO_RGHT) / SENS_RGHT), dynamicThreshRight));
  Serial.printf("  STA1   %5d   %.4f  %.4f  %.4f  %s\n", r1, v1,
                fabs((v1 - ZERO_STA1) / SENS_STA1), THRESHOLD_STATIC,
                getDiscrepancyState(true, fabs((v1 - ZERO_STA1) / SENS_STA1), THRESHOLD_STATIC));
  Serial.printf("  STA2   %5d   %.4f  %.4f  %.4f  %s\n", r2, v2,
                fabs((v2 - ZERO_STA2) / SENS_STA2), THRESHOLD_STATIC,
                getDiscrepancyState(true, fabs((v2 - ZERO_STA2) / SENS_STA2), THRESHOLD_STATIC));
  Serial.printf("  PSU    %5d   %.4f  ->  %.2f V\n", rp, (rp / 4095.0f) * ADC_REF,
                (rp / 4095.0f) * ADC_REF * PSU_DIV_RATIO);
  Serial.printf("  BATT   %5d   %.4f  ->  %.2f V   (stubbed, not published)\n", rb,
                (rb / 4095.0f) * ADC_REF, (rb / 4095.0f) * ADC_REF * BATT_DIV_RATIO);
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
  if (!mcpHealthy || calibrationActive) snprintf(act, sizeof(act), "UNKNOWN (65535)");
  else                                  snprintf(act, sizeof(act), "%d", activeCommand);
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
    case 'h': printMenu();            break;
    case 'i': printInfo();            break;
    case 'r': readSensorsVerbose();   break;
    case 's': sensorStream = !sensorStream;
              Serial.printf(">> Sensor stream %s\n", sensorStream ? "ON" : "OFF"); break;
    case 'c': calibrateZero();        break;
    case 'g': calibrateGain();        break;
    case 'v': calibrateDividers();    break;
    case 'm': manualCalibrate();      break;
    case 'p': printCalBlock();        break;
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
  pinMode(PIN_VOLT_PSU, INPUT);  pinMode(PIN_VOLT_BATT, INPUT);
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
   * v2.0 initialised the MCP first and did while(1) on failure, ~24 lines before
   * ETH.begin(). An I2C fault therefore meant the PHY never came up, MQTT never
   * connected, and the LWT never even registered (it is part of client.connect).
   * The node went completely mute and looked identical to an unplugged cable.
   * Now the node always gets online and reports what it can, and an MCP fault
   * surfaces as 'state' = 65535 on an ONLINE node — a distinct signature.
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
  // NVS persistence of the last command is deferred — on reboot the node comes up
  // at 0 and waits for the retained 'value' topic to re-command it.
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
