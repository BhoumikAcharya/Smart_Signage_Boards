# Metro Signage IoT: Raspberry Pi Bridge

**Version:** 1.0.1

**Role:** Master Translation Gateway (Modbus TCP ↔ MQTT)

## 📌 Overview

This Python application is the central nervous system of the Metro Signage IoT network. It serves as an industrial protocol bridge, translating commands from a legacy SCADA/PLC system (via Modbus TCP) into lightweight IoT messages (via MQTT) for up to 100 ESP32 edge nodes.

It runs a fully local Modbus TCP server, maintains thread-safe state dictionaries, handles Hand/Off/Auto (MUX) switching, and presents a flicker-free CLI dashboard for real-time monitoring.

## 🏗️ System Architecture

The application utilizes a robust multi-threaded design to prevent network blocking and ensure zero-latency translation:

1. **Main Thread (Logic & UI):** Handles the Modbus memory translation, MUX routing, array comparisons, and renders the ANSI-escaped terminal UI.

2. **Modbus Thread (SCADA Facing):** A daemon thread running `pymodbus.server` listening on port `502`.

3. **MQTT Thread (Edge Facing):** A background thread managed by Paho-MQTT's `loop_start()` handling asynchronous inbound telemetry from the ESP32 nodes.

## ✨ Key Features

### 1. Air-Gapped Network Support

The Modbus server binds to `0.0.0.0`, allowing the Raspberry Pi to operate with dual IP addresses. The internal `eth0` interface can host the isolated ESP32 MQTT network, while an alias IP or USB-Ethernet adapter handles the external Modbus SCADA traffic.

### 2. Thread-Safe State Management

To prevent data tearing when the background MQTT thread receives data at the exact microsecond the Main thread is reading it, a `threading.Lock()` (`data_lock`) isolates all global telemetry dictionaries.

### 3. HMI / SCADA MUX Logic (Hand/Off/Auto)

The bridge supports seamless switching between Automated SCADA control and Manual HMI control without losing state tracking:

* Modbus Register `43001` acts as the MUX flag.

* If `0` (Auto): The bridge copies commands from the SCADA buffer (`42001+`) to the active execution zone (`40001+`).

* If `1` (Manual): The bridge locks out SCADA and copies commands from the HMI buffer (`41001+`) to the execution zone.

### 4. 4-State Discrepancy Translation

The ESP32 nodes send intelligent diagnostic strings instead of binary OK/FAIL states. The bridge intercepts these strings and translates them into integers for SCADA diagnostics:

* `"OFF"` ➔ `0` (Intended OFF, no current)

* `"ON"` ➔ `1` (Intended ON, current flowing)

* `"FAIL_OPEN"` ➔ `2` (Intended ON, but 0 Amps flowing — e.g., burnt LED)

* `"FAIL_SHORT"` ➔ `3` (Intended OFF, but current flowing — e.g., welded relay)

### 5. Ghost Message Purging & Retained States

On startup, the bridge executes `perform_startup_cleanup()`. It publishes `"OFFLINE"` and `"---"` to all 100 nodes with `retain=True`. This purges the Mosquitto broker's database of any stale data from previous unexpected shutdowns, ensuring the UI and SCADA always start with a clean slate.

### 6. Hardware Watchdog Pinging

The Main thread broadcasts a `"PING"` payload to the `metro/signage/scan` topic every 60 seconds. This resets the hardware fail-safe timers (Dead-Man Switch) programmed into the ESP32 microcontrollers.

## 🗺️ Modbus Memory Map

The system uses a highly structured memory map allowing the PLC to control and diagnose 100 unique nodes seamlessly.

| Memory Block | Range | Description | 
| ----- | ----- | ----- | 
| **Active Execution Zone** | `40001 - 40100` | The actual commands (0-3 or 10000+ Modbus offset) sent to the ESP32s. | 
| **HMI Manual Buffer** | `41001 - 41100` | Commands queued by the local Graphical HMI. | 
| **SCADA Auto Buffer** | `42001 - 42100` | Commands queued by the central PLC. | 
| **MUX Control Flag** | `43001` | `0` = Read from SCADA Buffer, `1` = Read from HMI Buffer. | 
| **Diagnostic Block** | `45001 - 45600` | 6 continuous telemetry registers per node (See below). | 

### Diagnostic Register Layout (Per Node)

Each node occupies exactly 6 registers in the `45001+` diagnostic block. For example, Node 1 (40001) occupies `45001 - 45006`. Node 2 (40002) occupies `45007 - 45012`.

1. **Network Status:** `1` = Online, `0` = Offline

2. **Main Power:** `1` = OK, `0` = PSU Failure

3. **LHS LED Health:** `0`, `1`, `2`, or `3` (via 4-State logic)

4. **RHS LED Health:** `0`, `1`, `2`, or `3` (via 4-State logic)

5. **Battery Percentage:** `0` to `100` (%)

6. **Static LED Health:** `0`, `1`, `2`, or `3` (via 4-State logic)

## 🚀 Installation & Usage

### 1. Prerequisites

Ensure you have Python 3 installed along with a running instance of Eclipse Mosquitto on the Raspberry Pi.

```bash
sudo apt update
sudo apt install mosquitto mosquitto-clients python3-venv
```

### 2. Install Dependencies

It is highly recommended to use a virtual environment to satisfy PEP-668 requirements.

```bash
python3 -m venv bridge_env
source bridge_env/bin/activate
pip install paho-mqtt pymodbus
```

### 3. Run the Bridge

```bash
python3 rpi_v1.py
```

You will see the screen clear, the startup cleanup execute, and the double-buffered terminal dashboard appear tracking all nodes in real-time.