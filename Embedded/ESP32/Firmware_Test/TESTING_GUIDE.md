# Hardware Bring-Up Testing Guide — `Firmware_Test.ino`

> **Read this first.** This guide explains *what* the testing sketch does, *why*, and gives a
> *step-by-step* procedure to validate a freshly assembled V3 board. For the full system/hardware
> reference see [`README.md`](./README.md).

---

## 1. What is this and what are we testing?

`Firmware_Test.ino` is a **standalone bench tool**. It is **not** the real product firmware — it
does **no networking at all** (no Ethernet, no MQTT, no Modbus, no Raspberry Pi). It talks to you
only over the **USB serial monitor**.

Its single job: **prove the physical hardware works before we trust it with the network stack.**
We are isolating and confirming, one subsystem at a time:

| # | Subsystem | What we confirm |
|---|-----------|-----------------|
| 1 | **I2C bus + MCP23017** | The expander is found at `0x20` on the **finalized pins SDA=GPIO4 / SCL=GPIO13**. If this fails, nothing else can be driven. |
| 2 | **10x MOSFET channels** | Each Port A0–A4 (LHS) and B0–B4 (RHS) gate switches its LED strip on/off individually. Catches dead MOSFETs, swapped wiring, cold solder joints. |
| 3 | **4x ACS712 current sensors** | LHS, RHS, Static 1, Static 2 each report sane current when their load is on, ~0 A when off. Catches miswired/backwards sensors and validates calibration. |
| 4 | **PSU + Battery voltage dividers** | GPIO36 (PSU) and GPIO39 (Battery) read voltages in the expected range. Catches wrong divider ratios or open dividers. |

> **Why no network here?** If we test everything at once and something is wrong, we can't tell if
> it's the LED wiring, the sensor, or the MQTT config. This sketch removes every variable except the
> board itself. Once the hardware passes here, we move to the production firmware in `../Firmware/`.

**What this sketch does NOT test:** Ethernet/LAN8720, MQTT to the Pi, Modbus, the 4-state
discrepancy logic, the watchdog, and the hold-last-state failsafe. All of that lives in the
production firmware and is validated separately.

---

## 2. What you need

- Assembled V3 board (or breadboard equivalent) powered per `README.md` §1.
- USB connection from your PC to the ESP32 (for flashing + serial monitor).
- **Arduino IDE / arduino-cli** with the **ESP32 board core** installed.
- Library: **Adafruit MCP23017** (`Adafruit_MCP23X17`) — install via Library Manager.
- A **multimeter** (to verify divider voltages and, ideally, cross-check ACS712 current).
- The LED strips + loads connected (so currents are real).

---

## 3. Serial command reference

Open the Serial Monitor at **115200 baud**. The menu prints on boot (press `h` to reprint).

| Key | Action |
|-----|--------|
| `0`–`4` | Toggle one **LHS** MOSFET (Port A0–A4) |
| `5`–`9` | Toggle one **RHS** MOSFET (Port B0–B4) |
| `a` | Turn **all** MOSFETs ON |
| `x` | Turn **all** MOSFETs OFF |
| `c` | Run the **chase** animation self-test (5 cycles), then restore previous state |
| `r` | Read **all 4 current sensors** once |
| `s` | Toggle **continuous 1 Hz current stream** on/off |
| `v` | Read **PSU + Battery** divider voltages once |
| `h` | Reprint the menu |

After each MOSFET toggle it prints the live port bitmask, e.g. `Port A[00100] Port B[00000]`.

---

## 4. Step-by-step test procedure

Do these **in order**. Do not proceed past a failing step — fix it first.

### Step 0 — Flash and connect
1. Open `Firmware_Test.ino` in the Arduino IDE, select your ESP32 board + port.
2. Upload.
3. Open Serial Monitor @ **115200 baud**.

### Step 1 — I2C / MCP23017 detection (gate to everything)
1. On boot, watch for:
   `Probing MCP23017 @0x20 on SDA=4/SCL=13 ... OK`
