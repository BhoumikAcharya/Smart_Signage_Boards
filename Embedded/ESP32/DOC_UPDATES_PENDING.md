# Documentation Updates — PENDING

**Created:** 2026-07-21, after `Firmware.ino` v3.0.0 was written and compiled.
**Status:** deliberately deferred until the 6 bench nodes have been validated.

**Why deferred:** several values in v3.0.0 are placeholders that will change once
real hardware is measured (calibration constants, PSU thresholds, settle timing).
Writing the docs now would mean writing them twice. This file is the checklist so
nothing is lost in between.

**How to use:** work top to bottom after bench validation. Every entry gives the
file, the line, what is currently wrong, and what it should say.

---

## 0. BLOCKING — must be answered on the bench before docs can be written

These are unresolved facts, not writing tasks. Docs cannot be completed without
them.

| # | Question | Where it lands |
| --- | --- | --- |
| B1 | **Chase direction.** Does the physical arrow sweep the intended way? See §6 — the production frame table and the `Moving_LEDs` reference disagree. | `esp32_contract.md` §2, `firmware_doc.md` |
| B2 | **ACS712 variant per channel.** Not recorded anywhere in the repo. Read the marking off the parts. | `esp32_contract.md` §1, `Connections.md` |
| B3 | **Expected load currents** — per arrow strip, per static zone. Currently `0.000` placeholders. | `Firmware.ino` header, `esp32_contract.md` §5 |
| B4 | **PSU fail/recover thresholds.** Must sit below mains voltage but above loaded battery voltage. | `esp32_contract.md` §6a, SCADA operator doc |
| B5 | **`THRESH_OFF_DETECT` viability.** Does an idle channel wander past 0.030 A and raise false `FAIL_SHORT`? | `esp32_contract.md` §5 |
| B6 | **`MODE_SETTLE_MS` viability.** Does the LED load actually settle within 1200 ms? | `esp32_contract.md` §5 |
| B7 | **A5–A7 / B5–B7 genuinely unconnected?** The MCP health pattern depends on it. | `esp32_contract.md` §1 |

---

## 1. `RaspberryPi/Gateway/Gateway_doc.md`

### 1.1 — Line 126: meaning of `65535` on register `+7` — **HIGH**

Currently reads as a single-cause sentinel. It now has **two distinct causes**
and they need different operator responses:

* node is offline or has never reported (pre-existing), **and**
* node is ONLINE but cannot reach or verify its MOSFET driver (new in v3.0.0).

### 1.2 — Line 136: the alarm rule is now **wrong** — **CRITICAL**

Currently instructs:

> SCADA should alarm on `40001+i != 45008+(i*8)` whenever the node is online and
> register `+7` is **not** `65535`.

That was correct when `65535` only meant "offline / not yet reported". It now
tells the integrator to **filter out the most serious fault in the system**.
Replace with the three-way branch:

```
IF 45001+(i*8) == 0
    THEN "NODE OFFLINE"                          // ignore +7, it is stale
ELSE IF 45008+(i*8) == 65535
    THEN "NODE CANNOT CONTROL OUTPUTS - hardware fault, dispatch"
ELSE IF 45008+(i*8) != 40001+i
    THEN "COMMAND NOT ACCEPTED - node running a different mode"
```

Add the operating principle explicitly: **check network status (`+0`) before
trusting any other register in the block.** This already applies to the PSU alarm
at line 147; it is now a general rule for this map, not a one-off.

Also state that an online node reporting `65535` raises an alarm that **cannot be
cleared by commanding anything** — it clears only when the hardware is fixed.
That is deliberate. The integrator must not "fix" it with a filter.

### 1.3 — Fault signature table — **MEDIUM**

Add. Four faults that were previously ambiguous now have distinct signatures:

| Fault | `status` | `state` (+7) | `current1/2` |
| --- | --- | --- | --- |
| Board dead / cable out / PSU+battery gone | OFFLINE | stale | stale |
| I2C / MCP23017 fault | ONLINE | 65535, mismatch | FAIL_OPEN |
| LED strip or MOSFET fault | ONLINE | matches commanded | FAIL_OPEN |
| PSU failed, running on battery | ONLINE | matches commanded | normal |

