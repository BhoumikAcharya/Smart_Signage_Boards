# Metro Signage Gateway — SCADA / PLC Integration Guide

**Document status:** authoritative for SCADA integration.
**Applies to:** Gateway `Pi.py` **v2.0.0**, ESP32 `Firmware.ino` **v3.0.0**.
**Written:** 2026-07-27.
**Audience:** the PLC / SCADA engineer connecting a control system to the Raspberry Pi gateway.

This document is **self-contained**. You do not need to read the firmware or gateway
source to integrate correctly. Everything the control system needs — addressing,
encoding, alarm logic, timing, and failure behaviour — is here.

> Companion documents (for developers, not integrators): `Gateway_doc.md`,
> `../pi_agent.md`, `../../ESP32/esp32_contract.md`.

---

## 1. What this system is

A Raspberry Pi acts as a protocol bridge. Your PLC speaks **Modbus TCP** to the Pi.
The Pi translates that into **MQTT** messages for up to **100 ESP32 sign controllers**
on a separate, isolated network. Telemetry flows back the same way.

```
   PLC / SCADA  --Modbus TCP-->  Raspberry Pi Gateway  --MQTT-->  100x ESP32 signs
   10.45.2.x         :502          10.45.2.50                      192.168.1.x
                                   192.168.1.10
```

You never see MQTT. You read and write holding registers; the gateway does the rest.

**What a "node" is:** one ESP32 controlling one signage board. Each board has two
banks of moving-arrow LED strips (left and right) plus two always-on static
illuminated zones. Commands control the **arrows only**.

---

## 2. Connection parameters

| Parameter | Value |
| --- | --- |
| Protocol | Modbus **TCP** |
| Gateway IP (SCADA side) | **`10.45.2.50`** |
| Port | **`502`** |
| Unit / Device / Slave ID | **Irrelevant — any value answers** |
| Register type | **Holding registers only** (FC 03 / 06 / 16) |
| Byte order | Standard Modbus big-endian, 16-bit unsigned |
| Max concurrent connections | Multiple supported; the HMI already holds one on loopback |

### 2.1 Unit ID does not matter

The gateway presents **one** Modbus device that exposes 100 signs as register
offsets. It is **not** 100 Modbus units. The server runs `single=True`, so every
unit ID maps to the same register space. Set whatever your PLC defaults to.

### 2.2 Only holding registers exist

Coils, discrete inputs and input registers are **deliberately not populated**.
A read of FC 01 / 02 / 04 returns a Modbus **exception**, not zeros.

This is intentional: a misconfigured PLC fails loudly instead of silently reading
a wall of zeros and reporting every sign healthy.

| Function code | Supported | Use |
| --- | --- | --- |
| **03** Read Holding Registers | ✅ | All reads |
| **06** Write Single Register | ✅ | Single command / MUX flag |
| **16** Write Multiple Registers | ✅ | Block command writes |
| 01 / 02 / 04 / 05 / 15 | ❌ exception | — |

### 2.3 Network — the PLC must be on the same /24

The SCADA segment is `10.45.2.0/24` and **there is no router on it.** The Pi has
no default gateway on that interface. Anything outside `10.45.2.0/24` is
unreachable in both directions. Assign the PLC an address in that subnet.

The `192.168.1.0/24` sign network is isolated. IP forwarding is disabled on the Pi
and the MQTT broker does not listen on the SCADA address, so the sign network is
not reachable from the PLC by design. Do not plan on reaching it.

### 2.4 There is no off-by-one

Verified against a live server with a live client:

> **Modbus wire address = PLC register number − 40001.**

Register `40001` is wire address `0`. Register `45001` is wire address `5000`.
If your PLC displays 4xxxx-style register numbers, use the table in §3 directly.
If it wants raw wire addresses, subtract 40001. **Do not add a correction of your
own** — a `+1` fudge here is the classic way to make every node read one slot off.

---

## 3. Register map