2. **If it says `NOT FOUND!`** the sketch halts. Check, in order:
   - SDA on **GPIO4**, SCL on **GPIO13** (not the old 16/17).
   - **4.7 kΩ pull-ups** on both SDA and SCL to the 3.3 V rail.
   - MCP23017 **RESET → 3.3 V** and **A0/A1/A2 → GND** (address `0x20`).
   - 3.3 V present on the MCP23017 VCC.
3. Only continue once you see `OK` and the menu.

### Step 2 — MOSFET channels (one at a time)
1. Press `x` to ensure all off.
2. Press `0`. The **first LHS strip (A0)** should light. Confirm the bitmask shows `Port A[00001]`.
3. Press `0` again to turn it off. Repeat for keys `1`,`2`,`3`,`4` (LHS A1–A4).
4. Repeat for keys `5`–`9` (RHS B0–B4).
5. **Record any channel that does not light** (dead MOSFET, wiring, or solder). A strip lighting on
   the *wrong* key = swapped gate wiring.
6. Press `a` (all on) then `x` (all off) to confirm bulk control.
7. Press `c` to watch the chase animation sweep across both sides smoothly.

### Step 3 — Current sensors (correlate with MOSFETs)
1. Press `x` (all off), then `r`. All four currents should read **≈ 0 A** for the switched loads.
   - **Static1 / Static2 are always-on**, so they should already show a **non-zero** current here
     (their zones are permanently powered). That's expected, not a fault.
2. Press `s` to start the 1 Hz stream.
3. Turn on the **LHS** strips (`0`–`4`). **LHS current should rise**; RHS should stay ~0.
4. Turn on the **RHS** strips (`5`–`9`). **RHS current should rise**.
5. Confirm **Static1** and **Static2** hold steady non-zero readings throughout.
6. Press `s` again to stop the stream.
7. **Sanity vs multimeter:** if a sensor reads negative-looking or wildly off values, it may be
   wired backwards or be the wrong ACS712 variant — note it for calibration (see §5).

### Step 4 — Voltage dividers
1. Press `v`. You'll see raw ADC + estimated volts for **PSU** and **Battery**.
2. Compare the estimated volts against a **multimeter** on the 14.5 V bus and the battery.
3. If the numbers are off, the `PSU_DIV_RATIO` / `BATT_DIV_RATIO` placeholders need correcting
   (see §5). The raw ADC should be **stable** and **below 4095** (never railing).

### Step 5 — Record results
Note, for the next phase:
- Any dead/miswired MOSFET channels.
- Any misbehaving current sensor.
- The **ACS712 variant** you actually fitted (5 A / 20 A / 30 A).
- The **measured divider ratios** from the multimeter.

---

## 5. Calibration notes (expected during testing)

The sketch ships with **placeholder** calibration that WILL need correcting on the bench:

- **ACS712 `SENS` / `ZERO`** (currently `0.146` / `2.400`): `SENS` is Volts-per-Amp for your
  sensor. Standard parts are ~`0.185` (5 A), `0.100` (20 A), `0.066` (30 A) — `0.146` matches none,
  so confirm the variant and update `SENS_LEFT/RGHT/STA1/STA2`. `ZERO` is the sensor's output
  voltage at 0 A (nominally VCC/2 ≈ 2.5 V) — read it with all loads off and set it.
- **Divider ratios** `PSU_DIV_RATIO` / `BATT_DIV_RATIO` (currently `5.30`): set these to
  `(measured bus voltage) / (voltage at the ADC pin)` from your multimeter.

These placeholders don't block the pass/fail checks above — they only affect the *accuracy* of the
numbers. Get the wiring proven first, then dial in calibration.

---

## 6. Pass criteria

The board passes hardware bring-up when:

- [ ] MCP23017 detected `OK` on SDA=4 / SCL=13.
- [ ] All 10 MOSFET channels switch their correct strip individually (`0`–`9`), plus `a`/`x`/`c`.
- [ ] Switched-load currents track their MOSFETs (LHS/RHS rise when on, ~0 when off).
- [ ] Static1 + Static2 show steady non-zero current.
- [ ] PSU + Battery dividers read plausible, stable, non-railing values.

Once all boxes are ticked, we proceed to the **production firmware** (`../Firmware/`) to add
Ethernet, MQTT to the Pi, the 4-state discrepancy logic, the watchdog, and the hold-last-state
failsafe.
