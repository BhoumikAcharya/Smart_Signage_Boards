## Raspberry Pi Modbus-MQTT Bridge

Name: Bhoumik Acharya

Date: 06/05/2026

Version: 1.0.1

Role: Master Translation Gateway

### 1. Architectural Overview

This Python application serves as the central nervous system of the Metro Signage network. It runs a local PyModbus TCP Server to communicate with the industrial SCADA/PLC system and simultaneously runs a Paho-MQTT client to communicate with up to 100 ESP32 edge nodes.

The application operates on a multi-threaded architecture:

- Main Thread: Manages the Modbus memory translation, MUX logic, and the ANSI-escaped terminal UI.

- Modbus Thread: A daemon thread running StartTcpServer.

- MQTT Thread: A background thread managed by Paho-MQTT's loop_start() handling asynchronous inbound messages from the edge nodes.

### 2. Thread-Safe State Management

    To prevent data tearing when the MQTT thread writes data and the Main thread reads data, the system uses threading.Lock() (instantiated as data_lock).

- #### Global Dictionaries (The State Buffer)

    The application maintains 5 primary dictionaries, keyed by the Modbus Address (e.g., 40001):

    - device_statuses = {} | Stores "ONLINE:<IP>" or "OFFLINE".

    - device_power_states = {} | Stores main PSU health: "OK", "FAIL", or "---".

    - device_current1 = {} | Stores Load 1 health: "OK", "FAIL", or "---".

    - device_current2 = {} | Stores Load 2 health: "OK", "FAIL", or "---".

    - device_batt_pct = {} | Stores battery percentage as a string: "100", "50", "0", or "---".

### 3. Core Functions

- #### on_message(client, userdata, msg)

    - Trigger: Fires asynchronously whenever a subscribed MQTT topic receives a payload.

    - Logic: 1. Splits the topic string to extract the register_address (e.g., 40001) and the msg_type (e.g., power) 2. Acquires data_lock, 3. Updates the corresponding global dictionary, 4. If the status is "OFFLINE", it actively scrubs the other dictionaries for that node, resetting them to "---" to prevent the UI from displaying stale, inaccurate sensor data.

    - Fault Tolerance: Wrapped in a silent try/except block. If an ESP32 sends a malformed packet, the function aborts rather than crashing the bridge.

- #### perform_startup_cleanup(client)

    - Trigger: Runs once upon successful connection to the MQTT broker (mqtt_connected_event.wait()).

    - Logic: Iterates through all 100 possible nodes. It publishes "OFFLINE" and "---" to all status/sensor topics with retain=True. This purges the Mosquitto broker's database of any "ghost" data left over from previous unexpected shutdowns.

- #### bridge_and_display_loop(modbus_context, mqtt_client)

    - This is the infinite while True loop running on the Main thread.

    - Heartbeat (PING): Checks time.time(). Every 60 seconds, it publishes "PING" to metro/signage/scan. This resets the hardware fail-safe timers on all ESP32s.

    - MUX Logic: Reads Modbus Register 43001 (Index 3000).

        - If 1 (HMI mode), it copies memory from 41001-41100 into the active output zone 40001-40100.

        - If 0 (SCADA mode), it copies memory from 42001-42100 into the active output zone.

    - Command Dispatch: Compares the active output zone against last_known_values. If a change occurred, it publishes the new integer command (0-3) to the ESP32 via MQTT with retain=True.

    - SCADA Diagnostic Translation: Converts the text-based global dictionaries into pure 16-bit integers so the PLC can read them. It writes 6 sequential registers per node to the 45001+ block.

        - Reg 1: Status (1=Online, 0=Offline)

        - Reg 2: Power (1=OK, 0=Fail)

        - Reg 3: Current1 (1=OK, 0=Fail)

        - Reg 4: Current2 (1=OK, 0=Fail)

        - Reg 5: Battery (0-100 integer)

        - Reg 6: Buffer (Reserved)

    - UI Rendering: Uses \033[H (Cursor Home) and \033[J (Clear to end) to draw a static, double-buffered terminal UI without CPU-intensive clearing.

### 4. Modbus Memory Map Summary

- 40001 - 40100: Active Relay Commands (0-3). Translated directly to MQTT.

- 41001 - 41100: HMI Manual Command Buffer.

- 42001 - 42100: SCADA Automated Command Buffer.

- 43001: MUX Switch (0=SCADA, 1=HMI).

- 45001 - 45600: Diagnostic Block (6 registers per node, reporting health up to PLC).