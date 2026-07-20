"""
Metro Signage Gateway — Modbus TCP <-> MQTT bridge (v2.0.0)

Targets the verified platform contract in ../pi_agent.md:
  paho-mqtt 2.1.0 (CallbackAPIVersion.VERSION2), pymodbus 3.11.3, mosquitto 2.0.21.

Runs headless under systemd. The terminal table is presentation ONLY —
no side effect may live inside a presentation conditional.
"""

import logging
import sys
import time
from threading import Thread, Lock, Event

import paho.mqtt.client as mqtt
from paho.mqtt.client import CallbackAPIVersion
from pymodbus.server import StartTcpServer
from pymodbus.datastore import (
    ModbusSequentialDataBlock,
    ModbusDeviceContext,   # was ModbusSlaveContext (renamed in 3.11.3)
    ModbusServerContext,
)

# --- Configuration ---
MODBUS_PORT = 502
MODBUS_BIND_HOST = "0.0.0.0"   # bind wildcard so a SCADA-side IP change needs no restart
MQTT_HOST = "127.0.0.1"        # loopback listener; ESP32s reach the broker on 192.168.1.10
MQTT_PORT = 1883
MQTT_TOPIC_PREFIX = "metro/signage/register"
REGISTERS_TO_BRIDGE = 100      # Scaled for up to 100 ESP32 nodes on the fiber ring

# --- Modbus datastore layout (see pi_agent.md §4) ---
ADDR_ACTIVE = 0        # 40001+  Active Execution Zone (MUX output -> MQTT)
ADDR_HMI = 1000        # 41001+  HMI Manual Buffer
ADDR_SCADA = 2000      # 42001+  SCADA Auto Buffer
ADDR_MUX_FLAG = 3000   # 43001   0 = SCADA/Auto, 1 = HMI/Manual
ADDR_DIAG = 5000       # 45001+  Diagnostic Block
DIAG_REGS_PER_NODE = 8 # 6 -> 7 (current4) -> 8 (actual executing state)
HR_SIZE = 6000         # covers the diag block end at index 5799

# Sentinel for "unknown", so an offline node is never mistaken for a real reading.
UNKNOWN = 65535

# Logs go to STDERR so they never corrupt the ANSI frame written to stdout.
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)-7s %(message)s",
    stream=sys.stderr,
)
log = logging.getLogger("gateway")

# Dictionaries to store status
device_statuses = {}       # ONLINE:<ip> / OFFLINE
device_power_states = {}   # Power Supply Status (OK/FAIL)
device_current1 = {}       # LHS arrows      (ACS712 GPIO 34)
device_current2 = {}       # RHS arrows      (ACS712 GPIO 35)
device_current3 = {}       # Static Zone 1   (ACS712 GPIO 32)
device_current4 = {}       # Static Zone 2   (ACS712 GPIO 33)
device_batt_pct = {}       # Battery Percentage (stubbed in firmware; slot reserved)
device_state = {}          # Command the node reports ACTUALLY executing

# Metric name -> backing dictionary. Single source of truth for subscribe,
# startup cleanup, and offline scrubbing — add a metric here and all three follow.
METRIC_STORES = {
    "power": device_power_states,
    "current1": device_current1,
    "current2": device_current2,
    "current3": device_current3,
    "current4": device_current4,
    "battery_pct": device_batt_pct,
    "state": device_state,
}

# Thread safety lock for the dictionaries
data_lock = Lock()
# Event to track MQTT connection status
mqtt_connected_event = Event()


# Helper to map ESP32 4-State strings to Modbus Integers for SCADA
def map_discrepancy_to_int(state_str):
    if state_str == "OFF": return 0
    if state_str == "ON": return 1
    if state_str == "FAIL_OPEN": return 2
    if state_str == "FAIL_SHORT": return 3
    return 99  # Unknown/Error state


