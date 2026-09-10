# Metro Signage HMI — Technical Documentation

Reference for `hmi_dark.py` and `hmi_light.py`: the redesigned Tkinter touch
panel for the Metro Signage system. Covers architecture, the data model, the
networking layer, the screen structure, and how to extend and maintain the code.

Companion documents: `SETUP.md` (this folder), `../hmi_contract.md`,
`../HMI_doc.md`, `../../pi_agent.md`, `../../Gateway/Pi.py`.

---

## 1. Purpose and role

The HMI is a **local touch panel** for a metro-signage SCADA system. It monitors
and manually overrides up to **100 ESP32 nodes**, each driving a signboard with
a left moving arrow, a right moving arrow, and two always-on static zones.

It is deliberately a **dumb terminal / client**:

- It never talks to the nodes directly.
- The master is the headless gateway daemon `Pi.py`, which owns the Modbus
  server and bridges Modbus ⇄ MQTT ⇄ the nodes.
- An HMI crash must never take down the SCADA-facing Modbus server — the two are
  separate processes and must stay that way.

This is a **UI-only rebuild** of the original `../HMI.py`. The networking,
threading, and register/topic contract are inherited **unchanged**; only the
presentation layer was redesigned into five screens.

---

## 2. Files

| File | Contents |
| --- | --- |
| `hmi_dark.py` | Full application, **dark** theme (cyan accent) — the primary build |
| `hmi_light.py` | Full application, **light** theme — identical except the `COLORS` palette |
| `SETUP.md` | Deployment / bring-up guide |
| `TECHNICAL.md` | This document |

**The two `.py` files differ only in the `COLORS` dictionary and the theme
words in the docstring.** All logic, layout, and behaviour are identical. When
you change anything other than colours, change **both files** (see §11).

Each file is standalone — it imports only `tkinter`, `paho.mqtt.client`, and
`pymodbus`. There is no shared module and no dependency on `../HMI.py`.

---

## 3. Architecture overview

```
   ┌────────────────────────── Raspberry Pi ──────────────────────────┐
   │                                                                   │
   │   ESP32 nodes ──MQTT──►  mosquitto  ◄──MQTT──  Pi.py (gateway)     │
   │        ▲                    ▲                     │  ▲             │
   │        │ commands           │ telemetry          │  │ Modbus 502  │
   │        └──MQTT── Pi.py ◄─────┘                    │  │             │
   │                                                   ▼  │             │
   │                            hmi_*.py  ── subscribes MQTT (telemetry)│
   │                                     ── Modbus client (control)     │
   └───────────────────────────────────────────────────────────────────┘
```

Two independent data paths reach the HMI:

- **Telemetry (read):** the HMI subscribes to `metro/signage/register/+/+` on
  the broker and stores every message in `NODE_DATA`. This drives the Fleet
  grid, Signage statuses, Node Detail telemetry rows, and the alarms.
- **Control (read + write):** the HMI is a Modbus **client** to `Pi.py`'s server.
  It reads the MUX flag and commanded/actual mode registers, and writes commands
  and MUX changes.

---

## 4. Configuration constants

At the top of each file:

```python
MODBUS_IP   = '127.0.0.1'   ; MODBUS_PORT = 502
MQTT_BROKER = '127.0.0.1'   ; MQTT_PORT   = 1883
REG_ACTUAL_BASE = 0     # 40001+  Active Execution Zone (read)
REG_HMI_BASE    = 1000  # 41001+  HMI Manual Buffer (write)
REG_FLAG_MUX    = 3000  # 43001   MUX flag (read + write)
MAX_NODES   = 100
SCREEN_W, SCREEN_H = 1280, 800
DEMO_MODE   = "--demo" in sys.argv    # LIVE by default; --demo seeds a mock fleet
FONT_SANS   = "DejaVu Sans"
FONT_MONO   = "DejaVu Sans Mono"
```

- **`127.0.0.1`** is correct because the HMI runs on the Pi. Do not point it at a
  node — nodes are reached only via the gateway.
