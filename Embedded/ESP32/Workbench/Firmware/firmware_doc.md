# ESP32 Signage Controller — Workbench Build (Firmware v4.0.0-p1)

**Phase:** 1 · Part 1 — *hardcoded bench build, no battery*
**Role:** Hardware edge controller & network node (bring-up / connectivity testing)
**Pairs with:** `RaspberryPi/Gateway/Pi.py` v2.0.0
**Contract:** [`../../esp32_contract.md`](../../esp32_contract.md) — the authoritative spec. Where this file and the contract disagree, the contract wins.
**Baseline:** forked from `ESP32/Firmware/Firmware.ino` v3.0.0. See [`../../Firmware/firmware_doc.md`](../../Firmware/firmware_doc.md) for the full architecture reference — this document only covers what is **different** in the Workbench build.
**Doc last reconciled against code:** 2026-08-25

---

## 1. Purpose of this build

Get a handful of nodes onto **wired Ethernet**, connected to the Pi/broker, executing commands and streaming telemetry, so they can be left running and observed on the bench. It is a **connectivity + functionality** build, not a production or calibration build.

Everything is **hardcoded**: per-unit IP + register, sensor calibration, the PSU divider ratio, and every threshold are compile-time `const`. There is no runtime calibration and nothing is persisted — you edit the constants at the top of the sketch and reflash.

> **Calibration honesty.** All four ACS712 current sensors are **5A modules** — arrows *and* static zones alike. Their datasheet sensitivity (nominally 0.185 V/A) has consistently proven **wrong** on these boards, so it is **not trusted**. For this round the sensitivity of every channel is **calibrated by hand**: measure it on the bench, edit the `SENS_*` constants in source, and reflash. Until that is done the `SENS_*` / `ZERO_*` / `PSU_DIV_RATIO` values in the sketch are **placeholders** — Ethernet, MQTT, command execution and the animation are unaffected, but the **static-zone** 4-state current health is only **approximate** and is **not yet a trustworthy alarm**. (The arrow channels no longer depend on calibration at all — they are intent-echo; see §2. Automated, NVS-backed calibration returns in Phase 2; this round is manual, edit-and-reflash.)

---

## 2. What changed from v3.0.0

This build is v3.0.0 with the calibration and battery scaffolding removed, the config frozen into constants, the chase pattern replaced, and the arrow current health simplified. The **network, command-decode, MCP-verify, PSU-detection, watchdog and IPC paths are carried over verbatim**; the behavioural changes are listed below.

### Removed — auto-calibration
- Serial commands `c` (zero), `g` (gain), `v` (dividers), `m` (manual), `p` (paste-block) and every helper behind them: `enterCalMode` / `exitCalMode` / `calSettle` / `checkDerivedSens` / `readSerialLine`.
- The `calibrationActive` flag and every gate on it (in `publishState`, `publish_state_msg`, `runAnimationStateMachine`, `SensorTask`, `printInfo`). With calibration gone the flag was always-false; the `FAULT` → 65535 path is now keyed on MCP health alone.
- The `EXPECTED_*` load-current constants, the `DS_SENS_*` datasheet references, and `SENS_TOLERANCE`.

### Removed — battery
- `PIN_VOLT_BATT` (was GPIO39), `BATT_DIV_RATIO`, and the battery line in the sensor stream. **No battery pin, no divider, no percentage, nothing published.** Battery is Phase 1 · Part 2.
- Nothing regressed on the gateway side: battery was already unpublished in v3.0.0, so the gateway/HMI continue to show 65535 = unknown, never 0%.

### Changed — config is now `const`
- `SENS_*`, `ZERO_*`, `PSU_DIV_RATIO`, `PSU_FAIL_VOLTS`, `PSU_OK_VOLTS` are now `const` (they were mutable only so the calibration routines could write them). Compiler-enforced "hardcoded".

### Changed — serial menu
- Reduced from `h/i/r/s/c/g/v/m/p` to **`h` / `i` / `s`** (see §4). `r` (read-once) dropped; `readSensorsVerbose()` is retained because the `s` stream uses it.
- `printMenu()` header now reads `v4.0.0-p1`.