Rows 2 and 3 were previously indistinguishable and have completely different
repair actions.

---

## 2. `RaspberryPi/pi_agent.md`

### 2.1 — Line 132 — **HIGH**
Same `65535` two-cause update as §1.1.

### 2.2 — Line 136 — **CRITICAL**
Same alarm-rule replacement as §1.2. Currently carries the identical wrong
"and `+7 != 65535`" instruction.

### 2.3 — §4 / §5 command encoding table — **HIGH**
**Known stale.** Still documents the old 2-relay `0–3` model. Real encoding is
`0–6` plus the `10000–11023` bitmask, arrows only. `Pi.py` passes the integer
through untouched, so only the doc is wrong — but it is the doc an integrator
would read first.

### 2.4 — Line 372, item 2 — **LOW**
Says the diag block is "now 7 registers/node (`45001–45700`)". It is **8**
(`45001–45800`). Superseded by the 2026-07-20 change; the line was not updated.

---

## 3. `ESP32/esp32_contract.md`

### 3.1 — §5, stale-threshold failure direction is **backwards** — **HIGH**

Currently: *"Carrying a stale threshold from a previous mode makes a healthy
board report `FAIL_OPEN`."*

Working through `getDiscrepancyState()`: when a side is expected OFF, a stale
**high** threshold means a genuinely shorted MOSFET drawing three strips' worth
of current still reads below it and reports **`OFF`**. The bug **masks a real
`FAIL_SHORT`** — silent fault suppression, not a nuisance alarm. Same fix, worse
consequence than documented. Correct the wording so the severity is right.

### 3.2 — §3, `state` payload semantics — **HIGH**

Document the two divergence causes and their different payloads:

* **Command rejected** → keep publishing the previous **numeric** value. The node
  is still faithfully executing it and knows so.
* **MCP unreachable / readback mismatch / calibration in progress** →
  non-numeric payload (v3.0.0 publishes `FAULT`), which the gateway maps to
  `65535`.

Include the rationale: reusing a stale number lets an operator clear the alarm by
accident — command the value that happens to match and the flag clears while the
sign stays dark. `65535` never equals a valid command.

### 3.3 — §1, ACS712 variant guidance — **MEDIUM**

Add the resolution analysis, since it drives which part goes where. One strip at
`THRESH_PER_STRIP` (0.060 A):

* 20 A part (0.100 V/A) → 6.0 mV ≈ **7 ADC counts** — marginal vs ADC noise
* 5 A part (0.185 V/A) → 11.1 mV ≈ **14 ADC counts**

Whole-side detection is fine either way (~22 counts). Single-strip-out is the
case at risk. Recommendation: **5 A on arrows, 20 A on static zones.** Record
what is actually fitted (B2).

### 3.4 — §1, MCP health pattern — **MEDIUM**

Undocumented. v3.0.0 drives `0xA0` into the unused A5–A7 / B5–B7 pins and reads
it back. Without it a dead I2C bus reads `0x00`, which **matches** a legitimate
"all MOSFETs off" write — so mode 0 could never be verified. Document the
dependency on those pins being unconnected (B7).

### 3.5 — New section: mode-change settle — **MEDIUM**

Undocumented and non-obvious. At `ALPHA = 0.15` and a 500 ms cadence the IIR
filter needs ~14 samples (~7 s) to reach 90% of a step, and the solid-ON
threshold sits at exactly 90% of expected. Without intervention **every** off→on
mode change publishes `FAIL_OPEN` for ~7 s. v3.0.0 snaps the filter on a mode
change and suppresses evaluation for `MODE_SETTLE_MS`. Document both, and the
constraint that `NOISE_FLOOR` must stay below `THRESH_OFF_DETECT`.

### 3.6 — §6a, PSU debounce and hysteresis — **MEDIUM**

Undocumented. v3.0.0 requires 3 consecutive agreeing reads (1.5 s) and uses a
hysteresis band (fail below `PSU_FAIL_VOLTS`, recover above `PSU_OK_VOLTS`).
Rationale: `power=FAIL` from an online node is the system's critical alarm, so
false positives are expensive.

### 3.7 — §8, gap list — **LOW**

