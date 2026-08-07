# ESP32 Firmware Contract — as required by Gateway v2.0.0

**Written:** 2026-07-20, immediately after `Pi.py` v2.0.0 was finalized.
**Doc last reconciled against code:** 2026-08-07, against `Firmware/Firmware.ino` **v3.0.0**.
**Purpose:** the authoritative reference for designing the production firmware. Everything here is fixed by the gateway that already exists — the firmware must conform to it, not the other way round.
**Companion docs:** `RaspberryPi/pi_agent.md` (platform contract), `RaspberryPi/Gateway/Gateway_doc.md`, `PCB/PCB_V3/Connections.md` (superseded in part — see §1).

> **Status change, 2026-08-07.** This file was written as a *specification for firmware that did not yet exist*. `Firmware.ino` v3.0.0 now implements it, so most of the document has become a description of shipped behaviour rather than a target. Sections that described gaps in the old v2.0 build have been corrected — see §8, which is now a regression list, not a work list.
>
> **Calibration constants, PSU thresholds and expected load currents in v3.0.0 remain placeholders** pending the 6-node bench round. Those are tracked in `DOC_UPDATES_PENDING.md` §0 and are the only things in this contract still waiting on hardware.

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

> **The I2C move is not optional.** GPIO17 is `ETH_CLOCK_GPIO17_OUT`, the 50 MHz RMII reference clock for the LAN8720. Putting I2C there has the bus fighting the Ethernet clock.
>
> ✅ **Fixed in v3.0.0** (`Firmware.ino:193-194`, `SDA 4` / `SCL 13`). The v2.0 build had `I2C_SDA 16` / `I2C_SCL 17` and was the origin of this warning. Retained because the wrong pins are still published in `Firmware/firmware_doc.md` (v2.0, pending rewrite) and in `PCB/PCB_V3/Connections.md` §3, so the collision can still reach someone through those files.

**LAN8720 RMII:** MDC=23, MDIO=18, CRS_DV=27, RXD0=25, RXD1=26, TXD0=19, TXD1=22, TX_EN=21, REFCLK=GPIO17 (`ETH_CLOCK_GPIO17_OUT`), PHY_ADDR=1, PHY_POWER=-1.

**MCP23017** @ `0x20` (A0/A1/A2→GND, RESET→3V3). Port **A0–A4 = LHS** 5 MOSFETs, Port **B0–B4 = RHS** 5 MOSFETs. 100R series, 10k pull-down. All 16 pins set OUTPUT; A5–A7/B5–B7 unused.

### MCP health pattern — how a dead I2C bus is detected

Every MOSFET write is a **verified** write: write both ports, read both back, compare. On top of the 10 real bits, v3.0.0 ORs a fixed pattern `MCP_HEALTH_PATTERN = 0xA0` into the unused A5–A7 / B5–B7 bits of both ports and expects it back.

**Why the pattern is necessary.** A dead or disconnected I2C bus reads back `0x00`. That **exactly matches** a legitimate "all MOSFETs off" write — so without the pattern, mode `0` could never be verified, and a node with a severed I2C bus sitting in mode 0 would report perfect health. Forcing a non-zero expected value into every port in every mode means a silent bus always fails the comparison.

> **This depends on A5–A7 / B5–B7 being genuinely unconnected.** If anything is ever wired to those pins, the readback comparison breaks and the node will report a permanent false `FAULT`. Confirm on the bench (`DOC_UPDATES_PENDING.md` B7) and record it in `PCB/PCB_V3/Connections.md`.

A failed readback calls `setMcpHealth(false)`, which publishes `state` = `FAULT` and stops further write attempts; `ensureMcpHealthy()` retries the whole I2C bring-up on a 2 s cadence and re-applies the held output intent when it succeeds, so a node recovers by itself once the fault is cleared.

**ADC:** 12-bit, `ADC_11db`, ADC1 only (ADC2 is unusable while WiFi/ETH is active). Median-of-51 filter, then IIR low-pass `ALPHA = 0.15`.

### ACS712 variant — which part goes where

Not currently recorded anywhere in the repo. It matters, because it sets whether a **single** strip failing is detectable. At `THRESH_PER_STRIP` (0.060 A), one strip out produces:

| Part | Sensitivity | Signal at 0.060 A | ADC counts |
| --- | --- | --- | --- |
| ACS712-20A | 0.100 V/A | 6.0 mV | **~7** — marginal against ADC noise |
| ACS712-05A | 0.185 V/A | 11.1 mV | **~14** — roughly double the margin |

