/*
 * ESP32 Enterprise Signage Controller — LED CHASE TEST BUILD
 * ---------------------------------------------------------
 * Purpose: Bench-tune the arrow chase animation. This sketch boots straight
 *          into the "D" demo loop from Firmware_Test.ino and runs it forever:
 *
 *              LEFT chase 10 s  ->  RIGHT chase 10 s  ->  ALL OFF 3 s  -> repeat
 *
 * Differences vs Firmware_Test.ino:
 *   - Chase direction is REVERSED: A4->A0 / B4->B0 (was A0->A4 / B0->B4).
 *   - The animation is generated at runtime instead of using fixed frames, so
 *     the number of lit LEDs in the arrow is adjustable (1-5, default 3).
 *   - Three chase modes: (1) wrapping loop, (2) reset at the end of the strip,
 *     (3) spaced trail with two dots 2 channels apart, reset at the end.
 *   - The step delay is adjustable in ms.
 *   - The demo is NON-BLOCKING, so settings can be changed live over Serial
 *     without stopping the animation.
 *
 * BOOT DEFAULT: CHASE 3 at 200 ms. That is the chosen signage animation —
 * power the board on and it runs with no Serial input at all. Modes 1 and 2 and
 * the other adjustments remain available over Serial for comparison.
 *
 * Sensors / voltage dividers are intentionally NOT included here — use
 * Firmware_Test.ino for those. This build is only about the LEDs.
 */

#include <Wire.h>
#include <Adafruit_MCP23X17.h>

// --- FINALIZED I2C PINS (per PCB_V3/Connections.md) ---
#define I2C_SDA 4
#define I2C_SCL 13
#define MCP_ADDR 0x20
Adafruit_MCP23X17 mcp;

// --- DEMO PHASE TIMING (the "D" configuration) ---
const unsigned long LEFT_MS  = 10000;
const unsigned long RIGHT_MS = 10000;
const unsigned long OFF_MS   = 3000;

// --- DEFAULTS ON POWER-ON ---
// The board boots into CHASE 3 at 200 ms — that is the animation the signage
// actually runs. Modes 1 and 2 stay available over Serial for comparison.
const int           DEFAULT_LEDS  = 3;   // arrow width for modes 1/2 (mode 3 caps at 2 dots)
const int           DEFAULT_MODE  = 3;   // spaced trail, reset at end
const unsigned long DEFAULT_DELAY = 200; // ms per animation step

// --- LIVE SETTINGS (changed over Serial) ---
int           chaseLeds  = DEFAULT_LEDS;
int           chaseMode  = DEFAULT_MODE;
unsigned long chaseDelay = DEFAULT_DELAY;
bool          paused     = false;

// --- DEMO STATE MACHINE ---
enum Phase { PHASE_LEFT, PHASE_RIGHT, PHASE_OFF };
Phase         phase      = PHASE_LEFT;
unsigned long phaseStart = 0;
unsigned long lastStep   = 0;
int           frameIdx   = 0;

// --- SERIAL LINE BUFFER ---
char lineBuf[24];
int  lineLen = 0;

// ========================================================
// ANIMATION
// ========================================================

/*
 * Frame generator. Each strip has 5 channels (0-4). The arrow HEAD walks down
 * from channel 4 towards channel 0, and the arrow body trails behind it, so
 * with chaseLeds = 3 the sequence is:
 *
 *   idx 0 -> A4,A3,A2
 *   idx 1 -> A3,A2,A1
 *   idx 2 -> A2,A1,A0
 *   idx 3 -> A1,A0,A4   <- wrap (mode 1 only)
 *   idx 4 -> A0,A4,A3   <- wrap (mode 1 only)
 *
 * Mode 2 stops before the wrapping frames and restarts at idx 0, so the arrow
 * always reads cleanly as "4 -> 0" with a clean reset.
 *
 * Mode 3 is a SPACED trail: the LEDs sit 2 channels apart instead of touching,
 * and a trailing LED is simply not lit until it lands on the strip. The head
 * still walks 4 -> 0 and there is no wrap:
 *
 *   idx 0 -> A4
 *   idx 1 -> A3
 *   idx 2 -> A2,A4
 *   idx 3 -> A1,A3
 *   idx 4 -> A0,A2      then restart at idx 0
 *
 * Mode 3 is capped at TWO dots. A third dot would land on A4 in the last frame
 * (A0,A2,A4), re-lighting the far end of the strip just as the arrow finishes,
 * which reads as a flash rather than as motion. An LED count of 1 still gives a
 * single dot; 2 and above all give the pair.
 */