All 9 gaps are addressed in v3.0.0. Mark resolved and keep as a regression list
rather than a work list, matching the convention already used in
`pi_agent.md:262`.

---

## 4. `ESP32/Firmware/firmware_doc.md`

**Full rewrite required — HIGH.** Still documents v2.0. Must cover, at minimum:

* Boot order is **network first, MCP second** — and why (an MCP fault must not
  silence the node)
* Validated command parsing; `atoi()` is gone
* `state` topic and verified writes
* Animation runs unconditionally, including through a network outage
* Modes 4/5/6, and that **mode 4 changed meaning** (was solid-ON-all, now
  solid-ON-left; old behaviour is mode 6)
* The serial menu (`h i r s c g v m p`)
* Calibration workflow: run `c` / `g` / `v`, press `p`, paste, reflash
* What is **not** implemented: NVS, OTA, battery
* **Known limitation:** without NVS, a reboot during a network outage leaves the
  sign dark — no retained `value` arrives to re-command it. This is the half of
  "hold last command" that does not yet survive a reboot. Acceptable on a bench,
  **not** acceptable for a site install.

Archive the v2.0 doc alongside `Version History/Firmware_2.0.0/` (already copied
there 2026-07-21).

---

## 5. New documents to create

### 5.1 — SCADA operator / integrator doc — **CRITICAL, does not exist**

The integrator currently has no single authoritative document. Must contain:

* Full `40001+` and `45001+` register maps, 8 registers per node
* The three-way `+7` alarm branch (§1.2)
* The PSU gating rule: `IF +0 == 1 AND +1 == 0 THEN "PSU FAILURE"` — an offline
  node otherwise reads as a PSU failure and buries the real battery-backed alarm
* The general principle: gate every register interpretation on `+0` first
* The fault signature table (§1.3)
* **The 7 → 8 register renumbering.** Any PLC program written against the old
  7-register map breaks. Node 1 `45001–45008`, node 2 `45009–45016`, node 100
  `45793–45800`.
* Battery reads `65535` (unknown), not `0` — it is stubbed, not flat

### 5.2 — Bench commissioning checklist — **HIGH**

For the 6-node round:

1. Read and record the ACS712 variant per channel (B2)
2. Confirm A5–A7 / B5–B7 unconnected (B7)
3. Flash, confirm MCP found on SDA=4 / SCL=13
4. `v` — divider calibration against a multimeter
5. `c` — zero calibration (arrows measured, static borrowed)
6. Fill `EXPECTED_*`, then `g` — gain calibration
7. `p` — capture the CSV line
8. **Compare all 6 nodes.** Tight agreement validates the assumed currents;
   scatter means the strips, sensors or wiring genuinely vary
9. Idle-channel watch on `s` for false `FAIL_SHORT` (B5)
10. Mode-change watch for false `FAIL_OPEN` (B6)
11. Pull the I2C connector live — expect `state` = 65535 with the node still
    ONLINE
12. Verify chase direction against intent (B1)

---

## 6. `PCB/PCB_V3/Connections.md`

* **Only 3 ACS712 documented** (lines 28–31). The 4th (GPIO33, Static Zone 2) is
  missing. — **HIGH**
* **No part numbers** for the ACS712s (B2). — **HIGH**
* Line 15 still says "all 5x MP1584". Finalized hardware is **4x**, no floating
  spare. — **MEDIUM**
* I2C pins: confirm the doc reflects SDA=4 / SCL=13. — **MEDIUM**
* **Undocumented: physical strip order.** Which physical arrow position each of
  A0–A4 and B0–B4 drives is recorded nowhere. This is what makes B1 unanswerable
  from the repo alone. — **HIGH**

---

## 7. Referenced but missing

`pi_agent.md:372` cites `hmi-specification.md` and `architecture-overview.md` as
needing updates. **Neither exists in this repository.** Either they live
elsewhere, or the reference is stale. Resolve the reference or delete it.

---

## 8. Note on the chase frame table (B1)

The production frame table and the `Moving_LEDs` reference implementation
disagree about direction. This is unresolved and must be settled on hardware —
see the bench checklist item 12 and `Connections.md` §6 above. Whichever way it
resolves, the outcome needs recording in `esp32_contract.md` §2, which currently
lists the frame values with no statement of intended direction at all.