def map_battery_to_int(batt_str):
    """'---' must NOT become 0 — that is indistinguishable from a flat battery."""
    try:
        return int(batt_str)
    except (TypeError, ValueError):
        return UNKNOWN


def map_state_to_int(state_str):
    """
    The command the node reports actually executing (0-6, or 10000-11023).
    UNKNOWN when the node is offline or has not reported — never 0, which would
    read as a genuine 'arrows OFF' and hide a divergence.
    """
    try:
        value = int(state_str)
    except (TypeError, ValueError):
        return UNKNOWN
    return value if 0 <= value <= 65534 else UNKNOWN


def on_message(client, userdata, msg):
    """Callback for incoming MQTT messages"""
    try:
        topic_parts = msg.topic.split('/')
        if len(topic_parts) != 5:
            return

        register_address = int(topic_parts[3])
        msg_type = topic_parts[4]
        payload = msg.payload.decode()

        with data_lock:
            if msg_type == "status":
                device_statuses[register_address] = payload
                if payload == "OFFLINE":
                    # Offline Scrubbing: clear old data so SCADA doesn't read stale values
                    for store in METRIC_STORES.values():
                        store[register_address] = "---"
            elif msg_type in METRIC_STORES:
                METRIC_STORES[msg_type][register_address] = payload

    except Exception:
        # Malformed packets must not kill the bridge — but they are never silent.
        log.warning("Dropped malformed MQTT message on %s", msg.topic, exc_info=True)


def on_connect(client, userdata, flags, reason_code, properties):
    if reason_code == 0:   # ReasonCode 0 is success — and falsy. Compare explicitly.
        client.subscribe(f"{MQTT_TOPIC_PREFIX}/+/status")
        for metric in METRIC_STORES:
            client.subscribe(f"{MQTT_TOPIC_PREFIX}/+/{metric}")
        mqtt_connected_event.set()
        log.info("MQTT connected to %s:%s", MQTT_HOST, MQTT_PORT)
    else:
        log.error("MQTT connect failed: %s", reason_code)


def on_disconnect(client, userdata, flags, reason_code, properties):
    mqtt_connected_event.clear()
    log.warning("MQTT disconnected: %s (auto-reconnect pending)", reason_code)


def perform_startup_cleanup(client):
    """Purges ghost messages from the broker on startup"""
    for i in range(REGISTERS_TO_BRIDGE):
        addr = 40001 + i
        client.publish(f"{MQTT_TOPIC_PREFIX}/{addr}/status", "OFFLINE", retain=True)
        for metric in METRIC_STORES:
            client.publish(f"{MQTT_TOPIC_PREFIX}/{addr}/{metric}", "---", retain=True)

        with data_lock:
            device_statuses[addr] = "OFFLINE"
            for store in METRIC_STORES.values():
                store[addr] = "---"

    client.publish("metro/signage/scan", "PING", retain=False)
    time.sleep(2)
    log.info("Startup cleanup complete for %d registers", REGISTERS_TO_BRIDGE)


def read_node_telemetry(addr):
    """Snapshot one node's MQTT-sourced state. Caller must NOT hold data_lock."""
    with data_lock:
        return {
            "status": device_statuses.get(addr, "UNKNOWN"),
            "power": device_power_states.get(addr, "---"),
            "c1": device_current1.get(addr, "---"),
            "c2": device_current2.get(addr, "---"),
            "c3": device_current3.get(addr, "---"),
            "c4": device_current4.get(addr, "---"),
            "batt": device_batt_pct.get(addr, "---"),
            "state": device_state.get(addr, "---"),
        }


