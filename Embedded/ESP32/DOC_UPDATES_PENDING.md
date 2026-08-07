# Documentation Updates — PENDING

**Created:** 2026-07-21, after `Firmware.ino` v3.0.0 was written and compiled.
**Status:** deliberately deferred until the 6 bench nodes have been validated.

**Why deferred:** several values in v3.0.0 are placeholders that will change once
real hardware is measured (calibration constants, PSU thresholds, settle timing).
Writing the docs now would mean writing them twice. This file is the checklist so
nothing is lost in between.

**How to use:** work top to bottom after bench validation. Every entry gives the
file, the line, what is currently wrong, and what it should say.

> **Progress, 2026-07-27.** §1 (`Gateway_doc.md`) and §2 (`pi_agent.md`) are
> **DONE** — none of them depended on bench measurements, only on reading the
> shipped v3.0.0 firmware. §7 is **resolved**. Everything still open (§0, §3–§6)
> is genuinely blocked on the 6-node bench round or belongs to the firmware/PCB
> docs. See the per-section markers below.

> ## **Progress, 2026-08-07 — every unblocked documentation item is now closed.**
>
> **Done this session:** §3 (`esp32_contract.md`), §4 (`firmware_doc.md` full
> rewrite), §9 (`HMI_doc.md`), §10 (root `README.md`), and a newly-found §11
> (`Firmware_Test/README.md`). As with §1/§2, none of it needed bench data — only a
> careful read of shipped v3.0.0.
>
> **Six items this checklist did not contain were found and fixed:** §3.8, §3.9,
> both halves of §11, and — after a full read rather than a keyword scan — nine
> further errors in the root README beyond the five listed in §10.
>
> **Three checklist entries were wrong about themselves**, on top of §2.3 which the
> 07-27 pass already caught:
> * §3.7 claimed all 9 gaps were closed in v3.0.0. Real figure: **7 of 9** — and NVS
>   was one of the two still open.
> * §6 asked to "confirm" I2C pins in `Connections.md` that were **already correct**.
> * §10's five bullets understated the README by nine further errors.
>
> **Verify every entry against the file before acting on it.** This checklist is a
> starting point, not a specification.
>
> ### Still open — every remaining item is blocked on hardware, or is code
>
> **No documentation work remains that can be done at a desk.**
>
> | Item | Blocked on |
> | --- | --- |
> | §0, B1–B7 | The 6-node bench round. B1/B2 now also recorded in `Connections.md` §7 |
> | §5.2 bench checklist | Structurally unblocked, but it is the *output* of the bench round — write it as the round is run |
> | §6 `Connections.md` | Unblocked items ✅ done. Only B1 (strip order) and B2 (part numbers) remain, held in its §7 |
> | §11.1 | A **code** change to `Firmware_Test.ino` (`SENS_* = 0.146` disagrees with production), not a doc change |
>
> **Next session is the bench.** Take §5.2's 12-step checklist and this file's §0
> table to the boards; every open question is answered there. §11.1 can be done any
> time by someone with the toolchain.

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

## 1. `RaspberryPi/Gateway/Gateway_doc.md` — ✅ DONE 2026-07-27

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

### 1.4 — ✅ Done, and one item this checklist missed

Also corrected while in the file:

* **The NVS claim in §6 was wrong.** The PING correction block asserted the last
  command "is persisted to NVS and re-applied on boot, so it survives a reboot
  with the network still down." v3.0.0 defers NVS — the node boots to mode `0`
  and waits for the retained `value`. A reboot during a network outage leaves
  the sign **dark**. Amended in place; also raised as `pi_agent.md` §12 item 10.
* Calibration is a **third** cause of `state` = `FAULT` (alongside MCP
  unreachable and readback mismatch), and current telemetry is held at its last
  value while calibrating. Added to the fault-signature table so a bench session
  is not mistaken for a field fault.
* "welded relay" → "shorted MOSFET" in the 4-state translation. The hardware has
  no relays.

---

## 2. `RaspberryPi/pi_agent.md` — ✅ DONE 2026-07-27

### 2.1 — Line 132 — **HIGH** — ✅ done
Same `65535` two-cause update as §1.1.