- **`DEMO_MODE`** is a command-line flag, not a source edit. Live is the default.
- **Fonts** default to DejaVu (present on Raspberry Pi OS); Tk falls back
  gracefully elsewhere.

---

## 5. Register map the HMI touches

Datastore index == Modbus wire address == PLC register − 40001. **There is no
off-by-one.** (Verified against a real pymodbus 3.11.3 server; see
`../../pi_agent.md §3`.)

| Purpose | PLC register | Wire address | HMI access | Used by |
| --- | --- | --- | --- | --- |
| Active Execution Zone | `40001+i` | `0 + i` | read | Node Detail *commanded* (in AUTO) |
| HMI Manual Buffer | `41001+i` | `1000 + i` | **write** | Mode buttons, per-LED apply |
| SCADA Auto Buffer | `42001+i` | `2000 + i` | never touch | (PLC owns it) |
| MUX Control Flag | `43001` | `3000` | read + write | Control-source toggle |

`pymodbus` 3.11.3 requires `count` as a **keyword** argument. All Modbus access
is centralised in `HMIApp.mb_read` / `mb_write` / `mb_ready`, which also handle
reconnection on a dropped socket.

**MUX:** `0` = SCADA/Auto, `1` = HMI/Manual. Writing `1` locks out the PLC; the
gateway then copies `41001+` (HMI buffer) into the active zone instead of
`42001+`.

---

## 6. MQTT telemetry

Subscribe: `metro/signage/register/+/+`. Topic address is the **holding-register
number** (`40001+i`), not the node index.

| Metric | Payload | Stored key | Display |
| --- | --- | --- | --- |
| `status` | `ONLINE:<ip>` / `OFFLINE` | `status`, `ip` | connection + IP |
| `power` | `OK` / `FAIL` | `power` | Main Power |
| `current1` | 4-state | `c1` | LHS Arrows |
| `current2` | 4-state | `c2` | RHS Arrows |
| `current3` | 4-state | `c3` | Static Zone 1 |
| `current4` | 4-state | `c4` | Static Zone 2 |
| `state` | int | `state` | actual executing mode |
| `battery_pct` | int | `batt` | greyed "N/A" |

The **4-state** values are `ON` / `OFF` / `FAIL_OPEN` / `FAIL_SHORT`. `OFF` is a
**healthy** state (intended off, no current) and is rendered neutral/dim — never
as a fault. Only `FAIL_*` is red.

Ping (Diagnostics → Broadcast Ping) publishes `metro/signage/scan = "PING"`.

The MQTT listener runs on paho's background thread. `on_connect` (re)subscribes
on every reconnect, so a broker restart is survived automatically. Messages
update `NODE_DATA` under `data_lock`.

---

## 7. Data model and derived state

### 7.1 `NODE_DATA` (module global)

A dict of 100 entries keyed by node index `1…100`, each holding `id`
(`ND-8091…`), `reg` (`40001…`), `ip`, `status`, `power`, `c1`–`c4`, `state`,
`batt`. All reads/writes are guarded by `data_lock` because the MQTT thread and
the Tk main thread both touch it.

### 7.2 Command encoding

| Value | Mode | Tier |
| --- | --- | --- |
| `0` / `1` / `2` | Arrows off / LHS chase / RHS chase | operator |
| `3` | Both chase | technician (latches) |
| `4` / `5` / `6` | Solid on — left / right / all | technician (latches) |
| `10000`–`11023` | Raw per-LED bitmask (`10000 + mask`) | technician |

`mode_label(value)` maps any legal value to a label, including the bitmask
range. Technician modes **latch** — no auto-revert — so they are styled as a
caution state and surfaced in the alarms.

### 7.3 Health derivation (single source of truth)

`node_health(nd) -> "ok" | "warn" | "fault" | "off"` derives a node's roll-up
health from telemetry only (so it works fleet-wide without per-node register
reads):

