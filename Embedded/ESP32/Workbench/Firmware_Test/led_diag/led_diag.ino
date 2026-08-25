/*
 * ESP32 Enterprise Signage Controller — I2C / MCP23017 FAULT DIAGNOSTIC
 * --------------------------------------------------------------------
 * Purpose: Find out WHY the LED chase freezes after ~35 s and why the board
 *          then reports "MCP23017 NOT FOUND" until it is power cycled.
 *
 * This runs the SAME animation as led_test_2.ino (Chase 2, 2-LED arrow,
 * 250 ms, LEFT 10s / RIGHT 10s / OFF 3s) so the electrical load profile is
 * identical and the fault reproduces the same way. Everything added here is
 * instrumentation.
 *
 * WHAT IT TELLS YOU
 * -----------------
 * Every I2C write is checked, and every 2 s the MCP23017's IODIRA register is
 * read back. We set IODIRA to 0x00 (all outputs) at init; the chip's power-on
 * default is 0xFF (all inputs). So the read-back is a three-way discriminator:
 *
 *   IODIRA reads 0x00  -> chip healthy, retained its configuration
 *   IODIRA reads 0xFF  -> THE MCP REBOOTED. It lost power and came back with
 *                         factory defaults => BROWNOUT / 3.3 V rail collapse
 *   read fails         -> bus wedged or chip not responding => latch-up,
 *                         held-low SDA, or a dead chip
 *
 * The endTransmission() error code narrows it further:
 *   2 = NACK on address  -> chip is not answering at all (reset, dead, latched)
 *   5 = timeout          -> a device is holding the bus low
 *
 * The ESP32's own reset reason is printed at boot. If it ever says BROWNOUT,
 * the supply problem is bad enough to be dropping the ESP32 too.
 *
 * Unlike led_test_2.ino this sketch NEVER halts. It logs, attempts bus
 * recovery, and keeps running so you can watch the whole failure play out.
 */

#include <Wire.h>
#include <esp_system.h>

// --- FINALIZED I2C PINS (per PCB_V3/Connections.md) ---
#define I2C_SDA 4
#define I2C_SCL 13
#define MCP_ADDR 0x20

// --- MCP23017 REGISTERS (IOCON.BANK = 0, the power-on default) ---
#define REG_IODIRA 0x00
#define REG_IODIRB 0x01
#define REG_GPIOA  0x12
#define REG_GPIOB  0x13

// --- ANIMATION: identical to led_test_2.ino so the load profile matches ---
const unsigned long LEFT_MS  = 10000;
const unsigned long RIGHT_MS = 10000;
const unsigned long OFF_MS   = 3000;
const int           CHASE_LEDS  = 2;
const int           CHASE_MODE  = 2;
const unsigned long CHASE_DELAY = 250;

// --- DIAGNOSTIC INTERVALS ---
const unsigned long HEALTH_MS    = 2000;  // read back IODIRA this often
const unsigned long HEARTBEAT_MS = 5000;  // print a summary line this often

// --- DEMO STATE MACHINE ---
enum Phase { PHASE_LEFT, PHASE_RIGHT, PHASE_OFF };
Phase         phase      = PHASE_LEFT;
unsigned long phaseStart = 0;
unsigned long lastStep   = 0;
int           frameIdx   = 0;
bool          animating  = true;

// --- DIAGNOSTIC COUNTERS ---
uint32_t      writeCount       = 0;
uint32_t      writeFailCount   = 0;
uint32_t      consecFails      = 0;
unsigned long firstFailMs      = 0;   // 0 = no failure yet
uint32_t      healthChecks     = 0;
uint32_t      healthFails      = 0;
uint32_t      mcpRebootCount   = 0;   // times IODIRA came back as 0xFF
uint32_t      recoveryAttempts = 0;
uint32_t      recoverySuccess  = 0;
bool          degraded         = false;
unsigned long lastHealth       = 0;
unsigned long lastHeartbeat    = 0;

// --- SERIAL LINE BUFFER ---
char lineBuf[24];
int  lineLen = 0;

// ========================================================
// TIME / FORMATTING
// ========================================================

// Every diagnostic line is timestamped in seconds since boot, so you can read
// the exact uptime at which the first failure appears. A consistent time points
// at something thermal; a varying one points at a load transient.
void stamp() {
  Serial.printf("[%8.2f s] ", millis() / 1000.0);
}

void printBits(uint8_t v) {
  for (int b = 4; b >= 0; b--) Serial.print((v >> b) & 1);
}

