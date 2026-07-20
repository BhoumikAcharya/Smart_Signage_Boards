# ESP32 Firmware Contract — as required by Gateway v2.0.0

**Written:** 2026-07-20, immediately after `Pi.py` v2.0.0 was finalized.
**Purpose:** the authoritative reference for designing the production firmware. Everything here is fixed by the gateway that already exists — the firmware must conform to it, not the other way round.
**Companion docs:** `RaspberryPi/pi_agent.md` (platform contract), `RaspberryPi/Gateway/Gateway_doc.md`, `PCB/PCB_V3/Connections.md` (superseded in part — see §1).

---

## 1. Finalized Hardware Pin Map

Confirmed 2026-07-13; supersedes `PCB_V3/Connections.md` where they disagree. Validated in `Firmware_Test/Firmware_Test.ino`.

| Function | GPIO | Notes |
| --- | --- | --- |
| I2C SDA | **4** | Was 16. 4.7k pull-up to 3V3 |
| I2C SCL | **13** | Was 17. 4.7k pull-up to 3V3 |
| ACS712 #1 — LHS arrows | 34 | switched load |
| ACS712 #2 — RHS arrows | 35 | switched load |
| ACS712 #3 — Static Zone 1 | 32 | **always-on** load |
| ACS712 #4 — Static Zone 2 | 33 | **always-on** load |
| PSU voltage divider | 36 | |
| Battery voltage divider | 39 | stubbed, see §7 |

> **The I2C move is not optional.** GPIO17 is `ETH_CLOCK_GPIO17_OUT`, the 50 MHz RMII reference clock for the LAN8720. The shipped `Firmware/Firmware.ino` still has `I2C_SCL 17` and therefore has I2C fighting the Ethernet clock. This is the single highest-priority fix.

**LAN8720 RMII:** MDC=23, MDIO=18, CRS_DV=27, RXD0=25, RXD1=26, TXD0=19, TXD1=22, TX_EN=21, REFCLK=GPIO17 (`ETH_CLOCK_GPIO17_OUT`), PHY_ADDR=1, PHY_POWER=-1.

**MCP23017** @ `0x20` (A0/A1/A2→GND, RESET→3V3). Port **A0–A4 = LHS** 5 MOSFETs, Port **B0–B4 = RHS** 5 MOSFETs. 100R series, 10k pull-down. All 16 pins set OUTPUT; A5–A7/B5–B7 unused.

**ADC:** 12-bit, `ADC_11db`, ADC1 only (ADC2 is unusable while WiFi/ETH is active). Median-of-51 filter, then IIR low-pass `ALPHA = 0.15`.

---

## 2. Command Decode — the contract the gateway publishes against

Subscribe: `metro/signage/register/<40001+i>/value`, **QoS 1**. Payload is a decimal integer as ASCII.

> **`<address>` is the holding-register number (`40001 + i`), NOT the node index.** Node 1 is `register/40001/value`. Get this wrong and every node reads OFFLINE with no error logged anywhere. It silently does not work.

**Commands control the moving arrows ONLY.** Static Zones 1 and 2 are hardwired always-on — current-monitored, never switched. No command value affects them.

| Value | Action | Port A (LHS) | Port B (RHS) | Access |
| --- | --- | --- | --- | --- |
| `0` | Arrows OFF | `0x00` | `0x00` | Operator |
| `1` | LHS chase | chase frame | `0x00` | Operator |
| `2` | RHS chase | `0x00` | chase frame | Operator |
| `3` | Both chase | chase frame | chase frame | PIN |
| `4` | Solid ON left | `0x1F` | `0x00` | PIN |
| `5` | Solid ON right | `0x00` | `0x1F` | PIN |
| `6` | Solid ON all | `0x1F` | `0x1F` | PIN |
| `10000`–`11023` | Raw bitmask | `mask & 0x1F` | `(mask >> 5) & 0x1F` | PIN |

`mask = value - 10000`, 10 bits, low 5 = Port A, high 5 = Port B.

