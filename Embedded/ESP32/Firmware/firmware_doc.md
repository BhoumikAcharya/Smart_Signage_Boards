# ESP32 Enterprise Signage Controller (Firmware v3.0.0)

**Architecture:** Dual-Core FreeRTOS, MCP23017 I2C Expander, 4x ACS712
**Role:** Hardware Edge Controller & Network Node
**Pairs with:** `RaspberryPi/Gateway/Pi.py` v2.0.0
**Contract:** [`../esp32_contract.md`](../esp32_contract.md) — the authoritative spec. Where this file and the contract disagree, the contract wins.
**Doc last reconciled against code:** 2026-08-07

> **This is the bench / bring-up round (6 nodes).** Calibration constants, PSU thresholds and the
> expected load currents in this build are **placeholders**. They are derived on the bench with the
> serial menu (§8), pasted back into the source and reflashed. Do not treat any current or voltage
> reading as trustworthy until that has been done.

> The v2.0 documentation is archived at [`Version History/Firmware_2.0.0/firmware_doc.md`](Version%20History/Firmware_2.0.0/firmware_doc.md). It describes I2C on GPIO 16/17, three current sensors and a different meaning for mode 4 — none of which are true of this build.

---

## 1. Architectural Overview

FreeRTOS decouples network operations from hardware sensing across both cores.

* **Core 1 (Application & Networking):** the main `loop()` — Ethernet events, MQTT payload parsing, the serial menu, MCP recovery, and the non-blocking LED animation state machine.
* **Core 0 (Hardware DSP):** a dedicated `SensorTask`. ADC reads, median filtering, PSU hysteresis/debounce, and the 4-state discrepancy logic.
* **IPC:** a single-slot FreeRTOS queue (`xQueueOverwrite` / `xQueueReceive`) passes `NodeStateMsg` from Core 0 to Core 1. Overwrite semantics mean the network side always gets the *latest* sensor state and never a backlog.
* **Watchdog:** `esp_task_wdt`, 15 s, panic reset, fed from both cores. It detects a frozen core and reboots the chip. **It does not react to network loss** — see §7.

---

## 2. Hardware Pin Mapping

### Ethernet PHY (LAN8720) — hardwired
MDC=23, MDIO=18, CRS_DV=27, RXD0=25, RXD1=26, TXD0=19, TXD1=22, TX_EN=21, **REFCLK=GPIO17** (`ETH_CLOCK_GPIO17_OUT`), PHY_ADDR=1, PHY_POWER=−1.

### I2C Bus (MCP23017 expander @ 0x20)
* **GPIO 4:** I2C SDA
* **GPIO 13:** I2C SCL

> **⚠️ Changed from v2.0, and not optional.** v2.0 used GPIO 16/17. **GPIO17 is the LAN8720's 50 MHz
> RMII reference clock** — putting I2C there has the bus fighting the Ethernet clock. Any document or
> sketch still showing 16/17 is describing the old build.

### Analog Sensors (ADC1 only)
ADC2 is unusable while the Ethernet/WiFi stack is active. 12-bit, `ADC_11db`.

| GPIO | Sensor | Load | Switched? |
| --- | --- | --- | --- |
| 36 | PSU voltage divider | — | — |
| 39 | Battery voltage divider | — | stubbed, see §9 |
| 34 | ACS712 #1 | LHS arrows | Yes (Port A) |
| 35 | ACS712 #2 | RHS arrows | Yes (Port B) |
| 32 | ACS712 #3 | Static Zone 1 | **No — always on** |
| 33 | ACS712 #4 | Static Zone 2 | **No — always on** |

### MOSFET outputs
Port **A0–A4 = LHS** (5 MOSFETs), Port **B0–B4 = RHS** (5 MOSFETs). A5–A7 / B5–B7 unused and unconnected — the health pattern (§5) depends on that.

---

## 3. Boot Order — network FIRST, MCP second

Deliberate, and the reason is worth understanding before anyone "tidies" `setup()`.