| Block | PLC registers | Wire address | Written by | Your access |
| --- | --- | --- | --- | --- |
| Active Execution Zone | `40001 – 40100` | `0 – 99` | **Gateway** | **Read only** |
| HMI Manual Buffer | `41001 – 41100` | `1000 – 1099` | Local HMI | **Do not touch** |
| **SCADA Auto Buffer** | `42001 – 42100` | `2000 – 2099` | **You** | **Read / write** |
| MUX Control Flag | `43001` | `3000` | HMI or you | **Read / write** |
| Diagnostic Block | `45001 – 45800` | `5000 – 5799` | **Gateway** | **Read only** |

Registers `40001 – 46000` all exist. The gaps (`40101–40999`, `43002–44999`,
`45801–46000`) read as `0` and are unused. Reads above `46000` return an exception.

### 3.1 Node addressing

Nodes are numbered **1 to 100**.

| For node `N` | Register |
| --- | --- |
| Write command | `42000 + N` |
| Read active (confirmed) command | `40000 + N` |
| Diagnostic block base | `45001 + (N − 1) × 8` |

**Worked example — node 7:**

* Command it: write `42007`
* Read back what is actually being sent: `40007`
* Its diagnostics: `45049` to `45056` *(45001 + 6×8 = 45049)*

Node 1 → `45001–45008`. Node 100 → `45793–45800`.

### 3.2 ⚠️ The diagnostic block is 8 registers per node

It was **7** in an earlier revision, and **6** before that. If you are working from
any documentation or PLC program predating 2026-07-20, **every node above node 1
is at the wrong address** and you will be reading node 2's power state as node 1's
battery level, and so on — with no error anywhere.

Verify before commissioning: node 1's network status must be at `45001`, and
**node 2's must be at `45009`** (not `45008`).

### 3.3 Modbus transaction size limits

Function code 03 reads a maximum of **125 registers** per transaction. The full
diagnostic block is 800 registers, so it needs **7 reads**:

| Read | Registers | Nodes covered |
| --- | --- | --- |
| 1 | `45001 – 45120` | 1 – 15 |
| 2 | `45121 – 45240` | 16 – 30 |
| 3 | `45241 – 45360` | 31 – 45 |
| 4 | `45361 – 45480` | 46 – 60 |
| 5 | `45481 – 45600` | 61 – 75 |
| 6 | `45601 – 45720` | 76 – 90 |
| 7 | `45721 – 45800` | 91 – 100 |

Each read above is 120 registers = exactly 15 nodes, so **no node is ever split
across two transactions.** Use these boundaries; splitting a node's 8 registers
across two reads lets you latch a half-updated block during a state change.

Function code 16 writes a maximum of 123 registers, so the whole command buffer
`42001–42100` (100 registers) fits in **one** write.

---

## 4. Control — how to command a sign

### 4.1 The MUX flag (`43001`) decides who is in charge

| `43001` | Mode | Gateway behaviour |
| --- | --- | --- |
| `0` | **SCADA / Auto** | Copies `42001+` → `40001+`. **Your commands take effect.** |
| `1` | **HMI / Manual** | Copies `41001+` → `40001+`. **Your commands are ignored.** |

The copy runs **once per second, continuously.** It is not edge-triggered.

> ### ⚠️ You must read `43001` and display it
>
> When it is `1`, a local technician has taken manual control at the panel. Your
> writes to `42001+` still succeed at the Modbus level — they are accepted, stored,
> and **silently not applied.** Nothing reports an error.
>
> Without an indication on the SCADA screen, an operator will command a sign,
> see the write succeed, see nothing change, and have no way to find out why.
>
> **Required:** a prominent "LOCAL / MANUAL CONTROL ACTIVE" banner whenever
> `43001 == 1`. Recommended: suppress or visually disable command controls while it
> is set.

Either side may write `43001`. Agree the operational policy with the site: normally
the HMI claims and releases it, and SCADA only forces it back to `0` under a defined
procedure.

### 4.2 Never write to `40001+`

The Active Execution Zone is the gateway's **output**. It is overwritten from the
selected buffer once per second. A value you write there survives less than one
second and then vanishes. Read it to confirm what the gateway is actually sending;
never write it.

### 4.3 Command value encoding

Write these integers to `42001+`. **Commands drive the moving arrows only.** The two
static illuminated zones are hardwired always-on — they are current-monitored but
cannot be switched. No command value affects them.