def write_diagnostic_block(modbus_context, i, tele):
    """
    Translate one node's telemetry into the 8-register SCADA diagnostic block.
    Node i base index = 5000 + (i * 8)  ->  node 1 = 45001-45008.

    UNCONDITIONAL. This ran inside `if sys.stdout.isatty():` in v1.0.1, which meant
    that under systemd the PLC read zeros from every diagnostic register, forever.
    """
    values = [
        1 if tele["status"].startswith("ONLINE:") else 0,   # +0 Network Status
        1 if tele["power"] == "OK" else 0,                  # +1 Main Power
        map_discrepancy_to_int(tele["c1"]),                 # +2 LHS LED Health
        map_discrepancy_to_int(tele["c2"]),                 # +3 RHS LED Health
        map_battery_to_int(tele["batt"]),                   # +4 Battery % (stubbed)
        map_discrepancy_to_int(tele["c3"]),                 # +5 Static Zone 1 Health
        map_discrepancy_to_int(tele["c4"]),                 # +6 Static Zone 2 Health
        map_state_to_int(tele["state"]),                    # +7 Actual executing state
    ]
    modbus_context[0].setValues(3, ADDR_DIAG + (i * DIAG_REGS_PER_NODE), values)


def render_frame(current_values, telemetry, mux_flag, display_limit):
    """Pure string building. No side effects live here."""
    frame = '\033[H'   # cursor home
    frame += "--- Metro Signage Modbus-MQTT Bridge (v2.0.0) ---\n"
    frame += "PLC -> Modbus -> MQTT -> ESP32s\n"

    mode_text = "HMI (MANUAL)" if mux_flag == 1 else "SCADA (AUTO)"
    frame += f"CURRENT MUX MODE: {mode_text} (Flag 43001: {mux_flag})\n"

    sep = ("+------------+---------+----------+----------+-----------------+----------"
           "+-------------+-------------+-------------+-------------+--------+\n")
    frame += sep
    frame += ("| Register   |  Value  |  Actual  |  Status  |   IP Address    |  Power   "
              "|   LHS LED   |   RHS LED   |  Static Z1  |  Static Z2  | Batt % |\n")
    frame += sep

    for i in range(display_limit):
        addr = 40001 + i
        tele = telemetry[i]
        raw_status = tele["status"]

        if raw_status.startswith("ONLINE:"):
            status_text = "ONLINE"
            ip_text = raw_status.split(":", 1)[1] or "---"
        elif raw_status == "OFFLINE":
            status_text, ip_text = "OFFLINE", "---"
        else:
            status_text, ip_text = raw_status, "---"

        batt_display = f"{tele['batt']}%" if tele["batt"] != "---" else "---"

        # Commanded vs actually-executing. '!' marks a node running something other
        # than what it was told — the silent failure this column exists to expose.
        actual = tele["state"]
        if actual == "---":
            actual_display = "---"
        elif actual != str(current_values[i]):
            actual_display = f"{actual} !"
        else:
            actual_display = actual

        frame += (f"| {addr:<10} | {current_values[i]:>7} | {actual_display:>8} "
                  f"| {status_text:<8} | {ip_text:<15} "
                  f"| {tele['power']:<8} | {tele['c1']:<11} | {tele['c2']:<11} "
                  f"| {tele['c3']:<11} | {tele['c4']:<11} | {batt_display:>6} |\n")

    if REGISTERS_TO_BRIDGE > display_limit:
        frame += ("| ...        | ...     | ...      | ...      | ...             | ...      "
                  "| ...         | ...         | ...         | ...         | ...    |\n")
        note = (f"(Displaying {display_limit} of {REGISTERS_TO_BRIDGE} nodes. "
                f"All {REGISTERS_TO_BRIDGE} mapping {DIAG_REGS_PER_NODE} registers/node to SCADA)")
        # Pad from the separator so the box stays square if columns ever change.
        frame += f"| {note:<{len(sep.rstrip(chr(10))) - 4}} |\n"

    frame += sep
    frame += "Monitoring... Press Ctrl+C to stop.\n"
    frame += '\033[J'   # clear to end of screen, removes ghosting
    return frame