v2.0 initialised the MCP23017 first and did `while(1)` on failure — about 24 lines *before* `ETH.begin()`. An I2C fault therefore meant the PHY never came up, MQTT never connected, and the Last Will was never even registered (it is part of `client.connect`). **The node went completely mute and looked identical to an unplugged cable.**

v3.0.0 brings up Ethernet and MQTT first, then probes the MCP **non-fatally**. A node always gets online and reports what it can. An I2C fault now surfaces as `state` = `65535` on an **ONLINE** node — a distinct, actionable signature rather than silence.

If the MCP is not found, `ensureMcpHealthy()` retries the full I2C bring-up from `loop()` on a 2 s cadence, forever, and re-applies the held output intent when it succeeds. A node recovers by itself once the fault is fixed.

---

## 4. MQTT Topic Structure

Topics are keyed by `ASSIGNED_REGISTER` — **the holding-register number (`40001 + i`), not the node index.** Node 1 is `40001`.

**RX (subscribe):**
* `metro/signage/register/[ID]/value` — QoS 1. Integer command.
* `metro/signage/scan` — QoS 0. `PING` triggers a full telemetry re-publish.

**TX (publish, all retained):**

| Topic | Payload |
| --- | --- |
| `.../status` | `ONLINE:<ip>` or `OFFLINE` |
| `.../power` | `OK` or `FAIL` |
| `.../current1` / `current2` | 4-state string — LHS / RHS arrows |
| `.../current3` / `current4` | 4-state string — Static Zone 1 / 2 |
| `.../state` | active command integer, **or** `FAULT` |

**Last Will and Testament:** registered on `.../status` with payload `OFFLINE`, QoS 1, retained. This is what lets the gateway scrub a node that drops without a clean disconnect.

**`battery_pct` is never published** — see §9.

### Current telemetry is deliberately withheld in two cases

The four `current*` topics are suppressed **before the first sensor cycle completes** and **for the whole of a calibration session**. Previously retained values stay on the broker, so the registers hold rather than blanking. Calibration switches loads by design; publishing during it would raise `FAIL_OPEN` / `FAIL_SHORT` alarms in a live control room every time a technician runs `c` or `g`.

---

## 5. Control Logic & Animation Modes

10 MOSFETs via the MCP23017. **Commands drive the moving arrows only** — the two static zones are hardwired always-on, current-monitored but never switched. No command value affects them.

### Mode A: Macro animations (`0`–`6`)

| Value | Action | Port A (LHS) | Port B (RHS) |
| --- | --- | --- | --- |
| `0` | Arrows OFF | `0x00` | `0x00` |
| `1` | LHS chase | chase frame | `0x00` |
| `2` | RHS chase | `0x00` | chase frame |
| `3` | Both chase | chase frame | chase frame |
| `4` | **Solid ON left** | `0x1F` | `0x00` |
| `5` | Solid ON right | `0x00` | `0x1F` |
| `6` | **Solid ON all** | `0x1F` | `0x1F` |

> **⚠️ MODE 4 CHANGED MEANING IN v3.0.0.** Under v2.0, `4` = solid ON *all ten* MOSFETs. That is now
> mode **6**; mode 4 is left-side only. **Clear the retained `value` topics on the broker when
> upgrading a node from v2.0** — a retained `4` will light the wrong zone after reflashing, and
> nothing reports an error because the node is faithfully executing the value it was given.

**Chase frames:** `{0x07, 0x0E, 0x1C, 0x19, 0x13}` — 5 frames, 300 ms each, exactly 3 of 5 strips lit per frame. Driven by a non-blocking `millis()` state machine; never `delay()`.

**Technician modes latch.** No auto-revert timer. The PIN gate lives in the HMI — firmware executes any valid value it receives, and must not gain mode gating.

### Mode B: Raw bitmask override (`10000`–`11023`)

`mask = value - 10000`, 10 bits. Low 5 → Port A, high 5 → Port B. Allows exact control of individual strips.