### 2.2 — Line 136 — **CRITICAL** — ✅ done
Same alarm-rule replacement as §1.2. Carried the identical wrong
"and `+7 != 65535`" instruction; replaced with the three-way branch, plus the
fault-signature table.

### 2.3 — §4 / §5 command encoding table — ~~**HIGH**~~ — ❌ **this entry was wrong**

Checked against the file: §4 already carries the correct `0–6` + `10000–11023`
arrows-only table, explicitly marked "CORRECTED 2026-07-20". The obsolete 2-relay
`0–3` model is **not** present. This checklist entry was itself stale.

What §5 *did* need, and got: `state` may carry the non-numeric payload `FAULT`;
`battery_pct` is never published at all by v3.0.0; and the two divergence cases
(rejected command → previous number, unverifiable outputs → `FAULT`) now have
their own table.

### 2.4 — Line 372, item 2 — **LOW** — ✅ done
Corrected to **8** registers/node (`45001–45800`). While in §12: items 1/6/7/8
marked resolved against `hmi_contract.md`, item 4 re-pointed at the real file,
and items 10–12 added (NVS gap, `+7` two-cause alarm, missing SCADA doc).

---

## 3. `ESP32/esp32_contract.md` — ✅ DONE 2026-08-07

All seven entries below are complete, plus two this checklist missed (§3.8, §3.9).
The file now carries a "last reconciled against code" date in its header, matching
the convention `Gateway_doc.md` uses.

### 3.1 — §5, stale-threshold failure direction is **backwards** — **HIGH** — ✅ done

Currently: *"Carrying a stale threshold from a previous mode makes a healthy
board report `FAIL_OPEN`."*

Working through `getDiscrepancyState()`: when a side is expected OFF, a stale
**high** threshold means a genuinely shorted MOSFET drawing three strips' worth
of current still reads below it and reports **`OFF`**. The bug **masks a real
`FAIL_SHORT`** — silent fault suppression, not a nuisance alarm. Same fix, worse
consequence than documented. Correct the wording so the severity is right.

### 3.2 — §3, `state` payload semantics — **HIGH** — ✅ done

Document the two divergence causes and their different payloads:

* **Command rejected** → keep publishing the previous **numeric** value. The node
  is still faithfully executing it and knows so.
* **MCP unreachable / readback mismatch / calibration in progress** →
  non-numeric payload (v3.0.0 publishes `FAULT`), which the gateway maps to
  `65535`.

Include the rationale: reusing a stale number lets an operator clear the alarm by
accident — command the value that happens to match and the flag clears while the
sign stays dark. `65535` never equals a valid command.

### 3.3 — §1, ACS712 variant guidance — **MEDIUM** — ✅ done (B2 still open)

Add the resolution analysis, since it drives which part goes where. One strip at
`THRESH_PER_STRIP` (0.060 A):

* 20 A part (0.100 V/A) → 6.0 mV ≈ **7 ADC counts** — marginal vs ADC noise
* 5 A part (0.185 V/A) → 11.1 mV ≈ **14 ADC counts**

Whole-side detection is fine either way (~22 counts). Single-strip-out is the
case at risk. Recommendation: **5 A on arrows, 20 A on static zones.** Record
what is actually fitted (B2).

### 3.4 — §1, MCP health pattern — **MEDIUM** — ✅ done (B7 still open)

Undocumented. v3.0.0 drives `0xA0` into the unused A5–A7 / B5–B7 pins and reads
it back. Without it a dead I2C bus reads `0x00`, which **matches** a legitimate
"all MOSFETs off" write — so mode 0 could never be verified. Document the
dependency on those pins being unconnected (B7).

### 3.5 — New section: mode-change settle — **MEDIUM** — ✅ done, as §5a

Undocumented and non-obvious. At `ALPHA = 0.15` and a 500 ms cadence the IIR
filter needs ~14 samples (~7 s) to reach 90% of a step, and the solid-ON
threshold sits at exactly 90% of expected. Without intervention **every** off→on
mode change publishes `FAIL_OPEN` for ~7 s. v3.0.0 snaps the filter on a mode
change and suppresses evaluation for `MODE_SETTLE_MS`. Document both, and the
constraint that `NOISE_FLOOR` must stay below `THRESH_OFF_DETECT`.

### 3.6 — §6a, PSU debounce and hysteresis — **MEDIUM** — ✅ done (B4 still open)