- `off` — not ONLINE
- `fault` — power FAIL, any current `FAIL_OPEN`/`FAIL_SHORT`, or state
  "cannot verify outputs" (`65535`)
- `warn` — actual state is a technician mode (latched)
- `ok` — otherwise

`counts()` tallies the fleet; `build_alarms()` produces the sorted alarm list
(fault → warn → offline) with a human cause per entry. Both are pure reads over
`NODE_DATA`.

`parse_actual_state(raw)` interprets the `state` payload into
`(text, colour_key, numeric_or_None)`; a non-numeric or `65535` payload becomes
"CANNOT VERIFY OUTPUTS" (a hardware fault, not a missing reading).

`four_state_health(value)` maps a single 4-state string to a colour key,
enforcing the "OFF is healthy" rule.

---

## 8. The design system

### 8.1 Palette

Colours live in one `COLORS` dict. **This is the only thing that differs between
`hmi_dark.py` and `hmi_light.py`.** Both use a cyan accent and the same
green / amber / red / grey status language.

Tkinter has **no alpha channel**, so the translucent tints from the mockups are
pre-blended into solid hex values (e.g. `ok_bg`, `warn_bg`, `accent_bg`, and the
matching `_bd` border tints).

### 8.2 Flat by necessity

Tkinter cannot render rounded corners, drop shadows, gradients, glows, or
translucency. The UI is therefore a **flat-rectangle** interpretation of the
design: panels are `tk.Frame`s with a 1-px `highlightthickness` hairline border;
status dots and LED lamps are drawn on small `tk.Canvas`es. Two helpers keep
this consistent:

- `card(parent, **kw)` — a bordered panel frame.
- `dot(parent, color, size)` / `set_dot(canvas, color)` — a status LED and its
  recolour.

### 8.3 Typography

Four-ish roles via `tkfont.Font` on `HMIApp`: `f_h1`, `f_h2`, `f_body`,
`f_small`, `f_label`, plus monospace `f_mono` / `f_mono_bold` / `f_mono_big` for
all machine data (node IDs, IPs, registers, counts, raw values).

---

## 9. Screens

`HMIApp` is the shell: it builds the networking clients, holds the four page
frames in a `frames` dict, stacks them with `place(relwidth=1, relheight=1)`,
and raises the active one with `show_frame(name, context)`. A shared `TopBar`
provides brand, nav, live clock, and the online-count pill.

| Class | Screen | Notes |
| --- | --- | --- |
| `FleetOverviewFrame` | **Fleet Overview** | 10×10 status grid (tiles cached and recoloured in place), online/health counts, right-hand alarms rail. Tiles open Node Detail. |
| `SignageListFrame` | **Signage List** | Searchable, paginated node table (`PER_PAGE = 12`); the **global** control-source toggle (writes `43001`); `VirtualKeyboard` for touch search. |
| `NodeDetailFrame` | **Node Detail** | Telemetry (4-state colours, greyed battery) + control (control-source band, commanded-vs-actual with mismatch flag, operator/technician mode buttons, Per-LED button). Mode buttons lock when the source is REMOTE. |
| `PerLedDialog` | **Per-LED Control** | Modal `Toplevel`; ten clickable LED lamps (left ×5, right ×5), All-On / All-Off, live raw value; Apply writes `10000 + mask` to `41001+offset`. |
| `DiagnosticsFrame` | **Diagnostics** | Broker/Modbus/service health, online/offline + warning/fault counts, Broadcast Ping. |

The Node Detail **commanded-vs-actual** panel is the required safety display:
*commanded* comes from Modbus, *actual* from the MQTT `state` metric. Divergence
raises **⚠ MISMATCH**; an unknown/offline actual is treated as unknown, not a
mismatch.

---

## 10. Runtime model

### 10.1 Threads

- **Tk main thread** — all UI. Never blocks on the network.
- **paho MQTT thread** — `loop_start()`; updates `NODE_DATA` under `data_lock`.
- Modbus reads/writes happen inline on the Tk thread inside the 500 ms tick;
  they are cheap and centralised in `mb_*`, which reconnect silently.

