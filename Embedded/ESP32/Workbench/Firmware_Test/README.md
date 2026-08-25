# ESP32 V3 Signage Controller — Testing Build & System Reference

> **Purpose of this document:** A single, authoritative reference for the V3 smart-signage
> hardware and its firmware/software behaviour. It is written so that **any agent or engineer**
> opening this folder can understand the finalized wiring, the command/telemetry model, and what
> the testing build does — without reading every source file first.
>
> **Status:** Finalized hardware (user-confirmed 2026-07-13). Some software behaviours (failsafe,
> battery, HMI) are intentionally **deferred** and marked as such below. Do not treat deferred
> items as implemented.

---

## 0. System at a Glance

A distributed metro-signage network. Each **ESP32 edge node** drives LED signage (animated
left/right arrows + always-on static panels), monitors its own health (currents, voltages), and
talks over **Ethernet** to a **Raspberry Pi gateway**. The Pi bridges **Modbus TCP** (SCADA/PLC +
touch HMI) to **MQTT** (the ESP32 fleet), for up to 100 nodes.

```
  PLC / SCADA ──Modbus TCP──┐
                            ├──►  Raspberry Pi Gateway  ──MQTT──►  ESP32 nodes (x N)
  Touch HMI  ──Modbus TCP──┘        (Modbus <-> MQTT bridge)         │
                                                                     └─► LED signage + sensors
```

This folder (`Firmware_Test/`) contains the **testing build** — a Serial-driven hardware bring-up
sketch that validates the board **without** needing the Ethernet/MQTT/Modbus stack online. The
networked **production firmware** lives in `../Firmware/`.

---

## 1. Finalized Hardware Connections (V3)

> This section supersedes `PCB/PCB_V3/Connections.md` where they differ.

### 1.1 Power Tree

**Input protection chain (in order):**
```
14.5V Input Terminal Block (+) ──► Fuse Holder ──► 470uF Electrolytic Cap ──► Diode ──► Battery Terminal Block
                                                                                            │
                                                                        System Bus tapped HERE (post-diode)
```
- The **system bus** is taken *after* the diode, at the point where the backup **battery** ties in.
- The diode provides reverse-polarity / isolation between input and battery.

**Buck converters — 4x MP1584 (not 5; no floating spare):**

| Buck | Output | Feeds |
|------|--------|-------|
| MP1584 #1 | 12V | Static LED Zone 1 |
| MP1584 #2 | 12V | Static LED Zone 2 |
| MP1584 #3 | 12V | Left + Right arrow LEDs |
| MP1584 #4 | 5V  | ESP32, LAN8720, MCP23017, 4x ACS712, and AMS1117 input |

**Linear regulator — AMS1117-3.3:**
- Input from the **5V** MP1584.
- 3.3V rail feeds: ESP32 (3V3), LAN8720, MCP23017, and the I2C pull-ups on SDA (GPIO4) / SCL (GPIO13).

**Ground:** single **common GND** net across input, battery, all bucks, AMS1117, ESP32, PHY,
expander, all ACS712, and all MOSFET sources.

### 1.2 ESP32 ↔ LAN8720 (Ethernet PHY, RMII)

| Signal | ESP32 GPIO | Notes |
|--------|-----------|-------|
| MDC | 23 | Management clock |
| MDIO | 18 | Management data |
| CRS_DV | 27 | RMII carrier sense (fixed-function) |
| RXD0 | 25 | RMII (fixed-function) |
| RXD1 | 26 | RMII (fixed-function) |
| TXD0 | 19 | RMII (fixed-function) |
| TXD1 | 22 | RMII (fixed-function) |
| TX_EN | 21 | RMII (fixed-function) |
| nINT / REF_CLK | TX2 = **GPIO17** | **50 MHz reference clock OUT** → `ETH_CLOCK_GPIO17_OUT` |

> **Why GPIO17 matters:** GPIO17 sources the PHY reference clock. The I2C bus was therefore moved
> off 16/17 to **4/13** (see §1.4) to eliminate the clock conflict that existed in older firmware.

### 1.3 Analog Sensors (ADC1 only) — **4x ACS712**

| GPIO | Sensor | Role | Switched? |
|------|--------|------|-----------|
| 36 (VP) | Voltage divider | PSU bus (14.5V → ≤3.0V) | — |
| 39 (VN) | Voltage divider | Battery (Vbatt → ≤3.0V) | — |
| 34 | ACS712 #1 | **LHS** arrow load | Yes (Port A) |
| 35 | ACS712 #2 | **RHS** arrow load | Yes (Port B) |
| 32 | ACS712 #3 | **Static Zone 1** | No (always-on) |
| 33 | ACS712 #4 | **Static Zone 2** | No (always-on) |

### 1.4 I2C Expander — MCP23017 @ `0x20`