### The animation runs unconditionally

`runAnimationStateMachine()` is called from `loop()` outside the MQTT-connected branch and independent of MCP health. v2.0 called it only inside the connected branch, so a broker outage **froze the chase mid-frame** — directly contradicting the hold-last-command requirement.

If the MCP is known-bad the state machine still counts frames but skips the write, because retrying a dead I2C bus every loop iteration would stall on timeouts thousands of times a second. `ensureMcpHealthy()` owns recovery and re-applies the held state, so the sign resumes by itself.

### Validated command parsing

v2.0 used `atoi()`, which returns `0` for unparseable input — and `0` is a valid command meaning "arrows off". **A corrupt payload silently blanked the sign.** v3.0.0 uses `strtol()` and checks the end pointer, so "the number zero" is distinguishable from "not a number".

A rejected command (malformed or out of range) is logged and **`state` is re-published**, so the gateway always sees the divergence. v1.1.1 logged the error and kept running; the gateway never learned.

### Verified writes — and the `state` topic

`writeGPIOA()` returns `void`, so v2.0 could not tell a successful write from a failed one and would report a healthy `state` while the sign was dark. v3.0.0 writes both ports, reads both back, and compares.

On top of the 10 real bits, a fixed **health pattern** (`0xA0`) is ORed into the unused A5–A7 / B5–B7 bits and expected back. **Why:** a dead I2C bus reads back `0x00`, which exactly matches a legitimate "all MOSFETs off" write — so without the pattern, mode `0` could never be verified and a node with a severed bus sitting in mode 0 would report perfect health.

`state` is published **only after a verified write**, never optimistically. A write that fails verification does not advance `activeCommand` at all.

| Divergence cause | `state` payload | Meaning |
| --- | --- | --- |
| Command rejected | previous **numeric** value | Still faithfully running the old mode, and knows it |
| MCP unreachable / readback mismatch / calibrating | `FAULT` → gateway maps to `65535` | Does **not know** what the outputs are doing |

> The second case is deliberately **impossible to clear by commanding anything** — `65535` never
> equals a valid command. Reusing a stale number would let an operator accidentally green the alarm
> by commanding the value that happens to match, while the sign stays dark.

---

## 6. Hardware DSP & 4-State Discrepancy Logic

Core 0 compares the **intended state** against the **measured current** to produce four diagnostic strings. Spelling is load-bearing — the gateway maps these to `1/0/2/3` and anything else becomes `99`.

```
                 measured >= threshold    measured < threshold
  intended ON          ON                     FAIL_OPEN
  intended OFF         FAIL_SHORT             OFF
```

**Static zones are always intended ON**, with a fixed threshold (`THRESHOLD_STATIC`) since their load never changes. Their only legal states are `ON` and `FAIL_OPEN`. If one reports `OFF`, the fault is in the sensing chain or the calibration, not the load.

**Signal chain:** median-of-51 ADC filter → noise gate (`NOISE_FLOOR`, 0.020 A) → IIR low-pass (`ALPHA = 0.15`).

### Dynamic thresholding — both sides, every mode change

`setThresholds()` scales the expected current by how many strips should be lit: `THRESH_PER_STRIP * (n - 0.5)`. A side expected **OFF** does not get "no threshold" — it gets a deliberately **low** one (`THRESH_OFF_DETECT`, 0.030 A) so a stuck-on MOSFET is still caught.

> **⚠️ v2.0 only ever set the side that was turning ON.** Modes 4 and 5 are the trap: one side goes
> solid while the other must be expected OFF. A side left carrying a stale **high** threshold from a
> previous solid mode (≈0.270 A) means a genuinely shorted MOSFET drawing three strips' worth
> (≈0.180 A) reads *below* it and reports **`OFF`** — a clean bill of health.
>
> This is **silent suppression of a real `FAIL_SHORT`**, not a nuisance alarm. Fixed in v3.0.0.

