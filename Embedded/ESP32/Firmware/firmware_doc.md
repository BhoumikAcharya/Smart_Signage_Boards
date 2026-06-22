# ESP32 Enterprise Signage Controller (Firmware v2.0)

**Architecture:** Dual-Core FreeRTOS, MCP23017 I2C Expander, 4x ACS712  
**Role:** Hardware Edge Controller & Network Node

---

## 1. Architectural Overview

This firmware utilizes FreeRTOS to strictly decouple network operations from high-speed hardware control via a Dual-Core architecture.

* **Core 1 (Application & Networking):** Handles the main `loop()`, Ethernet events, MQTT payload parsing, and executes the high-speed, non-blocking LED animation state machine.
* **Core 0 (Hardware DSP):** Runs a dedicated `SensorTask`. It obsessively handles ADC reads, applies median filtering to ignore EMI noise, and calculates the 4-State discrepancy logic.
* **Inter-Process Communication (IPC):** The cores share data seamlessly using a FreeRTOS `QueueHandle_t` (Mailbox pattern), eliminating race conditions between the network and the sensors.

---

## 2. Hardware Pin Mapping

### Ethernet PHY (LAN8720) - Hardwired
* **GPIO 0, 18, 19, 21, 22, 23, 25, 26, 27** ### I2C Bus (MCP23017 Expander)
* **GPIO 16:** I2C SDA
* **GPIO 17:** I2C SCL

### Analog Sensors (ADC1 Only)
* **GPIO 36:** PSU Voltage Monitor
* **GPIO 39:** Battery Voltage Monitor
* **GPIO 34:** ACS712 #1 (Left Arrows Current)
* **GPIO 35:** ACS712 #2 (Right Arrows Current)
* **GPIO 32:** ACS712 #3 (Always ON Current)

---

## 3. MQTT Topic Structure

The firmware subscribes and publishes to the following topics, uniquely identified by its `ASSIGNED_REGISTER` (e.g., `40001`):

* **RX (Receive):**
  * `metro/signage/register/[ID]/value`: Accepts integer commands for animations.
  * `metro/signage/scan`: Accepts `PING` to reset fail-safe watchdog.

* **TX (Publish):**
  * `metro/signage/register/[ID]/status`: Network status (`ONLINE:<IP>` or `OFFLINE`).
  * `metro/signage/register/[ID]/power`: PSU health (`OK` or `FAIL`).
  * `metro/signage/register/[ID]/current1`: Left Load State (4-State String).
  * `metro/signage/register/[ID]/current2`: Right Load State (4-State String).
  * `metro/signage/register/[ID]/current3`: Always ON Load State (4-State String).
  * `metro/signage/register/[ID]/battery_pct`: Battery percentage (0-100).

---

## 4. Control Logic & Animation Modes

The system controls 10 MOSFETs via the MCP23017. The commands received via MQTT are split into two distinct operational modes using a "Modbus Offset" strategy:

### Mode A: Macro Animations (Payloads `0` to `4`)
Standard commands trigger pre-programmed bitmask animation sequences that update every 300ms using a non-blocking `millis()` state machine.
* `0`: All OFF
* `1`: Left Chase (3-LED sequential movement)
* `2`: Right Chase
* `3`: Both Chase
* `4`: Solid ON (Fail-Safe State)

### Mode B: Raw Bitmask Override (Payloads `10000` to `11023`)
Advanced control mode. The firmware subtracts the `10000` offset, leaving a 10-bit integer (0-1023). It slices this integer into two 5-bit chunks and pushes them directly to Port A (Left) and Port B (Right) of the MCP23017, allowing exact control of individual LED strips.

---

## 5. Hardware DSP & 4-State Discrepancy Logic

Instead of a binary `OK/FAIL`, Core 0 compares the **Intended State** (set by Core 1's animations) against the **Actual Current** (measured by the ACS712s) to produce 4 highly accurate diagnostic strings:

1. **`ON`**: Microcontroller intended for the LED to be ON, and current is successfully flowing.
2. **`OFF`**: Microcontroller intended for the LED to be OFF, and no current is flowing.
3. **`FAIL_OPEN`**: Intended ON, but 0 Amps flowing (Broken wiring, burnt LED, or blown MOSFET).
4. **`FAIL_SHORT`**: Intended OFF, but current is still flowing (Welded MOSFET or short to main power).

### Dynamic Thresholding
To prevent false `FAIL_OPEN` alarms during Mode B (where a user might only turn on 1 LED strip instead of 3), Core 1 mathematically counts the number of active `1`s in the bitmask (`countActiveStrips()`). It dynamically scales the expected amperage threshold so Core 0 knows exactly how much current to expect for that specific frame of animation.

---

## 🚨 HIGHLIGHT: Changes from Previous Version (v1 -> v2)

If you are migrating from the old relay-based architecture, here are the major changes implemented in this version:

1. **Relays Removed, I2C Added:** Dropped physical relays on GPIO 4/13. Integrated the `Adafruit_MCP23X17` library to drive 10 independent solid-state MOSFET channels over I2C (GPIO 16/17).
2. **Non-Blocking Animations:** Added `runAnimationStateMachine()` on Core 1 to create visual "Chase" effects using bitmask arrays without using `delay()`, keeping the network fully responsive.
3. **Modbus Offset Feature:** Added the 10,000+ integer block logic to allow granular, individual control of all 10 MOSFETs alongside the standard macro animations.
4. **Added 3rd Load Tracking:** Integrated `PIN_CURR_ALWY` (GPIO 32) and `current3` MQTT topic to track the "Always ON" LED strips.
5. **4-State Discrepancy Logic:** Completely replaced the old binary `OK/FAIL` current monitoring. The system now cross-references expected software states with physical hardware reality to output `ON`, `OFF`, `FAIL_OPEN`, or `FAIL_SHORT`.
6. **Dynamic Thresholds:** Fixed a bug where turning on a single LED strip triggered a failure. Thresholds now scale dynamically (`dynamicThreshLeft`, `dynamicThreshRight`) based on the exact number of active pins.
7. **Individual Sensor Calibration:** Replaced global `SENSITIVITY` and `ZERO_VOLT` variables with dedicated baseline variables for all three ACS712 sensors to account for manufacturing tolerances.