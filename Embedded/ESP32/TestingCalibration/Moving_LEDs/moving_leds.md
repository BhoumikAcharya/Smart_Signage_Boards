# ESP32 5-Channel MOSFET LED Chase Controller

## Project Overview
This project uses an ESP32 DevKit V1 to control five 12V LED strips via IRLZ44N N-channel logic-level MOSFETs. It features a non-blocking, directional "chase" animation that creates the illusion of movement toward the left. The system is controlled dynamically via serial communication.

## Hardware Requirements
* **Microcontroller:** ESP32 DevKit V1
* **Switches:** 5x IRLZ44N N-Channel MOSFETs
* **Resistors:** * 5x 100-ohm resistors (Gate current limiting)
  * 5x 10k-ohm resistors (Gate pull-down)
* **Power:** * 12V DC Power Supply Unit (PSU) for LEDs
  * USB Power (5V) for ESP32
* **Load:** 5x 12V LED Strips

---

## Hardware Architecture

This system utilizes a **Low-Side Switch** topology. The 12V supply is connected directly to the load, and the MOSFETs interrupt the ground path.

### MOSFET Pinout & Wiring (Per Channel)
1. **Gate (Left Pin):** * Connect to the ESP32 GPIO pin via a **100-ohm resistor** in series.
   * Connect a **10k-ohm pull-down resistor** from the Gate to Ground to ensure the MOSFET stays OFF during system boot.
2. **Drain (Middle Pin):** * Connect to the **Negative (-)** terminal of the LED strip.
3. **Source (Right Pin):** * Connect to the **Common Ground**.

### Power & Ground Routing
* **LED Positive:** Connect the Positive (+) terminal of all LED strips directly to the 12V PSU Positive (+) terminal.
* **Common Ground (CRITICAL):** The ESP32 Ground (GND), the 12V PSU Negative (-) terminal, and all 5 MOSFET Source pins **must** be wired together to share the same 0V reference.

---

## Pin Mapping

The LEDs are physically arranged from Left to Right to facilitate the directional chase effect.

| LED Position | ESP32 GPIO Pin | Physical Board Label |
| :--- | :--- | :--- |
| LED 1 (Far Left) | GPIO 18 | D18 |
| LED 2 | GPIO 17 | TX2 |
| LED 3 | GPIO 16 | RX2 |
| LED 4 | GPIO 19 | D19 |
| LED 5 (Far Right) | GPIO 5 | D5 |

---

## Functionality & Logic

### 1. Serial Control Interface
The ESP32 listens to the Serial Monitor (115200 baud) for user commands:
* `ON`: Initiates the leftward chase animation.
* `OFF`: Immediately halts the animation and turns all MOSFETs (and LEDs) OFF.

### 2. Non-Blocking Animation (`millis()`)
To ensure the system remains responsive to the `OFF` command at any exact moment, the code avoids the standard `delay()` function. Instead, it utilizes a non-blocking state machine driven by `millis()`. The ESP32 constantly checks the elapsed time and only updates the LED states when the designated `interval` (120ms) has passed.

### 3. The Chase Algorithm
* The animation lights up a block of 3 adjacent LEDs at a time.
* The "head" (leading edge) of this block starts on the right and decrements its index to move left.
* The two trailing LEDs are calculated using modulo arithmetic (`% numLeds`) to ensure the block wraps seamlessly from the left edge back to the right edge without throwing an array out-of-bounds error.

---

## Source Code

```cpp
/*
 * ESP32 5-Channel MOSFET Chase Controller
 * Physical Layout (Left to Right): D18, D17, D16, D19, D5
 * Function: Left-moving 3-LED chase effect with non-blocking Serial control
 */

// Define the pins in exact physical order from LEFT to RIGHT
const int ledPins[] = {18, 17, 16, 19, 5};
const int numLeds = 5;

// State Variables
bool isAnimating = false;
int currentHead = 4; // Start the "head" of our moving block at the far right

// Timing variables for the non-blocking stopwatch
unsigned long previousMillis = 0;
const long interval = 120; // Speed of the chase in milliseconds (lower = faster)

void setup() {
  Serial.begin(115200);
  
  // Initialize all pins as outputs and ensure they start OFF
  for (int i = 0; i < numLeds; i++) {
    pinMode(ledPins[i], OUTPUT);
    digitalWrite(ledPins[i], LOW);
  }
  
  delay(500);
  Serial.println("\n--- 5-Channel System Boot Complete ---");
  Serial.println("Type 'ON' to start the left-moving chase.");
  Serial.println("Type 'OFF' to stop and turn off all LEDs.");
}

// Helper function to quickly turn everything off
void turnAllOff() {
  for (int i = 0; i < numLeds; i++) {
    digitalWrite(ledPins[i], LOW);
  }
}

void loop() {
  // ==========================================
  // 1. LISTEN FOR COMMANDS (Runs constantly)
  // ==========================================
  if (Serial.available() > 0) {
    String input = Serial.readStringUntil('\n');
    input.trim();
    input.toUpperCase();
    
    if (input == "ON") {
      isAnimating = true;
      Serial.println("Status: Animation Started [<<< LEFTWARD CHASE <<<]");
    } 
    else if (input == "OFF") {
      isAnimating = false;
      turnAllOff();
      Serial.println("Status: Animation Stopped [ALL OFF]");
    }
  }

  // ==========================================
  // 2. RUN THE ANIMATION (Runs when allowed)
  // ==========================================
  if (isAnimating) {
    unsigned long currentMillis = millis();
    
    // Check if enough time has passed to move to the next frame
    if (currentMillis - previousMillis >= interval) {
      // Reset the stopwatch
      previousMillis = currentMillis;
      
      // Wipe the previous frame clean
      turnAllOff();
      
      // Calculate which 3 LEDs make up our moving block
      // 'currentHead' is the leading edge (left-most point of the block)
      // The other two LEDs trail behind it to the right
      int led1 = currentHead;
      int led2 = (currentHead + 1) % numLeds; // % numLeds ensures it safely wraps around
      int led3 = (currentHead + 2) % numLeds;
      
      // Light up the current frame
      digitalWrite(ledPins[led1], HIGH);
      digitalWrite(ledPins[led2], HIGH);
      digitalWrite(ledPins[led3], HIGH);
      
      // Move the head of the block one step to the LEFT for the next frame
      currentHead--;
      
      // If the head moves past the left edge (index 0), reset it to the right edge (index 4)
      if (currentHead < 0) {
        currentHead = numLeds - 1; 
      }
    }
  }
}