> **⚠️ MODE 4 CHANGED MEANING.** In the shipped `Firmware.ino`, case 4 = solid ON *all ten* MOSFETs. That is now mode **6**. Mode 4 is left-side only. Clear retained `value` topics on the broker before flashing, or a stale retained `4` will light the wrong zone.

**The PIN is enforced in the HMI, not here.** Firmware executes any valid value it receives. Do not add mode gating to the firmware.

**Chase frames:** `{0x07, 0x0E, 0x1C, 0x19, 0x13}`, 5 frames, 300 ms/frame, each frame lights exactly 3 of 5 strips. Must be driven by a non-blocking `millis()` state machine — never `delay()`.

**Technician modes latch.** No auto-revert timer.

---

## 3. Telemetry — what the gateway subscribes to

Publish to `metro/signage/register/<40001+i>/<metric>`, all with **`retain=True`**.

| Metric | Payload | Notes |
| --- | --- | --- |
| `status` | `ONLINE:<ip>` or `OFFLINE` | Gateway splits on the first `:` to extract the IP |
| `power` | `OK` \| `FAIL` | PSU divider on GPIO36 |
| `current1` | 4-state string | LHS arrows |
| `current2` | 4-state string | RHS arrows |
| `current3` | 4-state string | Static Zone 1 |
| `current4` | 4-state string | **Static Zone 2 — NEW, not in shipped firmware** |
| `battery_pct` | integer `0`–`100` | stubbed, see §7 |
| `state` | integer | **The command this node is ACTUALLY executing.** See below. |

### `state` — the acknowledgment topic

Everything else in the system shows what a node was *commanded*. This topic is the only thing that reports what it is *doing*. Publish it:

* whenever the active command changes (including a value restored from NVS on boot),
* in response to a PING,
* **never** optimistically — publish only after the MOSFET write has actually happened.

Payload is the active command integer (`0`–`6`, or `10000`–`11023`), as ASCII. If firmware **rejects** a command as out of range, it must keep publishing the *old* value — that is precisely the divergence the gateway needs to see. v1.1.1 logged `[ERROR] Malformed Payload` and silently kept running; the gateway never learned. Do not repeat that.

The gateway exposes this at diagnostic register `+7` and flags `commanded != actual` on the dashboard.

**4-state strings, exactly:** `ON`, `OFF`, `FAIL_OPEN`, `FAIL_SHORT`. The gateway maps these to `1/0/2/3`; anything else becomes `99`. Spelling is load-bearing.

```
                 measured >= threshold    measured < threshold
  intended ON          ON                     FAIL_OPEN
  intended OFF         FAIL_SHORT             OFF
```

**Last Will and Testament:** connect with LWT on `.../status`, payload `OFFLINE`, **QoS 1, retained**. This is what makes the gateway's offline-scrubbing work when a node drops without a clean disconnect.

**Publish on change, not on a timer.** The gateway's UI redraws once a second regardless; needless retained republishes just churn the broker.

---

## 4. Static Zones — the asymmetry that matters

Static zones are **always intended ON**. Therefore:

* Their only legal states are `ON` and `FAIL_OPEN`. They can never legitimately report `OFF` or `FAIL_SHORT`.
* Their threshold is **static** (`THRESHOLD_ALWY`-style), not scaled by mode, because their load never changes.
* If a static zone reports `OFF`, the fault is in the sensing chain or the calibration — not the load.

---

## 5. Dynamic Thresholds — must be extended for modes 4/5/6

The discrepancy logic scales the expected current by how many strips should be lit. Chase frames light 3 of 5; solid lights 5 of 5. Existing code uses `THRESH_PER_STRIP * (n - 0.5)`.

| Mode | Left expected | Left threshold | Right expected | Right threshold |
| --- | --- | --- | --- | --- |
| `0` | OFF | — | OFF | — |
| `1` | ON (3) | `× 2.5` | OFF | — |
| `2` | OFF | — | ON (3) | `× 2.5` |
| `3` | ON (3) | `× 2.5` | ON (3) | `× 2.5` |
| `4` | ON (5) | `× 4.5` | **OFF** | — |
| `5` | **OFF** | — | ON (5) | `× 4.5` |
| `6` | ON (5) | `× 4.5` | ON (5) | `× 4.5` |
| bitmask | `popcount(portA)` | `× (n - 0.5)` | `popcount(portB)` | `× (n - 0.5)` |