**Normal operating modes:**

| Value | Meaning |
| --- | --- |
| `0` | Arrows OFF |
| `1` | Left-hand chase animation |
| `2` | Right-hand chase animation |

**Maintenance modes** — valid, and executed without challenge when sent from SCADA:

| Value | Meaning |
| --- | --- |
| `3` | Both chase animations |
| `4` | Solid ON — left bank only |
| `5` | Solid ON — right bank only |
| `6` | Solid ON — everything |
| `10000 – 11023` | Raw MOSFET bitmask. `value − 10000` = 10-bit mask; low 5 bits = left bank, high 5 bits = right bank. |

Any other value is **rejected by the node**, which keeps running its previous mode
and reports the divergence (see §5.4).

> **Maintenance modes are PIN-protected on the local HMI, but not on Modbus.**
> The gateway forwards whatever integer is in the register. A `6` written from
> SCADA lights the whole sign solid with no challenge. The PIN prevents local
> operator misuse; **it is not a security boundary and does not apply to you.**
> If maintenance modes should not be reachable from SCADA, enforce that in the PLC
> program.

**Modes latch.** There is no auto-revert timer. A sign left in solid-ON stays lit
until something explicitly changes it.

### 4.4 ⚠️ Commands must be re-asserted cyclically, not written once

**This is the most important operational requirement in this document.**

The gateway holds its registers in volatile memory. There is **no persistence.**
When the gateway process restarts — a crash, a service restart, a Pi reboot — the
entire register space is re-initialised to **zeros**, including your command buffer
`42001+`.

One second later the gateway copies that zeroed buffer into the active zone and
publishes `0` to **every node**. The service is configured to restart automatically
after 5 seconds.

> **Net effect: a gateway restart turns every sign off, and they stay off until the
> PLC writes its commands again.**

**Therefore the PLC must write `42001+` on a cycle** — every scan, or on a timer of
a few seconds — **not once on operator change.** A write-on-change-only design will
work perfectly in testing and then blank the entire installation the first time the
gateway restarts in service.

Confirm your commands are landing by reading back `40001+`, which should match
`42001+` within one second while the MUX is `0`.

### 4.5 Command timing

The gateway forwards a command to a node **only when the register value changes**.
Re-writing the same value costs nothing and sends no traffic — so cyclic re-assertion
(§4.4) is cheap and does not flood the sign network.

| Event | Latency |
| --- | --- |
| Your write → active zone `40001+` | ≤ 1 s |
| Active zone → node acts on it | typically < 1 s |
| Node acts → `+7` reflects it | ≤ 1 s after the node confirms |
| **Total: your write → confirmed executing** | **allow 3 s** |

Do not alarm on a commanded-vs-actual mismatch until it has persisted for at least
**5 seconds.** A mismatch during the first few seconds after a command is normal.

---

## 5. Diagnostics — reading node health

Each node occupies 8 consecutive registers starting at `45001 + (N − 1) × 8`.

| Offset | Content | Encoding |
| --- | --- | --- |
| **+0** | Network Status | `1` = Online, `0` = Offline |
| **+1** | Main Power | `1` = OK, `0` = PSU failure — **gate on +0** |
| **+2** | Left arrows health | 4-state |
| **+3** | Right arrows health | 4-state |
| **+4** | Battery percentage | `0`–`100`, or `65535` = unknown |
| **+5** | Static Zone 1 health | 4-state |
| **+6** | Static Zone 2 health | 4-state |
| **+7** | **Actual executing mode** | mode number, or `65535` = unknown — **two causes** |

### 5.1 The 4-state health encoding

| Value | Meaning | Interpretation |
| --- | --- | --- |
| `0` | `OFF` | Commanded off, no current flowing. Correct. |
| `1` | `ON` | Commanded on, current flowing. Correct. |
| `2` | `FAIL_OPEN` | Commanded on, **no current** — burnt strip, broken wire, failed output. |
| `3` | `FAIL_SHORT` | Commanded off, **current flowing** — shorted output stage. |
| `99` | unknown | Node offline, or value not yet reported. **Not a fault — ignore.** |

`2` and `3` are genuine hardware faults requiring dispatch. `99` is an absence of
information and must never raise a health alarm.