### Changed — chase animation (new frame table + delay)
- The chase pattern (modes 1/2/3) is now **Chase 1: a 2-LED wrapping arrow**, direction `A4 → A0`, stepping every **250 ms** (was a 3-LED sliding window at 300 ms in v3.0.0).
- Frame table `chaseFrames[] = {0x18, 0x0C, 0x06, 0x03, 0x11}` — bits `4→0`: `11000, 01100, 00110, 00011, 10001`. The last is the **wrap frame** (`A4`+`A0` lit together): the arrow breaks across the seam once per cycle, which is what keeps motion continuous with no jump-back. It is intentional; do not remove it.
- One sweep = 5 × 250 ms = **1.25 s**. Solid modes 4/5/6 are unaffected (`0x1F`).
- The chase now lights **2 strips** (was 3), so modes 1/2/3 call `setThresholds(2, …)`. With arrow health now intent-echo (next section) this only sets the "expected on" intent flag and a display threshold — it no longer drives a pass/fail verdict.
- Source of truth for the pattern/rationale: [`../Firmware_Test/led_test_2/CHASE_ANIMATION_SPEC.md`](../Firmware_Test/led_test_2/CHASE_ANIMATION_SPEC.md). Note the production firmware is **command-driven** (mode 1 = LHS chase, 2 = RHS, 3 = both), so it does **not** implement that spec's bench-only phase loop (LEFT/RIGHT/OFF auto-cycling).

### Changed — arrow current health is now intent-echo (not measured)
- `current1` / `current2` (LHS / RHS arrows) now report **only `ON` / `OFF`, echoing the commanded intent** — never `FAIL_OPEN` / `FAIL_SHORT`. The reading updates immediately on a command (no filter, no settle window).
- **Why.** At 2-strip chase current the 5A ACS712's signal is ~40 ADC counts (~32 mV, ~0.17 A) — *inside* the sensor's own noise. No threshold cleanly separates a working chase from noise, so a measured verdict false-alarms both ways: `FAIL_SHORT` when the arrows are off, `FAIL_OPEN` when they chase. This is a hardware SNR wall, not a tuning problem.
- **Static zones are unchanged.** `current3` / `current4` keep the real measured 4-state (`ON` / `FAIL_OPEN`) — they draw enough current to be reliable, and their verdict is still held for `MODE_SETTLE_MS` after a mode change.
- **Compensating control.** Genuine arrow health moves to the **Phase-3 scheduled all-on self-test**: driving all 5 strips gives ~5× the current (~100 counts), which clears the noise for "is this side alive". Planned with an on-demand "run test now" trigger. Between tests, a dark arrow is *not* caught by current sensing — but the `state` topic still catches command / MCP-path failures, so the sign is not blind to everything.
- **⚠ Contract ripple — not yet applied.** The gateway maps `current1/2` → diag registers **+2 / +3** ("LHS / RHS LED Health"), documented as 4-state in [`esp32_contract.md`](../../esp32_contract.md) and `RaspberryPi/Gateway/SCADA_Integration_Guide.md`. Those registers now only ever carry **0 / 1** for the arrows (never 2 / 3), so any SCADA alarm keyed on `FAIL_*` for the arrows will never fire. **Those authoritative docs still need updating to say so** — this workbench doc is ahead of them.

---

## 3. Hardcoded configuration (edit + reflash)

All at the top of the sketch.

| Constant | Value (node 1) | Notes |
| --- | --- | --- |
| `mqtt_server_ip` | `192.168.1.10` | Pi, ESP32-side static IP. Same for all nodes. |
| `mqtt_port` | `1883` | |
| `ASSIGNED_REGISTER` | `40001` | **Per-node.** node 1 = 40001, node 2 = 40002 … |
| `local_IP` | `192.168.1.101` | **Per-node — must be unique.** |
| `gateway_ip` / `subnet` / `primaryDNS` | `192.168.1.1` / `255.255.255.0` / `8.8.8.8` | Same for all nodes. |
| `SENS_LEFT/RGHT` , `ZERO_*` | `0.185`, `2.500` | Arrows — **5A** ACS712. Placeholder; **sensitivity set by manual per-channel calibration** this round (datasheet 0.185 is not trusted). |
| `SENS_STA1/STA2` | `0.100` | Static — **5A** ACS712 (all four sensors are 5A). The `0.100` in the code is a **stale 20A placeholder**, wrong for a 5A part — **replace by manual calibration**. |
| `PSU_DIV_RATIO` | `5.30` | **Nominal.** |
| `PSU_FAIL_VOLTS` / `PSU_OK_VOLTS` | `10.0` / `11.0` | Hysteresis band. **Placeholders.** |
| `THRESH_PER_STRIP` / `THRESHOLD_STATIC` | `0.060` / `0.080` A | Design thresholds. |
| `THRESH_OFF_DETECT` / `NOISE_FLOOR` | `0.030` / `0.020` A | `NOISE_FLOOR` **must** stay below `THRESH_OFF_DETECT`. |

