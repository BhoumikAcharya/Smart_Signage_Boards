**Modbus-to-MQTT Bridge for Metro Emergency Signage**

> **Reconciled 2026-08-07** against Gateway `Pi.py` v2.0.0 and ESP32 `Firmware.ino` v3.0.0.
> The previous revision (2026-02-17) predated the relay → MOSFET redesign and described hardware and
> a control protocol that no longer exist. See `Embedded/ESP32/DOC_UPDATES_PENDING.md` §10.

**Authoritative documents** — this README is an orientation page; each of these is the source of truth for its area:

| Area | Document |
| --- | --- |
| PLC / SCADA integration | [`Embedded/RaspberryPi/Gateway/SCADA_Integration_Guide.md`](Embedded/RaspberryPi/Gateway/SCADA_Integration_Guide.md) |
| Gateway internals | [`Embedded/RaspberryPi/Gateway/Gateway_doc.md`](Embedded/RaspberryPi/Gateway/Gateway_doc.md) |
| Platform / deployment | [`Embedded/RaspberryPi/pi_agent.md`](Embedded/RaspberryPi/pi_agent.md) |
| ESP32 firmware contract | [`Embedded/ESP32/esp32_contract.md`](Embedded/ESP32/esp32_contract.md) |
| HMI | [`Embedded/RaspberryPi/HMI/hmi_contract.md`](Embedded/RaspberryPi/HMI/hmi_contract.md) |

---

1. Overview Of the Product -

  This project is a smart EROS, designed for reliable operation in public transit environments like metros and railways. It implements a robust communication bridge on a Raspberry Pi, designed to integrate a central industrial control system (using Modbus) with a distributed network of emergency signs (using MQTT), all over **wired Ethernet**. Making the product implemented on a complete local network.

  This architecture allows a PLC or a central control room to instantly and reliably activate or change emergency signage across multiple locations in a station or tunnel, using a combination of industrial-grade protocols and modern IoT technology.

> **There is no wireless link anywhere in this system.** Every ESP32 node reaches the gateway over
> wired Ethernet via a LAN8720 PHY. The nodes have no Wi-Fi credentials and no Wi-Fi code path — the
> ESP32's radio is left off, which is also why only ADC1 is usable for the sensors.

2. System Architecture

  The project is built on a decoupled, hub-and-spoke architecture, which is ideal for critical systems:

  Central Control (PLC/Modbus Master): The primary industrial controller, located in a central control room. It initiates commands by sending standard Modbus "Write Multiple Holding Registers (16(0x10))" requests to the Raspberry Pi.

  2.1. Raspberry Pi (The Bridge & Broker): This is the core of the system and performs three critical roles simultaneously.

  2.2. Modbus TCP Server: A Python script runs continuously, listening for commands from the central controller and updating an internal data model.

  2.3. MQTT Broker: The industry-standard Mosquitto broker runs as a service, managing all communication with the signage units.

  2.4. Industrial HMI: A 10.1" capacitive touch HMI (Waveshare DSI, 1280x800) is connected to the RaspberryPi with manual override.

  2.5. Emergency Signs (ESP32 Clients): Each emergency light signage unit is powered by an ESP32.

  2.6. The ESP32s connect to the Raspberry Pi over Ethernet with a ETH PHY module called the LAN8720 with MQTT communication.

  2.7. They subscribe to specific MQTT topics and wait for commands.

  2.8. They are responsible for the final physical action — driving the LED strips through **10 logic-level N-channel MOSFETs**, switched over I2C by an MCP23017 port expander.

> **The relays are gone.** Earlier revisions of the design switched the signage with 5V dual-channel
> relay modules on GPIO 4/13, and the ESP32 toggled a GPIO directly. The current hardware has no
> relays: the ESP32 talks I2C to an MCP23017 (SDA=GPIO4, SCL=GPIO13) which drives 10 MOSFET gates —
> 5 for the left-hand arrows (Port A0–A4) and 5 for the right (Port B0–B4).
>
> **Only the moving arrows are switched.** The two static zones are hardwired always-on: they are
> current-monitored but can never be commanded on or off.