### 5.2 ⚠️ Rule zero: gate everything on `+0`

**Check network status (`+0`) before interpreting any other register in the block.**

When a node is offline, the remaining seven registers hold stale or default values.
They are not merely unhelpful — several of them read as *specific, plausible faults*:

* `+1` reads `0`, identical to a genuine PSU failure.
* `+7` reads `65535`, identical to a hardware output fault.

Every alarm rule below is written on the assumption that you have already
established `+0 == 1`.

### 5.3 ⚠️ PSU failure alarm — must be gated

Register `+1` is binary with no unknown state, so an offline node reads `0`.
A naive `IF +1 == 0 THEN alarm` fires for every unreachable node.

This matters more than it first appears. **Each board has battery backup
specifically so that it survives losing mains and stays online to report it.**
A `power = FAIL` from an **online** node is the system's most valuable early
warning: that sign is running on borrowed time and will go dark when the battery
flattens. Letting every offline node raise the same alarm buries it.

```
IF  45001+(N-1)*8 == 1  AND  45002+(N-1)*8 == 0
    THEN "PSU FAILURE - node running on battery, dispatch"
```

### 5.4 ⚠️ Register `+7` — commanded vs actual

`40001+` holds what a node was **told** to do. `+7` holds what it **reports actually
doing**. When they differ, the sign is displaying something other than what every
screen in the control room says it is displaying. **Nothing else in the system
detects this.**

`65535` on `+7` has **two entirely different causes**:

1. **The node is offline or has never reported.** No information. `+7` is stale.
2. **The node is online but cannot control or verify its outputs.** The node
   actively reports this — it has lost contact with its output driver, or a write
   to the outputs failed verification. **This is a hardware fault requiring
   dispatch.**

**Required alarm logic — three-way branch:**

```
IF   45001+(N-1)*8 == 0
     THEN "NODE OFFLINE"                                    // ignore +7, it is stale
ELSE IF 45008+(N-1)*8 == 65535
     THEN "NODE CANNOT CONTROL OUTPUTS - hardware fault, dispatch"
ELSE IF 45008+(N-1)*8 <> 40000+N
     THEN "COMMAND NOT ACCEPTED - node running a different mode"
```

> ### ⚠️ Do not filter out `65535`
>
> An earlier revision of the developer documentation instructed integrators to
> alarm on commanded ≠ actual *only when `+7` is not `65535`*. That advice was
> correct when `65535` meant only "offline", and is **now wrong** — following it
> filters out the most serious fault the system can report.
>
> The `65535` fault **cannot be cleared by commanding anything.** This is
> deliberate. `65535` never equals a valid command, so an operator cannot
> accidentally clear the alarm by commanding a value that happens to match a stale
> readback while the sign stays dark. It clears only when the hardware is repaired.
>
> If this alarm appears to be "stuck", the sign is broken. It is not a nuisance
> alarm and must not be suppressed.

### 5.5 Battery reads `65535`, not `0`

Battery monitoring is **not yet implemented in the node firmware**. The register is
reserved and reads the `65535` "unknown" sentinel.

`65535` is used rather than `0` precisely because `0` would be indistinguishable
from a genuinely flat battery. **Do not alarm on this register.** Do not display it
as `0%`. Display it as "not available" or hide it until the feature ships.

### 5.6 Fault signature table

Use this to route a fault to the right response. The middle two rows were
indistinguishable in earlier firmware and need completely different repairs.

| Symptom | `+0` | `+7` | `+2`/`+3` | Diagnosis | Action |
| --- | --- | --- | --- | --- | --- |
| Node unreachable | `0` OFFLINE | stale | stale | Board dead, cable out, or **both** PSU and battery gone | Check link and power at the cabinet |
| Online, cannot drive outputs | `1` ONLINE | `65535` | `2` FAIL_OPEN | Output driver / internal bus fault | Dispatch — driver board |
| Online, outputs commanded but dark | `1` ONLINE | matches command | `2` FAIL_OPEN | LED strip, wiring or output stage | Dispatch — strip / wiring |
| Online, on battery | `1` ONLINE | matches command | normal | Mains or PSU failed | Dispatch — PSU. **Time-critical** |
| Online, wrong mode | `1` ONLINE | ≠ command | normal | Command rejected or missed | Re-command; if it persists, dispatch |
| Output on when commanded off | `1` ONLINE | matches command | `3` FAIL_SHORT | Shorted output stage | Dispatch — safety-relevant |

