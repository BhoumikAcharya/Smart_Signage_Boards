# LED Chase Test Guide — `led_test_2.ino`

> Companion to [`../TESTING_GUIDE.md`](../TESTING_GUIDE.md). That guide proves the **board** works.
> This one is for tuning the **arrow chase animation** on the bench — direction, arrow width,
> reset behaviour, and speed.

---

## 0. Before you compile

The sketch lives in its **own folder**, `led_test_2/led_test_2.ino`. Open that file and upload
normally.

> **Don't copy it next to `Firmware_Test.ino`.** The Arduino IDE concatenates every `.ino` in a
> sketch folder into one program, so the two would collide with duplicate `setup()` / `loop()`
> errors.

---

## 1. What this sketch does

It boots **straight into the `D` demo loop** from `Firmware_Test.ino` — no key press needed — and
repeats it forever:

```
LEFT chase 10 s  →  RIGHT chase 10 s  →  ALL OFF 3 s  →  (repeat)
```

Only the active side animates; the other side is held dark.

**On power-on it runs Chase 3 at a 200 ms step delay** — the chosen signage animation. Plug the
board in and that is what plays, with no Serial input at all. Everything else below is available
over Serial for comparison, and none of it persists across a reset.

Three things are different from `Firmware_Test.ino`:

| | `Firmware_Test.ino` | `led_test_2.ino` |
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

### The three chase modes

- **Chase 1 (`c 1`)** — plays all 5 frames including the wrap. Continuous motion, but the
  arrow splits across the seam twice per cycle.
- **Chase 2 (`c 2`)** — stops after the last frame that fits inside the strip (`A2, A1, A0` for a
  3-LED arrow) and **restarts at `A4, A3, A2`**. The arrow always reads cleanly 4→0, at the cost of
  a visible jump back to the top.
- **Chase 3 (`c 3`, the boot default)** — **spaced trail.** Two dots sit **2 channels apart** instead of touching, and
  the trailing dot is simply **not lit until it lands on the strip**. Head still walks 4→0, no wrap,
  resets after the head reaches `A0`:

| Frame | Channels lit |
|-------|--------------|
| 0 | `A4` |
| 1 | `A3` |
| 2 | `A2, A4` |
| 3 | `A1, A3` |
| 4 | `A0, A2` → restart |

  So it starts as a single dot walking in from the far end, and once it's two channels deep a second
  dot appears behind it. Reads as a dashed arrow rather than a solid bar.

  **Chase 3 is capped at 2 dots.** A third dot would land on `A4` in the last frame (`A0, A2, A4`),
  re-lighting the far end of the strip just as the arrow finishes — that reads as a flash, not as
  motion, so it's excluded. The LED count still works *downward*: `1` gives a single dot walking 4→0
  (identical to Chase 2 at 1 LED), while `2`, `3`, `4` and `5` all give the pair above. For a wider
  solid arrow use Chase 1 or Chase 2. Typing `c 3` prints the live frame table so you can see
  exactly what you'll get.

Frame count per cycle:

| Arrow LEDs | Chase 1 | Chase 2 | Chase 3 |
|-----------|---------|---------|---------|
| 1 | 5 | 5 (identical — a single LED never wraps) | 5 (single dot) |
| **2** | 5 | 4 | **5** |
| **3** | **5** | **3** | 5 (capped to 2 dots) |
| 4 | 5 | 2 | 5 (capped to 2 dots) |
| 5 | 5 | 1 (all channels on, static) | 5 (capped to 2 dots) |

> Because Chase 2 has fewer frames, one full cycle takes **less time** at the same step delay. A
> 3-LED Chase 2 cycle is 3 × delay; Chase 1 and Chase 3 are 5 × delay. Expect Chase 2 to look faster
> even though the per-step speed is unchanged.

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
| `c 3` | **Chase 3** — spaced trail, 2 dots 2 channels apart, resets after `A0` (**boot default**) |
| `d 200` | Set the step delay to 200 ms (accepts **5–5000**) |
| `p` | Pause / resume the demo (pausing turns all LEDs off) |
| `?` | Print current settings **and the full frame table** |
| `h` | Reprint the menu |

**Power-on defaults:** **Chase 3, 200 ms step delay**, LED count 3. These are **not** saved across a
reset — every power cycle comes back to Chase 3 @ 200 ms, as intended.

> The LED count of 3 only matters if you switch to Chase 1 or Chase 2, since **Chase 3 caps at
> 2 dots** (see §2). It's left at 3 so that typing `c 1` or `c 2` gives you the familiar 3-LED arrow
> to compare against. The boot status line prints a note reminding you of the cap.

Changing any setting **restarts the animation from frame 0** so the new geometry is visible
immediately instead of appearing mid-sweep. The 10 s / 10 s / 3 s phase timing keeps running
underneath — a setting change does not restart the demo phases.

The `?` output shows the live frame table with bits printed **4→0** (`A4 A3 A2 A1 A0`), so
`[11100]` means A4, A3, A2 are lit:

```
>> LEDs:3  Mode:3 (spaced trail, reset at end)  Delay:200 ms  Frames:5  running
   frames: [10000] [01000] [10100] [01010] [00101]
   (bit order shown is 4..0, i.e. A4 A3 A2 A1 A0)
   (Chase 3 is a 2-dot pattern — the LED count of 3 is capped at 2)
```

---

## 4. Step-by-step bench procedure