uint8_t buildFrame(int idx) {
  uint8_t f = 0;

  if (chaseMode == 3) {
    int head = 4 - idx;                       // 4,3,2,1,0
    int dots = (chaseLeds > 2) ? 2 : chaseLeds;
    for (int k = 0; k < dots; k++) {
      int pos = head + 2 * k;                 // trail sits 2 channels behind
      if (pos <= 4) f |= (1 << pos);          // off the strip = not lit (no wrap)
    }
    return f;
  }

  for (int k = 0; k < chaseLeds; k++) {
    int pos = ((4 - idx - k) % 5 + 5) % 5;
    f |= (1 << pos);
  }
  return f;
}

// How many frames before the sequence repeats.
//   mode 1 = all 5 head positions (arrow wraps around the strip)
//   mode 2 = only the frames that fit inside the strip, then reset
//   mode 3 = all 5 head positions, trail clipped at the edge, then reset
int frameCount() {
  if (chaseMode == 2) return 6 - chaseLeds; // 3 LEDs -> 3 frames, 1 LED -> 5
  return 5;
}

// Restart the animation from the top. Called after any setting change so the
// new geometry is visible immediately instead of mid-sweep.
void restartAnimation() {
  frameIdx = 0;
  lastStep = 0; // forces a step on the next loop() pass
}

void allOff() {
  mcp.writeGPIOA(0x00);
  mcp.writeGPIOB(0x00);
}

void enterPhase(Phase p) {
  phase      = p;
  phaseStart = millis();
  restartAnimation();
  allOff(); // guarantee the previous phase's side goes dark

  switch (phase) {
    case PHASE_LEFT:  Serial.println(F("   [DEMO] LEFT chase (10s)"));  break;
    case PHASE_RIGHT: Serial.println(F("   [DEMO] RIGHT chase (10s)")); break;
    case PHASE_OFF:   Serial.println(F("   [DEMO] ALL OFF (3s)"));      break;
  }
}

void serviceDemo() {
  unsigned long now = millis();

  // --- phase advance ---
  unsigned long phaseLen = (phase == PHASE_LEFT)  ? LEFT_MS
                         : (phase == PHASE_RIGHT) ? RIGHT_MS
                                                  : OFF_MS;
  if (now - phaseStart >= phaseLen) {
    enterPhase(phase == PHASE_LEFT  ? PHASE_RIGHT
             : phase == PHASE_RIGHT ? PHASE_OFF
                                    : PHASE_LEFT);
    return;
  }

  if (phase == PHASE_OFF) return; // nothing to animate

  // --- animation step ---
  if (lastStep == 0 || now - lastStep >= chaseDelay) {
    lastStep = now;
    uint8_t f = buildFrame(frameIdx);
    if (phase == PHASE_LEFT) mcp.writeGPIOA(f);
    else                     mcp.writeGPIOB(f);
    frameIdx = (frameIdx + 1) % frameCount();
  }
}

// ========================================================
// SERIAL INTERFACE
// ========================================================

void printBits(uint8_t v) {
  for (int b = 4; b >= 0; b--) Serial.print((v >> b) & 1);
}

const char *modeName(int m) {
  switch (m) {
    case 2:  return "reset at end";
    case 3:  return "spaced trail, reset at end";
    default: return "wrapping loop";
  }
}

void printStatus() {
  Serial.printf(">> LEDs:%d  Mode:%d (%s)  Delay:%lu ms  Frames:%d  %s\n",
                chaseLeds,
                chaseMode,
                modeName(chaseMode),
                chaseDelay,
                frameCount(),
                paused ? "PAUSED" : "running");
  Serial.print(F("   frames: "));
  for (int i = 0; i < frameCount(); i++) {
    Serial.print(F("[")); printBits(buildFrame(i)); Serial.print(F("] "));
  }
  Serial.println();
  Serial.println(F("   (bit order shown is 4..0, i.e. A4 A3 A2 A1 A0)"));
  if (chaseMode == 3 && chaseLeds > 2) {
    Serial.printf("   (Chase 3 is a 2-dot pattern — the LED count of %d is capped at 2)\n", chaseLeds);
  }
}