3. Features -

  3.1. Protocol Bridging: Seamlessly translates industrial-grade Modbus TCP commands into lightweight MQTT messages suitable for the edge nodes.

  3.2. Real-Time, Reliable Control: Uses a multi-threaded approach on the Raspberry Pi to ensure low latency between receiving a Modbus command and publishing the corresponding MQTT message.

  3.3. Scalable: Easily add or remove signage units across a station without any changes to the central controller or bridge logic. The register map is fixed at **100 nodes** (`40001`–`40100`); each sign only needs to know the broker's address and its own register number.

  3.4. Decoupled & Robust: The central controller does not need to know anything about the individual signs, and the signs do not need to know about the controller. This separation makes the system highly reliable and easy to maintain.

  3.5. Custom Logic: The bridge script contains specific control logic, allowing complex actions to be easily implemented.

  3.6. Diagnostics data: Diagnostics data of the LEDs, ESP32 and PSU Failure are communicated through MQTT to the RaspberryPi. Rather than a binary OK/FAIL, each of the four current channels reports a **4-state** value — `ON`, `OFF`, `FAIL_OPEN` (intended on, no current: burnt LED or blown MOSFET) or `FAIL_SHORT` (intended off, current flowing).

  3.7. Commanded vs Actual: Each node publishes what it is **actually executing**, not just what it was told. A node that cannot verify its own outputs reports a hardware fault that cannot be cleared by commanding anything. This is the system's most serious alarm — see the integration guide.

  3.8. Battery diagnostics are **still in development**. The firmware deliberately publishes nothing for battery, and the gateway reports `65535` = unknown. It is never reported as `0%`, which would be indistinguishable from a genuinely flat battery. **SCADA must not alarm on it.**

4. Hardware Components -

  This is the list of components that i have used (feel free to experiment on your own as well)

    Raspberry Pi 5(8GB RAM with Ethernet)

    ESP32 Development Board (DevKit 1)

    LAN8720 ETH PHY Module for the ESP32

    4x ACS712 Current Sensor — 5A parts on the two arrow channels, 20A on the two static zones

    MCP23017 I2C port expander (address 0x20)

    10x logic-level N-channel MOSFETs (100R gate series, 10k pull-down)

    4x MP1584 buck converter (3x 12V LED rails + 1x 5V), AMS1117-3.3 for the 3.3V rail

    12V LED strips — 5x left arrow, 5x right arrow, 2x static zone

    PLC or Modbus Master Simulator (like OpenModscan) for testing

    14.5V PSU (Preferrably a battery charger w/o XT60 connectors)

    12.8V LFP Battery (For ample battery backup)

    10.1" Waveshare DSI Capacitive Touch Display for HMI (1280x800)

> Wiring detail lives in `PCB/PCB_V3/Connections.md`, but **the finalized pin map is
> `Embedded/ESP32/esp32_contract.md` §1**, which supersedes it where they disagree.

5. Software & Libraries -

  5.1. Raspberry Pi:

    Raspberry Pi OS (Debian 13)

  Python 3

    Mosquitto 2.0.21 (MQTT Broker)

    pymodbus **3.11.3** (Python Modbus Library)

    paho-mqtt **2.1.0** (Python MQTT Client Library), `CallbackAPIVersion.VERSION2`

> **These versions are pinned, not incidental.** pymodbus 3.14 removes the datastore API this bridge
> is built on, and paho 1.x callback signatures fail outright on 2.x. See `pi_agent.md` §6–§7 before
> upgrading anything.

  5.2. ESP32:

    Arduino IDE or PlatformIO

    PubSubClient (Arduino MQTT Library)

    Adafruit_MCP23X17 (I2C port expander)

  5.3. Modbus Master:

    OpenModscan (For testing the control values in the Holding Registers of the PLC)

