# HMI Contract — as required by Gateway v2.0.0

**Written:** 2026-07-20, after `Pi.py` v2.0.0 was finalized. The HMI is the **last** build task, after the ESP32 firmware.
**Purpose:** capture every constraint the finished gateway imposes on the HMI, so the screen design starts from facts rather than from the current (outdated) `HMI.py`.
**Companion docs:** `../pi_agent.md`, `../Gateway/Gateway_doc.md`, `../../ESP32/esp32_contract.md`.

---

## 1. Platform

| Item | Value |
| --- | --- |
| Display | **Waveshare 10.1" DSI LCD, 1280x800 — CONFIRMED** |
| Python | 3.13.5, venv at `~/metro-gw/venv` (`--system-site-packages`) |
| Toolkit | Tkinter (`python3-tk` via apt — **cannot** be pip-installed) |
| MQTT | `paho-mqtt==2.1.0`, `CallbackAPIVersion.VERSION2` |
| Modbus | `pymodbus==3.11.3` (client side) |
| Connects to | `127.0.0.1` — Modbus 502, MQTT 1883 |
| systemd | **separate unit from the Gateway.** An HMI crash must never take down the SCADA-facing Modbus server. Never merge them. |

The existing `HMI.py` is laid out entirely with absolute `.place()` coordinates sized for 1024x600. The 10.1" panel is 1280x800 — **every coordinate in the file is wrong** and the layout needs rebuilding, not nudging. The extra 200 px of height also means `nodes_per_page = 8` can grow.

---

## 2. Register Map the HMI Touches

| Purpose | PLC register | Datastore index | HMI access |
| --- | --- | --- | --- |
| Active Execution Zone | `40001+i` | `0 + i` | **read** (what is actually executing) |
| HMI Manual Buffer | `41001+i` | `1000 + i` | **write** (commands) |
| SCADA Auto Buffer | `42001+i` | `2000 + i` | do not touch — the PLC owns it |
| MUX Control Flag | `43001` | `3000` | read + write |
| Diagnostic Block | `45001 + i*8` | `5000 + i*8` | read (optional — MQTT is richer) |

Datastore index == wire address == PLC register − 40001. **There is no off-by-one.** Verified against a real pymodbus server. Do not "fix" it.

**MUX:** `0` = SCADA/Auto, `1` = HMI/Manual. Writing `1` locks out the PLC and the gateway starts copying `41001+` instead of `42001+`.

---

## 3. Command Encoding

Commands drive the **moving arrows only**. Static Zones 1 and 2 are hardwired always-on and can never be switched — the HMI must not offer any control implying otherwise.

**Operator modes — always available:**

| Value | Label |
| --- | --- |
| `0` | Arrows OFF |
| `1` | LHS chase |
| `2` | RHS chase |

**Technician modes — behind the PIN:**

| Value | Label |
| --- | --- |
| `3` | Both chase |
| `4` | Solid ON — left |
| `5` | Solid ON — right |
| `6` | Solid ON — all |
| `10000`–`11023` | Raw bitmask (`10000 + mask`; low 5 bits Port A, high 5 Port B) |

**Technician modes latch** — no auto-revert. A sign left solid-ON stays lit until changed. The HMI must therefore make a non-operator mode **unmistakable** on screen: the mode indicator should read as an alarm state, and ideally the Dashboard should surface a count of nodes currently in a technician mode so nobody has to page through 100 rows to find one.

**The PIN gates this HMI only.** The gateway publishes verbatim and the PLC writes `42001+` freely, so technician modes can also arrive from SCADA. The PIN prevents local operator misuse; it is **not** a security boundary and must not be described as one.

---

## 4. Telemetry — MQTT, not Modbus

Subscribe to `metro/signage/register/+/+` on `127.0.0.1:1883`. Topic address is the **holding-register number** (`40001+i`), not the node index.

| Metric | Payload | HMI display |
| --- | --- | --- |
| `status` | `ONLINE:<ip>` / `OFFLINE` | connection LED + IP |
| `power` | `OK` / `FAIL` | Main Power row |
| `current1` | 4-state | **LHS Arrows** |
| `current2` | 4-state | **RHS Arrows** |
| `current3` | 4-state | **Static Zone 1** ← missing today |
| `current4` | 4-state | **Static Zone 2** ← missing today |
| `battery_pct` | int | greyed "N/A", see §6 |
| `state` | int | **actual executing mode** — see below |

### Commanded vs Actual — a required HMI display

`state` is what the node reports **actually executing**; the Modbus register is what it was **commanded**. When they differ, the node is running something else and the operator must be told — a sign in this state shows the wrong thing while the HMI claims it is correct.