**During commissioning only:** a node undergoing bench calibration also reports
ONLINE with `+7 = 65535`, and its four health registers hold at their last value.
Expected on the bench; **treat as a genuine fault on a live site.**

### 5.7 Recommended alarm priorities

| Condition | Suggested priority |
| --- | --- |
| Online + `+7 == 65535` (cannot control outputs) | **High** — sign state unknown and uncontrollable |
| Online + `FAIL_SHORT` on any channel | **High** — output energised when it should not be |
| Online + `power == 0` (on battery) | **High** — will fail entirely when the battery flattens |
| Online + `FAIL_OPEN` on any channel | **Medium** — sign partly or wholly dark |
| Online + commanded ≠ actual, `+7` numeric, > 5 s | **Medium** — displaying the wrong thing |
| Offline | **Medium** — but see §6.2 before setting the delay |
| Battery register | **Do not alarm** — not implemented |

---

## 6. Timing, polling and startup behaviour

### 6.1 Poll rate

The gateway refreshes the entire diagnostic block **once per second**. Polling
faster returns identical data and wastes bandwidth on both networks.

**Recommended: poll the diagnostic block every 1–2 seconds.**

Underlying node telemetry updates on change, plus a full refresh from every node
every 60 seconds. A health state can therefore be up to ~1 second stale in normal
operation.

### 6.2 ⚠️ Gateway restart — expect a burst of OFFLINE

When the gateway starts, it clears stale data by marking **all 100 nodes offline**,
then asks every node to re-report. Nodes respond within a few seconds.

**A node that misses that first request stays reading OFFLINE for up to 60 seconds**
until the next scheduled refresh.

> **Required: inhibit offline alarms for 90 seconds after the Modbus connection is
> (re-)established.** Without this, every gateway restart floods the control room
> with up to 100 spurious offline alarms.

Combine this with §4.4: on reconnection, **re-assert all commands immediately**, then
hold alarms off for 90 seconds.

### 6.3 Detecting that the gateway itself is down

If the gateway process is not running, the TCP connection to port 502 is **refused**
— you get a connection error, not stale data. Treat loss of the Modbus connection as
its own high-priority alarm: while it persists, **every** sign state on your screens
is unknown, regardless of what was last read.

Do not display last-known values as current during a connection outage.

---

## 7. Commissioning checklist

Work through this before handover. Each step catches a specific, known failure mode.

**Connectivity**

1. PLC has an address in `10.45.2.0/24`. Confirm with a ping to `10.45.2.50`.
2. Modbus TCP connects on port `502`. Unit ID irrelevant.
3. Read `43001`. It must return `0` or `1`, not an exception.
4. Confirm FC 01 / 02 / 04 return an **exception**. If they return zeros, you are not
   talking to this gateway.

**Addressing — catches the 7→8 register error**

5. Read `45001`. This is node 1's network status: `0` or `1`.
6. Power on **node 2 only**. Confirm `45009` goes to `1` and `45008` does **not**.
   If node 2 appears at `45008`, your map is the old 7-register layout — stop and fix
   it before going further.
7. Confirm node 100 reads at `45793–45800`.

**Control path**

8. Set `43001 = 0` (SCADA/Auto).
9. Write `1` to `42001`. Within 1 s, `40001` must read `1`. Within ~3 s, `45008`
   must read `1`. The physical sign must show a left chase.
10. Write `0` to `42001`. Confirm the sign clears and `45008` returns to `0`.
11. Set `43001 = 1`. Write `2` to `42001`. Confirm `40001` does **not** change —
    this proves MUX lockout works. Confirm your SCADA screen shows the local-control
    banner. Set `43001` back to `0`.

**Alarm logic**

12. Disconnect one node's network cable. Confirm: `+0` → `0`, and that your PSU alarm
    (§5.3) does **not** fire, and your `+7` alarm reports "NODE OFFLINE" rather than
    a hardware fault. Reconnect.