6. How It Works

  The central control room's PLC sends a Modbus command — a mode value written into a holding register in the SCADA buffer (`42001`–`42100`, one per node) — to the Raspberry Pi's IP address on port 502.

  The Python Modbus server script running on the Pi receives the command and updates its internal datastore.

  A dedicated "bridge" thread in the script detects this state change. Depending on the MUX flag (`43001`), it copies either the SCADA buffer or the HMI buffer into the active execution zone (`40001+`).

  The script, acting as an MQTT client, connects to the local Mosquitto broker and publishes the register's integer **verbatim** to that node's topic — for example node 1 receives payload `1` on `metro/signage/register/40001/value`.

  The Mosquitto broker forwards this message to the sign subscribed to that topic.

  The target ESP32 receives the value, decodes it, and executes the physical action — writing the corresponding bit pattern to the MCP23017, which switches the MOSFET gates driving the LED strips. The write is **read back and verified** before the node reports the mode as executing.

**Command values:**

| Value | Meaning |
| --- | --- |
| `0` / `1` / `2` | Arrows OFF / LHS chase / RHS chase (operator) |
| `3` | Both chase (technician) |
| `4` / `5` / `6` | Solid ON left / right / all (technician) |
| `10000`–`11023` | Raw 10-bit MOSFET bitmask, `value − 10000` (technician) |

> **The topic address is the holding-register number (`40001 + i`), not the node index.** Node 1 is
> `register/40001/value`. Get this wrong and the node reads OFFLINE with no error logged anywhere.
>
> **On network loss a node holds its last command** and keeps animating — there is no fail-safe timer
> and no forced ON/OFF state. Note that this survives a link outage but **not** a reboot: NVS
> persistence is not yet implemented, so a node rebooting during an outage comes up dark.

7. Setup & Usage

7.1. Raspberry Pi Setup

  Install the broker:

```bash
sudo apt update && sudo apt install mosquitto mosquitto-clients python3-venv python3-tk -y
```

  Install the Python libraries. **PEP 668 is enforced on Debian 13 — a bare `pip3 install` is refused by the system Python.** Use a venv with `--system-site-packages` so it can also see the apt-installed Tkinter that the HMI needs:

```bash
python3 -m venv --system-site-packages ~/metro-gw/venv
source ~/metro-gw/venv/bin/activate
pip install paho-mqtt==2.1.0 pymodbus==3.11.3
```

  Set the static IPs of the RaspberryPi — `10.45.2.50` on the SCADA side and `192.168.1.10` on the ESP32 side. These are committed values, not examples. See `pi_agent.md` §9 for the NetworkManager profile and §10 for the Mosquitto drop-in config.

7.2. ESP32 Setup

  Open `Embedded/ESP32/Firmware/Firmware.ino` in the Arduino IDE.

  Set `ASSIGNED_REGISTER` for each board — node 1 is `40001`, node 2 is `40002`, and so on. This is the only per-node change; there are no Wi-Fi credentials to set, as the nodes are wired.

  Upload to each ESP32. Confirm on the serial monitor (115200 baud) that the MCP23017 is found on SDA=4 / SCL=13.

> Flash `Firmware_Test/Firmware_Test.ino` first to validate the hardware over serial with no network
> stack — see `Embedded/ESP32/Firmware_Test/README.md`. Production firmware also carries its own
> commissioning menu; press `h` on the serial console.

7.3. Running the Bridge

```bash
source ~/metro-gw/venv/bin/activate
python3 Embedded/RaspberryPi/Gateway/Pi.py
```

  Port 502 is privileged. **Do not run the bridge as root** — keep the daemon unprivileged and grant only the one capability it needs, via the systemd unit in `Embedded/RaspberryPi/Gateway/deploy/`:

```ini
AmbientCapabilities=CAP_NET_BIND_SERVICE
Restart=always
```

  Keep the Gateway and the HMI as **two separate systemd units**. The decoupling exists so an HMI crash cannot take down the SCADA-facing Modbus server.

  The system is now live. Commands sent from the PLC will be relayed over Ethernet to the emergency signs.

> **You can exercise the whole gateway and HMI with zero ESP32 hardware** using
> `Embedded/RaspberryPi/Gateway/bench/simulate_node.py` and `scada_probe.py`. See `pi_agent.md` §13.
