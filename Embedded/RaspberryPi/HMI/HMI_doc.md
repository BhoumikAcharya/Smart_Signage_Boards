## Metro Signage SCADA HMI - Technical & Design Specifications

Document Version: 1.0 (Baseline)
Project Phase: V1 Decoupled Architecture

| Version | Date | Author | Description |
| ------- | ---- | ------ | ----------- |
| 1.0 | May 2026 | Core Engineering | Initial V1 Baseline creation. |

### Revision History:
#### Name: Bhoumik Acharya

#### Version: 1.0

#### Last Edited Date: 05/05/2026

#### Description: This is the baseline, created the UI, interlinked pages and cimmunication with the nodes and the gateway

### 1. System Overview & Architectural Paradigm

#### 1.1 Objective

    The Metro Signage HMI is a Python/Tkinter-based graphical interface designed to run on a Raspberry Pi with a 1024x600 Waveshare capacitive touch display. It provides real-time monitoring and manual override capabilities for up to 100 ESP32-based edge nodes.

#### 1.2 The "Decoupled Viewer" Architecture (The Why)

    Initially, the HMI was conceptualized as a monolith (hosting the UI, Modbus Server, and MQTT Logic in one script). However, for V1, a strict Decoupled Architecture was adopted.

    The Reason: If the HMI UI crashes, freezes, or is restarted by an operator, the physical SCADA network must not go down.

    The Implementation: The Modbus Server and MUX bridging logic run in a separate, headless, 24/7 background service. The Tkinter HMI script acts purely as a "Dumb Terminal/Client." It connects to 127.0.0.1:502 (Modbus) and 127.0.0.1:1883 (MQTT) simply to view and push commands to the true master service.

### 2. Design System & UI/UX Constraints

#### 2.1 The "Industrial Precision" Theme

    The UI was built to be highly visible in varying, potentially low-light industrial environments.

    Backgrounds: Deep charcoal and obsidian (#131313, #1c1b1b) to reduce eye strain.

    Active States: High-luminance Cyan (#00daf3) for immediate recognition of active relays/selected elements.

    Status Colors: Standardized semantic colors: Lime Green (#6cec00) for OK, Safety Orange (#ff8a00) for degraded/overrides, Red (#ffb4ab) for critical faults.

#### 2.2 Touch Ergonomics (The Why)

    Operating on a 7-inch touchscreen, often with gloved hands, necessitates specific UI choices:

    No OS Keyboards: Standard Linux virtual keyboards break fullscreen Kiosk applications. A custom VirtualKeyboard class was built from scratch directly into Tkinter to ensure modal, safe data entry.

    Large Touch Targets: Buttons do not use internal padding (ipadx). Instead, they use explicit character width/height definitions (e.g., width=14, height=2) to guarantee a massive bounding box that will not clip text on Linux window managers.

### 3. State Management & Threading Model

    Tkinter is strictly single-threaded. If it waits for a network packet, the screen freezes. To solve this, V1 uses a three-pillar asynchronous approach.

#### 3.1 The NODE_DATA Dictionary

    A global dictionary acts as the central source of truth. It is guarded by a threading.Lock() to prevent race conditions between reading and writing.

#### 3.2 The MQTT Background Thread (The Writer)

    The paho-mqtt library runs a background loop_start(). When it receives a payload from an ESP32 (e.g., a battery update), it briefly grabs the thread lock, updates the specific node in the NODE_DATA dictionary, and releases the lock.

#### 3.3 The sync_loop Heartbeat (The Reader)

    The Tkinter main thread runs a .after(500, self.sync_loop) function. Twice a second, the UI grabs the thread lock, takes a snapshot of NODE_DATA, and updates text labels/canvas colors on the currently active screen.

    The Reason: This guarantees a buttery-smooth 60FPS user interface, as the UI never waits on a network socket; it only ever reads from local memory.

### 4. Protocol & Register Mapping

#### 4.1 MQTT Integration (ESP32 to HMI)

    The HMI subscribes to metro/signage/register/+/+.

    Incoming: Parses status, power, current1, current2, and battery_pct to update telemetry.

    Outgoing: The BROADCAST PING button publishes "PING" to metro/signage/scan to reset the 5-minute hardware fail-safes on all ESP32s.

#### 4.2 Modbus TCP Integration (HMI to SCADA/Gateway)

    The HMI acts as a Modbus Client to the local Gateway daemon.

    MUX Toggle (Register 43001 / Address 3000): Writing 1 engages HMI Manual Mode. Writing 0 reverts to SCADA Auto Mode.

    Relay Overrides (Registers 41001+ / Address 1000+): When in Manual mode, the HMI calculates a binary state (0 to 3) based on the Relay 1 and Relay 2 buttons and writes it to the specific node's offset in the 41000 memory block.

    Live Polling: The UI actively polls the Modbus registers every 500ms to visually reflect the physical state of the relays, ensuring the UI accurately represents what the PLC is doing even when the HMI is locked in SCADA mode.

### 5. Deployment Considerations (Kiosk Mode)

    For physical deployment, the V1 application is designed to be hardened:

    self.attributes('-fullscreen', True) must be enabled in production to hide the Linux taskbar.

    The Raspberry Pi OS must have screen-blanking (xset s noblank) disabled.

    The hmi_app.py script should be triggered via a systemd service upon boot to guarantee auto-recovery in the event of a station power loss.