const char *twiError(uint8_t rc) {
  switch (rc) {
    case 0: return "OK";
    case 1: return "data too long";
    case 2: return "NACK on ADDRESS - chip not answering (reset/dead/latched)";
    case 3: return "NACK on DATA";
    case 4: return "other error";
    case 5: return "TIMEOUT - a device is holding the bus low";
    default: return "unknown";
  }
}

const char *resetReason() {
  switch (esp_reset_reason()) {
    case ESP_RST_POWERON:  return "POWER-ON (clean cold start)";
    case ESP_RST_EXT:      return "EXTERNAL (reset button)";
    case ESP_RST_SW:       return "SOFTWARE";
    case ESP_RST_PANIC:    return "PANIC / exception";
    case ESP_RST_INT_WDT:  return "INTERRUPT WATCHDOG";
    case ESP_RST_TASK_WDT: return "TASK WATCHDOG";
    case ESP_RST_WDT:      return "OTHER WATCHDOG";
    case ESP_RST_BROWNOUT: return "*** BROWNOUT *** - the 3.3V rail collapsed!";
    case ESP_RST_DEEPSLEEP: return "deep sleep wake";
    default:               return "UNKNOWN";
  }
}

// ========================================================
// RAW I2C (we bypass the Adafruit driver so we can see every return code)
// ========================================================

uint8_t rawWrite(uint8_t reg, uint8_t val) {
  Wire.beginTransmission(MCP_ADDR);
  Wire.write(reg);
  Wire.write(val);
  return Wire.endTransmission(); // 0 = success
}

// NOTE: this deliberately uses a full STOP (endTransmission(true)) between
// setting the register pointer and reading it back, NOT a repeated start.
// Repeated start is the least reliable transaction type on the ESP32 Arduino
// core and can return error 4 on its own — which would make the diagnostic
// blame the hardware for a quirk in the driver. The MCP23017 latches its
// address pointer, so a STOP in between works fine and is unambiguous.
bool rawRead(uint8_t reg, uint8_t &out, uint8_t &rc) {
  Wire.beginTransmission(MCP_ADDR);
  Wire.write(reg);
  rc = Wire.endTransmission(true);
  if (rc != 0) return false;
  if (Wire.requestFrom((uint8_t)MCP_ADDR, (uint8_t)1) != 1) { rc = 4; return false; }
  out = Wire.read();
  return true;
}

// Standard I2C bus recovery: if a slave is stuck mid-byte holding SDA low, it
// releases after it sees enough clock edges. We bit-bang 9 clocks then issue a
// manual STOP.
//
// Do not over-read the success ratio. The verification that follows is several
// transactions long, so on a noisy bus it fails by chance regardless of whether
// the bus was ever actually stuck.
void i2cBusRecover() {
  Wire.end();

  pinMode(I2C_SCL, OUTPUT_OPEN_DRAIN);
  pinMode(I2C_SDA, INPUT_PULLUP);
  digitalWrite(I2C_SCL, HIGH);
  delayMicroseconds(10);

  for (int i = 0; i < 9; i++) {
    digitalWrite(I2C_SCL, LOW);  delayMicroseconds(10);
    digitalWrite(I2C_SCL, HIGH); delayMicroseconds(10);
  }

  // Manual STOP: SDA low -> SCL high -> SDA high
  pinMode(I2C_SDA, OUTPUT_OPEN_DRAIN);
  digitalWrite(I2C_SDA, LOW);  delayMicroseconds(10);
  digitalWrite(I2C_SCL, HIGH); delayMicroseconds(10);
  digitalWrite(I2C_SDA, HIGH); delayMicroseconds(10);

  Wire.begin(I2C_SDA, I2C_SCL);
  Wire.setClock(100000);
  Wire.setTimeOut(50);
}

// Push our configuration into the chip: both ports all-outputs, all off.
bool mcpConfigure() {
  if (rawWrite(REG_IODIRA, 0x00) != 0) return false;
  if (rawWrite(REG_IODIRB, 0x00) != 0) return false;
  if (rawWrite(REG_GPIOA,  0x00) != 0) return false;
  if (rawWrite(REG_GPIOB,  0x00) != 0) return false;
  return true;
}

void attemptRecovery() {
  recoveryAttempts++;
  stamp();
  Serial.printf("~~ RECOVERY attempt #%lu: pulsing 9 clocks + STOP, then reconfiguring\n",
                (unsigned long)recoveryAttempts);

  i2cBusRecover();

  if (mcpConfigure()) {
    uint8_t v, rc;
    if (rawRead(REG_IODIRA, v, rc) && v == 0x00) {
      recoverySuccess++;
      stamp();
      Serial.println(F("~~ RECOVERY SUCCEEDED - bus is back and config verified"));
      return;
    }
  }
  stamp();
  Serial.println(F("~~ RECOVERY FAILED - did not verify after the pulse + STOP"));
  Serial.println(F("   NOTE: this check is 4 writes + 1 read. At a high error rate it fails"));
  Serial.println(F("   by chance alone, so a low success ratio here means little. Only treat"));
  Serial.println(F("   the bus as truly stuck if writes NEVER recover on their own."));
}

