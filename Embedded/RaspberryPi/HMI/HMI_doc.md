## Metro Signage SCADA HMI - Technical & Design Specifications

Document Version: 1.1
Project Phase: V1 Decoupled Architecture

> **Designing or rebuilding the HMI? Read [`hmi_contract.md`](hmi_contract.md) instead.** That is the
> authoritative source of truth — platform, register map, command encoding, telemetry semantics and
> the outstanding design questions. **This file is the implementation and design-rationale view**: it
> records *why* the UI is built the way it is, and assumes you are working on `HMI.py` itself. Where
> the two disagree, the contract wins.
>
> (Same split as `Gateway_doc.md` ↔ `Gateway/SCADA_Integration_Guide.md`.)

| Version | Date | Author | Description |
| ------- | ---- | ------ | ----------- |
| 1.0 | May 2026 | Core Engineering | Initial V1 Baseline creation. |
| 1.1 | 2026-08-07 | Core Engineering | Reconciled against `HMI.py` as fixed 2026-07-27 and firmware v3.0.0. Removed the dead-man-switch claim, the 2-relay control model, and the 1024x600 geometry. |

### Revision History:
#### Name: Bhoumik Acharya

#### Version: 1.1

#### Last Edited Date: 07/08/2026

#### Description: v1.0 was the baseline — created the UI, interlinked pages and communication with the nodes and the gateway. v1.1 is a documentation-only reconciliation: v1.0 described a 2-relay control model, a 1024x600 panel and a fail-safe timer that no longer exist (and in the last case never did). No UI work in this revision.

### 1. System Overview & Architectural Paradigm

#### 1.1 Objective

    The Metro Signage HMI is a Python/Tkinter-based graphical interface designed to run on a Raspberry Pi with a Waveshare 10.1" DSI display at 1280x800. It provides real-time monitoring and manual override capabilities for up to 100 ESP32-based edge nodes.

> **Panel size corrected 2026-08-07.** v1.0 of this document specified **1024x600**. The shipping
> panel was confirmed as the Waveshare 10.1" DSI (**1280x800**) on 2026-07-20 — see `hmi_contract.md`
> §1. `HMI.py` was rescaled to match on 2026-07-27 (`SCREEN_W, SCREEN_H = 1280, 800`, dashboard
> columns converted to `relx`, `nodes_per_page` raised 8 → **11** to use the extra 200 px of height).
> The full visual redesign is still open — that round rescaled the layout, it did not rebuild it.

#### 1.2 The "Decoupled Viewer" Architecture (The Why)

    Initially, the HMI was conceptualized as a monolith (hosting the UI, Modbus Server, and MQTT Logic in one script). However, for V1, a strict Decoupled Architecture was adopted.

    The Reason: If the HMI UI crashes, freezes, or is restarted by an operator, the physical SCADA network must not go down.

    The Implementation: The Modbus Server and MUX bridging logic run in a separate, headless, 24/7 background service. The Tkinter HMI script acts purely as a "Dumb Terminal/Client." It connects to 127.0.0.1:502 (Modbus) and 127.0.0.1:1883 (MQTT) simply to view and push commands to the true master service.

### 2. Design System & UI/UX Constraints

