# Metro Signage IoT: Raspberry Pi Bridge

**Version:** 2.0.0 — documents `Pi.py` v2.0.0, paired with ESP32 `Firmware.ino` **v3.0.0**

**Doc last reconciled against code:** 2026-07-27

**Role:** Master Translation Gateway (Modbus TCP ↔ MQTT)

> **Integrating a PLC/SCADA system? Read [`SCADA_Integration_Guide.md`](SCADA_Integration_Guide.md) instead.** It is the authoritative, self-contained integration document — addressing, encoding, alarm logic, timing and failure behaviour. This file is the *developer* view of the gateway and assumes you are working on the code.

## 📌 Overview

This Python application is the central nervous system of the Metro Signage IoT network. It serves as an industrial protocol bridge, translating commands from a legacy SCADA/PLC system (via Modbus TCP) into lightweight IoT messages (via MQTT) for up to 100 ESP32 edge nodes.

It runs a fully local Modbus TCP server, maintains thread-safe state dictionaries, handles Hand/Off/Auto (MUX) switching, and presents a flicker-free CLI dashboard for real-time monitoring.

## 🏗️ System Architecture

The application utilizes a robust multi-threaded design to prevent network blocking and ensure zero-latency translation:

1. **Main Thread (Logic & UI):** Handles the Modbus memory translation, MUX routing, array comparisons, and renders the ANSI-escaped terminal UI.

2. **Modbus Thread (SCADA Facing):** A daemon thread running `pymodbus.server` listening on port `502`.

3. **MQTT Thread (Edge Facing):** A background thread managed by Paho-MQTT's `loop_start()` handling asynchronous inbound telemetry from the ESP32 nodes.

## ✨ Key Features

### 1. Air-Gapped Network Support

The Modbus server binds to `0.0.0.0`, allowing the Raspberry Pi to operate with dual IP addresses. The internal `eth0` interface can host the isolated ESP32 MQTT network, while an alias IP or USB-Ethernet adapter handles the external Modbus SCADA traffic.

### 2. Thread-Safe State Management

To prevent data tearing when the background MQTT thread receives data at the exact microsecond the Main thread is reading it, a `threading.Lock()` (`data_lock`) isolates all global telemetry dictionaries.

### 3. HMI / SCADA MUX Logic (Hand/Off/Auto)

The bridge supports seamless switching between Automated SCADA control and Manual HMI control without losing state tracking:

* Modbus Register `43001` acts as the MUX flag.

* If `0` (Auto): The bridge copies commands from the SCADA buffer (`42001+`) to the active execution zone (`40001+`).

* If `1` (Manual): The bridge locks out SCADA and copies commands from the HMI buffer (`41001+`) to the execution zone.

### 4. 4-State Discrepancy Translation

The ESP32 nodes send intelligent diagnostic strings instead of binary OK/FAIL states. The bridge intercepts these strings and translates them into integers for SCADA diagnostics:

* `"OFF"` ➔ `0` (Intended OFF, no current)

* `"ON"` ➔ `1` (Intended ON, current flowing)

* `"FAIL_OPEN"` ➔ `2` (Intended ON, but 0 Amps flowing — e.g., burnt LED)

* `"FAIL_SHORT"` ➔ `3` (Intended OFF, but current flowing — e.g., shorted MOSFET)

### 5. Ghost Message Purging & Retained States

On startup, the bridge executes `perform_startup_cleanup()`. It publishes `"OFFLINE"` and `"---"` to all 100 nodes with `retain=True`. This purges the Mosquitto broker's database of any stale data from previous unexpected shutdowns, ensuring the UI and SCADA always start with a clean slate.

### 6. Broadcast PING (telemetry re-publish, NOT a dead-man switch)

The Main thread broadcasts a `"PING"` payload to the `metro/signage/scan` topic every 60 seconds. Each ESP32 responds by re-publishing its full telemetry set, which lets the gateway rebuild state without waiting for a value to change.

