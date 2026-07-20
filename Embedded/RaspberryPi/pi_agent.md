# Raspberry Pi Gateway Platform — Verified Contract

**Status:** Base platform commissioned and verified 2026-07-14.
**Scope:** Raspberry Pi 5 host running the headless Gateway daemon (`Pi.py`) and the Tkinter HMI (`HMI.py`).
**Audience:** Firmware/coding agent. Everything below is either *verified on the target hardware* or *explicitly flagged as open*. Do not assume anything not stated here.

---

## 1. Platform Baseline (verified)

| Item | Value | How verified |
| --- | --- | --- |
| Board | Raspberry Pi 5 Model B Rev 1.1 | `/proc/device-tree/model` |
| OS | Debian GNU/Linux 13 (trixie) | `/etc/os-release` |
| Python | 3.13.5 | `python3 --version` |
| Network stack | NetworkManager (dhcpcd + systemd-networkd both inactive) | `systemctl is-active` |
| MQTT broker | Mosquitto 2.0.21 | `apt` |
| Venv path | `~/metro-gw/venv` | — |

**PEP 668 is enforced.** The system Python refuses `pip install`. All Python work happens in the venv, which was created with `--system-site-packages` so it can see the apt-installed `python3-tk` (Tkinter cannot be pip-installed; the HMI dies at import without it).

---

## 2. Pinned Dependencies — DO NOT UPGRADE BLINDLY

```
paho-mqtt==2.1.0
pymodbus==3.11.3
```

### Why pymodbus is pinned to 3.11.3 (not latest)

The Gateway's MUX loop reads and writes the Modbus server's datastore from a worker thread:

```python
mux_flag = modbus_context[0].getValues(3, 3000, count=1)[0]
modbus_context[0].setValues(3, 0, hmi_values)
```

This pattern was tested against every 3.x major:

| pymodbus | `ModbusSlaveContext` | `context[0]` | `getValues`/`setValues` | Verdict |
| --- | --- | --- | --- | --- |
| 3.6.9 – 3.9.2 | present | works | works | runs unmodified |
| **3.11.3** | renamed `ModbusDeviceContext` | works | works | **PINNED — 2-line change** |
| 3.14.0 | **gone** | **raises** | **does not exist** | **hard fail** |

In 3.14.0 `ModbusServerContext` is not subscriptable and `ModbusDeviceContext` has **no** `getValues`/`setValues` methods at all. The replacements (`ServerContext.async_getValues/async_setValues`) return `DEVICE_BUSY` for anything that is not a simulator context. The entire datastore-poking design has no supported API path in 3.14.

**Future work (not now):** 3.11.3 emits deprecation warnings — `ModbusDeviceContext`, `ModbusServerContext`, and the data blocks are all removed in pymodbus v4, replaced by `SimData`/`SimDevice`. Migrating is a rewrite of the bridge core, deliberately deferred.

Freeze with `pip freeze > ~/metro-gw/requirements.txt`. A silent `pip install --upgrade` is how a working installation stops working.

---

## 3. VERIFIED: There is no Modbus off-by-one

Tested with a real pymodbus 3.11.3 server and a real client over TCP:

```
setValues(3, 0,    [0xBEEF])  ->  wire address 0     reads 0xBEEF
setValues(3, 3000, [0xCAFE])  ->  wire address 3000  reads 0xCAFE
```

> **Datastore index == Modbus wire address == PLC register number − 40001.**

No `+1` adjustment anywhere, in any direction. `Pi.py`'s existing address arithmetic is correct. Do not "fix" it.

---

## 4. Modbus Register Map (authoritative)

Single Modbus device, `single=True` — one device context answers on **every** unit ID. The Pi is *one* Modbus device presenting 100 nodes as register offsets, **not** 100 Modbus units. The PLC's unit-ID setting is irrelevant.

Node index `i` is 0-based (`i = 0` is node 1 / register 40001).

| Block | PLC registers | Datastore index | Written by | Read by |
| --- | --- | --- | --- | --- |
| Active Execution Zone | `40001–40100` | `0 + i` | Gateway (MUX output) | Gateway → MQTT |
| HMI Manual Buffer | `41001–41100` | `1000 + i` | HMI | Gateway (MUX) |
| **SCADA Auto Buffer** | `42001–42100` | `2000 + i` | **PLC** | Gateway (MUX) |
| MUX Control Flag | `43001` | `3000` | HMI / PLC | Gateway |
| Diagnostic Block | `45001–45800` | `5000 + (i * 8)` | Gateway | PLC / HMI |