### Step 0 — Flash and connect
1. Open `led_test_2/led_test_2.ino`, select your ESP32 board + port, upload.
2. Open Serial Monitor @ **115200 baud**, line ending **Newline**.
3. Confirm `Probing MCP23017 @0x20 on SDA=4/SCL=13 ... OK`. If it says `NOT FOUND!` the sketch
   halts — fix the I2C wiring first (see [`../TESTING_GUIDE.md`](../TESTING_GUIDE.md) §4 Step 1).
4. Confirm the boot status line reads **`Mode:3 (spaced trail, reset at end)  Delay:200 ms`** and
   that the spaced-trail animation starts on its own.

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

### Step 3 — Confirm the default animation (Chase 3)
1. Straight out of boot you should see the **spaced trail**: a single dot at `A4`, then `A3`, then
   two dots at `A2`+`A4`, `A1`+`A3`, `A0`+`A2`, then back to the start.
2. Confirm the gap between the two dots is exactly **one dark channel**.
3. Confirm **`A4` stays off in the final `A0`+`A2` frame** — it must not flash back on as the arrow
   finishes.
4. Confirm the step rate looks like **200 ms** — a full 5-frame sweep takes about **1 second**, so
   roughly 10 sweeps per 10-second side.

### Step 4 — Compare against the other chase modes
1. Type `c 1` + Enter. You get the 3-LED solid arrow with the two wrap frames where it splits across
   the ends of the strip.
2. Type `c 2` + Enter. The split frames disappear — the arrow runs `A4,A3,A2` → `A3,A2,A1` →
   `A2,A1,A0` and then **jumps back to the top**.
3. Type `c 3` + Enter to return to the default.
4. Decide which reads better on the real signage board at viewing distance. That's the whole point
   of this sketch.

### Step 5 — Arrow width
1. Type `2` + Enter. In Chase 1 or 2 the arrow narrows to **2 LEDs**; in Chase 3 nothing changes,
   since it is already a 2-dot pattern.
2. Type `1` + Enter. A **single LED** should walk 4→3→2→1→0 and repeat, in every mode.
3. Type `4`, then `5`, while in Chase 1 or 2. At 5 the whole strip is lit (an arrow as wide as the
   strip has nowhere to move in Chase 2, and just rotates in Chase 1).
4. Type `3` to return to the default. Type `?` to confirm the frame table matches what you see.
5. Try an out-of-range value like `7` — it should reject with `!! LED count must be 1-5` and leave
   the animation untouched.
6. While in Chase 3, type `3` — it should accept the value but print the **2-dot cap note**, and the
   animation should stay at two dots.

### Step 6 — Speed
1. Type `d 350` + Enter — the chase should visibly slow down from the 200 ms default.
2. Type `d 60` — noticeably faster.
3. Sweep for the speed that looks right on the physical board, then `d 200` to return to the
   default. Typical range to try: **80–300 ms**.
4. Try `d 2` — it should reject with `!! Delay must be 5-5000 ms`.

> **Note:** at very short delays you're limited by the I2C write rate to the MCP23017. If the
> animation stops getting faster below ~20 ms, that's the bus, not the code.

### Step 7 — Record the winning combination
The current defaults are **Chase 3 @ 200 ms**. If bench testing lands somewhere else, write down
what you settled on:

- **Chase mode:** ______ (1 = wrap, 2 = reset, 3 = spaced trail)
- **Step delay:** ______ ms
- **Arrow LEDs:** ______ (only applies to modes 1 and 2)

To change the power-on defaults, edit the constants near the top of `led_test_2.ino`:

```cpp
const int           DEFAULT_LEDS  = 3;   // arrow width for modes 1/2 (mode 3 caps at 2 dots)
const int           DEFAULT_MODE  = 3;   // spaced trail, reset at end
const unsigned long DEFAULT_DELAY = 200; // ms per animation step
```

These same three values are what should eventually be carried into the production firmware's
animation code.

---

## 5. Pass criteria

- [ ] MCP23017 detected `OK` on SDA=4 / SCL=13.
- [ ] Demo starts on its own at power-on — no key press required — in **Chase 3 @ 200 ms**.
- [ ] Chase runs **A4→A0** on the LEFT phase and **B4→B0** on the RIGHT phase.
- [ ] Phase timing is 10 s / 10 s / 3 s, with the idle side fully dark.
- [ ] The default `c 3` runs `A4` → `A3` → `A2,A4` → `A1,A3` → `A0,A2` and then restarts, with `A4`
      **off** in that final frame — at any LED count.
- [ ] `c 1` / `c 2` still switch to the solid-arrow modes, and `c 3` returns to the default.
- [ ] Typing `1`–`5` changes the arrow width live in modes 1 and 2, without stopping the demo.
- [ ] `d <ms>` changes the speed live.
- [ ] After a power cycle, the sketch comes back at **Chase 3 / 200 ms**.

---

## 6. Troubleshooting

| Symptom | Likely cause |
|---------|--------------|
| Compile fails with `redefinition of 'void setup()'` | A copy of the sketch ended up next to `Firmware_Test.ino` — see §0. |
| Commands do nothing | Serial Monitor line ending is set to **No line ending**. Set it to **Newline**. |
| `!! Unknown command 'x'` on every input | Same as above, or you typed the argument without the letter (e.g. `200` instead of `d 200` — a bare number is read as an LED count). |
| Arrow runs 0→4 instead of 4→0 | Strip is physically wired in reverse relative to the MCP port. Confirm channel-to-strip mapping with `Firmware_Test.ino` keys `0`–`9`. |
| Animation stutters | Step delay is near the I2C write floor — raise it above ~20 ms. |
| One channel never lights during the chase | Not an animation bug. Test that MOSFET individually in `Firmware_Test.ino`. |