> **Correction (2026-07-20):** earlier revisions of this document claimed the PING "resets the hardware fail-safe timers (Dead-Man Switch)". **No such timer exists.** The firmware decision on record is that a node **holds its last command** on network loss. There is no forced-ON and no forced-OFF fail-safe. The ESP32 watchdog (`esp_task_wdt`, 15 s) recovers an internal hang only; it is not a network fail-safe.
>
> **Amended (2026-07-27), firmware v3.0.0:** "hold last command" survives a *link* outage but **not a reboot**. NVS persistence is **deferred** — see `Firmware.ino` v3.0.0, which comes up at mode `0` and waits for the retained `value` topic to re-command it. So:
>
> * link drops, node stays powered → animation keeps running, command held (the animation state machine now runs unconditionally, outside the MQTT-connected branch).
> * node reboots while the network is down → **the sign comes up dark and stays dark** until the broker is reachable and delivers the retained `value`.
>
> Acceptable on the bench. **Not acceptable for a site install** — NVS persistence is the outstanding gap that closes it.

### 7. Headless-Safe Diagnostics

The SCADA diagnostic translation runs **unconditionally**, outside the `isatty()` presentation branch. In v1.0.1 the `setValues` call that populated the entire `45001+` block sat inside that branch, so under `systemd` the PLC read zeros from every diagnostic register with no error logged. Logs are written to **stderr** so they never corrupt the ANSI frame on stdout.

> **Standing rule for this codebase: no side effect may live inside a presentation conditional.**

## 🗺️ Modbus Memory Map

The system uses a highly structured memory map allowing the PLC to control and diagnose 100 unique nodes seamlessly.

| Memory Block | Range | Description | 
| ----- | ----- | ----- | 
| **Active Execution Zone** | `40001 - 40100` | The actual commands sent to the ESP32s (see encoding below). | 
| **HMI Manual Buffer** | `41001 - 41100` | Commands queued by the local Graphical HMI. | 
| **SCADA Auto Buffer** | `42001 - 42100` | Commands queued by the central PLC. | 
| **MUX Control Flag** | `43001` | `0` = Read from SCADA Buffer, `1` = Read from HMI Buffer. | 
| **Diagnostic Block** | `45001 - 45800` | **8** continuous telemetry registers per node (See below). | 

### Command Value Encoding

The gateway publishes the register's integer **verbatim** — it does not interpret it. The ESP32 firmware decodes it:

**Commands control the moving arrows ONLY.** The two static zones are hardwired always-on — they are current-monitored but never switched. No command value can turn them on or off.

**Operator modes** — always available on the HMI:

| Value | Meaning |
| ----- | ----- |
| `0` | Arrows OFF |
| `1` | LHS chase animation |
| `2` | RHS chase animation |

**Technician modes** — PIN-gated on the HMI:

| Value | Meaning |
| ----- | ----- |
| `3` | Both chase animations |
| `4` | Solid ON — left MOSFETs (Port A, `0x1F`) |
| `5` | Solid ON — right MOSFETs (Port B, `0x1F`) |
| `6` | Solid ON — everything (both ports, `0x1F`/`0x1F`) |
| `10000 - 11023` | **Raw bitmask.** `value - 10000` = 10-bit MOSFET mask; low 5 bits = Port A (LHS), high 5 bits = Port B (RHS). |

> The old 2-bit "Relay 1 / Relay 2" encoding (`0-3` as an LHS/RHS relay pair) is **obsolete**, superseded by the modes above.

**Technician modes latch.** No auto-revert timer — a mode holds until explicitly changed, consistent with the hold-last-command behaviour on network loss. A sign left in solid-ON stays lit until someone changes it. **The latch does not survive a node reboot** — see the NVS caveat in §6.

> **The PIN is an HMI-side control, not a protocol-level one.** The gateway publishes whatever integer is in the register, and the PLC writes freely to the SCADA buffer (`42001+`). A technician mode arriving from SCADA is executed without challenge. The PIN prevents *local operator* misuse; it is not a security boundary.

