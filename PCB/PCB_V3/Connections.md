# PCB V3 — Connections

**Doc last reconciled against code:** 2026-08-07.

> **The finalized pin map is [`Embedded/ESP32/esp32_contract.md`](../../Embedded/ESP32/esp32_contract.md) §1**, confirmed 2026-07-13. It supersedes this file wherever the two disagree. This document is the wiring / build sheet.
>
> **Two items here are still unresolved and marked 🔴 below** — the ACS712 part numbers and the physical arrow strip order. Both must be settled on the bench; see §7.

### 1. Power Tree Connections
* **Main Input:** 14.5V DC entering via terminal block. Connect the positive terminal to the anode of a reverse-polarity protection/isolation diode.
* **Battery:** Connect the backup battery positive terminal in parallel to the cathode of the isolation diode (the 14.5V system bus).
* **Buck Converters (4x MP1584):**
    * **IN (All 4 units):** Connect to the 14.5V system bus.
    * **Buck 1 OUT (12V):** Connect to the positive terminals for the LHS and RHS LEDs.
    * **Buck 2 OUT (12V):** Connect to the positive terminals for the Static LEDs (**Static Zone 1**).
    * **Buck 3 OUT (12V):** Connect to the positive terminals for the Static LEDs (**Static Zone 2**).
    * **Buck 4 OUT (5.0V):** Connect to the VCC pins of the 4x ACS712 sensors and the IN pin of the AMS1117-3.3 linear regulator.
* **Linear Regulator (AMS1117-3.3):**
    * **OUT (3.3V Rail):** Connect to the ESP32 (3V3 pin), LAN8720 VCC, MCP23017 VCC, and the I2C Pull-up resistors.

> **Corrected 2026-08-07 — the count is 4x MP1584, not 5x.** The earlier revision listed five units
> with "Buck 4 OUT (12V): Float/No Connection (Expansion buffer)". The finalized hardware has **no
> floating spare**; the 5.0V unit is now Buck 4.
>
> **All four bucks tap the system bus post-diode**, on the battery side. This is deliberate and
> load-bearing: during a PSU failure the battery therefore carries the **whole** load including the
> three LED rails, so the LEDs stay lit, the current sensors keep reading normally, and there is no
> false `FAIL_OPEN` cascade while the node reports `power=FAIL`. It also means battery runtime is set
> by the full LED load, not just the logic. See `esp32_contract.md` §6a.

### 2. Common Ground Net
* **GND:** Tie the ground of the 14.5V input, the battery, all 4x MP1584 converters, the AMS1117, the ESP32, the LAN8720, the MCP23017, all ACS712 sensors, and all MOSFET Sources to a single continuous Common GND net.

### 3. ESP32 & Ethernet Connections
* **Power:** 3V3 pin to 3.3V Rail. GND to Common GND.
* **Ethernet PHY (LAN8720):**
    * GPIO 17 ➔ ETH_CLK
    * GPIO 18 ➔ ETH_MDIO
    * GPIO 23 ➔ ETH_MDC
    * GPIO 0, 19, 21, 22, 25, 26, 27 ➔ Ethernet MAC Pins

> **⚠️ GPIO 17 → ETH_CLK is correct and must stay.** It is `ETH_CLOCK_GPIO17_OUT`, the 50 MHz RMII
> reference clock for the LAN8720. This is precisely *why* the I2C bus was moved off 16/17 to 4/13
> (§5) — the two were fighting for the pin. Do not "fix" this line by analogy with that move.

### 4. Sensor Connections (ADC1)
* **Power Supply Monitor (GPIO 36 / VP):** Connect to the center point of a voltage divider stepping down the 14.5V system bus to a maximum of 3.0V.
* **Battery Monitor (GPIO 39 / VN):** Connect to the center point of a voltage divider stepping down the Battery positive terminal to a maximum of 3.0V.
* **Current Sensors (4x ACS712):**

| GPIO | Sensor | Load | Switched? | Part 🔴 |
| --- | --- | --- | --- | --- |
| 34 | ACS712 #1 | **LHS arrows** | Yes — MCP Port A | 5A assumed |
| 35 | ACS712 #2 | **RHS arrows** | Yes — MCP Port B | 5A assumed |
| 32 | ACS712 #3 | **Static Zone 1** | **No — hardwired always-on** | 20A assumed |
| 33 | ACS712 #4 | **Static Zone 2** | **No — hardwired always-on** | 20A assumed |