Undocumented. v3.0.0 requires 3 consecutive agreeing reads (1.5 s) and uses a
hysteresis band (fail below `PSU_FAIL_VOLTS`, recover above `PSU_OK_VOLTS`).
Rationale: `power=FAIL` from an online node is the system's critical alarm, so
false positives are expensive.

### 3.7 — §8, gap list — ~~**LOW**~~ — ✅ done, but **this entry's premise was wrong**

The entry claimed "all 9 gaps are addressed in v3.0.0." Checked against the
source: **7 of 9**. Gap 5 (NVS persistence) is deliberately deferred and gap 9
(voltage-divider ratios) is still a placeholder. Marking all nine resolved would
have buried the NVS gap in a table captioned "closed".

Done as specified otherwise — converted to a regression list on the
`pi_agent.md` §8 convention, with per-item status. Four gaps that v3.0.0
*introduced* were added as items 10–13 (PSU thresholds, `THRESH_OFF_DETECT`,
`MODE_SETTLE_MS`, expected currents), all of which map to §0 bench questions.

### 3.8 — §6 asserted NVS persistence — ✅ **FIXED** — was **CRITICAL**, not on this list

`esp32_contract.md` §6 carried the exact false claim that was hunted down and
corrected in `Gateway_doc.md` §6 on 2026-07-27: *"the active command is persisted
to NVS on every change and re-applied on boot, so it survives a reboot with the
network still down."* v3.0.0 has no `Preferences` and no `nvs_*` calls at all.

**The 07-27 pass fixed the gateway doc and `pi_agent.md` §12 item 10 but never
checked the contract**, so for eleven days the ESP32 contract — the document a
firmware author would actually build from — directly contradicted both. Replaced
with the three-scenario table and the bench-vs-site warning.

**Lesson for the remaining sections: a correction applied to one document must be
grepped for across all of them.** The same false claim also survives in
`Firmware/firmware_doc.md` (§4) and in `HMI_doc.md` (§9).

### 3.9 — §1 and §8 warned about already-fixed bugs — ✅ **FIXED** — not on this list

§1 called `I2C_SCL 17` *"the single highest-priority fix"* and §8 listed the pin
collision, the missing 4th ACS712 and the absent modes 4/5/6 as open Critical/High
gaps. All were fixed in v3.0.0 before this checklist was even created.

A contract that cries wolf about resolved problems trains its readers to skim, and
the two genuinely open gaps in that table (5 and 9) were sitting in the same list.
Reworded to record the fix, retaining the GPIO17 rationale — the wrong pins are
still published in `firmware_doc.md` and `Connections.md` §3, so the collision can
still reach someone through those files.

---

## 4. `ESP32/Firmware/firmware_doc.md` — ✅ DONE 2026-08-07

Full rewrite complete. Every bullet below is covered, plus the MCP health pattern,
the verified-write mechanism, telemetry suppression, the threshold ordering
constraint, and the three things the calibration routines structurally *cannot* do
(static zero borrowed from the arrows, divider ratios needing a multimeter, gain
derived against an assumed current).

The v2.0 doc was confirmed byte-identical to the copy already at
`Version History/Firmware_2.0.0/firmware_doc.md` before being replaced, and the new
file links to that archive.

**Original requirement list, all covered:**

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

### 5.1 — SCADA operator / integrator doc — ✅ **DONE 2026-07-27**

Created: **`RaspberryPi/Gateway/SCADA_Integration_Guide.md`**. Self-contained —
an integrator needs no other file. Everything on the original list is covered:

* ✅ Full `40001+` and `45001+` register maps, 8 registers per node
* ✅ The three-way `+7` alarm branch (§1.2)
* ✅ The PSU gating rule, with the battery-backup rationale for why it matters
* ✅ The general principle: gate every register interpretation on `+0` first
* ✅ The fault signature table (§1.3), extended with recommended alarm priorities
* ✅ The 7 → 8 register renumbering, plus a commissioning step that *detects* the
  old layout (power node 2 alone; it must appear at `45009`, not `45008`)
* ✅ Battery reads `65535`, not `0` — with an explicit "do not alarm" instruction

Added beyond the original list, because an integrator would otherwise hit them
in service:

* **Cyclic command re-assertion is MANDATORY.** The Modbus datastore has no
  persistence — a gateway restart zeroes `42001+` and publishes `0` to all 100
  nodes, blanking every sign. A write-on-change PLC program passes every test and
  then blacks out the installation on the first service restart. Raised as
  `pi_agent.md` §12 item 13.
* **MUX lockout is silent.** With `43001 == 1` the PLC's writes are accepted and
  discarded with no error. The guide requires a local-control banner on the SCADA
  screen.
* **90 s offline-alarm inhibit after (re)connect** — startup cleanup marks all 100
  nodes offline, and a node that misses the first PING stays that way for up to
  60 s.
* Modbus transaction limits: the 800-register diag block needs 7 reads of 120
  (FC03 caps at 125); boundaries chosen so no node is split across two reads.
* FC 01/02/04 return exceptions — a positive check that you are talking to the
  right device.
* Commissioning checklist, escalation table, and a one-screen quick reference.

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

## 6. `PCB/PCB_V3/Connections.md` — 🟡 unblocked items DONE 2026-08-07

Everything that could be done without the boards is done. The two genuinely
hardware-blocked items (B1, B2) are now **recorded inside the file itself** as a §7
"UNRESOLVED" section, rather than existing only in this checklist — so someone
reading the wiring sheet at the bench sees what still needs answering, and knows
this file is where the answer goes.

* ✅ **4th ACS712 added.** Only three were documented; GPIO33 / Static Zone 2 was
  missing entirely. Now a 4-row table with the switched/always-on distinction.
* 🔴 **Part numbers (B2)** — still open, now §7.1 with the resolution analysis
  showing why it matters (single-strip detection margin: ~7 ADC counts on a 20A
  part vs ~14 on a 5A).
* ✅ **"5x MP1584" → 4x.** It appeared in **three** places, not just line 15 — the
  section heading, the "IN (All 5 units)" line and the common-ground net — plus a
  "Buck 4 OUT (12V): Float/No Connection (Expansion buffer)" entry that had to be
  deleted and the 5.0V unit renumbered 5 → 4. Also recorded *why* all four tap the
  bus post-diode (the battery must carry the LED rails, or the PSU-fail alarm
  arrives with a false `FAIL_OPEN` cascade).
* ~~I2C pins: confirm the doc reflects SDA=4 / SCL=13.~~ — ✅ **was already correct**,
  verified 2026-08-07. This sub-item was never checked before being written down.
  Both pin lines are now annotated as verified, and §3's `GPIO 17 → ETH_CLK` carries
  an explicit "this is correct, do not 'fix' it by analogy with the I2C move"
  warning — that line is the one someone tidying the file would plausibly break.
* ✅ **Nomenclature:** Load 3 → **Static Zone 1**, Load 4 → **Static Zone 2**. Done
  throughout, including the buck rails, which said "Zone A"/"Zone B". **This closes
  `pi_agent.md` §12 item 4**, which named this file as its owner.
* 🔴 **Physical strip order (B1)** — still open, now §7.2. Explicitly stated as the
  one missing fact that makes the chase direction unanswerable from the repo alone,
  with instructions to record it as an MCP-pin → physical-position table and to
  carry the outcome into `esp32_contract.md` §2.
* ✅ **Added beyond this list:** an explicit "A5–A7 / B5–B7 must stay UNCONNECTED"
  entry in §5. This is B7, and it was a **latent build hazard** — the six pins read
  as spare capacity on a wiring sheet, but firmware drives a health pattern through
  them to detect a dead I2C bus. Anything wired there causes a permanent
  un-clearable fault. Nothing in this repo said so where a builder would look.

---

## 7. Referenced but missing — ✅ RESOLVED 2026-07-27

`pi_agent.md` §12 cited `hmi-specification.md`, `architecture-overview.md` and
`pin-map.md`. **None of the three exists in this repository**, and `git log`
shows they never did.

Resolution: `hmi_contract.md` (2026-07-20) is the HMI source of truth and already
carries what items 1, 6 and 8 asked for; §9 of `pi_agent.md` carries the
committed IPs (item 7); the pin nomenclature belongs in
`PCB/PCB_V3/Connections.md` (item 4). §12 has been re-pointed accordingly, with a
note at the top of the section recording that the three cited documents do not
exist — so the next reader does not go looking for them again.