`HR_SIZE = 6000` (indices 0–5999). Covers the diag block end at index 5799. Sequential block; the gaps are intentionally unused.

Only `hr` (holding registers) is populated. `di`/`co`/`ir` are left `None`, so a PLC attempting to read coils or input registers gets a Modbus exception rather than a silent zero. This is deliberate — it fails loudly on a misconfigured PLC.

### MUX semantics (register 43001)

| Value | Mode | Gateway behaviour |
| --- | --- | --- |
| `0` | SCADA / Auto | copies `42001+` → `40001+` |
| `1` | HMI / Manual | copies `41001+` → `40001+` |

### Command value encoding (40001/41001/42001 zones)

> **CORRECTED 2026-07-20.** This section previously documented a 2-bit "Relay 1 / Relay 2" pair (`0`=both off, `1`=R1, `2`=R2, `3`=both). **That model is obsolete** — the hardware is 10 MOSFETs behind an MCP23017, not two relays. The gateway publishes the integer verbatim and never interpreted it, so `Pi.py` was always correct; only this table was wrong.

**Commands drive the moving arrows ONLY.** Static Zones 1 and 2 are hardwired always-on — current-monitored, never switched. No command value affects them.

| Value | Meaning | HMI access |
| --- | --- | --- |
| `0` | Arrows OFF | Operator |
| `1` | LHS chase animation | Operator |
| `2` | RHS chase animation | Operator |
| `3` | Both chase animations | **PIN** |
| `4` | Solid ON — left MOSFETs (Port A `0x1F`) | **PIN** |
| `5` | Solid ON — right MOSFETs (Port B `0x1F`) | **PIN** |
| `6` | Solid ON — everything (`0x1F`/`0x1F`) | **PIN** |
| `10000`–`11023` | **Raw bitmask.** `value - 10000` = 10-bit MOSFET mask; low 5 bits = Port A (LHS), high 5 bits = Port B (RHS). | **PIN** |

Only `0`/`1`/`2` are normal signage operation. Everything from `3` up is a maintenance facility and sits behind a **technician PIN**, on a panel separate from operator mode control.

**Technician modes LATCH** (decided 2026-07-20). There is no auto-revert timeout — a mode set by a technician holds until it is explicitly changed. This is consistent with the network-loss behaviour (hold last command, persisted to NVS), and means a sign left in a solid-ON lamp-test state stays that way until someone changes it. The HMI must therefore make the active mode unmistakable when it is not an operator mode.

> **The PIN gates the HMI, not the protocol.** The gateway publishes the register value verbatim and the PLC writes the SCADA buffer (`42001+`) freely, so a technician mode originating from SCADA executes unchallenged. The PIN prevents local operator misuse; it is not a security boundary.

### Diagnostic Block layout — 8 registers per node

Base index for node `i` = `5000 + (i * 8)`.

| Offset | Register (node 1) | Content | Encoding |
| --- | --- | --- | --- |
| +0 | 45001 | Network Status | `1` = Online, `0` = Offline |
| +1 | 45002 | Main Power | `1` = OK, `0` = PSU Fail — **gate on +0, see below** |
| +2 | 45003 | LHS LED Health | 4-state (below) |
| +3 | 45004 | RHS LED Health | 4-state |
| +4 | 45005 | Battery Percentage | `0`–`100`, `65535` = unknown |
| +5 | 45006 | **Static Zone 1** Health | 4-state |
| +6 | 45007 | **Static Zone 2** Health | 4-state |
| +7 | 45008 | **Actual Executing State** | mode the node reports running; `65535` = unknown |

Node 1 → `45001–45008`. Node 2 → `45009–45016`. Node 100 → `45793–45800`.

> **Register `+7` added 2026-07-20.** The `40001+` zones hold what a node was *commanded*; `+7` holds what it *reports executing*. A mismatch means the node is running something else — stale command, rejected payload, missed publish — and **nothing else in the system detects it**. Alarm on `commanded != actual` when the node is online and `+7 != 65535`.
>
> This renumbered the whole block (was 7/node, `45001–45700`). Any PLC program written against the old map must be updated.