void printMenu() {
  Serial.println(F("\n=========================================="));
  Serial.println(F("  ESP32 V3 LED CHASE TEST"));
  Serial.println(F("=========================================="));
  Serial.println(F("Runs the DEMO loop automatically on power-on:"));
  Serial.println(F("  LEFT chase 10s -> RIGHT chase 10s -> OFF 3s (repeat)"));
  Serial.println(F("Chase direction: A4->A0 and B4->B0"));
  Serial.println(F("Boot default:    CHASE 3 @ 200 ms"));
  Serial.println(F("------------------------------------------"));
  Serial.println(F("Commands (type, then press Enter):"));
  Serial.println(F("  <1-5>     -> set number of LEDs in the arrow (default 3)"));
  Serial.println(F("  n <1-5>   -> same as above, explicit form"));
  Serial.println(F("  c 1       -> CHASE 1: arrow wraps around (A1,A0,A4 ...)"));
  Serial.println(F("  c 2       -> CHASE 2: arrow resets after A2,A1,A0"));
  Serial.println(F("  c 3       -> CHASE 3: spaced trail, 2 dots 2 channels apart (default)"));
  Serial.println(F("  d <ms>    -> set step delay in milliseconds (default 200)"));
  Serial.println(F("  p         -> pause / resume the demo"));
  Serial.println(F("  ?         -> show current settings + frame table"));
  Serial.println(F("  h         -> show this menu"));
  Serial.println(F("==========================================\n"));
}

void setLeds(int n) {
  if (n < 1 || n > 5) {
    Serial.println(F("!! LED count must be 1-5"));
    return;
  }
  chaseLeds = n;
  restartAnimation();
  Serial.printf(">> LED count set to %d (%d frames)\n", chaseLeds, frameCount());
  if (chaseMode == 3 && chaseLeds > 2) {
    Serial.println(F("   (Chase 3 is a 2-dot pattern — capped at 2, switch to mode 1 or 2 for wider)"));
  }
}

void setMode(int m) {
  if (m < 1 || m > 3) {
    Serial.println(F("!! Chase mode must be 1, 2 or 3"));
    return;
  }
  chaseMode = m;
  restartAnimation();
  printStatus(); // the frame table is the quickest way to see what changed
}

void setDelay(long ms) {
  if (ms < 5 || ms > 5000) {
    Serial.println(F("!! Delay must be 5-5000 ms"));
    return;
  }
  chaseDelay = (unsigned long)ms;
  Serial.printf(">> Step delay set to %lu ms\n", chaseDelay);
}

void handleLine(char *s) {
  while (*s == ' ') s++;
  if (*s == '\0') return;

  char cmd = *s;
  char *arg = s + 1;
  while (*arg == ' ') arg++;

  // Bare number = LED count shortcut ("type 2, get a 2-LED arrow")
  if (cmd >= '0' && cmd <= '9') { setLeds(atoi(s)); return; }

  switch (cmd) {
    case 'n': case 'N': setLeds(atoi(arg));  break;
    case 'c': case 'C': setMode(atoi(arg));  break;
    case 'd': case 'D': setDelay(atol(arg)); break;
    case 'p': case 'P':
      paused = !paused;
      if (paused) allOff();
      else        enterPhase(PHASE_LEFT);
      Serial.printf(">> Demo %s\n", paused ? "PAUSED (LEDs off)" : "RESUMED from LEFT phase");
      break;
    case '?':           printStatus(); break;
    case 'h': case 'H': printMenu();   break;
    default:
      Serial.printf("!! Unknown command '%c' — press h for the menu\n", cmd);
      break;
  }
}

void serviceSerial() {
  while (Serial.available() > 0) {
    char c = Serial.read();
    if (c == '\n' || c == '\r') {
      if (lineLen > 0) {
        lineBuf[lineLen] = '\0';
        handleLine(lineBuf);
        lineLen = 0;
      }
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

  Wire.begin(I2C_SDA, I2C_SCL);
  Serial.print(F("\nProbing MCP23017 @0x20 on SDA=4/SCL=13 ... "));
  if (!mcp.begin_I2C(MCP_ADDR, &Wire)) {
    Serial.println(F("NOT FOUND!"));
    Serial.println(F("Check: SDA=GPIO4, SCL=GPIO13, 4.7k pull-ups, RESET->3V3, A0/A1/A2->GND."));
    while (1) delay(1000);
  }
  Serial.println(F("OK"));

  for (int i = 0; i < 16; i++) mcp.pinMode(i, OUTPUT);
  allOff();

  printMenu();
  printStatus();

  enterPhase(PHASE_LEFT); // demo starts immediately, no key needed
}

void loop() {
  serviceSerial();
  if (!paused) serviceDemo();
}