| Pin | Connection |
|-----|-----------|
| VCC | 3.3V rail |
| RESET | 3.3V rail |
| A0 / A1 / A2 | GND (→ address `0x20`) |
| **SDA** | **GPIO4** (4.7kΩ pull-up to 3.3V) |
| **SCL** | **GPIO13** (4.7kΩ pull-up to 3.3V) |

### 1.5 MOSFET Outputs — 10x logic-level N-channel

| Group | Gates driven by | Series R | Pull-down |
|-------|-----------------|----------|-----------|
| LHS (5 strips) | MCP23017 **Port A0–A4** | 100Ω | 10kΩ to GND |
| RHS (5 strips) | MCP23017 **Port B0–B4** | 100Ω | 10kΩ to GND |

- All MOSFET **Sources → common GND**; **Drains → LED strip (−) terminals**.
- **Only the LHS and RHS arrow strips are switched.** The two **static zones are permanently
  powered and only current-monitored** (no on/off control).

---

## 2. Software Functionality

### 2.1 Command Model (production firmware)

Commands arrive as a single integer (via MQTT `.../value`, bridged from Modbus):

- **Macro animations `0–4`** (arrow LEDs):
  - `0` All OFF · `1` Left chase · `2` Right chase · `3` Both chase · `4` Solid ON
- **Raw bitmask `10000–11023`**: subtract 10000 → 10-bit value. Low 5 bits → Port A (LHS),
  high 5 bits → Port B (RHS). Enables exact per-strip control of all 10 MOSFETs.

Static zones are not addressed by commands — they are always on.

### 2.2 Telemetry / Diagnostics

Each node reports a **4-state discrepancy** per switched load, computed by comparing the *intended*
state against *measured* current with dynamic, strip-count-scaled thresholds:

| State | Meaning |
|-------|---------|
| `ON` | Intended ON, current flowing |
| `OFF` | Intended OFF, no current |
| `FAIL_OPEN` | Intended ON, but no current (broken wire / burnt LED / open MOSFET) |
| `FAIL_SHORT` | Intended OFF, but current flowing (welded/shorted MOSFET) |

Plus PSU power health (`OK`/`FAIL`) and network status (`ONLINE:<ip>` / `OFFLINE`).

> **Channel count note:** current firmware publishes `current1` (LHS), `current2` (RHS),
> `current3` (Static 1). The finalized hardware adds a **4th ACS712 (Static 2)** → a **`current4`**
> channel and matching Modbus diagnostic slot must be added. Not yet implemented.

### 2.3 Communication

- **MQTT (ESP32 ↔ broker on Pi):**
  - RX: `metro/signage/register/<ID>/value`, `metro/signage/scan` (`PING`)
  - TX (retained): `.../status`, `/power`, `/current1`, `/current2`, `/current3` (+ future `/current4`)
  - Last Will & Testament publishes `OFFLINE` on disconnect.
- **Modbus TCP (Pi ↔ SCADA + HMI):** pymodbus server on port 502, single holding-register bank.

### 2.4 Holding Register Map (Pi gateway)

| Zone | Modbus reg | Address | Purpose |
|------|-----------|---------|---------|
| Active execution | 40001–40100 | 0–99 | Command pushed to each ESP32 |
| HMI manual buffer | 41001–41100 | 1000–1099 | Commands from touch HMI |
| SCADA auto buffer | 42001–42100 | 2000–2099 | Commands from PLC |
| MUX flag | 43001 | 3000 | 0 = SCADA/Auto, 1 = HMI/Manual |
| Diagnostics | 45001–45600 | 5000+ | N regs/node (status, power, LHS, RHS, batt, static…) |

> The per-node diagnostic layout will change when `current4` (Static 2) and the battery
> keep/cut decision are finalized. Treat the current 6-reg/node layout as provisional.

### 2.5 Watchdog Timer (internal hang-recovery) vs. Failsafe (network loss)

Two **separate** mechanisms — do not conflate them:

- **Watchdog** — ESP32 `esp_task_wdt`, **15s timeout, panic reset**, fed from both cores. Detects a
  frozen/deadlocked core and reboots the chip. It does **not** react to network loss.
- **Failsafe (LOCKED) = HOLD LAST COMMAND.** On network loss the node keeps executing the last
  command it received (no forced Solid-ON, no forced OFF). If the last state was `1`, it stays `1`.

> **⚠️ Corrected 2026-08-07 — NVS persistence was never implemented.** This section previously
> claimed the last command is *"persisted to NVS (flash) on change and reloaded+applied on boot, so
> the last state survives a reboot even if the network is still down."* Firmware v3.0.0 contains no
> `Preferences` and no `nvs_*` calls.
>
> **Hold-last-command survives a link outage but NOT a reboot.** A node boots to mode `0` and waits
> for the retained `value` topic. Reboot during a network outage → **the sign comes up dark and
> stays dark**. Bench-acceptable, not site-acceptable. NVS remains an open firmware gap —
> `esp32_contract.md` §6, `pi_agent.md` §12 item 10.