**Ordering constraint:** `NOISE_FLOOR` must stay **below** `THRESH_OFF_DETECT`. The noise gate snaps small readings to zero; if it sat above the off-threshold it would zero out exactly the currents `FAIL_SHORT` exists to catch. Preserve this when retuning on the bench.

### Mode-change settle

At `ALPHA = 0.15` and the 500 ms cadence, the IIR filter needs ~14 samples (~7 s) to reach 90% of a step — and the solid-ON threshold sits at exactly 90% of expected. Without intervention, **every** off→on mode change would publish `FAIL_OPEN` for about seven seconds. Every command would raise a false critical alarm.

Two-part fix: the filter is **snapped** to the instantaneous reading on a mode change (it exists to smooth noise, not a step the firmware itself caused), and state evaluation is **suppressed for `MODE_SETTLE_MS`** (1200 ms) while the load settles.

Only **mode** changes are stamped, not chase frames — a chase holds 3 strips lit throughout, so the load is steady. Stamping each frame would suppress evaluation permanently in modes 1–3, since 300 ms < 1200 ms.

---

## 7. Network Failsafe — HOLD LAST COMMAND

**There is no dead-man switch and no fail-safe timer.** Earlier project docs claimed the 60 s broadcast PING reset one; it never existed.

* On network loss the node **holds its current command** and keeps animating.
* No forced Solid-ON, no forced OFF.
* The `esp_task_wdt` (15 s, panic reboot) is **internal hang recovery only**.
* **PING** re-publishes the full telemetry set. That is its entire purpose.

### ⚠️ Known limitation — this does NOT survive a reboot

**NVS persistence is deferred.** There is no `Preferences` and no `nvs_*` call anywhere in v3.0.0.

| Scenario | Result |
| --- | --- |
| Link drops, node stays powered | Animation continues, command held. Works as intended. |
| Reboot, network **up** | Comes up at mode `0`, retained `value` restores it within seconds. |
| Reboot **during** a network outage | **Comes up dark and stays dark.** |

The node boots to `commandedValue = 0` and waits for the retained `value` topic. Nothing on the node remembers what it was displaying.

**Bench-acceptable. NOT site-acceptable** — a power blip taking out the field switch and the signs together brings the signs back blank, while the control system reports them as correctly showing mode 0. This is the outstanding firmware gap (`pi_agent.md` §12 item 10).

### PSU failure — the alarm the system exists to deliver

`power=FAIL` from an **ONLINE** node is the system's critical alarm: the battery backup exists so a node survives PSU loss and stays up to report it. All four buck converters tap the bus post-diode, so the battery carries the whole load and the LEDs stay lit — no false `FAIL_OPEN` cascade during an outage.

Because false positives are expensive, the transition is gated twice:

* **Hysteresis** — fail below `PSU_FAIL_VOLTS`, recover only above `PSU_OK_VOLTS`, hold in between. A single threshold would make a sagging supply chatter forever.
* **Debounce** — 3 consecutive agreeing reads (1.5 s at the 500 ms cadence) before the state flips, so a mains dip or switching inrush cannot publish a one-cycle `FAIL`.

> **Both thresholds are placeholders.** They must sit below normal supply voltage but **above the
> battery's loaded voltage**. Too high and the node reports `FAIL` while running fine on mains; too
> low and a real failure is never reported because the battery holds the bus up. This is the
> narrowest calibration window in the system and it needs the real PSU and battery under real load.

---

## 8. Serial Menu & Calibration Workflow

115200 baud. Production firmware carries its own bench menu, so a node does **not** need reflashing with the test sketch to be calibrated.

| Key | Action |
| --- | --- |
| `h` | menu |
| `i` | node info — network, MCP health, active mode |
| `r` | read all sensors once |
| `s` | toggle 1 Hz sensor stream (raw counts + volts + amps) |
| `c` | auto-calibrate ZERO points (arrows only) |
| `g` | auto-calibrate GAIN from expected currents |
| `v` | calibrate voltage dividers (needs a multimeter) |
| `m` | manually set one channel's zero/sens |
| `p` | print paste-ready calibration block |