13. With a node online, have the bench technician disconnect its output driver.
    Confirm `+7` → `65535` while `+0` stays `1`, and that this raises the
    **hardware fault** branch, not the offline branch.
14. Confirm the battery register reads `65535` and raises no alarm.

**Resilience — catches the write-once mistake**

15. Restart the gateway service. Confirm that within ~10 s **the PLC has re-asserted
    all commands** and the signs return to their commanded state without operator
    action. **If the signs stay dark, your PLC is writing on change only — fix it
    (§4.4).**
16. Confirm offline alarms are inhibited for 90 s after that restart (§6.2).

---

## 8. Quick reference

```
CONNECT      10.45.2.50 : 502   Modbus TCP   any unit ID   holding registers only
WIRE ADDR    PLC register - 40001

COMMAND node N       write  42000+N            0,1,2 normal   3-6,10000-11023 maintenance
CONFIRM node N       read   40000+N            must match within 1 s
MUX FLAG             r/w    43001              0 = SCADA controls   1 = HMI controls
DIAG node N base     read   45001 + (N-1)*8    8 registers

  +0 network   1=online 0=offline      +4 battery   65535=unknown (not implemented)
  +1 power     1=ok 0=fail (gate!)     +5 static Z1 4-state
  +2 left LED  4-state                 +6 static Z2 4-state
  +3 right LED 4-state                 +7 actual    mode, or 65535=unknown (2 causes)

4-STATE      0=OFF  1=ON  2=FAIL_OPEN  3=FAIL_SHORT  99=unknown/offline

ALARMS       gate EVERYTHING on +0 == 1 first
             PSU:  +0==1 AND +1==0                    -> "on battery, dispatch"
             +7:   +0==0                              -> "offline" (ignore +7)
                   +7==65535                          -> "cannot control outputs, dispatch"
                   +7 <> commanded (>5 s)             -> "command not accepted"

MUST DO      re-assert 42001+ CYCLICALLY - a gateway restart zeroes it and blanks every sign
             display a banner whenever 43001 == 1 - your writes are ignored
             inhibit offline alarms 90 s after (re)connect
             never write 40001+   never alarm on battery   never filter 65535
```

---

## 9. Known limitations at this revision

Disclosed so they are designed around rather than discovered in service.

| # | Limitation | Impact on SCADA |
| --- | --- | --- |
| 1 | **No register persistence.** A gateway restart zeroes the command buffer and blanks every sign. | Handled by cyclic re-assertion — §4.4. **Mandatory.** |
| 2 | **Nodes do not retain their command across a reboot.** A node that reboots while the sign network is down comes up dark and stays dark until the network returns. | A sign may be dark with no fault reported, because an offline node reports nothing. Covered by the offline alarm. |
| 3 | **Battery monitoring not implemented.** Register `+4` reads `65535`. | Do not alarm; do not display as a percentage. |
| 4 | **Maintenance modes are not gated on the Modbus interface.** The HMI PIN does not apply to SCADA writes. | Enforce in the PLC program if required. |
| 5 | **Calibration is indistinguishable from an output fault** (both give ONLINE + `65535`). | Only occurs during commissioning. Treat as a real fault in service. |
| 6 | **No authentication on Modbus.** Any host on `10.45.2.0/24` can command every sign. | Security is by network isolation only. Control physical and switch access to that segment. |

---

## 10. Escalation

| Symptom | First check |
| --- | --- |
| Cannot connect at all | PLC address inside `10.45.2.0/24`; `ping 10.45.2.50`; gateway service running |
| Connects, all registers zero | Reading the wrong function code — confirm holding registers (FC 03) |
| Connects, all nodes offline | Sign network down, or MQTT broker not running on the Pi |
| Commands accepted, nothing happens | Read `43001` — local HMI has control |
| Signs went dark after a Pi restart | PLC is not re-asserting commands cyclically — §4.4 |
| One node reads plausible but wrong data | Register map off by one node — confirm node 2 is at `45009`, §3.2 |
| Every node's data shifted by one slot | 7-vs-8 register layout error — §3.2 |
