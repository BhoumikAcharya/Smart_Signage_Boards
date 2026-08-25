# MCP23017 / I2C Fault Diagnostic — `led_diag.ino`

Bench tool for the fault where the LED chase runs ~35 s, freezes, and then reports
`MCP23017 NOT FOUND` until the board is power cycled.

It runs the **same animation** as `led_test_2.ino` (Chase 2, 2-LED arrow, 250 ms, 10 s / 10 s / 3 s)
so the electrical load profile is identical and the fault reproduces. Everything else is
instrumentation. **It never halts** — it logs, retries, and keeps running.

---

## Reading the output

Every line is timestamped in seconds since boot. Note the uptime of the **first** failure.

### The headline: `IODIRA` read-back

We set `IODIRA` to `0x00` (all outputs) at init. The chip's power-on default is `0xFF`. Every 2 s the
sketch reads it back, which gives a three-way answer:

| Log line | Meaning | Cause |
|---|---|---|
| *(silent)* | `IODIRA` = `0x00` | Chip healthy, config retained |
| `IODIRA = 0xFF - THE MCP23017 REBOOTED` | Chip lost power and restarted | **Brownout / 3.3 V rail collapse** |
| `IODIRA READ FAILED` | The read didn't complete | Compare against the **write** failure rate before concluding anything |
| `IODIRA = 0x?? - corrupted register` | Bits got mangled in transit | **Noise on SDA/SCL** |

### The `endTransmission` error code

| Code | Meaning | Points at |
|---|---|---|
| `2` NACK on address | Chip is not answering | Reset, dead, or latched up |
| `5` timeout | Something is holding the bus low | Wedged slave, latch-up |
| `3` NACK on data | Chip acked its address then quit | Marginal supply |

### The ESP32 reset reason

Printed at boot. If it ever reads **`*** BROWNOUT ***`**, the rail is collapsing hard enough to drop
the ESP32 itself — that settles it immediately, no meter needed.

### Recovery

After 3 consecutive write failures the sketch bit-bangs 9 clock pulses plus a STOP to unstick a
wedged slave, then reconfigures and verifies.

> **Don't over-read the success ratio.** The verification is 4 writes plus a read, so on a noisy bus
> it fails by chance regardless of whether anything was ever stuck. A low `recov` figure alongside
> frequent `WRITES RECOVERED` lines means the bus was never actually wedged — it means transfers are
> marginal.

---

## Commands

| Key | Action |
|---|---|
| `a` | Animation on/off — **the output-drive test** (see below) |
| `z` | Force a bus recovery now |
| `?` | Full diagnostic dump |
| `h` | Menu |

### The `a` test

Press `a` to stop the animation. LEDs go dark and no pin ever changes state — but the sketch **keeps
writing `0x00` to GPIOA every 250 ms**, so the write path is still measured.

That last part matters. In an earlier version, idle mode stopped writing altogether, which left the
test measuring nothing but reads and made the result useless. Compare the **write** failure rate in
the heartbeat between the two modes:

- **Same failure rate either way** → the bus itself is at fault. Driving the outputs is irrelevant.
- **Only fails with the animation on** → driving the outputs is the trigger. Look at the gate network
  or a short on an MCP output pin.

### Reads vs writes

Reads use a full STOP between setting the register pointer and reading it back, **not** a repeated
start — repeated start is the least reliable transaction type on the ESP32 Arduino core and returns
error 4 on its own, which would make the diagnostic blame the hardware for a driver quirk.

Even so, when reads and writes disagree, **trust the writes.** They're the simpler transaction.

---

## Suggested run order

> **A genuine cold start means removing power for ~10 seconds.** The MCP23017 runs down to 1.8 V, so
> a reset button press or a quick unplug reboots the ESP32 while the MCP sails through on the
> decaying rail, still in whatever state it was in. Half the confusing runs during the first
> investigation were this. Check the boot banner says `POWER-ON` **and** that you actually cut power.

1. Cold start, let it run untouched until it fails. Record the **uptime of the first write failure**
   and the error code.
2. Cold start, press `a` immediately, let it run the same duration. Compare the **write** failure
   rate against step 1.
3. Repeat step 1 a few times — **is the first-failure uptime consistent?** Consistent points at
   something thermal or cumulative; varying points at a transient.
4. Only then reach for the meter.

## What this tool can't tell you

It measures the I2C link, so it will faithfully report a fault that originates anywhere upstream —
supply, ground, layout, or the chip. **A failure here is not evidence against any of those**, and the
error codes alone won't separate them. The comparisons in steps 1–3, and running the same sketch on a
known-good board, are what carry the information.