// Tracked write. Logs the transition into and out of the failed state rather
// than spamming a line per write.
bool mcpWrite(uint8_t reg, uint8_t val) {
  writeCount++;
  uint8_t rc = rawWrite(reg, val);

  if (rc == 0) {
    if (consecFails > 0) {
      stamp();
      Serial.printf(">> WRITES RECOVERED after %lu consecutive failures\n",
                    (unsigned long)consecFails);
      consecFails = 0;
      degraded = false;
    }
    return true;
  }

  writeFailCount++;
  consecFails++;

  if (firstFailMs == 0) {
    firstFailMs = millis();
    Serial.println();
    stamp();
    Serial.println(F("!!!!!! FIRST I2C WRITE FAILURE !!!!!!"));
    stamp();
    Serial.printf("   reg 0x%02X val 0x%02X -> error %u: %s\n", reg, val, rc, twiError(rc));
    stamp();
    Serial.printf("   survived %lu successful writes / %.2f s before this\n",
                  (unsigned long)(writeCount - 1), firstFailMs / 1000.0);
    Serial.println();
  } else if (!degraded) {
    stamp();
    Serial.printf("!! write failed again: reg 0x%02X -> error %u: %s\n", reg, rc, twiError(rc));
  }

  degraded = true;

  if (consecFails == 3) attemptRecovery();
  return false;
}

// ========================================================
// HEALTH CHECK - the headline diagnostic
// ========================================================

void healthCheck() {
  healthChecks++;
  uint8_t v = 0, rc = 0;

  if (!rawRead(REG_IODIRA, v, rc)) {
    healthFails++;
    stamp();
    Serial.printf("## HEALTH: IODIRA READ FAILED (error %u: %s)\n", rc, twiError(rc));
    stamp();
    Serial.println(F("##   => compare this against the WRITE failure rate in the heartbeat."));
    stamp();
    Serial.println(F("##      Reads failing while writes pass = marginal transfers, not a dead chip."));
    return;
  }

  if (v == 0x00) return; // healthy, stay quiet

  if (v == 0xFF) {
    mcpRebootCount++;
    Serial.println();
    stamp();
    Serial.printf("## HEALTH: IODIRA = 0xFF - THE MCP23017 REBOOTED (occurrence #%lu)\n",
                  (unsigned long)mcpRebootCount);
    stamp();
    Serial.println(F("##   => it lost power and came back at factory defaults."));
    stamp();
    Serial.println(F("##   => THIS IS A BROWNOUT. Measure 3.3V at the MCP VCC pin."));
    Serial.println();
    mcpConfigure(); // put it back so we can see if it happens repeatedly
    return;
  }

  stamp();
  Serial.printf("## HEALTH: IODIRA = 0x%02X (expected 0x00) - corrupted register\n", v);
  stamp();
  Serial.println(F("##   => data corruption on the bus. Suspect noise on SDA/SCL."));
  mcpConfigure();
}

void heartbeat() {
  stamp();
  Serial.printf("-- up %.0fs | writes %lu (fail %lu) | health %lu (fail %lu) | mcp reboots %lu | recov %lu/%lu | %s\n",
                millis() / 1000.0,
                (unsigned long)writeCount,   (unsigned long)writeFailCount,
                (unsigned long)healthChecks, (unsigned long)healthFails,
                (unsigned long)mcpRebootCount,
                (unsigned long)recoverySuccess, (unsigned long)recoveryAttempts,
                degraded ? "DEGRADED" : "ok");
}

// ========================================================
// ANIMATION (bit-identical frames to led_test_2.ino)
// ========================================================

uint8_t buildFrame(int idx) {
  uint8_t f = 0;
  for (int k = 0; k < CHASE_LEDS; k++) {
    int pos = ((4 - idx - k) % 5 + 5) % 5;
    f |= (1 << pos);
  }
  return f;
}

int frameCount() {
  if (CHASE_MODE == 2) return 6 - CHASE_LEDS; // 2 LEDs -> 4 frames
  return 5;
}

void allOff() {
  mcpWrite(REG_GPIOA, 0x00);
  mcpWrite(REG_GPIOB, 0x00);
}