> **Power register `+1` has no unknown state.** An offline node reads `0`, identical to a real PSU failure. Because the battery backup exists so a node *survives PSU loss and stays online to report it*, `power=FAIL` from an ONLINE node is the critical alarm — and offline nodes raising the same alarm would bury it. **PLC must gate:** `IF +0 == 1 AND +1 == 0 THEN "PSU FAILURE"`. Decision on record (2026-07-20): keep the binary encoding, document the gating requirement. Same gating applies to the LED-health registers.

**4-state discrepancy encoding:**

| String (MQTT) | Int (Modbus) | Meaning |
| --- | --- | --- |
| `OFF` | `0` | Intended OFF, no current |
| `ON` | `1` | Intended ON, current flowing |
| `FAIL_OPEN` | `2` | Intended ON, 0 A — broken wire / burnt LED / blown MOSFET |
| `FAIL_SHORT` | `3` | Intended OFF, current flowing — welded MOSFET / short to power |
| *(unknown / `---`)* | `99` | Unparseable or node offline |

---

## 5. MQTT Topic Contract (authoritative)

Broker: Mosquitto 2.0.21. Gateway and HMI connect to `127.0.0.1:1883`. ESP32 nodes connect to `192.168.1.10:1883`.

### Address format — CRITICAL

> `<address>` in every topic is the **holding-register number** (`40001 + i`), **NOT** the node index.

Node 1 is `metro/signage/register/40001/power`. It is **not** `register/1/power`.

If firmware and gateway disagree on this, **nothing matches, every node reads OFFLINE, and no error is logged anywhere in the system.** It simply, silently, does not work. This is the single most dangerous mismatch in the project.

### Telemetry — ESP32 publishes, Gateway + HMI subscribe

`metro/signage/register/<40001+i>/<metric>`

| Metric | Payload | Notes |
| --- | --- | --- |
| `status` | `ONLINE:<ip>` or `OFFLINE` | Gateway parses the IP out of the `ONLINE:` prefix |
| `power` | `OK` \| `FAIL` | |
| `current1` | 4-state string | LHS arrows (ACS712 on GPIO 34) |
| `current2` | 4-state string | RHS arrows (ACS712 on GPIO 35) |
| `current3` | 4-state string | **Static Zone 1** (ACS712 on GPIO 32) |
| `current4` | 4-state string | **Static Zone 2** (ACS712 on GPIO 33) |
| `battery_pct` | integer `0`–`100` | stubbed in firmware; omit rather than publish `0` |
| `state` | integer | **NEW 2026-07-20.** The mode the node is ACTUALLY executing — not what it was told. Restores v1.1.1's "SCADA blind spot fix". Publish on every command change and on PING. |

**Naming rationale:** wire-level metric names stay *sensor-indexed* (`current1`…`current4`) because `current1/2/3` are already deployed — `current4` is an addition, not a rename. `Static Zone 1 / 2` are the *display* labels used in the HMI and documentation only.

### Commands — Gateway publishes, ESP32 subscribes

| Topic | Payload | Retain | Notes |
| --- | --- | --- | --- |
| `metro/signage/register/<40001+i>/value` | integer `0`–`6` or `10000`–`11023` | **`retain=True`** | Node's target arrow state (see §4 encoding) |
| `metro/signage/scan` | `PING` | `retain=False` | Broadcast, every 60 s |

`retain=True` on commands is **required**: it is what lets an ESP32 receive its target state immediately on boot/reconnect instead of coming up blank.

### Retained-message deletion

An empty payload with the retain flag deletes a retained message. `perform_startup_cleanup()` depends on retained-message semantics heavily — understand this before touching it.

---

## 6. pymodbus 3.11.3 API Contract

```python
from pymodbus.datastore import (
    ModbusSequentialDataBlock,
    ModbusDeviceContext,      # was ModbusSlaveContext  (renamed)
    ModbusServerContext,
)
from pymodbus.server import StartTcpServer

HR_SIZE = 6000

store   = ModbusDeviceContext(hr=ModbusSequentialDataBlock(0, [0] * HR_SIZE))
context = ModbusServerContext(devices=store, single=True)   # was slaves=

# Bind wildcard, NOT a specific IP. See note below.
StartTcpServer(context, address=("0.0.0.0", 502))           # blocking; run in a thread
```