Detecting a **whole side** failing is comfortable either way (3 strips ≈ 22 counts even on the 20 A part). It is the single-strip-out case that is at risk.

**Recommendation: 5 A parts on the arrows, 20 A parts on the static zones.** The arrows are the low-current channels that need the resolution; the static zones carry more current and only ever need to distinguish `ON` from `FAIL_OPEN`. v3.0.0's defaults (`SENS_LEFT/RGHT = 0.185`, `SENS_STA1/STA2 = 0.100`) assume exactly that.

> **Read the markings off the fitted parts and flip the defaults if they disagree** (`DOC_UPDATES_PENDING.md` B2). The `g` calibration routine warns if the derived gain deviates from the declared datasheet value by more than `SENS_TOLERANCE` (20%), which will catch a mismatch — but only if someone is watching the serial output.

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

> **⚠️ MODE 4 CHANGED MEANING — and the change has now shipped.** Under v2.0, case 4 = solid ON *all ten* MOSFETs. As of v3.0.0 that is mode **6**, and mode 4 is left-side only (`Firmware.ino:503-507`).
>
> **Clear the retained `value` topics on the broker when upgrading a node from v2.0.** A retained `4` published against the old firmware will light only the left side once the node is reflashed, and nothing in the system reports an error — the node is faithfully executing the value it was given.

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
| `current4` | 4-state string | Static Zone 2 — added in v3.0.0 |
| `battery_pct` | — | **Never published.** Stubbed; see §7 |
| `state` | integer **or** `FAULT` | **The command this node is ACTUALLY executing.** See below. |

> The four `current*` topics are also **suppressed** before the first sensor cycle completes and for the whole of a calibration session — see §3a. `status`, `power` and `state` continue publishing throughout.

### `state` — the acknowledgment topic

Everything else in the system shows what a node was *commanded*. This topic is the only thing that reports what it is *doing*. Publish it:

* whenever the active command changes,
* in response to a PING,
* whenever MCP health changes state,
* **never** optimistically — publish only after the MOSFET write has been written *and read back verified*. A write that fails verification does not advance `activeCommand` at all.

#### The two divergence causes carry different payloads — deliberately

| Cause | Payload | What it means |
| --- | --- | --- |
| **Command rejected** (malformed or out of range) | the **previous numeric** value | The node is still faithfully running the old mode and *knows* it |
| **MCP unreachable / readback mismatch / calibration running** | non-numeric — v3.0.0 publishes `FAULT` | The node does **not know** what its outputs are doing |

The gateway maps any non-numeric payload to `65535` and exposes this at diagnostic register `+7`.

> **Why the second case must not reuse the last known number.** An operator sees the commanded-vs-actual mismatch, tries commanding the value that happens to match the stale number, the gateway sees `commanded == actual`, the flag clears and the screen goes green — while the sign is still dark. `65535` can never equal a valid command, so the fault **cannot be cleared by commanding anything**. It clears only when the hardware is fixed, or when the technician exits calibration. That is the intended behaviour and must not be "fixed" with a filter at the SCADA end.

Rejecting a command silently is the failure mode being designed out: v1.1.1 logged `[ERROR] Malformed Payload` and kept running, and the gateway never learned. v3.0.0 re-publishes `state` on every rejection so the divergence is always visible.

**Parsing must distinguish "the number zero" from "not a number."** `atoi()` returns `0` for unparseable input, and `0` is a valid command meaning arrows-off — so a corrupt payload blanks the sign with no error anywhere. v3.0.0 uses `strtol()` and checks the end pointer.

**4-state strings, exactly:** `ON`, `OFF`, `FAIL_OPEN`, `FAIL_SHORT`. The gateway maps these to `1/0/2/3`; anything else becomes `99`. Spelling is load-bearing.

```
                 measured >= threshold    measured < threshold
  intended ON          ON                     FAIL_OPEN
  intended OFF         FAIL_SHORT             OFF
```

**Last Will and Testament:** connect with LWT on `.../status`, payload `OFFLINE`, **QoS 1, retained**. This is what makes the gateway's offline-scrubbing work when a node drops without a clean disconnect.

**Publish on change, not on a timer.** The gateway's UI redraws once a second regardless; needless retained republishes just churn the broker.

---

## 3a. When current telemetry is deliberately withheld

The four `current*` topics are **not** published in two situations. In both, the previously retained values stay on the broker and the gateway keeps showing them — so the registers hold their last value rather than going to a "no data" state.