void enterPhase(Phase p) {
  phase      = p;
  phaseStart = millis();
  frameIdx   = 0;
  lastStep   = 0;
  allOff();

  stamp();
  switch (phase) {
    case PHASE_LEFT:  Serial.println(F("   [DEMO] LEFT chase (10s)"));  break;
    case PHASE_RIGHT: Serial.println(F("   [DEMO] RIGHT chase (10s)")); break;
    case PHASE_OFF:   Serial.println(F("   [DEMO] ALL OFF (3s)"));      break;
  }
}

// Idle mode still exercises the WRITE path at the same rate, writing 0x00 to
// GPIOA. No pin ever changes state and no LED is driven — so this separates
// "any bus traffic fails" from "driving the outputs fails".
//
// Without this, stopping the animation stops all writes, and the test measures
// nothing but reads. That made the first run of this diagnostic inconclusive.
void serviceIdleWrites() {
  unsigned long now = millis();
  if (lastStep == 0 || now - lastStep >= CHASE_DELAY) {
    lastStep = now;
    mcpWrite(REG_GPIOA, 0x00);
  }
}

void serviceDemo() {
  unsigned long now = millis();

  unsigned long phaseLen = (phase == PHASE_LEFT)  ? LEFT_MS
                         : (phase == PHASE_RIGHT) ? RIGHT_MS
                                                  : OFF_MS;
  if (now - phaseStart >= phaseLen) {
    enterPhase(phase == PHASE_LEFT  ? PHASE_RIGHT
             : phase == PHASE_RIGHT ? PHASE_OFF
                                    : PHASE_LEFT);
    return;
  }

  if (phase == PHASE_OFF) return;

  if (lastStep == 0 || now - lastStep >= CHASE_DELAY) {
    lastStep = now;
    uint8_t f = buildFrame(frameIdx);
    mcpWrite(phase == PHASE_LEFT ? REG_GPIOA : REG_GPIOB, f);
    frameIdx = (frameIdx + 1) % frameCount();
  }
}

// ========================================================
// SERIAL
// ========================================================

void printMenu() {
  Serial.println(F("\n=========================================="));
  Serial.println(F("  MCP23017 / I2C FAULT DIAGNOSTIC"));
  Serial.println(F("=========================================="));
  Serial.println(F("Runs the led_test_2 animation (Chase 2, 2 LEDs, 250 ms)"));
  Serial.println(F("with every I2C write checked and IODIRA read back every 2 s."));
  Serial.println(F("------------------------------------------"));
  Serial.println(F("  a -> ANIMATION on/off. OFF still writes 0x00 to GPIOA every 250 ms,"));
  Serial.println(F("       so the write path is measured either way - but no pin changes"));
  Serial.println(F("       state and no LED is driven. Compare the write failure rate"));
  Serial.println(F("       between the two: same rate = the bus itself; only fails with"));
  Serial.println(F("       the animation on = driving the outputs is the trigger."));
  Serial.println(F("  z -> force a bus recovery now (tests whether recovery works)"));
  Serial.println(F("  ? -> full diagnostic dump"));
  Serial.println(F("  h -> this menu"));
  Serial.println(F("==========================================\n"));
}

void printDump() {
  uint8_t iodira = 0, gpioa = 0, rc = 0;
  bool okA = rawRead(REG_IODIRA, iodira, rc);
  bool okG = rawRead(REG_GPIOA,  gpioa,  rc);

  Serial.println(F("\n---------- DIAGNOSTIC DUMP ----------"));
  Serial.printf("uptime            : %.2f s\n", millis() / 1000.0);
  Serial.printf("last reset reason : %s\n", resetReason());
  Serial.printf("writes            : %lu total, %lu failed, %lu consecutive now\n",
                (unsigned long)writeCount, (unsigned long)writeFailCount,
                (unsigned long)consecFails);
  if (firstFailMs) Serial.printf("FIRST failure at  : %.2f s\n", firstFailMs / 1000.0);
  else             Serial.println(F("FIRST failure at  : none yet"));
  Serial.printf("health checks     : %lu total, %lu failed\n",
                (unsigned long)healthChecks, (unsigned long)healthFails);
  Serial.printf("MCP reboots       : %lu  (IODIRA found at 0xFF => brownout)\n",
                (unsigned long)mcpRebootCount);
  Serial.printf("recoveries        : %lu succeeded / %lu attempted\n",
                (unsigned long)recoverySuccess, (unsigned long)recoveryAttempts);
  if (okA) Serial.printf("IODIRA now        : 0x%02X %s\n", iodira,
                         iodira == 0x00 ? "(healthy)" : iodira == 0xFF ? "(CHIP REBOOTED)" : "(corrupt)");
  else     Serial.println(F("IODIRA now        : READ FAILED"));
  if (okG) { Serial.print(F("GPIOA now         : ")); printBits(gpioa); Serial.println(); }
  else       Serial.println(F("GPIOA now         : READ FAILED"));
  Serial.printf("animation         : %s\n", animating ? "running" : "STOPPED (LEDs off)");
  Serial.println(F("-------------------------------------\n"));
}