- `ModbusDeviceContext` takes keyword-only block args — `hr=` must be a keyword.
- Datastore access from a worker thread: `context[0].getValues(3, addr, count=n)` / `context[0].setValues(3, addr, values)`. Function code `3` = holding registers.

**Bind `0.0.0.0`, not `10.45.2.50`.** The listener then does not care what the interface address is, so changing the SCADA-side IP requires no daemon restart and drops no PLC session. Binding to a specific address forces a restart on every IP change.

---

## 7. paho-mqtt 2.1.0 API Contract — VERSION2

The project targets **`CallbackAPIVersion.VERSION2`**. Gateway and HMI must both use it. Do not mix conventions.

```python
import paho.mqtt.client as mqtt
from paho.mqtt.client import CallbackAPIVersion

def on_connect(client, userdata, flags, reason_code, properties):
    if reason_code == 0:
        client.subscribe("metro/signage/register/+/+")
    else:
        log.error("MQTT connect failed: %s", reason_code)

def on_disconnect(client, userdata, flags, reason_code, properties):
    log.warning("MQTT disconnected: %s", reason_code)

def on_message(client, userdata, msg):      # unchanged from v1
    ...

client = mqtt.Client(CallbackAPIVersion.VERSION2)
client.on_connect    = on_connect
client.on_disconnect = on_disconnect
client.on_message    = on_message
client.connect("127.0.0.1", 1883, 60)
client.loop_start()
```

Three traps:

1. **`mqtt.Client("SomeClientId")` no longer works.** The first positional arg is now `CallbackAPIVersion`. Passing a client-ID string there fails immediately. Use `client_id=` as a keyword if you need one.
2. **`reason_code` is a `ReasonCode` object, not an int.** `reason_code == 0` works, but `if reason_code:` is *inverted* from intuition — success is `0`, which is falsy. Compare explicitly.
3. **`on_disconnect` gained BOTH `flags` and `properties`.** A v1-style 3-arg handler throws *at disconnect time* — i.e. only when the network drops, which is exactly when you need it.

---

## 8. Known Bugs in `Pi.py` v1.0.1 — ✅ ALL FIXED IN v2.0.0 (2026-07-20)

> **Status: resolved.** Every item below was fixed in the `Pi.py` v2.0.0 rewrite. The section is retained as the rationale record — these are the failure modes to regression-test against, not an outstanding work list. Verified by an offline logic harness (diag-block indexing, `65535` sentinel, offline scrubbing of all four current channels, malformed-topic resilience).

### CRITICAL — the SCADA diagnostic block is never written under systemd

`Pi.py` line 148 opens `if sys.stdout.isatty():`. Line 203 — the `setValues` that populates the **entire** `45001+` diagnostic block — sits inside it:

```python
if sys.stdout.isatty():                                    # line 148
    ...
    for i in range(REGISTERS_TO_BRIDGE):
        ...
        modbus_context[0].setValues(3, diag_base, [...])   # line 203  <-- SIDE EFFECT
```

Run by hand in a terminal: works perfectly. Run headless under systemd: `isatty()` is `False`, the branch is skipped, and **the PLC reads zeros from every diagnostic register, forever.** No error, no log line. The 4-state discrepancy translation — the system's entire reason for existing — silently does nothing in production.

**Fix:** hoist the Modbus translation and `setValues` out of the rendering branch. Compute and write unconditionally; only the `frame +=` string-building belongs under `isatty()`.

> **Standing rule for this codebase: no side effect may live inside a presentation conditional.**

### Also fix

