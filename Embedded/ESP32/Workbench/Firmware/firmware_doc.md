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

> **Calibration honesty.** The `SENS_*` / `ZERO_*` / `PSU_DIV_RATIO` values are **datasheet-nominal placeholders, not measured on the board.** Ethernet, MQTT, command execution and the animation are unaffected, but the 4-state current health is only **approximate** until Phase-2 auto-calibration derives per-board values. Good enough to verify connectivity and functionality; **not yet a trustworthy alarm.**

---

## 2. What changed from v3.0.0

This build is v3.0.0 with the calibration and battery scaffolding removed and the config frozen into constants. **No behavioural change to the network, command, animation, MCP-verify, PSU-detection, or telemetry paths** — those are carried over verbatim.

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
| `SENS_LEFT/RGHT` , `ZERO_*` | `0.185`, `2.500` | Arrows — 5A ACS712 assumed. **Nominal.** |
| `SENS_STA1/STA2` | `0.100` | Static — 20A ACS712 assumed. **Nominal.** |
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
| `s` | Toggle the 1 Hz sensor stream: per-channel raw ADC, volts, amps, threshold, and 4-state — plus the PSU bus voltage. |

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