### Workflow: `v` → `c` → `g` → `p` → paste → reflash

**Nothing is persisted.** All calibration lives in RAM until the block printed by `p` is pasted back into the top of `Firmware.ino` and the board is reflashed. When NVS lands, the same routines will write to flash and this step disappears — nothing else has to change.

`p` also emits a **CSV line** for cross-comparing the 6 bench nodes in a spreadsheet.

**Three things the routines cannot do, and why:**

1. **Static zones cannot be zeroed in place.** They are hardwired always-on with no MOSFET, so while the board is powered there is always current through those sensors and a true zero can never be observed — one measurement against two unknowns. `c` therefore **borrows** the arrows' measured zero: same part, same 5 V rail, same board, same temperature, same ADC. Far better than the datasheet's nominal 2.5 V. For a true static zero, calibrate before the strips are wired.
2. **Divider ratios cannot be self-derived.** The node can only read a divided voltage, never the true bus voltage. `v` asks you to measure it and type it in.
3. **Gain is derived against an *assumed* current.** `g` solves `sens = (Vmeasured − Vzero) / Iexpected` from the `EXPECTED_*` constants — which forces the sensor to agree with the assumption. If the real strips draw something else, the sensor is now calibrated to call the wrong current "normal" and **will never alarm on it**. The derived value is sanity-checked against the datasheet and warns on >20% deviation. **Calibrate all 6 bench nodes and compare:** tight agreement validates the assumption; scatter means the strips, sensors or wiring genuinely vary. Find that on the bench, not in a station.

> Entering calibration sets `calibrationActive`, which publishes `state` = `FAULT` and freezes the
> current registers for the duration. To SCADA the node appears **ONLINE + `65535` + frozen
> currents** — expected during commissioning, a fault anywhere else. **Coordinate before calibrating
> a node that is already commissioned.**

> The `Firmware_Test` menu handlers **cannot be copied across**: they use blocking `delay()`, which
> here would drop the broker session on keepalive and then trip the 15 s watchdog into a panic
> reboot. v3.0.0's equivalents pump the watchdog and the MQTT client while they wait.

---

## 9. Not Implemented in v3.0.0

| Feature | Status |
| --- | --- |
| **NVS persistence** | Deferred. Last command and calibration are both lost on reboot — see §7. |
| **OTA update** | Not present. Every change requires physical reflashing. |
| **Battery monitoring** | Stubbed. The divider is on GPIO39 and `BATT_DIV_RATIO` is calibratable, but nothing is published. |

**Battery deliberately publishes nothing** rather than `0`. The gateway maps the missing value to the sentinel `65535` = unknown; `0` would be indistinguishable from a genuinely flat battery. SCADA has been told explicitly not to alarm on it.

---

## 🚨 Changes from v2.0

1. **I2C moved 16/17 → 4/13.** GPIO17 is the LAN8720 50 MHz reference clock.
2. **4th ACS712 added** (GPIO33, Static Zone 2) plus the `current4` topic.
3. **Modes 4/5/6 added — mode 4 changed meaning.** Was solid-ON-all, now solid-ON-left; the old behaviour is mode 6.
4. **`state` topic added** — what the node is *actually* executing, published only after a verified write.
5. **Command parsing validated.** `atoi()` is gone; a corrupt payload no longer blanks the sign.
6. **Animation no longer stops when MQTT drops.** v2.0 froze the chase mid-frame on a broker outage.
7. **MCP failure no longer bricks the node.** Boot order is network-first; I2C retries forever in the background.
8. **MOSFET writes are read back and verified**, with a health pattern on the unused pins so an all-off write is still verifiable.
9. **Thresholds set for both sides on every mode change** — v2.0's stale thresholds masked a real `FAIL_SHORT`.
10. **Serial menu** for bench work (`h i r s c g v m p`).