| Bug | Location | Impact |
| --- | --- | --- |
| `mqtt.Client("ModbusBridgeClient")` | `__main__` | Fails on paho 2.x. First positional is now `CallbackAPIVersion`. |
| `on_connect(client, userdata, flags, rc)` | callback | Needs 5 params in VERSION2. |
| No `on_disconnect` handler | — | Broker drops are invisible. |
| `ModbusSlaveContext` / `slaves=` | `__main__` | Renamed in 3.11.3 → `ModbusDeviceContext` / `devices=`. |
| `except Exception: break` | bridge loop, ~line 243 | **One transient error kills the bridge permanently.** Log and continue instead. |
| `except Exception: pass` | `on_message` | Silently swallows real bugs alongside malformed packets. Log at minimum. |
| `int("---") → ValueError → 0` | battery translation | An **offline** node reports **0 % battery** to SCADA — indistinguishable from a genuinely flat battery. Use a sentinel (e.g. `65535`). |
| `MQTT_BROKER_HOST = "0.0.0.0"` used as the **Modbus** bind address | config | Misnomer. Functionally correct, but rename it (`MODBUS_BIND_HOST`) before it misleads someone. |
| Diagnostic block hardcoded to 6 regs/node (`i * 6`) | ~line 202 | **Now 7.** Must become `5000 + (i * 7)` with the `current4` value appended. |

---

## 9. Network Configuration (committed)

Single `eth0` NIC carrying two addresses — the "air gap" is **logical**, not physical.

| Link | Initiator | Target | Port |
| --- | --- | --- | --- |
| SCADA → Gateway | PLC (Modbus **master**) | `10.45.2.50` | 502 |
| ESP32 → Gateway | ESP32 (MQTT client) | `192.168.1.10` | 1883 |
| HMI → Gateway | HMI (both clients) | `127.0.0.1` | 502 + 1883 |

NetworkManager profile `metro-gw`:

```
ipv4.method:        manual
ipv4.addresses:     10.45.2.50/24, 192.168.1.10/24
ipv4.gateway:       -- (none)
ipv4.never-default: yes
ipv6.method:        disabled
```

`ipv4.never-default yes` is load-bearing: without it, `eth0` fights `wlan0` for the default route and internet access dies the moment the field switch is plugged in.

**The air gap is enforced by `net.ipv4.ip_forward = 0`** (Debian default). If it is ever `1`, a packet arriving on `10.45.2.50` can be relayed to `192.168.1.10` and the isolation is fiction. Assert this in the deployment checklist.

Both PLC and ESP32s must be **inside their respective /24s** — there is no gateway on either side, so anything off-subnet is unreachable.

---

## 10. Mosquitto Configuration

Config lives in `/etc/mosquitto/conf.d/metro.conf` (a drop-in — never edit `mosquitto.conf`, apt upgrades will clobber it).

```
listener 1883 127.0.0.1
listener 1883 192.168.1.10     # ESP32 segment — bind fails if eth0 has no carrier
allow_anonymous true
persistence true
persistence_location /var/lib/mosquitto/
```

**The broker is deliberately NOT bound to `0.0.0.0`.** Explicit per-address listeners mean the broker is *never* reachable from `10.45.2.50` — the SCADA side. Even if someone later bridges the two switches, the PLC network cannot reach MQTT. Defence in depth, enforced at the socket rather than by trusting the topology.

Verify with `ss -tlnp | grep 1883` — you must **not** see `0.0.0.0:1883`.

**Startup-order hazard:** the `192.168.1.10` listener cannot bind before `eth0` has carrier and an address. Mosquitto will fail to start. The service needs an ordering dependency on the network being online, or the daemon must be resilient to the broker being briefly absent.

`allow_anonymous true` is acceptable on an air-gapped segment but is a **decision, not a default**. Per-node credentials are the eventual target for a public-transit deployment.

---

## 11. Deployment Notes

**Port 502 is privileged.** A pymodbus server run as user `pi` gets `PermissionError` on bind. Preferred fix — keep the daemon unprivileged and grant only the one capability:

```
# in the systemd unit
AmbientCapabilities=CAP_NET_BIND_SERVICE
```

