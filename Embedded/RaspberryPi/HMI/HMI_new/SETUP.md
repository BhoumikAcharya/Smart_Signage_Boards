# Metro Signage HMI — Setup Guide

Step-by-step setup for running the redesigned touch-panel HMI (`hmi_dark.py` /
`hmi_light.py`) on the Raspberry Pi, both for a bench demo and against real
ESP32 nodes.

---

## 1. What this is (and what it is not)

The HMI is a **dumb terminal**. It only *displays* the system and *sends
commands* — it never talks to the signage nodes directly. The master is the
gateway daemon (`Pi.py`). The HMI reads from two places on the Pi:

| What you see | Source | Protocol | Address |
| --- | --- | --- | --- |
| Grid colours, online/offline, IP, power, current sensors, actual mode, alarms | ESP32 nodes → broker | **MQTT** | `127.0.0.1:1883` |
| Control-source state, commanded mode; where mode buttons / MUX toggle write | gateway register table | **Modbus TCP** | `127.0.0.1:502` |

Because the HMI connects to `127.0.0.1`, **it must run on the Pi** — the same
host as the broker and the gateway.

```
  4 ESP32 PCBs ──┐
                 ├── Ethernet switch ── Raspberry Pi (192.168.1.10)
   (each on      │                        │
    192.168.1.x) │                        ├─ mosquitto broker  :1883
                 ┘                        ├─ Pi.py  (Modbus server :502  +  MQTT bridge)  ← master
                                          └─ hmi_dark.py  (Tkinter kiosk, reads 127.0.0.1)
```

**Three processes must run on the Pi:** `mosquitto`, `Pi.py`, and the HMI.

---

## 2. Prerequisites

| Item | Value |
| --- | --- |
| Board | Raspberry Pi 5, Raspberry Pi OS (Debian 13) |
| Display | Waveshare 10.1" DSI, 1280 × 800 |
| Python | 3.13+ in the venv at `~/metro-gw/venv` (created with `--system-site-packages`) |
| Packages | `paho-mqtt==2.1.0`, `pymodbus==3.11.3` (pip, in venv) · `python3-tk` (apt, system) |
| Broker | `mosquitto` 2.0.x |

`python3-tk` **cannot** be pip-installed — it comes from apt, which is why the
venv was created with `--system-site-packages`.

Confirm the dependencies resolve:

```bash
~/metro-gw/venv/bin/python -c "import tkinter, paho.mqtt.client, pymodbus; print('deps OK')"
```

---

## 3. One-time preparation

### 3.1 Place the file

`hmi_dark.py` is fully self-contained (it imports only `tkinter`, `paho`, and
`pymodbus` — no local modules), so it can live anywhere on the Pi, e.g.
`~/metro-gw/`. Copy `hmi_dark.py` (and/or `hmi_light.py`) across with `scp`,
`git`, or a USB stick.

### 3.2 Let the broker accept the PCBs

By default mosquitto 2.0 only listens on loopback, so the nodes cannot reach it.
Add a LAN listener — `/etc/mosquitto/conf.d/lan.conf`:

```
listener 1883 0.0.0.0
allow_anonymous true
```

Match `allow_anonymous` (or add `password_file`) to whatever the node firmware
uses. Then:

```bash
sudo systemctl restart mosquitto
systemctl is-active mosquitto      # -> active
```

### 3.3 Fix the Pi's LAN IP

Give the Pi's ethernet port (into the switch) the static IP **`192.168.1.10`** —
that is the broker address the gateway and firmware expect. A bare switch has
no DHCP, so also give **each PCB a static IP** on `192.168.1.x`.

> If you must use a different Pi IP, update the node firmware's broker address
> to match; the HMI itself always uses `127.0.0.1` and does not change.

### 3.4 Configure the nodes (firmware side)

Each node is identified purely by the **register number** in its MQTT topic.
Flash your four PCBs so they:

- publish to broker `192.168.1.10:1883`, and
- own a distinct register — `40001`, `40002`, `40003`, `40004`.

They will then appear as tiles **01–04 (ND-8091 … ND-8094)**. The remaining 96
registers stay OFFLINE (grey), which is correct.

---

## 4. Running

### 4.1 Start order (every boot)

Start these three, in order:

```bash
# 1) broker (usually already a running service)
systemctl is-active mosquitto || sudo systemctl start mosquitto

# 2) the gateway — the master. Leave it running.
~/metro-gw/venv/bin/python ~/metro-gw/Gateway/Pi.py

# 3) the HMI (its own terminal, on the touchscreen)
~/metro-gw/venv/bin/python ~/metro-gw/hmi_dark.py
```

### 4.2 Live vs demo

- `python3 hmi_dark.py` → **live**: reads real Modbus + MQTT.
- `python3 hmi_dark.py --demo` → seeds a representative mock fleet, for design
  review with no gateway/nodes.

### 4.3 Fullscreen kiosk

For deployment, open `hmi_dark.py` and uncomment (near the top of `HMIApp`):

```python
self.attributes('-fullscreen', True)
```

`Esc` leaves fullscreen during development.

### 4.4 Dark vs light

`hmi_dark.py` and `hmi_light.py` are identical except the colour palette. Run
whichever suits the panel's environment. (See `TECHNICAL.md` for how they are
kept in sync.)

---

## 5. Verify it works

1. **Diagnostics screen** — MQTT should read **CONNECTED** and Modbus
   **RUNNING · 502**. If either says NOT CONNECTED, that service is not up.
2. **Power the PCBs** — tiles 01–04 turn green on Fleet Overview and show their
   IPs on the Signage List.
3. **Control a node** — on Signage List set **CONTROL SOURCE → LOCAL**, open a
   node, press a mode button (`0`–`2`, or technician `3`–`6`). The sign changes.
   Node Detail flags **⚠ MISMATCH** if the node reports running a different mode.

### Bench dry-run (recommended before real PCBs)

Prove the whole HMI ↔ gateway ↔ MQTT chain with fake nodes first:

```bash
# terminals: mosquitto (service), then:
~/metro-gw/venv/bin/python ~/metro-gw/Gateway/bench/simulate_node.py --nodes 4 &
~/metro-gw/venv/bin/python ~/metro-gw/Gateway/Pi.py            # if not already running
~/metro-gw/venv/bin/python ~/metro-gw/hmi_dark.py
```

Then swap in your real PCBs one at a time.

---

## 6. Controlling the system

| Action | Where | What it writes |
| --- | --- | --- |
| Switch whole fleet to manual | Signage List → CONTROL SOURCE → **LOCAL** | `43001 = 1` |
| Switch whole fleet to auto | Signage List → CONTROL SOURCE → **REMOTE** | `43001 = 0` |
| Command a node's arrows | Node Detail → mode button | `41001 + offset = 0…6` |
| Drive individual LEDs | Node Detail → Per-LED Control → tap LEDs → Apply | `41001 + offset = 10000…11023` |
| Ask all nodes to re-report | Diagnostics → Broadcast Ping | MQTT `metro/signage/scan = PING` |

The control-source toggle is **global** — it governs all 100 nodes at once.
When it is REMOTE, the mode buttons on Node Detail are locked.

---

## 7. Troubleshooting

| Symptom | Likely cause | Fix |
| --- | --- | --- |
| All tiles grey; Diagnostics MQTT **CONNECTED** | Nodes can't reach the broker, or wrong register/broker IP | Check §3.2 LAN listener, node firmware broker = `192.168.1.10`, distinct registers |
| Diagnostics Modbus **NOT CONNECTED**; control does nothing | `Pi.py` not running | Start the gateway (§4.1 step 2) |
| Fake data still showing | Launched with `--demo`, or an old copy with `DEMO_MODE = True` | Run without `--demo` |
| HMI window opens but no data at all | Broker down | `sudo systemctl start mosquitto` |
| A node shows but never changes on command | Control source is REMOTE, or write failed | Set CONTROL SOURCE → LOCAL; check gateway logs (stderr) |
| Node runs a mode you didn't send (⚠ MISMATCH) | Node executing something other than commanded | Expected alarm — investigate that node's firmware/hardware |

Both the HMI and the gateway log to **stderr**, so run them from a terminal
(not backgrounded silently) while debugging.

---

## 8. Notes

- The HMI has **not yet been run against live hardware** as of this writing —
  expect first bring-up to surface an mosquitto-binding, node-IP, or register
  detail. The bench dry-run in §5 de-risks this.
- A start-all script / systemd units are intentionally **not** included yet —
  they depend on the final Pi repository layout, which is still to be decided.
- Deeper detail on the code (data model, screens, register map, extension
  points) is in **`TECHNICAL.md`** in this folder.
