# Chase Animation — Implementation Specification

> **Audience: whoever implements the animation in the production ESP32 firmware** (the build with
> Ethernet, MQTT, and Modbus). This document is self-contained. You do not need to read the bench
> guide or the test sketch to implement from it, though both are referenced as the working
> reference implementation.
>
> Companion docs: [`LED_TEST_GUIDE.md`](./LED_TEST_GUIDE.md) is the bench-testing procedure.
> [`led_test_2.ino`](./led_test_2.ino) is the validated reference implementation.

---

## 1. The production configuration

**This is the animation the signage runs. Implement this as the power-on default.**

| Parameter | Value |
|---|---|
| **Chase mode** | **1 — wrapping** |
| **Arrow width** | **2 LEDs** |
| **Step delay** | **250 ms** |
| **Direction** | **`A4 → A0`** on the left side, **`B4 → B0`** on the right |
| **Phase loop** | LEFT 10 s → RIGHT 10 s → ALL OFF 3 s → repeat |

Only the active side animates. The idle side is held fully dark.

These are **not** runtime-configurable requirements in production — the bench sketch exposes serial
commands to change them, the production firmware does not need to. If they are made configurable,
these remain the defaults.

### Exact output for this configuration

The full frame sequence, 5 frames, repeating. Bits shown **4→0** (`A4 A3 A2 A1 A0`):

| Frame | Channels lit | Binary | Byte |
|---|---|---|---|
| 0 | `A4, A3` | `11000` | `0x18` |
| 1 | `A3, A2` | `01100` | `0x0C` |
| 2 | `A2, A1` | `00110` | `0x06` |
| 3 | `A1, A0` | `00011` | `0x03` |
| 4 | `A0, A4` | `10001` | `0x11` ← **wrap frame** |

Then back to frame 0.

**If your implementation produces these five bytes in this order, it is correct.** Test against this
table directly.

> **Frame 4 is intentional.** The arrow visibly breaks across the ends of the strip on this frame.
> That is the defining behaviour of Chase 1 and the reason it has no jump-back — motion is
> continuous. Do not "fix" it. Chase 2 (§4) is the variant that removes it.

**Timing:** 5 frames × 250 ms = **1.25 s per sweep**. A 10-second phase fits **exactly 8 sweeps**, so
each side ends on a whole sweep rather than mid-arrow. Preserve this relationship if you change the
delay.

---

## 2. Hardware mapping

| | |
|---|---|
| Expander | MCP23017 at I2C address **`0x20`** |
| I2C pins | **SDA = GPIO4, SCL = GPIO13** (changed from 16/17 to free GPIO17 for the Ethernet clock) |
| Left side (LHS) | **Port A** — register `GPIOA` = **`0x12`** |
| Right side (RHS) | **Port B** — register `GPIOB` = **`0x13`** |
| Direction registers | `IODIRA` = `0x00`, `IODIRB` = `0x01` — set both to `0x00` (all outputs) at init |
| Channels per side | 5, using **bits 0–4**. Bits 5–7 unused, always 0 |

**Bit N of the port byte drives channel N.** Bit 0 = `A0`, bit 4 = `A4`. Writing `0x18` to `0x12`
lights `A4` and `A3` on the left side.

Register addresses above assume `IOCON.BANK = 0`, which is the power-on default. Do not change it.

---

## 3. Frame generation

Frames are **generated at runtime**, not stored as constants. This is what makes the width
adjustable. `idx` is the frame index within the cycle; `n` is the arrow width in LEDs.

### Chase 1 (production) and Chase 2

```c
uint8_t buildFrame(int idx, int n) {
  uint8_t f = 0;
  for (int k = 0; k < n; k++) {
    int pos = ((4 - idx - k) % 5 + 5) % 5;   // modulo-5, wrapped into 0..4
    f |= (1 << pos);
  }
  return f;
}
```

The head sits at channel `4 - idx` and walks downward as `idx` increases. Each additional LED `k`
trails one channel behind. The double-modulo handles negative intermediates in C.

Worked example, `n = 2`, `idx = 4`: `k=0` gives `pos = 0`; `k=1` gives `(4-4-1) = -1`, and
`((-1 % 5) + 5) % 5 = 4`. Result: bits 0 and 4 → `0x11`. That's the wrap frame.

**Chase 1 and Chase 2 use the same generator.** They differ only in frame count (§3.2).