1. **Before the first sensor cycle has completed** (`haveSensorData == false`). Publishing at boot would assert `OFF`/`FAIL_OPEN` states that no measurement supports.
2. **Throughout a calibration session** (`calibrationActive == true`). Calibration switches loads on and off by design, so the readings are meaningless relative to the commanded mode. Publishing them would raise `FAIL_OPEN` and `FAIL_SHORT` alarms in a live control room every time a technician runs `c` or `g` on the bench.

Calibration additionally forces `state` to `FAULT`, so a bench session appears to SCADA as **ONLINE + `65535` + frozen current registers**. That combination is expected during commissioning and should be treated as a fault anywhere else — it is documented in the fault-signature tables in `Gateway_doc.md` and `SCADA_Integration_Guide.md` §5.6 for exactly that reason.

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

A side expected **OFF** does not get "no threshold" — it gets a deliberately **low** one, `THRESH_OFF_DETECT` (0.030 A), so a stuck-on MOSFET is still caught. Both sides are re-armed on every mode change (`setThresholds()`), never just the side that is turning on.

> **Modes 4 and 5 are the trap: one side goes solid while the other must be expected OFF.**
>
> ⚠️ **Corrected 2026-08-07 — this document previously had the failure direction backwards.** It read: *"carrying a stale threshold from a previous mode makes a healthy board report `FAIL_OPEN`."* That describes a nuisance alarm. The real consequence is the opposite and considerably worse.
>
> Work it through `getDiscrepancyState()`. A side left carrying a stale **high** threshold from a previous solid mode (`× 4.5` ≈ 0.270 A) is now expected OFF. A genuinely shorted MOSFET drawing three strips' worth of current (≈ 0.180 A) reads **below** that stale threshold, so the intended-OFF row of the truth table returns **`OFF`** — a clean bill of health.
>
> The bug does not raise a false alarm. It **silently suppresses a real `FAIL_SHORT`**: live LEDs, a sign showing the wrong thing, and every screen in the system reporting normal. v2.0 never cleared `dynamicThreshLeft/Right` when a side turned off; ✅ fixed in v3.0.0.

**Ordering constraint: `NOISE_FLOOR` (0.020 A) must stay below `THRESH_OFF_DETECT` (0.030 A).** The noise gate snaps small readings to zero. If it ever sat above the off-threshold it would zero out exactly the currents `FAIL_SHORT` exists to catch, re-creating the bug above by a different route. Anyone retuning these two constants on the bench must preserve the ordering.

---

## 5a. Mode-change settle — why evaluation is suppressed after a switch

Non-obvious, and it will look like a bug to anyone reading the sensor loop without this note.

The IIR filter is deliberately slow. At `ALPHA = 0.15` and the 500 ms sensor cadence it needs roughly **14 samples (~7 s)** to climb to 90% of a step change — and the solid-ON threshold sits at exactly 90% of expected (4.5 of 5 strips). Left alone, **every** off→on mode change would publish `FAIL_OPEN` for about seven seconds before clearing itself. Every single command would raise a false critical alarm.

v3.0.0 fixes this in two parts:

1. **Snap the filter** to the instantaneous reading on a mode change. The filter exists to smooth sensor noise, not to smooth a step change the firmware itself caused.
2. **Suppress state evaluation** for `MODE_SETTLE_MS` (1200 ms) while the load physically settles. `power` still evaluates and publishes during the window; only the four current states are withheld.

> **Only mode changes are stamped, not chase frames.** A chase holds exactly 3 of 5 strips lit in every frame, so the load is steady across the animation and needs no settle window. Stamping each frame would suppress current evaluation permanently in modes 1–3, since the 300 ms frame interval is shorter than the 1200 ms window.

`MODE_SETTLE_MS` is a placeholder until the real LED load is measured (`DOC_UPDATES_PENDING.md` B6).

---

## 6. Network Failsafe — HOLD LAST COMMAND

**There is no dead-man switch and no fail-safe timer.** Three project docs previously claimed the 60 s broadcast PING resets one; it does not exist.

* On network loss the node **holds its current command** and keeps animating. The animation state machine runs unconditionally from `loop()` — outside the MQTT-connected branch and independent of MCP health.
* No forced Solid-ON, no forced OFF.
* `esp_task_wdt` (15 s, panic reboot) is **internal hang recovery only**. It is not a network watchdog.

### ⚠️ "Hold last command" survives a link outage but NOT a reboot