---

## 8. Note on the chase frame table (B1)

The production frame table and the `Moving_LEDs` reference implementation
disagree about direction. This is unresolved and must be settled on hardware —
see the bench checklist item 12 and `Connections.md` §6 above. Whichever way it
resolves, the outcome needs recording in `esp32_contract.md` §2, which currently
lists the frame values with no statement of intended direction at all.

---

## 9. `RaspberryPi/HMI/HMI_doc.md` — ✅ DONE 2026-08-07 — was **NOT PREVIOUSLY TRACKED**

Last touched **2026-05-06**, labelled "Document Version 1.0 (Baseline)". It was the
oldest live document in the repo and this checklist never indexed it. All five
items below are done; the file is now v1.1.

**§9.4's open question was resolved by following the repo's existing convention:**
`HMI_doc.md` is kept as the *implementation and design-rationale* view with a
pointer at the top to `hmi_contract.md` as source of truth — the same split as
`Gateway_doc.md` ↔ `SCADA_Integration_Guide.md`. Retiring it would have thrown away
the design rationale in §1.2/§2.2 (why the architecture is decoupled, why the
custom virtual keyboard exists), which is recorded nowhere else.

### 9.1 — Line 79: the dead-man switch myth — **HIGH**

Still reads: the BROADCAST PING button *"publishes PING to metro/signage/scan to
reset the 5-minute hardware fail-safes on all ESP32s."*

**No such timer has ever existed.** This was established on 2026-07-20 and
corrected in `Gateway_doc.md` §6, `esp32_contract.md` §6, `pi_agent.md` §12 item 5
and in `HMI.py` itself — whose ping panel was reworded on 2026-07-27. The
`HMI_doc.md` copy is the **last surviving instance of the claim** in the
repository, and `pi_agent.md` §12 item 5 already flags it in red.

Replace with the PING's real purpose: it makes every node re-publish its full
telemetry set, so the gateway can rebuild state without waiting for a value to
change.

### 9.2 — Line 77: ingests only `current1`/`current2` — **HIGH**

Describes pre-2026-07-27 `HMI.py`: *"Parses status, power, current1, current2, and
battery_pct."* The code now ingests **all four** current channels plus `state`, and
renders a commanded-vs-actual panel. This is `pi_agent.md` §12 item 3, which
records the code as fixed and the doc as not.

Also drop `battery_pct` from the ingest list, or mark it explicitly unpopulated —
firmware v3.0.0 never publishes it.

### 9.3 — Panel size — **MEDIUM**

If the doc states a display geometry, it must say **1280x800** (Waveshare 10.1"
DSI, confirmed 2026-07-20). `HMI.py` was rescaled on 2026-07-27. See
`hmi_contract.md` §1 and `pi_agent.md` §12 item 8.

### 9.4 — Relationship to `hmi_contract.md` — **MEDIUM**

Two HMI documents now exist with no stated relationship. `hmi_contract.md`
(2026-07-20) is the source of truth. `HMI_doc.md` should either be reduced to an
implementation/operator view with a pointer at the top — the pattern
`Gateway_doc.md` uses for `SCADA_Integration_Guide.md` — or be retired outright.
**Decide which before rewriting it**, or the same divergence recurs.

---

## 10. Root `README.md` — ✅ DONE 2026-08-07 — was **NOT PREVIOUSLY TRACKED**

Last touched **2026-02-17**. It predated the entire relay → MOSFET architecture
change and described hardware that no longer exists.