### Chase 3 — spaced trail (not used in production, documented for completeness)

```c
int head = 4 - idx;
int dots = (n > 2) ? 2 : n;             // hard cap at 2 dots
for (int k = 0; k < dots; k++) {
  int pos = head + 2 * k;               // trail sits 2 channels behind
  if (pos <= 4) f |= (1 << pos);        // off the strip = not lit, no wrap
}
```

Dots sit 2 channels apart and a trailing dot is simply not lit until it lands on the strip. Capped at
2 dots because a third would light `A4` on the final frame, which reads as a flash rather than
motion.

### 3.2 Frame count

```c
int frameCount(int mode, int n) {
  if (mode == 2) return 6 - n;   // 2 LEDs -> 4 frames, 3 LEDs -> 3 frames
  return 5;                       // modes 1 and 3 always run all 5 head positions
}
```

**Chase 1 always has 5 frames regardless of width** — the arrow wraps, so every head position is
valid. Chase 2 stops before the frames that would wrap, so its cycle shortens as the arrow widens.

Advance with `idx = (idx + 1) % frameCount(mode, n)`.

---

## 4. Mode reference

| Mode | Behaviour | Frames (n=2) | Production |
|---|---|---|---|
| **1** | Wraps around the strip. Continuous motion; arrow breaks across the seam once per cycle. | 5 | **✅ this one** |
| 2 | Stops before the wrap and restarts at the top. Always reads cleanly 4→0, at the cost of a visible jump back. | 4 | no |
| 3 | Spaced trail — two dots 2 channels apart, no wrap, capped at 2 dots. | 5 | no |

---

## 5. Phase sequencing

```
PHASE_LEFT   10 000 ms   animate Port A (0x12), hold Port B at 0x00
PHASE_RIGHT  10 000 ms   animate Port B (0x13), hold Port A at 0x00
PHASE_OFF     3 000 ms   both ports 0x00
```

On entering any phase: reset the frame index to 0 and write `0x00` to **both** ports, so the outgoing
phase's side is guaranteed dark before the new one starts.

---

## 6. Integration requirements

**These matter more in production than they did on the bench.**

1. **The animation must be non-blocking.** No `delay()` anywhere in the animation path. The
   production firmware also services Ethernet, MQTT and Modbus; a blocking animation will stall
   them and trip watchdogs. Drive it from `millis()` comparisons in the main loop — the reference
   implementation does exactly this and can be lifted as-is.

2. **Two independent timers.** One for the phase (10 s / 10 s / 3 s), one for the animation step
   (250 ms). They are not nested and must not be conflated.

3. **Every I2C write must be checked.** `Wire.endTransmission()` returns 0 on success. The bench
   firmware ignored this and a bus fault presented as LEDs silently frozen on their last frame with
   no error anywhere. In production this must surface as a fault condition. See
   [`../led_diag/`](../led_diag/) for the diagnostic tooling and the failure signatures.

4. **Do not halt on I2C failure.** The bench sketch halts forever if the MCP23017 isn't found at
   boot. Production must log, retry, and keep the rest of the system alive.

5. **Failsafe interaction is out of scope here.** How the animation yields to hold-last-state or the
   discrepancy logic is defined by the production firmware, not by this document.

---

## 7. Verification

Implementation is correct when:

- [ ] Left phase writes exactly `0x18, 0x0C, 0x06, 0x03, 0x11` to register `0x12`, repeating.
- [ ] Right phase writes the same five bytes to register `0x13`.
- [ ] Steps advance every **250 ms**; one sweep is **1.25 s**; a 10 s phase contains **8 sweeps**.
- [ ] The idle side reads `0x00` throughout the other side's phase.
- [ ] Phase loop is 10 s / 10 s / 3 s and repeats indefinitely.
- [ ] Frame 4 (`0x11` — `A0` and `A4` together) is present. Its absence means Chase 2 was
      implemented by mistake.
- [ ] No `delay()` in the animation path; Modbus/MQTT/Ethernet stay responsive throughout.

---

## 8. Provenance

These values were chosen by bench comparison on physical signage using
[`led_test_2.ino`](./led_test_2.ino), which exposes all three modes, widths 1–5, and the step delay
over serial so they can be compared at real viewing distance.

> **Note:** the bench sketch's own power-on default is currently **Chase 2**, not Chase 1. That is a
> bench-tool default and does not override this document. **§1 is the production contract.**