def bridge_and_display_loop(modbus_context, mqtt_client):
    last_known_values = [None] * REGISTERS_TO_BRIDGE
    last_ping_time = time.time()
    is_tty = sys.stdout.isatty()

    # Clear the screen entirely just ONCE before the loop starts
    if is_tty:
        sys.stdout.write('\033[2J')

    while True:
        try:
            # --- ESP32 WATCHDOG PING ---
            if time.time() - last_ping_time > 60:
                mqtt_client.publish("metro/signage/scan", "PING", retain=False)
                last_ping_time = time.time()

            # --- MUX LOGIC (Hand/Off/Auto) ---
            mux_flag = modbus_context[0].getValues(3, ADDR_MUX_FLAG, count=1)[0]
            source = ADDR_HMI if mux_flag == 1 else ADDR_SCADA
            modbus_context[0].setValues(
                3, ADDR_ACTIVE,
                modbus_context[0].getValues(3, source, count=REGISTERS_TO_BRIDGE),
            )

            # Now read the ACTUAL output zone (40001+)
            current_values = modbus_context[0].getValues(3, ADDR_ACTIVE, count=REGISTERS_TO_BRIDGE)

            # --- COMMAND FAN-OUT: Modbus -> MQTT ---
            for i in range(REGISTERS_TO_BRIDGE):
                if current_values[i] != last_known_values[i]:
                    addr = 40001 + i
                    # Publish the Modbus integer exactly as-is.
                    # Supports macro modes 0-6 AND the 10000-11023 raw bitmask.
                    # retain=True is required so a rebooting ESP32 gets its target state.
                    mqtt_client.publish(
                        f"{MQTT_TOPIC_PREFIX}/{addr}/value", str(current_values[i]), retain=True
                    )
                    last_known_values[i] = current_values[i]

            # --- SCADA DIAGNOSTIC TRANSLATION (unconditional — never gate on isatty) ---
            telemetry = [read_node_telemetry(40001 + i) for i in range(REGISTERS_TO_BRIDGE)]
            for i in range(REGISTERS_TO_BRIDGE):
                write_diagnostic_block(modbus_context, i, telemetry[i])

            # --- PRESENTATION ONLY ---
            if is_tty:
                sys.stdout.write(
                    render_frame(current_values, telemetry, mux_flag,
                                 min(REGISTERS_TO_BRIDGE, 15))
                )
                sys.stdout.flush()

            time.sleep(1)

        except Exception:
            # Log and CONTINUE. v1.0.1 used `break`, so one transient error killed
            # the bridge permanently and Restart=always merely crash-looped it.
            log.exception("Error in bridge loop; continuing")
            time.sleep(1)


if __name__ == '__main__':
    mqtt_client = None
    try:
        store = ModbusDeviceContext(hr=ModbusSequentialDataBlock(0, [0] * HR_SIZE))
        context = ModbusServerContext(devices=store, single=True)

        mqtt_client = mqtt.Client(CallbackAPIVersion.VERSION2, client_id="ModbusBridgeClient")
        mqtt_client.on_connect = on_connect
        mqtt_client.on_disconnect = on_disconnect
        mqtt_client.on_message = on_message
        mqtt_client.connect(MQTT_HOST, MQTT_PORT, 60)
        mqtt_client.loop_start()

        log.info("Waiting for MQTT connection to establish...")
        if mqtt_connected_event.wait(timeout=10):
            perform_startup_cleanup(mqtt_client)
        else:
            log.error("MQTT did not connect within 10s — starting anyway, "
                      "paho will keep retrying in the background.")

        modbus_thread = Thread(
            target=StartTcpServer,
            kwargs={'context': context, 'address': (MODBUS_BIND_HOST, MODBUS_PORT)},
            daemon=True,
        )
        modbus_thread.start()
        log.info("Modbus TCP server listening on %s:%s", MODBUS_BIND_HOST, MODBUS_PORT)

        bridge_and_display_loop(context, mqtt_client)

    except KeyboardInterrupt:
        log.info("Script stopped by user.")
    finally:
        log.info("Shutting down...")
        if mqtt_client:
            mqtt_client.loop_stop()
