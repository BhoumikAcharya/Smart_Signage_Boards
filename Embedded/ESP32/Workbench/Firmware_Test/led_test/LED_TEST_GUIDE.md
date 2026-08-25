# LED Chase Test Guide — `led_test.ino`

> Companion to [`TESTING_GUIDE.md`](./TESTING_GUIDE.md). That guide proves the **board** works.
> This one is for tuning the **arrow chase animation** on the bench — direction, arrow width,
> reset behaviour, and speed.

---

## ⚠️ 0. Read this before you compile

The Arduino IDE **concatenates every `.ino` file in a sketch folder into one program**. Because
`led_test.ino` sits next to `Firmware_Test.ino`, opening either one and hitting Upload will pull in
**both**, and the compile will fail with duplicate `setup()` / `loop()` errors.

Pick one of these before flashing:

**Option A — give it its own sketch folder (recommended, permanent fix)**

```bash
mkdir -p ../led_test && mv led_test.ino ../led_test/led_test.ino
```

Then open `Embedded/ESP32/led_test/led_test.ino` and upload normally.

**Option B — temporarily park the other sketch**

Rename `Firmware_Test.ino` to `Firmware_Test.ino.bak` while you work on the LED test, then rename
it back. (The IDE only picks up files ending in `.ino`.)

Everything below assumes you've done one of the two.

---

## 1. What this sketch does

It boots **straight into the `D` demo loop** from `Firmware_Test.ino` — no key press needed — and
repeats it forever:

```
LEFT chase 10 s  →  RIGHT chase 10 s  →  ALL OFF 3 s  →  (repeat)
```

Only the active side animates; the other side is held dark.

Three things are different from `Firmware_Test.ino`:

| | `Firmware_Test.ino` | `led_test.ino` |
|---|---|---|
| **Direction** | `A0 → A4`, `B0 → B4` | **`A4 → A0`, `B4 → B0`** |
| **Frames** | 5 hard-coded constants | generated at runtime from the LED count |
| **Blocking?** | demo blocks until you press a key | **non-blocking** — you can change settings while it runs |

There are **no sensor or voltage commands here**. Use `Firmware_Test.ino` for those.

---

## 2. How the animation is built

Each strip has 5 channels (`0`–`4`). The arrow **head** walks from channel 4 down to channel 0, with
the body trailing behind it. With the default 3-LED arrow:

| Frame | Channels lit | Notes |
|-------|--------------|-------|
| 0 | `A4, A3, A2` | |
| 1 | `A3, A2, A1` | |
| 2 | `A2, A1, A0` | end of the strip |
| 3 | `A1, A0, A4` | **wrap** — arrow breaks across the seam |
| 4 | `A0, A4, A3` | **wrap** |

Frames 3 and 4 are the ones where the arrow visually "breaks apart" and stops reading as an arrow.
That's exactly what **Chase 2** removes.

### Chase 1 vs Chase 2

- **Chase 1 (`c 1`, default)** — plays all 5 frames including the wrap. Continuous motion, but the
  arrow splits across the seam twice per cycle.
- **Chase 2 (`c 2`)** — stops after the last frame that fits inside the strip (`A2, A1, A0` for a
  3-LED arrow) and **restarts at `A4, A3, A2`**. The arrow always reads cleanly 4→0, at the cost of
  a visible jump back to the top.

Frame count per cycle:

| Arrow LEDs | Chase 1 | Chase 2 |
|-----------|---------|---------|
| 1 | 5 | 5 (identical — a single LED never wraps) |
| 2 | 5 | 4 |
| **3** | **5** | **3** |
| 4 | 5 | 2 |
| 5 | 5 | 1 (all channels on, static) |

> Because Chase 2 has fewer frames, one full cycle takes **less time** at the same step delay. A
> 3-LED Chase 2 cycle is 3 × delay; Chase 1 is 5 × delay. Expect it to look faster even though the
> per-step speed is unchanged.

---

## 3. Serial command reference

Serial Monitor at **115200 baud**, line ending set to **Newline** (or Both NL & CR). Commands are
**line-based** — type the command, then press **Enter**.

| Command | Action |
|---------|--------|
| `1` … `5` | Set the number of LEDs in the arrow (bare number shortcut) |
| `n 2` | Same thing, explicit form |
| `c 1` | **Chase 1** — arrow wraps around the strip |
| `c 2` | **Chase 2** — arrow resets after `A2, A1, A0` |
| `d 200` | Set the step delay to 200 ms (accepts **5–5000**) |
| `p` | Pause / resume the demo (pausing turns all LEDs off) |
| `?` | Print current settings **and the full frame table** |
| `h` | Reprint the menu |

**Power-on defaults:** 3 LEDs, Chase 1, 120 ms step delay. These are **not** saved across a reset —
every power cycle comes back to a 3-LED arrow, as intended.

Changing any setting **restarts the animation from frame 0** so the new geometry is visible
immediately instead of appearing mid-sweep. The 10 s / 10 s / 3 s phase timing keeps running
underneath — a setting change does not restart the demo phases.

The `?` output shows the live frame table with bits printed **4→0** (`A4 A3 A2 A1 A0`), so
`[11100]` means A4, A3, A2 are lit:

```
>> LEDs:3  Mode:1 (wrapping loop)  Delay:120 ms  Frames:5  running
   frames: [11100] [01110] [00111] [10011] [11001]
   (bit order shown is 4..0, i.e. A4 A3 A2 A1 A0)
```

---

## 4. Step-by-step bench procedure

### Step 0 — Flash and connect
1. Resolve the two-`.ino` conflict (§0).
2. Select your ESP32 board + port, upload.
3. Open Serial Monitor @ **115200 baud**, line ending **Newline**.
4. Confirm `Probing MCP23017 @0x20 on SDA=4/SCL=13 ... OK`. If it says `NOT FOUND!` the sketch
   halts — fix the I2C wiring first (see `TESTING_GUIDE.md` §4 Step 1).

### Step 1 — Confirm the reversed direction
1. Watch the LEFT phase. The arrow must move **from A4 towards A0** — i.e. from the far end of the
   strip back towards channel 0. This is the **opposite** of `Firmware_Test.ino`.
2. If it runs A0→A4 instead, your **strip is wired in reverse order** relative to the MCP23017
   port — note which physical strip corresponds to which channel number.
3. Confirm the RIGHT phase does the same thing on Port B (`B4 → B0`).

### Step 2 — Confirm the phase timing
1. Time one full cycle: LEFT should hold for ~10 s, RIGHT for ~10 s, then **everything dark for
   3 s**.
2. During LEFT, the RHS strips must be **completely off** (and vice versa). Any bleed-through means
   a MOSFET is stuck on — go back to `Firmware_Test.ino` and test that channel with `0`–`9`.

### Step 3 — Arrow width
1. Type `2` + Enter. The arrow should immediately narrow to **2 LEDs**.
2. Type `1` + Enter. A **single LED** should walk 4→3→2→1→0 and repeat.
3. Type `4`, then `5`. At 5 the whole strip is lit (an arrow as wide as the strip has nowhere to
   move in Chase 2, and just rotates in Chase 1).
4. Type `3` to return to the default. Type `?` to confirm the frame table matches what you see.
5. Try an out-of-range value like `7` — it should reject with `!! LED count must be 1-5` and leave
   the animation untouched.

### Step 4 — Chase 1 vs Chase 2
1. With 3 LEDs, watch **Chase 1** and look for the two wrap frames where the arrow splits across the
   ends of the strip.
2. Type `c 2` + Enter. The split frames should disappear — the arrow now runs `A4,A3,A2` →
   `A3,A2,A1` → `A2,A1,A0` and then **jumps back to the top**.
3. Decide which one reads better on the real signage board at viewing distance. That's the whole
   point of this sketch.
4. Repeat the comparison at 2 LEDs and 1 LED. (At 1 LED the two modes are identical — that's
   expected, a single LED never wraps.)

### Step 5 — Speed
1. Type `d 250` + Enter — the chase should visibly slow down.
2. Type `d 60` — noticeably faster.
3. Sweep for the speed that looks right on the physical board. Typical range to try: **80–200 ms**.
4. Try `d 2` — it should reject with `!! Delay must be 5-5000 ms`.

> **Note:** at very short delays you're limited by the I2C write rate to the MCP23017. If the
> animation stops getting faster below ~20 ms, that's the bus, not the code.

### Step 6 — Record the winning combination
Write down the three numbers you settled on:

- **Arrow LEDs:** ______ (default 3)
- **Chase mode:** ______ (1 = wrap, 2 = reset)
- **Step delay:** ______ ms (default 120)

To make them the power-on defaults, edit the constants near the top of `led_test.ino`:

```cpp
const int           DEFAULT_LEDS  = 3;   // arrow is 3 LEDs wide
const int           DEFAULT_MODE  = 1;   // wrapping loop
const unsigned long DEFAULT_DELAY = 120; // ms per animation step
```

These same three values are what should eventually be carried into the production firmware's
animation code.

---

## 5. Pass criteria

- [ ] MCP23017 detected `OK` on SDA=4 / SCL=13.
- [ ] Demo starts on its own at power-on — no key press required.
- [ ] Chase runs **A4→A0** on the LEFT phase and **B4→B0** on the RIGHT phase.
- [ ] Phase timing is 10 s / 10 s / 3 s, with the idle side fully dark.
- [ ] Typing `1`–`5` changes the arrow width live, without stopping the demo.
- [ ] `c 2` removes the wrap frames and resets the arrow at the top of the strip.
- [ ] `d <ms>` changes the speed live.
- [ ] After a power cycle, the sketch comes back at **3 LEDs / Chase 1 / 120 ms**.

---

## 6. Troubleshooting

| Symptom | Likely cause |
|---------|--------------|
| Compile fails with `redefinition of 'void setup()'` | Both `.ino` files are in the same folder — see §0. |
| Commands do nothing | Serial Monitor line ending is set to **No line ending**. Set it to **Newline**. |
| `!! Unknown command 'x'` on every input | Same as above, or you typed the argument without the letter (e.g. `200` instead of `d 200` — a bare number is read as an LED count). |
| Arrow runs 0→4 instead of 4→0 | Strip is physically wired in reverse relative to the MCP port. Confirm channel-to-strip mapping with `Firmware_Test.ino` keys `0`–`9`. |
| Animation stutters | Step delay is near the I2C write floor — raise it above ~20 ms. |
| One channel never lights during the chase | Not an animation bug. Test that MOSFET individually in `Firmware_Test.ino`. |