### Diagnostic Register Layout (Per Node)

Each node occupies exactly **8** registers in the `45001+` diagnostic block. Node 1 (40001) occupies `45001 - 45008`; Node 2 occupies `45009 - 45016`; Node 100 occupies `45793 - 45800`.

| Offset | Register (Node 1) | Content | Encoding |
| ----- | ----- | ----- | ----- |
| +0 | `45001` | Network Status | `1` = Online, `0` = Offline |
| +1 | `45002` | Main Power | `1` = OK, `0` = PSU Failure — **see gating note** |
| +2 | `45003` | LHS LED Health | 4-state |
| +3 | `45004` | RHS LED Health | 4-state |
| +4 | `45005` | Battery Percentage | `0`–`100`, or `65535` = unknown |
| +5 | `45006` | **Static Zone 1** Health | 4-state |
| +6 | `45007` | **Static Zone 2** Health | 4-state |
| +7 | `45008` | **Actual Executing State** | the mode the node reports running (`0`–`6`, `10000+`), or `65535` = unknown — **two causes, see below** |

**Battery is currently stubbed in firmware.** The node deliberately does not publish `battery_pct` at all; the gateway maps the missing value to the `65535` sentinel. This is deliberate: `0` would be indistinguishable from a genuinely flat battery, so an offline or unfitted node must never report `0%` to SCADA.

Unparseable or offline 4-state values map to `99`.

### ⚠️ Commanded vs Actual — register `+7`

Registers `40001+` hold what the node was **told** to do. Register `+7` holds what it **reports actually doing**. If they differ, the node is running something other than what the control system believes — a stale command, a rejected payload, or a missed publish.

**A sign in this state displays the wrong thing while every screen says it is correct.** Nothing else in the system detects it.

#### `65535` on `+7` has two distinct causes — and they need different responses

1. **The node is offline or has never reported.** The gateway has no value to translate. `+7` is stale; ignore it and treat the node as unreachable.
2. **The node is ONLINE but cannot control or verify its outputs.** Firmware v3.0.0 publishes the non-numeric payload `FAULT` on the `state` topic when it cannot reach the MCP23017, when a MOSFET write fails readback verification, or while a bench calibration routine is running. The gateway maps any non-numeric payload to `65535`.

Cause 2 is a **hardware fault requiring dispatch**. It is deliberately impossible to clear by commanding anything: `65535` never equals a valid command, so an operator cannot accidentally green the alarm by guessing the value that matches a stale number while the sign stays dark. It clears only when the hardware is fixed (or the technician exits calibration).

> **The integrator must not "fix" this with a filter.** Suppressing `65535` suppresses the most serious fault in the system.

#### Required alarm logic — three-way branch

```
IF   45001+(i*8) == 0
     THEN "NODE OFFLINE"                                  // ignore +7, it is stale
ELSE IF 45008+(i*8) == 65535
     THEN "NODE CANNOT CONTROL OUTPUTS - hardware fault, dispatch"
ELSE IF 45008+(i*8) != 40001+i
     THEN "COMMAND NOT ACCEPTED - node running a different mode"
```

> **Superseded (2026-07-27).** This section previously instructed: *"alarm on `40001+i != 45008+(i*8)` whenever the node is online and register `+7` is not `65535`."* That was correct only while `65535` meant "offline / not yet reported". Since v3.0.0 it also means "online but cannot drive its outputs" — so the old rule **filters out the most serious fault in the system.** Use the three-way branch above.

**Operating principle: check network status (`+0`) before trusting any other register in the block.** This is not a one-off for the PSU alarm below — it is the general rule for this memory map.

### Fault signature table

Four faults that were previously ambiguous now have distinct signatures. Rows 2 and 3 were indistinguishable before v3.0.0 and have completely different repair actions.