> **Corrected 2026-08-07.** This section previously stated: *"The active command is persisted to NVS on every change and re-applied on boot, so it survives a reboot with the network still down."* **That is false and was never implemented.** v3.0.0 contains no `Preferences`, no `nvs_*` calls. The same wrong claim was found and corrected in `Gateway_doc.md` §6 and logged as `pi_agent.md` §12 item 10 on 2026-07-27; this file was missed in that pass and contradicted both for eleven days.

Actual v3.0.0 behaviour:

| Scenario | Result |
| --- | --- |
| Link drops, node stays powered | Animation keeps running, command held. Works as intended. |
| Node reboots, network **up** | Comes up at mode `0`, broker delivers the retained `value`, sign restores within seconds. |
| Node reboots **during** a network outage | **The sign comes up dark and stays dark** until the broker is reachable again. |

The node boots to `commandedValue = 0` and waits for the retained `value` topic to re-command it. Nothing on the node remembers what it was displaying.

**This is bench-acceptable and NOT site-acceptable.** A power blip that takes out the field switch and the signs together brings the signs back blank, and the control system will report them as correctly showing mode 0. NVS persistence is the outstanding firmware gap that closes it — tracked as `pi_agent.md` §12 item 10 and §8 gap 5 below.

**PING** (`metro/signage/scan`, payload `PING`, QoS 0, every 60 s from the gateway): respond by re-publishing the full telemetry set. That is its entire purpose — it lets the gateway rebuild state without waiting for a value to change.

---

## 6a. PSU Failure on Battery — the alarm the system exists to deliver

The battery backup is there so the node **survives a PSU failure and reports it**. `power=FAIL` published by an **ONLINE** node is therefore the system's critical alarm, not an edge case.

* On PSU loss the node must **stay connected and keep publishing**. Losing the link defeats the entire purpose of the backup.
* All four MP1584 bucks — including the three LED rails — tap the system bus post-diode where the battery ties in, so the battery carries the **whole** load. LEDs stay lit, which means the current sensors keep reading normally and there is no false `FAIL_OPEN` cascade during an outage. It also means battery runtime is set by the full LED load.
* Do **not** treat a PSU-fail reading as a reason to change LED state. Hold the last command, as always.
* An **OFFLINE** node is a different fault entirely — network loss, dead board, or an exhausted battery. It is **not** a PSU failure and must never be reported as one.

### Debounce and hysteresis — required, not optional

Because `power=FAIL` from an ONLINE node is *the* critical alarm, false positives are expensive: cry wolf a few times and the control room learns to ignore it. v3.0.0 gates the transition twice.

**Hysteresis.** A single threshold makes a supply sagging to exactly that value chatter `OK`/`FAIL` indefinitely. Two thresholds instead: fail below `PSU_FAIL_VOLTS`, recover only above `PSU_OK_VOLTS`, and **hold the current state anywhere in between**. `PSU_OK_VOLTS` must always be greater than `PSU_FAIL_VOLTS`.

**Debounce.** `PWR_DEBOUNCE_COUNT = 3` consecutive agreeing reads are required before the state flips — 1.5 s at the 500 ms sensor cadence. A mains dip or the inrush from a large load switching nearby cannot publish a one-cycle `FAIL`. The counter resets whenever a reading agrees with the committed state, so the three reads must be consecutive.

> **⚠️ Both thresholds are placeholders in v3.0.0** (`PSU_FAIL_VOLTS = 10.0`, `PSU_OK_VOLTS = 11.0`). They must sit **below the normal supply voltage but above the battery's loaded voltage**. Set too high, a node reports `FAIL` while running perfectly on mains; set too low, a genuine PSU failure is never reported because the battery holds the bus above the threshold. This is the narrowest calibration window in the system and it cannot be derived from the datasheets — it needs the real PSU and the real battery chemistry under real load (`DOC_UPDATES_PENDING.md` B4).

---

## 7. Battery — stubbed, slot reserved

Battery is **not implemented** and is the next task after the HMI. Keep the divider on GPIO39 unpopulated in software.

Either omit `battery_pct` entirely, or publish nothing — do **not** publish `0`. The gateway maps a missing/unparseable value to the sentinel `65535`, which is what tells SCADA "unknown" as opposed to "flat battery". Publishing `0` defeats that.

✅ **v3.0.0 complies by omission** — it never publishes the topic at all, so diagnostic register `+4` reads `65535` on every node. The SCADA integrator has been told explicitly not to alarm on it (`SCADA_Integration_Guide.md` §5.5). Note that `bench/simulate_node.py`-style test scripts that publish a `battery_pct` value are exercising a path the real hardware does not drive — see the warning in `pi_agent.md` §13.