Node Detail must show both, and flag the mismatch prominently. The Dashboard should surface a count of diverging nodes, for the same reason it should count technician-mode nodes: nobody will find one by paging through 100 rows.

Treat `65535` / missing as "unknown", not as a mismatch — an offline node has no actual state to report and would otherwise flag constantly.

**4-state values are `ON` / `OFF` / `FAIL_OPEN` / `FAIL_SHORT`** — never `OK`/`FAIL`. The current `HMI.py` compares against `"OK"`, so **every current sensor renders as an error colour regardless of actual state.** Suggested mapping:

| State | Colour | Meaning |
| --- | --- | --- |
| `ON` | success | intended on, current flowing |
| `OFF` | dim/neutral | intended off, no current — normal |
| `FAIL_OPEN` | error | intended on, 0 A — burnt LED / broken wire / blown MOSFET |
| `FAIL_SHORT` | error | intended off, current flowing — welded MOSFET / short |
| `---` | dim | unknown / node offline |

`OFF` is a **healthy** state and must not look like a fault. Static zones can only ever be `ON` or `FAIL_OPEN` (§4 of the ESP32 contract).

---

## 5. Bugs in the Current `HMI.py` — fix during the rework

| # | Bug | Impact |
| --- | --- | --- |
| 1 | `mqtt.Client()` with no args | **Hard-fails on paho 2.x.** First positional is now `CallbackAPIVersion` |
| 2 | `on_message` only, no `on_connect`/`on_disconnect` | Subscribe happens once at startup; a broker reconnect silently loses all subscriptions |
| 3 | Subscribe called before `loop_start()` | Fragile ordering — subscribe from `on_connect` instead |
| 4 | Compares current state to `"OK"` | Every current row shows an error colour permanently |
| 5 | No `current3` / `current4` ingest | Blind to both static zones |
| 6 | 2-relay model (`RELAY 1/2`, cmd 0–3) | Entire control panel is obsolete |
| 7 | `self.mux_manual` is local instance state, never read back from `43001` | HMI and gateway **silently disagree** after a gateway restart or a PLC-side MUX change. Poll 43001 in the sync loop |
| 8 | `read_holding_registers(reg, 1)` positional count | **Verify against pymodbus 3.11.3** — signature is `(address, count=..., device_id=...)`. Positional may break |
| 9 | Modbus client never reconnects on failure | One dropped socket and the panel is dead until restart |
| 10 | `except Exception: pass` in `on_message` and `refresh_data` | Silent failure everywhere; log instead |
| 11 | Ping panel says "reset 5-minute fail-safe timers" | **No such mechanism exists.** Reword: it makes nodes re-publish telemetry |
| 12 | `NODE_DATA[idx]["ip"] = payload.split(":")[1]` | Fine for IPv4; use `split(":", 1)` for consistency with the gateway |
| 13 | Battery gauge wired to a live value | Battery is stubbed — see §6 |
| 14 | `.place()` coordinates throughout | Sized for 1024x600, panel is 1280x800 |

---

## 6. Battery — greyed, not hidden

Battery is stubbed in firmware and is **the next task after this HMI**. Decision: keep the gauge visible but permanently greyed, labelled `N/A` / `NOT FITTED`, so the layout slot survives for when it lands.

Diagnostic register `+4` carries `65535` = unknown. If the HMI ever reads battery from Modbus rather than MQTT, it must treat `65535` as "no data" and **never** render it as `65535%` or coerce it to `0%`.

---

## 7. Screen Inventory (starting point for design)

1. **Dashboard** — 100-node list, paginated, search by node ID / IP. Per row: node ID, IP, connection LED. Worth adding: a fault indicator so a `FAIL_OPEN` doesn't require opening each node, and a count of nodes in technician modes (§3).
2. **Node Detail** — telemetry (connection, power, LHS, RHS, Static Z1, Static Z2, battery-greyed) + operator mode control (0/1/2) + MUX toggle.
3. **Diagnostics** — gateway/broker health, online/offline counts, broadcast PING.
4. **Technician panel (NEW, PIN-gated)** — modes 3–6 and the raw bitmask (10 per-MOSFET toggles). Physically separate from operator control. Needs a clear exit path back to a safe operator mode, since these latch.

---

## 8. Open Questions for the Design Session

* PIN storage and lifetime — hardcoded, config file, or hashed? Does it time out, or is one unlock good for the whole session?
* Is the MUX toggle **global** (it writes a single register, `43001`) but presented per-node in Node Detail? That is misleading as-is — one node's toggle silently changes every node's control source.
* Does the technician panel act on one node or broadcast to many?
* Does the Dashboard need a fault-first sort/filter, given 100 nodes and 8 per page?