### 2.6 Dual-Core Architecture (production firmware)

- **Core 1:** `loop()` — Ethernet events, MQTT parsing, non-blocking LED animation state machine.
- **Core 0:** `SensorTask` — ADC reads, median filtering, 4-state discrepancy logic.
- **IPC:** FreeRTOS queue (mailbox) shares state between cores without race conditions.

---

## 3. The Testing Build (`Firmware_Test.ino`)

Serial-driven hardware bring-up. **No Ethernet/MQTT/Modbus** — flash it to a bare board and confirm
what works. Uses the **finalized I2C pins (SDA=4, SCL=13)** and the same ACS712 calibration math as
production so calibration validates here.

**Serial menu (115200 baud):**

| Key | Action |
|-----|--------|
| `0`–`4` | Toggle an LHS MOSFET (Port A0–A4) |
| `5`–`9` | Toggle an RHS MOSFET (Port B0–B4) |
| `a` / `x` | All MOSFETs ON / OFF |
| `c` | Run chase animation on **both** sides (5 cycles) |
| `L` | LEFT-side chase for **10 s** (uppercase; RHS untouched) — press any key to stop early |
| `R` | RIGHT-side chase for **10 s** (uppercase; LHS untouched) — press any key to stop early |
| `D` | DEMO loop: LEFT 10 s → RIGHT 10 s → all OFF 3 s, repeating until any key is pressed |
| `r` | Read all current sensors once |
| `s` | Toggle continuous 1 Hz current stream |
| `v` | Read PSU + battery divider voltages once |
| `h` | Reprint the menu |

> **Uppercase `L`/`R`/`D`:** the directional/demo animation keys are uppercase so `R` (right chase)
> does not collide with lowercase `r` (read sensors). The 10 s chases and the demo loop are
> **blocking** while they run and **abortable** — press any key to stop and restore the prior state.

> **Calibration placeholders:** ACS712 `SENS/ZERO` and the voltage-divider ratios in the sketch are
> starting values. Verify on the bench with a multimeter and update them before trusting readings.
>
> ⚠️ **The test sketch's `SENS_*` defaults are `0.146`, which matches no standard ACS712 variant.**
> Production v3.0.0 has moved to the real datasheet figures — `0.185` (5 A part) on the arrows and
> `0.100` (20 A part) on the static zones. Until the sketch is brought in line, its current readings
> and production's will disagree on identical hardware. See `esp32_contract.md` §1.
>
> ~~**Not yet in the test sketch:** the 4th ACS712 (GPIO33 / Static 2).~~ ✅ **Present as of
> 2026-08-07** — `PIN_CURR_STA2 = 33` is declared and read alongside the other three. This note was
> stale.

---

## 4. Decision Log

### Locked (implement in production firmware)
1. **Failsafe = HOLD LAST COMMAND** (see §2.5). No forced ON/OFF. ⚠️ The "persisted to NVS" half of
   this decision is **not implemented** in v3.0.0 and is still open — see the correction in §2.5.
2. **4th current channel (`current4`, Static 2)** — add across firmware + MQTT + Pi + register map.
   All 4 currents use the 4-state string; static channels are ON/FAIL_OPEN only.
3. **Modbus-offset bitmask (10000+)** stays; HMI will later expose it as a separate advanced panel.
4. **Watchdog** unchanged (internal hang-recovery, 15s panic reboot).

### Still open / deferred
1. **Battery monitoring** — **kept STUBBED**: reserve the register slot, leave unimplemented,
   revisit later. (Not removed, not active.)
2. **HMI rework** — still on the old 2-relay model; needs modes `0–4` + a separate bitmask panel.
   Deferred to the HMI phase.
3. **ACS712 calibration** — `SENS = 0.146` does not match a standard ACS712 variant. Confirm the
   sensor model (5A/20A/30A) and recalibrate on the bench.

---

## 5. File Map

| Path | What it is |
|------|-----------|
| `Firmware_Test/Firmware_Test.ino` | **This** testing/bring-up sketch |
| `Firmware_Test/README.md` | This document |
| `../Firmware/Firmware.ino` | Production networked firmware (dual-core, MQTT/Modbus) |
| `../Firmware/firmware_doc.md` | Production firmware doc (predates V3 hardware — verify against §1) |
| `../../RaspberryPi/Gateway/Pi.py` | Modbus↔MQTT bridge gateway |
| `../../RaspberryPi/HMI/HMI.py` | Touch HMI client (Tkinter) |
| `PCB/PCB_V3/Connections.md` | Older wiring doc — **superseded by §1 here** |