---

## 8. Gap List — ✅ CLOSED IN v3.0.0, except where noted

> **Status: 7 of 9 resolved (2026-08-07 reconciliation).** This table was written against the v2.0 build. It is retained as the **regression list** — these are the failure modes to test against, not an outstanding work list — following the same convention as `pi_agent.md` §8. Two items remain genuinely open and are called out below.

| # | Gap | Severity | Status in v3.0.0 |
| --- | --- | --- | --- |
| 1 | `I2C_SCL 17` collides with `ETH_CLOCK_GPIO17_OUT` | **Critical** | ✅ Fixed — `SCL 13` |
| 2 | `I2C_SDA 16` — must be 4 | **Critical** | ✅ Fixed — `SDA 4` |
| 3 | No 4th ACS712 / no `current4` topic (GPIO33) | High | ✅ Fixed — `PIN_CURR_STA2`, `current4` published |
| 4 | Modes 4/5/6 absent; case 4 has the old all-on meaning | High | ✅ Fixed — see the §2 upgrade warning |
| 5 | No NVS persistence of last command | High | 🔴 **STILL OPEN — deliberately deferred.** See §6 |
| 6 | Thresholds never reset when a side turns OFF | High | ✅ Fixed — `setThresholds()` re-arms both sides |
| 7 | `NodeStateMsg` has no `state_alwy2` field | Medium | ✅ Fixed — `state_sta1` / `state_sta2` |
| 8 | `ACS712 SENS = 0.146` matches no standard variant | Medium | 🟡 Defaults now standard (0.185 / 0.100), but the **fitted part is still unconfirmed** — B2 |
| 9 | Voltage-divider ratios are placeholders | Medium | 🔴 **STILL OPEN** — `PSU_DIV_RATIO` / `BATT_DIV_RATIO` set with `v` on the bench |

**Gaps introduced by v3.0.0 itself**, not present in the original list:

| # | Gap | Severity |
| --- | --- | --- |
| 10 | `PSU_FAIL_VOLTS` / `PSU_OK_VOLTS` are placeholders — B4 (see §6a) | **High** |
| 11 | `THRESH_OFF_DETECT` unvalidated against real idle-channel noise — B5 | Medium |
| 12 | `MODE_SETTLE_MS` unvalidated against the real LED load — B6 | Medium |
| 13 | Expected load currents are `0.000` placeholders in the `Firmware.ino` header — B3 | Medium |

Gaps 8–13 all close on the 6-node bench round. Gap 5 is a firmware task, not a measurement.

---

## 9. Bench Verification

`Firmware_Test/Firmware_Test.ino` already validates the hardware layer over Serial (I2C on 4/13, all 10 MOSFETs, all 4 ACS712, both dividers) with no network stack. Use it to confirm the board before flashing production firmware.

For the network layer, the gateway can be driven with no ESP32 present — see `RaspberryPi/Gateway/bench/`. Run the simulator first to confirm the gateway behaves, then swap in a real node and expect identical gateway output.

### The v3.0.0 serial menu is the commissioning interface

Production firmware carries its own bench menu at 115200 baud, so a node does **not** need reflashing with the test sketch to be calibrated:

| Key | Action |
| --- | --- |
| `h` | menu |
| `i` | node info — network, MCP health, active mode |
| `r` | read all sensors once |
| `s` | toggle 1 Hz sensor stream (raw counts + volts + amps) |
| `c` | auto-calibrate zero points (arrows only) |
| `g` | auto-calibrate gain from expected currents |
| `v` | calibrate voltage dividers (needs a multimeter) |
| `m` | manually set one channel's zero/sens |
| `p` | print paste-ready calibration block |

**Calibration results are not persisted.** The workflow is `v` → `c` → `g` → `p`, then paste the emitted block into the source and reflash. Everything is lost on reset until that happens.

> Entering calibration sets `calibrationActive`, which publishes `state` = `FAULT` and freezes the current registers for the whole session — by design (§3a). A live control room will see the node as ONLINE with `65535`. **Coordinate before calibrating a node that is already commissioned.**
>
> The `Firmware_Test` menu handlers **cannot be copied across** into production firmware: they use blocking `delay()`, which here would drop the broker session on keepalive and then trip the 15 s watchdog into a panic reboot. v3.0.0's equivalents pump the watchdog and the MQTT client while they wait.