### 10.2 The 500 ms sync loop

`HMIApp.sync_loop` runs every 500 ms via `after()`. It calls `refresh_data()` on
**only the active frame**, so off-screen frames cost nothing. Each frame's
`refresh_data()`:

- updates the top-bar clock and online pill;
- re-derives health/counts/alarms and recolours cached widgets in place (no
  teardown/rebuild, so touch targets don't flicker);
- Node Detail additionally reads the MUX flag and commanded register back every
  tick, so a gateway restart or a PLC-side MUX change corrects the UI within
  500 ms rather than leaving it silently disagreeing.

### 10.3 Write paths

| User action | Method | Modbus write |
| --- | --- | --- |
| Control source toggle | `SignageListFrame._set_mux` | `43001 = 0/1` |
| Mode button | `NodeDetailFrame._send_mode` | `41001 + offset = value` |
| Per-LED Apply | `PerLedDialog._apply` | `41001 + offset = 10000 + mask` |

All writes are ignored by the UI's own state until the register read-back
confirms them, so a failed write self-corrects instead of showing a false state.

---

## 11. Maintaining the two files

Because `hmi_dark.py` and `hmi_light.py` differ **only** in the `COLORS`
dictionary:

- **Colour-only change:** edit the one file's palette.
- **Any other change (logic, layout, a new widget):** apply the identical edit
  to **both** files, or regenerate the light file from the dark one by swapping
  the palette block.

The light palette block (drop-in replacement for the dark `COLORS`):

```python
COLORS = {
    "bg": "#eef1f5", "panel": "#ffffff", "elev": "#f4f7fa", "border": "#dce1e8",
    "text": "#182029", "dim": "#5c6672", "faint": "#93a0ac",
    "accent": "#0a8fa6", "accent_bg": "#e2f1f4", "accent_bd": "#a9d7de", "on_accent": "#ffffff",
    "ok": "#1f9d52", "ok_bg": "#e6f4ec", "ok_bd": "#b7ddc6",
    "warn": "#bf7c08", "warn_bg": "#f7efdd", "warn_bd": "#e6cf9f",
    "fault": "#d83a34", "fault_bg": "#fbe9e8", "fault_bd": "#f0c3c0",
    "off": "#8b98a6", "off_bg": "#eef1f4", "off_bd": "#d3dae1",
    "zebra": "#f5f7f9",
}
```

> If maintaining two copies becomes a burden, the recommended refactor is a
> shared `hmi_core.py` module plus two thin theme entry points that set `COLORS`
> and launch the app. This was deliberately not done yet to keep each file
> standalone and runnable on its own.

---

## 12. Known limitations / not yet done

- **Not hardware-tested.** The code is verified for construction, logic, the
  bitmask math, and the register/topic contract, but has not been run against a
  live gateway or real nodes. Expect first-integration debugging.
- **Flat aesthetic.** As above, Tkinter cannot reproduce the rounded/glowing
  look of the mockups; this is the closest flat interpretation.
- **No technician PIN gate.** Technician modes (3–6) and the per-LED panel are
  unrestricted in this build. A PIN gate is specified in `../hmi_contract.md §3`
  and deferred.
- **Alarms rail shows the top 9** with a "+N more" note; it does not scroll yet.
- **Battery** is permanently greyed (`N/A / NOT FITTED`) — stubbed in firmware.

---

## 13. Quick reference

```
Run live:        python3 hmi_dark.py
Run mock fleet:  python3 hmi_dark.py --demo
Fullscreen:      uncomment  self.attributes('-fullscreen', True)  in HMIApp
Exit fullscreen: Esc
Needs running:   mosquitto  +  Gateway/Pi.py  +  this HMI  (all on the Pi)
Reads:           MQTT 127.0.0.1:1883 (telemetry) · Modbus 127.0.0.1:502 (control)
Writes:          41001+offset (commands) · 43001 (control source)
```