> **It was materially worse than the five items below**, which were written from a
> keyword scan rather than a full read. The full read turned up **nine more**
> errors, several of which would actively mislead someone setting the system up:
>
> * **"wireless emergency signs"** (line 5) and **"wirelessly relayed"** (line 134)
>   — there is no radio anywhere in this system. The same sentence also said "all
>   through ethernet connectivity", so it contradicted itself.
> * **"Update the Wi-Fi credentials"** in the ESP32 setup steps — there are none.
> * **The worked example was wrong end to end**: it described writing `0-3` to
>   `40001-40010`, publishing payload `ON` to
>   `metro/station1/platformA/emergency_exit/set`. The real encoding is `0`–`6`
>   plus `10000`–`11023`, published as a bare integer to
>   `metro/signage/register/<40001+i>/value`, and the PLC writes `42001+`, not
>   `40001+`.
> * **`pip3 install pymodbus paho-mqtt`** — refused outright by PEP 668 on Debian
>   13, and it pinned nothing. The pinned versions are load-bearing.
> * **`sudo python3 modbus_mqtt_bridge.py`** — wrong filename (it is `Pi.py`), and
>   running the bridge as root is explicitly the *wrong* approach; `pi_agent.md`
>   §11 specifies `CAP_NET_BIND_SERVICE` instead.
> * **`esp32_mqtt_client.ino`** — no such file; it is `Firmware/Firmware.ino`.
> * **"2x ACS712"** and **"2x 12V 12W LEDs"** — four sensors, twelve strips.
> * **7-inch HMI** in two places — the panel is 10.1".
> * Section 7.3 was mis-numbered as "3.".
>
> Rewritten in full. The structure, numbering and informal voice are preserved; a
> pointer table to the five authoritative documents was added at the top.

**Original five items, all fixed:**

* **Line 29:** nodes are *"responsible for the final physical action—activating
  the LEDs via a relay."* — **HIGH**
* **Line 57, BOM:** *"5V Dual Channel Relay Modules (to switch power to the LED
  signage)"*. The design uses **10 logic-level N-channel MOSFETs** driven by an
  MCP23017. — **HIGH**
* **Line 104:** *"toggling a GPIO pin to activate its connected relay"*. The ESP32
  drives no LED GPIO directly; everything goes through the I2C expander. — **HIGH**
* **Line 134:** *"commands ... will be wirelessly relayed"*. The ESP32 link is
  **wired Ethernet** (LAN8720). There is no wireless path anywhere in the system.
  — **HIGH**
* **Line 43:** battery diagnostics described as "still in development" — accurate,
  but should point at the `65535`-means-unknown behaviour rather than implying a
  value is published. — **LOW**

**Why this ranks above its severity.** It is the first file anyone opens — a new
contributor, an integrator, or a reviewer — and every architectural claim in it is
wrong. The four HIGH items are one editing pass with no open questions.

---

## 11. `ESP32/Firmware_Test/README.md` — ✅ DONE 2026-08-07 — **NOT PREVIOUSLY TRACKED**

Found by acting on the §3.8 lesson: after correcting the NVS claim in
`esp32_contract.md`, the claim was grepped for across every `.md` in the repo. It
turned up in **two more places in this file** — a document this checklist never
indexed, and which is otherwise the best-maintained hardware reference in the repo.

* **§2.5** asserted the last command is *"persisted to NVS (flash) on change and
  reloaded+applied on boot, so the last state survives a reboot even if the network
  is still down."* — **CRITICAL**, ✅ corrected in place.
* **§4 Decision Log item 1** repeated it as a *locked* decision — which is how the
  claim kept propagating into new documents. ✅ Annotated as not implemented.
* **§3** claimed the 4th ACS712 (GPIO33 / Static 2) was *"not yet in the test
  sketch."* ✅ **It is** — `Firmware_Test.ino:34` declares `PIN_CURR_STA2 = 33` and
  line 200 reads it. Note was stale; corrected.
* **§3** ✅ added: the sketch's `SENS_* = 0.146` defaults match no standard ACS712
  variant, while production v3.0.0 now uses `0.185` / `0.100`. On identical hardware
  the two builds will report different currents until the sketch is aligned. This is
  §8 gap 8 surfacing in a second file.

### 11.1 — Remaining, and it is a *code* task — **MEDIUM**

`Firmware_Test.ino` still carries `SENS_LEFT/RGHT/STA1/STA2 = 0.146`. Either bring
the sketch to the production defaults or have both read one shared header. Until
then the bench-validation build disagrees with the build being validated.

> **The `.md` sweep for a corrected claim is now the standing procedure** — see
> §3.8. Three of the four errors in this section would have shipped otherwise, and
> the Decision Log entry shows how a wrong "locked" decision reproduces itself into
> every document written afterwards. Archived version docs
> (`Firmware/Version History/**`) are exempt: they are historical records and
> `Firmware_1.1.1/firmware_doc.md` correctly documents the dead-man switch that
> v1.1.1 actually had.