> **Corrected 2026-08-07 — the 4th sensor was missing from this document.** Only three were listed;
> GPIO 33 (Static Zone 2) was absent, and the third was labelled "Static Load" singular. Firmware
> v3.0.0 reads all four and publishes `current1`–`current4`.
>
> **Nomenclature is now fixed project-wide:** Load 3 → **Static Zone 1**, Load 4 → **Static Zone 2**.
> This closes `pi_agent.md` §12 item 4, which names this file as its owner.
>
> 🔴 **The "Part" column is an assumption, not a record.** See §7.1.

### 5. I2C Expansion Connections (MCP23017)
* **VCC:** Connect to 3.3V Rail.
* **A0, A1, A2:** Connect all three directly to Common GND.
* **RESET:** Connect directly to 3.3V Rail.
* **SDA:** Connect to ESP32 GPIO 4. Connect a 4.7kΩ pull-up resistor from this net to the 3.3V Rail.
* **SCL:** Connect to ESP32 GPIO 13. Connect a 4.7kΩ pull-up resistor from this net to the 3.3V Rail.

> ✅ **These pins are correct** (verified 2026-08-07 against `Firmware.ino` v3.0.0 `I2C_SDA 4` /
> `I2C_SCL 13`). Earlier builds used GPIO 16/17 — see the warning in §3.

* **A5–A7 and B5–B7: leave UNCONNECTED.** These six pins must stay floating.

> **This is a firmware dependency, not spare capacity.** v3.0.0 drives a fixed health pattern
> (`0xA0`) into those unused bits on every MOSFET write and reads it back to prove the I2C bus is
> alive. Without it a dead bus reads `0x00`, which exactly matches a legitimate "all MOSFETs off"
> write — so mode 0 could never be verified and a node with a severed bus would report perfect
> health. **Wiring anything to these pins breaks the readback comparison and the node will report a
> permanent, un-clearable hardware fault.** See `esp32_contract.md` §1.

### 6. MOSFET Output Connections (10x Logic-Level N-Channel)
* **LHS LEDs (5x MOSFETs):**
    * **Gate:** Connect to MCP23017 pins A0 through A4. Insert a 100Ω series resistor between the MCP23017 pin and the Gate. Connect a 10kΩ pull-down resistor from the Gate to Common GND.
* **RHS LEDs (5x MOSFETs):**
    * **Gate:** Connect to MCP23017 pins B0 through B4. Insert a 100Ω series resistor between the MCP23017 pin and the Gate. Connect a 10kΩ pull-down resistor from the Gate to Common GND.
* **Sources (All 10x MOSFETs):** Connect directly to Common GND.
* **Drains (All 10x MOSFETs):** Connect to the respective Negative (-) output terminals for the LED strips.

> **Only the arrows are switched.** The two static zones have **no MOSFET at all** — they are wired
> directly to their buck rails and are permanently on. They are current-monitored (§4) but can never
> be commanded on or off, which is also why they cannot be zero-calibrated in place.

---

## 7. 🔴 UNRESOLVED — must be settled on the bench

Both items below are recorded here because this file is their owner. Neither can be
answered from the repository; both need the physical boards. Tracked as B1 and B2 in
`Embedded/ESP32/DOC_UPDATES_PENDING.md` §0.

### 7.1 — 🔴 ACS712 part numbers are not recorded anywhere

The "Part" column in §4 states what the **firmware assumes**, not what is fitted. Read
the markings off the actual parts and record them here.

It matters because it sets whether a **single strip failing** is detectable. At the
per-strip threshold (0.060 A):

| Part | Sensitivity | Signal at 0.060 A | ADC counts |
| --- | --- | --- | --- |
| ACS712-20A | 0.100 V/A | 6.0 mV | ~7 — marginal against ADC noise |
| ACS712-05A | 0.185 V/A | 11.1 mV | ~14 — roughly double the margin |

A whole side failing is detectable either way (~22 counts even on the 20A part). The
single-strip case is the one at risk — hence **5A on the arrows, 20A on the static
zones**, which is what `Firmware.ino` v3.0.0 defaults to. If the fitted parts differ,
the firmware constants must be flipped to match.

### 7.2 — 🔴 Physical arrow strip order is undocumented

**Which physical arrow position each of A0–A4 and B0–B4 actually drives is recorded
nowhere in this repository.**

This is the single missing fact that makes the chase direction unanswerable from the
repo alone: the production chase frame table (`{0x07, 0x0E, 0x1C, 0x19, 0x13}`) and the
`Moving_LEDs` reference implementation disagree about which way the arrow sweeps, and
without the strip order there is no way to tell which is right on paper.

**Record it here as a table** — MCP pin → physical position, for both ports — once the
boards are in front of someone. Then verify the sweep direction against intent and
record the outcome in `esp32_contract.md` §2, which currently lists the frame values
with no statement of intended direction at all.