| Fault | `+0` status | `+7` state | `current1/2` (`+2`/`+3`) | Action |
| --- | --- | --- | --- | --- |
| Board dead / cable out / PSU **and** battery gone | OFFLINE | stale | stale | Check power and link at the cabinet |
| I2C / MCP23017 fault | **ONLINE** | `65535`, mismatch | `FAIL_OPEN` | Dispatch — driver board / I2C wiring |
| LED strip or MOSFET fault | **ONLINE** | matches commanded | `FAIL_OPEN` | Dispatch — strip, wiring or output stage |
| PSU failed, running on battery | **ONLINE** | matches commanded | normal | Dispatch — mains / PSU. Node is on borrowed time |

A node in calibration also shows ONLINE + `65535`, with the four current registers **held at their last value** (the firmware suppresses current telemetry while calibrating, precisely so a bench session does not alarm a live control room). Expected during commissioning; unexpected on a live site.

### ⚠️ Power alarms MUST be gated on network status

Register `+1` is binary `1`/`0` with **no unknown state**, so an **offline** node reads `0` — identical to a genuine PSU failure. A naive `IF 45002 == 0 THEN alarm` fires for every unreachable node.

This matters more than it appears: the board has **battery backup specifically so a node survives PSU loss and stays online to report it**. `power=FAIL` from an **ONLINE** node is the system's critical alarm. Letting offline nodes raise the same alarm buries the real one.

**Required PLC logic:**

```
IF 45001+(i*8) == 1 AND 45002+(i*8) == 0 THEN raise "PSU FAILURE - node running on battery"
```

Never test the power register in isolation. The same gating applies to the four LED-health registers.

## 🚀 Installation & Usage

### 1. Prerequisites

Ensure you have Python 3 installed along with a running instance of Eclipse Mosquitto on the Raspberry Pi.

```bash
sudo apt update
sudo apt install mosquitto mosquitto-clients python3-venv
```

### 2. Install Dependencies

PEP 668 is enforced on Debian 13 — the system Python refuses `pip install`. Create the venv with `--system-site-packages` so it can also see the apt-installed `python3-tk` that the HMI needs (Tkinter cannot be pip-installed).

```bash
python3 -m venv --system-site-packages ~/metro-gw/venv
source ~/metro-gw/venv/bin/activate
pip install paho-mqtt==2.1.0 pymodbus==3.11.3
```

> **⚠️ DO NOT UPGRADE THESE BLINDLY.** The versions are pinned, not incidental.
>
> * **pymodbus must stay at `3.11.3`.** The bridge reads and writes the server's datastore from a worker thread (`context[0].getValues` / `setValues`). In **3.14.0** `ModbusServerContext` is no longer subscriptable and `ModbusDeviceContext` has no `getValues`/`setValues` at all — the replacements return `DEVICE_BUSY` for any non-simulator context. There is **no supported API path** for this design in 3.14. (3.11.3 does emit deprecation warnings; these blocks are removed entirely in pymodbus v4, which will be a rewrite of the bridge core.)
> * **paho-mqtt must be 2.x** and the code targets `CallbackAPIVersion.VERSION2`. On 2.x, `mqtt.Client("SomeClientId")` fails outright — the first positional argument is now the callback API version.
>
> Freeze the working set with `pip freeze > ~/metro-gw/requirements.txt`. A silent `pip install --upgrade` is how a working installation stops working.

### 3. Run the Bridge

```bash
python3 Pi.py
```

You will see the screen clear, the startup cleanup execute, and the double-buffered terminal dashboard appear tracking all nodes in real-time.

### 4. Running under systemd

Port 502 is privileged. Rather than running the daemon as root, keep it unprivileged and grant the single capability it needs:

```ini
AmbientCapabilities=CAP_NET_BIND_SERVICE
Restart=always
```

The terminal dashboard is suppressed automatically when stdout is not a TTY; the diagnostic translation still runs. Logs go to stderr, so `journalctl -u <unit>` captures them.

Keep the Gateway and the HMI as **two separate units**. The decoupling exists so that an HMI crash cannot take down the SCADA-facing Modbus server. Never merge them.