void handleLine(char *s) {
  while (*s == ' ') s++;
  if (*s == '\0') return;

  switch (*s) {
    case 'a': case 'A':
      animating = !animating;
      stamp();
      Serial.printf(">> Animation %s\n", animating
                    ? "RESUMED"
                    : "STOPPED - LEDs off, still writing 0x00 to GPIOA every 250 ms");
      if (animating) { enterPhase(PHASE_LEFT); }
      else           { allOff(); lastStep = millis(); }
      break;
    case 'z': case 'Z': attemptRecovery(); break;
    case '?':           printDump();       break;
    case 'h': case 'H': printMenu();       break;
    default:
      Serial.printf("!! Unknown command '%c' - press h for the menu\n", *s);
      break;
  }
}

void serviceSerial() {
  while (Serial.available() > 0) {
    char c = Serial.read();
    if (c == '\n' || c == '\r') {
      if (lineLen > 0) { lineBuf[lineLen] = '\0'; handleLine(lineBuf); lineLen = 0; }
    } else if (lineLen < (int)sizeof(lineBuf) - 1) {
      lineBuf[lineLen++] = c;
    }
  }
}

// ========================================================
// SETUP & LOOP
// ========================================================

void setup() {
  Serial.begin(115200);
  delay(500);

  Serial.println(F("\n\n=========================================="));
  Serial.println(F("  MCP23017 / I2C FAULT DIAGNOSTIC"));
  Serial.println(F("=========================================="));
  Serial.printf("Last reset reason: %s\n", resetReason());
  Serial.println(F("  (if this says BROWNOUT, the 3.3V rail is collapsing"));
  Serial.println(F("   hard enough to drop the ESP32 as well as the MCP)"));
  Serial.println();

  Wire.begin(I2C_SDA, I2C_SCL);
  Wire.setClock(100000); // explicit — do not rely on the core default
  Wire.setTimeOut(50);   // keep loop() responsive if the bus is held low

  // Probe with retries and bus recovery. This sketch NEVER halts, so a wedged
  // bus at boot still gives you a running log instead of a dead board.
  bool up = false;
  for (int attempt = 1; attempt <= 5 && !up; attempt++) {
    stamp();
    Serial.printf("Probing MCP23017 @0x%02X on SDA=%d/SCL=%d ... ", MCP_ADDR, I2C_SDA, I2C_SCL);
    Wire.beginTransmission(MCP_ADDR);
    uint8_t rc = Wire.endTransmission();
    if (rc == 0) {
      Serial.println(F("OK"));
      up = true;
      break;
    }
    Serial.printf("FAIL (error %u: %s)\n", rc, twiError(rc));
    if (attempt < 5) {
      stamp();
      Serial.println(F("   trying bus recovery before the next attempt..."));
      i2cBusRecover();
      delay(200);
    }
  }

  if (!up) {
    Serial.println();
    Serial.println(F("*** MCP23017 did not answer after 5 attempts + bus recovery. ***"));
    Serial.println(F("    NOTE: an ESP32 reset does NOT reset the MCP - it runs down to 1.8V,"));
    Serial.println(F("    so a brief power dip reboots the ESP32 while the MCP keeps whatever"));
    Serial.println(F("    state it had. For a genuine cold start, remove power for 10s."));
    Serial.println(F("    Check: SDA=GPIO4, SCL=GPIO13, 4.7k pull-ups, RESET->3V3, A0/A1/A2->GND."));
    Serial.println(F("    Continuing anyway so you can watch the retries.\n"));
  } else {
    if (!mcpConfigure()) {
      stamp();
      Serial.println(F("!! Chip answered but configuration writes failed."));
    }
  }

  printMenu();
  lastHealth = lastHeartbeat = millis();
  enterPhase(PHASE_LEFT);
}

void loop() {
  serviceSerial();

  if (animating) serviceDemo();
  else           serviceIdleWrites();

  unsigned long now = millis();

  if (now - lastHealth >= HEALTH_MS) {
    lastHealth = now;
    healthCheck();
  }

  if (now - lastHeartbeat >= HEARTBEAT_MS) {
    lastHeartbeat = now;
    heartbeat();
  }
}