(The alternative — bind high and NAT-redirect 502 → 5020 — works but leaks a port change into the SCADA integrator's config. Avoid.)

**Restart policy:** `Restart=always`. But note this masks the `except: break` bug above — the bridge would crash-loop rather than handle errors. Fix the error handling; do not rely on the restart.

**HMI:** separate systemd unit. The decoupled architecture exists so that an HMI crash cannot take down the SCADA-facing Modbus server. Keep them as two units; never merge them.

---

## 12. Open Items / Required Knowledge-Base Updates

These are **conflicts between the shipped code and the project docs**. Left unresolved, they will cause silent field failures.

| # | Item | Doc(s) affected |
| --- | --- | --- |
| 1 | **Register `42001` (SCADA Auto Buffer) is entirely missing from `hmi-specification.md` §4.2.** This is the block the PLC actually writes to. It is documented in `Gateway_doc.md` and used in `Pi.py`, but absent from the project's source of truth. | `hmi-specification.md` |
| 2 | **Diagnostic block is now 7 registers/node (`45001–45700`), not 6.** ✅ `Gateway_doc.md` updated 2026-07-20 and implemented in `Pi.py` v2.0.0. Still outstanding in the other two docs. | ~~`Gateway_doc.md`~~, `hmi-specification.md`, `architecture-overview.md` |
| 3 | **HMI ingests only `current1`/`current2`.** It is blind to Static Zone 1 and 2. Node Detail telemetry needs two more rows. | `hmi-specification.md` §3.2, §4.1 |
| 4 | **`pin-map.md` nomenclature:** Load 3 → **Static Zone 1**, Load 4 → **Static Zone 2**. | `pin-map.md` |
| 5 | **RESOLVED 2026-07-20 — the timer does NOT exist.** Confirmed: a node **holds its last command** on network loss; the command is persisted to NVS and re-applied on boot. No forced-ON, no forced-OFF. The `esp_task_wdt` (15 s) is internal hang recovery only. The PING's real purpose is to make nodes re-publish telemetry. ✅ `Gateway_doc.md` corrected. Still wrong in `HMI_doc.md` and `hmi-specification.md` §3.3 — and in `HMI.py`'s ping panel text, which must be reworded during the HMI rework. | ~~`Gateway_doc.md`~~, `HMI_doc.md`, `hmi-specification.md` |
| 6 | **Library versions are unpinned in the spec.** Must state `paho-mqtt 2.1.0`, `pymodbus 3.11.3`, `mosquitto 2.0.21`, and that the codebase targets paho `CallbackAPIVersion.VERSION2`. | `hmi-specification.md` §1 |
| 7 | **The IPs in `architecture-overview.md` are prefixed "e.g."** — they are now committed values (`10.45.2.50`, `192.168.1.10`). Drop the hedge, or the PLC integrator is working from a suggestion rather than a spec. | `architecture-overview.md` §2 |
| 8 | **RESOLVED 2026-07-20 — the Waveshare 10.1" DSI LCD (1280x800) is CONFIRMED** as the shipping panel. The 7"/1024x600 figure in the spec is superseded. `HMI.py`'s layout is built entirely from absolute `.place()` coordinates sized for 1024x600 and must be re-laid-out; pagination (`nodes_per_page = 8`) can grow with the extra vertical space. | `hmi-specification.md` §1, §5 |
| 9 | **HMI-configurable SCADA IP** (discussed, not built). Would be the first HMI action reaching the **OS** rather than the gateway's register space — a genuinely new class of action, absent from the §4.3 action-mapping table. Requires a root-owned, zero-argument helper script behind a narrow sudoers rule; the HMI must never get blanket `sudo`. | `hmi-specification.md` §3.3, §4.3 |

---

## 13. Bench Testing Without Hardware

MQTT round-trip is proven on loopback. You can exercise the full gateway and HMI with **zero ESP32 hardware** by faking a node:

```bash
mosquitto_pub -h 127.0.0.1 -t 'metro/signage/register/40001/status'      -m 'ONLINE:192.168.1.101' -r
mosquitto_pub -h 127.0.0.1 -t 'metro/signage/register/40001/power'       -m 'OK'         -r
mosquitto_pub -h 127.0.0.1 -t 'metro/signage/register/40001/current1'    -m 'ON'         -r
mosquitto_pub -h 127.0.0.1 -t 'metro/signage/register/40001/current2'    -m 'FAIL_OPEN'  -r
mosquitto_pub -h 127.0.0.1 -t 'metro/signage/register/40001/current3'    -m 'ON'         -r
mosquitto_pub -h 127.0.0.1 -t 'metro/signage/register/40001/current4'    -m 'FAIL_SHORT' -r
mosquitto_pub -h 127.0.0.1 -t 'metro/signage/register/40001/battery_pct' -m '87'         -r

# watch everything
mosquitto_sub -h 127.0.0.1 -t 'metro/#' -v
```

Clear a retained message with an empty payload:

```bash
mosquitto_pub -h 127.0.0.1 -t 'metro/signage/register/40001/power' -n -r
```