#### 2.1 The "Industrial Precision" Theme

    The UI was built to be highly visible in varying, potentially low-light industrial environments.

    Backgrounds: Deep charcoal and obsidian (#131313, #1c1b1b) to reduce eye strain.

    Active States: High-luminance Cyan (#00daf3) for immediate recognition of active operator modes/selected elements. Safety Orange marks technician modes (3-6), which latch and must be unmistakable on screen.

    Status Colors: Standardized semantic colors: Lime Green (#6cec00) for OK, Safety Orange (#ff8a00) for degraded/overrides, Red (#ffb4ab) for critical faults.

> **The 4-state current values are not `OK`/`FAIL`.** They are `ON` / `OFF` / `FAIL_OPEN` /
> `FAIL_SHORT`, and **`OFF` is a healthy state** — intended off, no current flowing. It must render
> dim/neutral, never as a fault. v1.0's `HMI.py` compared against `"OK"`, so every current row showed
> an error colour permanently; fixed 2026-07-27 via `four_state_color()`. Only `FAIL_*` is red. See
> `hmi_contract.md` §4.

#### 2.2 Touch Ergonomics (The Why)

    Operating on a 10.1-inch touchscreen, often with gloved hands, necessitates specific UI choices:

    No OS Keyboards: Standard Linux virtual keyboards break fullscreen Kiosk applications. A custom VirtualKeyboard class was built from scratch directly into Tkinter to ensure modal, safe data entry.

    Large Touch Targets: Buttons do not use internal padding (ipadx). Instead, they use explicit character width/height definitions (e.g., width=14, height=2) to guarantee a massive bounding box that will not clip text on Linux window managers.

### 3. State Management & Threading Model

    Tkinter is strictly single-threaded. If it waits for a network packet, the screen freezes. To solve this, V1 uses a three-pillar asynchronous approach.

#### 3.1 The NODE_DATA Dictionary

    A global dictionary acts as the central source of truth. It is guarded by a threading.Lock() to prevent race conditions between reading and writing.

#### 3.2 The MQTT Background Thread (The Writer)

    The paho-mqtt library runs a background loop_start(). When it receives a payload from an ESP32 (e.g., a current-state change), it briefly grabs the thread lock, updates the specific node in the NODE_DATA dictionary, and releases the lock.

#### 3.3 The sync_loop Heartbeat (The Reader)

    The Tkinter main thread runs a .after(500, self.sync_loop) function. Twice a second, the UI grabs the thread lock, takes a snapshot of NODE_DATA, and updates text labels/canvas colors on the currently active screen.

    The Reason: This guarantees a buttery-smooth 60FPS user interface, as the UI never waits on a network socket; it only ever reads from local memory.

### 4. Protocol & Register Mapping

#### 4.1 MQTT Integration (ESP32 to HMI)

    The HMI subscribes to metro/signage/register/+/+. The topic address is the holding-register number (40001+i), NOT the node index.

**Incoming** — parsed via `METRIC_KEYS` in `HMI.py`:

| Topic | Displayed as |
| --- | --- |
| `status` | connection LED + IP (`split(":", 1)`, matching the gateway's parse) |
| `power` | Main Power row |
| `current1` / `current2` | LHS / RHS Arrows |
| `current3` / `current4` | **Static Zone 1 / Static Zone 2** |
| `state` | **actual executing mode** — drives the commanded-vs-actual panel |
| `battery_pct` | permanently greyed `N/A` / `NOT FITTED` — firmware never publishes it |

> **Corrected 2026-08-07.** v1.0 listed only `status`, `power`, `current1`, `current2` and
> `battery_pct`. The HMI was blind to both static zones and to `state`; it was fixed on 2026-07-27
> (`pi_agent.md` §12 item 3) but this document was not updated with it. Note the inversion on the
> last row: `battery_pct` was the one metric v1.0 said it *did* read, and it is the one the firmware
> never sends.

**Outgoing** — the BROADCAST PING button publishes `"PING"` to `metro/signage/scan`. Every node responds by re-publishing its **full telemetry set**, which lets the gateway and the HMI rebuild state without waiting for a value to change.

> ### ⚠️ Corrected 2026-08-07 — the PING does NOT reset a fail-safe timer
>
> v1.0 of this document stated the PING *"resets the 5-minute hardware fail-safes on all ESP32s."*
> **No such timer has ever existed** in any shipped firmware. This was established on 2026-07-20 and
> corrected in `Gateway_doc.md` §6, `esp32_contract.md` §6, `pi_agent.md` §12 item 5, and in `HMI.py`
> itself — whose panel has read `TELEMETRY REFRESH` since 2026-07-27. **This document was the last
> place in the repository still carrying the claim.**
>
> What actually happens on network loss: the node **holds its last command** and keeps animating.
> No forced Solid-ON, no forced OFF. The ESP32's `esp_task_wdt` (15 s, panic reboot) is internal
> hang recovery only — it does not react to network loss.
>
> **Do not describe the PING as a safety mechanism to an operator.** Pressing it more often does not
> make the system safer, and an operator who believes signs will fail safe on link loss will make
> worse decisions during an outage than one who knows they hold their last state.
>
> One real caveat, from firmware v3.0.0: hold-last-command survives a **link outage** but not a
> **reboot**. A node that reboots while the network is down comes up dark and stays dark until the
> broker delivers the retained `value`. NVS persistence is an open firmware gap — `pi_agent.md` §12
> item 10.

#### 4.2 Modbus TCP Integration (HMI to SCADA/Gateway)

    The HMI acts as a Modbus Client to the local Gateway daemon.

    MUX Toggle (Register 43001 / Address 3000): Writing 1 engages HMI Manual Mode. Writing 0 reverts to SCADA Auto Mode.

**Mode Commands (Registers 41001+ / Address 1000+):** when in Manual mode, the HMI writes the mode integer to the specific node's offset in the 41000 block.

| Value | Meaning | Access |
| --- | --- | --- |
| `0` / `1` / `2` | Arrows OFF / LHS chase / RHS chase | Operator |
| `3` | Both chase | Technician |
| `4` / `5` / `6` | Solid ON left / right / all | Technician |
| `10000`–`11023` | Raw bitmask (`10000 + mask`) | Technician — **display only, HMI cannot send one** |

> **⚠️ Corrected 2026-08-07 — the 2-relay control model is obsolete.** v1.0 described the HMI as
> calculating *"a binary state (0 to 3) based on the Relay 1 and Relay 2 buttons."* **The hardware has
> no relays.** It has 10 logic-level N-channel MOSFETs driven over I2C by an MCP23017, and the command
> space is `0`–`6` plus the `10000+` bitmask block. `HMI.py`'s control panel was replaced with 7 mode
> buttons on 2026-07-27.
>
> **Commands drive the moving arrows only.** Static Zones 1 and 2 are hardwired always-on — current
> monitored, never switched. The HMI must not offer any control implying otherwise.
>
> **Technician modes latch.** There is no auto-revert timer; a sign left in Solid-ON stays lit until
> someone changes it. The PIN gate for modes 3–6 is **not yet built** — they are unrestricted in the
> current developer build (`hmi_contract.md` §5.2).

**Live Polling:** the UI polls the Modbus registers every 500 ms so it reflects what is actually executing, even when locked in SCADA mode. This includes **`43001` itself** — the register is the source of truth for control-source state, not a local instance variable. v1.0's `HMI.py` tracked the MUX in `self.mux_manual` and never read it back, so the HMI and the gateway silently disagreed after a gateway restart or any PLC-side MUX change.

> **The MUX toggle is global.** It writes the single register `43001`, which governs all 100 nodes, but v1.0 presented it inside per-node Node Detail — so one node's toggle silently changed the control source for every node. Relabelled `CONTROL SOURCE · GLOBAL (43001)` on 2026-07-27 as a minimum honest fix; where it actually belongs is still an open design question (`hmi_contract.md` §8).
>
> There is **no physical local/remote switch** anywhere in this system. The on-screen toggle is the only control-source control, and any lockout message must not tell an operator to go find a hardware switch.

### 5. Deployment Considerations (Kiosk Mode)

    For physical deployment, the V1 application is designed to be hardened:

    self.attributes('-fullscreen', True) must be enabled in production to hide the Linux taskbar.

    The Raspberry Pi OS must have screen-blanking (xset s noblank) disabled.

    The HMI.py script should be triggered via a systemd service upon boot to guarantee auto-recovery in the event of a station power loss.

> **Filename corrected 2026-08-07:** the script is `HMI.py`. No `hmi_app.py` exists in this repository.
>
> **Keep the HMI and the Gateway as two separate systemd units.** The entire decoupled architecture
> in §1.2 exists so that an HMI crash cannot take down the SCADA-facing Modbus server. Never merge
> them into one unit.
>
> **`-fullscreen` is currently commented out** in `HMI.py` (line 271) for developer convenience. It
> must be re-enabled for production, per §5 above.

### 6. Current Build Status

`HMI.py` had 14 defects corrected on 2026-07-27 — paho 2.x compatibility, reconnect handling, the 4-state colour mapping, static-zone ingest, the 2-relay panel, MUX readback, and the 1280x800 rescale. The full list is the regression table in `hmi_contract.md` §5.

> **⚠️ That build has not been executed.** No Python toolchain was available on the authoring machine,
> so the file has not been run against hardware or a live gateway. **Do not treat the HMI as working
> until the test plan in `hmi_contract.md` §5.1 has been run on the Pi** — it exercises each fix
> against `bench/simulate_node.py` and `bench/scada_probe.py`, neither of which needs an ESP32.

Deliberately **not** in the current round: the technician PIN gate, the raw bitmask send panel, dashboard fault/technician-mode counters, and the full visual redesign.