> Modes 4 and 5 are the trap: one side goes solid while **the other must be expected OFF**. Carrying a stale threshold from a previous mode makes a healthy board report `FAIL_OPEN`. The shipped firmware never clears `dynamicThreshLeft/Right` when a side turns off.

---

## 6. Network Failsafe — HOLD LAST COMMAND

**There is no dead-man switch and no fail-safe timer.** Three project docs previously claimed the 60 s broadcast PING resets one; it does not exist.

* On network loss the node **holds its current command** and keeps animating.
* The active command is **persisted to NVS on every change** and re-applied on boot, so it survives a reboot with the network still down.
* No forced Solid-ON, no forced OFF.
* `esp_task_wdt` (15 s, panic reboot) is **internal hang recovery only**. It is not a network watchdog.

**PING** (`metro/signage/scan`, payload `PING`, QoS 0, every 60 s from the gateway): respond by re-publishing the full telemetry set. That is its entire purpose — it lets the gateway rebuild state without waiting for a value to change.

---

## 6a. PSU Failure on Battery — the alarm the system exists to deliver

The battery backup is there so the node **survives a PSU failure and reports it**. `power=FAIL` published by an **ONLINE** node is therefore the system's critical alarm, not an edge case.

* On PSU loss the node must **stay connected and keep publishing**. Losing the link defeats the entire purpose of the backup.
* All four MP1584 bucks — including the three LED rails — tap the system bus post-diode where the battery ties in, so the battery carries the **whole** load. LEDs stay lit, which means the current sensors keep reading normally and there is no false `FAIL_OPEN` cascade during an outage. It also means battery runtime is set by the full LED load.
* Do **not** treat a PSU-fail reading as a reason to change LED state. Hold the last command, as always.
* An **OFFLINE** node is a different fault entirely — network loss, dead board, or an exhausted battery. It is **not** a PSU failure and must never be reported as one.

---

## 7. Battery — stubbed, slot reserved

Battery is **not implemented** and is the next task after the HMI. Keep the divider on GPIO39 unpopulated in software.

Either omit `battery_pct` entirely, or publish nothing — do **not** publish `0`. The gateway maps a missing/unparseable value to the sentinel `65535`, which is what tells SCADA "unknown" as opposed to "flat battery". Publishing `0` defeats that.

---

## 8. Gap List — shipped `Firmware.ino` vs this contract

| # | Gap | Severity |
| --- | --- | --- |
| 1 | `I2C_SCL 17` collides with `ETH_CLOCK_GPIO17_OUT` | **Critical** |
| 2 | `I2C_SDA 16` — must be 4 | **Critical** |
| 3 | No 4th ACS712 / no `current4` topic (GPIO33) | High |
| 4 | Modes 4/5/6 absent; case 4 has the old all-on meaning | High |
| 5 | No NVS persistence of last command | High |
| 6 | Thresholds never reset when a side turns OFF | High |
| 7 | `NodeStateMsg` has no `state_alwy2` field | Medium |
| 8 | `ACS712 SENS = 0.146` matches no standard variant — confirm the part and recalibrate on the bench | Medium |
| 9 | Voltage-divider ratios in the test build are placeholders | Medium |

---

## 9. Bench Verification

`Firmware_Test/Firmware_Test.ino` already validates the hardware layer over Serial (I2C on 4/13, all 10 MOSFETs, all 4 ACS712, both dividers) with no network stack. Use it to confirm the board before flashing production firmware.

For the network layer, the gateway can be driven with no ESP32 present — see `RaspberryPi/Gateway/bench/`. Run the simulator first to confirm the gateway behaves, then swap in a real node and expect identical gateway output.