> **Per-node checklist before flashing each board:** set `ASSIGNED_REGISTER` **and** `local_IP` to that node's unique values. Everything else is common. Clear retained `.../value` topics on the broker before flashing so a stale retained command does not light the wrong zone on boot.

---

## 4. Serial menu (115200 baud)

| Key | Action |
| --- | --- |
| `h` | Print the menu. |
| `i` | Node info: register, Ethernet IP, MQTT state, MCP health, commanded vs actually-executing mode, current port bytes. |
| `s` | Toggle the 1 Hz sensor stream: per-channel raw ADC, volts, amps and threshold, plus the PSU bus voltage. The `state` column shows the **intent-echo** for the arrows (raw amps still printed for observation) and the **measured 4-state** for the static zones. |

There is no interactive input in this build; `h/i/s` are single keypresses. (The blocking `readSerialLine` helper left with the calibration routines, so nothing in this build can stall the loop waiting on serial.)

---

## 5. What is unchanged and still load-bearing

Carried over from v3.0.0 without modification — refer to the [baseline doc](../../Firmware/firmware_doc.md) for detail:

- **Network-first boot.** An MCP/I2C fault never blocks the PHY; the node still comes ONLINE and reports the fault as `state` = 65535.
- **MCP write-verify-recover.** Every MOSFET write is read back (with the `0xA0` health pattern on the unused nibble). A silent bus fails the check; recovery retries every 2 s and re-applies the held output.
- **Verified `state` publish.** `state` advances only after a verified write; never optimistically.
- **Hold-last-command animation.** The state machine runs unconditionally, so the sign keeps animating through a broker or link outage. **No NVS yet** — a reboot comes up dark at mode 0 until the retained `value` topic re-commands it (Phase 2).
- **Validated command parse** (`strtol`, not `atoi`): a corrupt payload is rejected and the prior mode re-asserted.
- **PSU detection:** hysteresis band + 3-read debounce. `power=FAIL` from an ONLINE node is the system's critical alarm.
- **Dual-core FreeRTOS + 15 s watchdog**, single-slot overwrite queue Core 0 → Core 1.

---

## 6. Build & verify

Compiled clean against `esp32:esp32` core 3.3.8, `PubSubClient` 2.8, `Adafruit MCP23017` 2.3.2:

```
Sketch uses 1011836 bytes (77%) of program storage space.
Global variables use 49224 bytes (15%) of dynamic memory.
```

```bash
Embedded/bin/arduino-cli compile --fqbn esp32:esp32:esp32 Embedded/ESP32/Workbench/Firmware/Firmware.ino
```

> **Not yet flashed to hardware.** This is a source + compile milestone; on-bench verification of a few nodes against the Pi is the next step.

---

## 7. Roadmap out of this build

- **Phase 1 · Part 2 — battery.** Reintroduce the battery sense pin + divider and add the battery logic, publishing a real percentage. End of Phase 1 = this build plus battery.
- **Phase 2** (planned in detail later): NVS persistence of config → auto-calibration writing derived values to NVS → OTA over Ethernet → an NVS identity block (unique node ID + software/PCB version) for field troubleshooting.
- **Phase 3 — scheduled diagnostics** (planned in detail later): daily/weekly/monthly self-tests plus an on-demand "run test now" trigger. A test drives the arrows **all-on** (high-current, out of the ACS712 noise floor) to actually verify arrow health, and emits a per-node diagnostic report. **This is the compensating control for the arrow intent-echo** (§2) — the only place arrow LED faults are genuinely detected.
