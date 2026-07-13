import paho.mqtt.client as mqtt
from pymodbus.server import StartTcpServer
from pymodbus.datastore import ModbusSequentialDataBlock, ModbusSlaveContext, ModbusServerContext
from threading import Thread, Lock, Event
import time
import sys

# --- Configuration ---
MODBUS_PORT = 502
MQTT_BROKER_HOST = "0.0.0.0"
MQTT_PORT = 1883
MQTT_TOPIC_PREFIX = "metro/signage/register"
REGISTERS_TO_BRIDGE = 100 # Scaled for up to 100 ESP32 nodes on the fiber ring

# Dictionaries to store status
device_statuses = {}       # Stores ONLINE/OFFLINE
device_power_states = {}   # Stores Power Supply Status (OK/FAIL)
device_current1 = {}       # Stores LHS LED Status (ON/OFF/FAIL_OPEN/FAIL_SHORT)
device_current2 = {}       # Stores RHS LED Status (ON/OFF/FAIL_OPEN/FAIL_SHORT)
device_current3 = {}       # Stores Static LED Status (ON/OFF/FAIL_OPEN/FAIL_SHORT)
device_batt_pct = {}       # Stores Battery Percentage (100, 50, 0)

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
    return 99 # Unknown/Error state

def on_message(client, userdata, msg):
    """Callback for incoming MQTT messages"""
    try:
        topic_parts = msg.topic.split('/')
        if len(topic_parts) == 5:
            register_address = int(topic_parts[3])
            msg_type = topic_parts[4]
            payload = msg.payload.decode()

            with data_lock:
                if msg_type == "status":
                    device_statuses[register_address] = payload
                    if payload == "OFFLINE":
                        # Offline Scrubbing: Clear old data so SCADA doesn't read stale values
                        device_power_states[register_address] = "---"
                        device_current1[register_address] = "---"
                        device_current2[register_address] = "---"
                        device_current3[register_address] = "---"
                        device_batt_pct[register_address] = "---"
                
                elif msg_type == "power":
                    device_power_states[register_address] = payload
                
                elif msg_type == "current1":
                    device_current1[register_address] = payload
                
                elif msg_type == "current2":
                    device_current2[register_address] = payload
                    
                elif msg_type == "current3":
                    device_current3[register_address] = payload
                
                elif msg_type == "battery_pct":
                    device_batt_pct[register_address] = payload

    except Exception as e:
        pass # Silently drop malformed MQTT packets so the bridge doesn't crash

def on_connect(client, userdata, flags, rc):
    if rc == 0:
        client.subscribe(f"{MQTT_TOPIC_PREFIX}/+/status")
        client.subscribe(f"{MQTT_TOPIC_PREFIX}/+/power")
        client.subscribe(f"{MQTT_TOPIC_PREFIX}/+/current1")
        client.subscribe(f"{MQTT_TOPIC_PREFIX}/+/current2")
        client.subscribe(f"{MQTT_TOPIC_PREFIX}/+/current3")
        client.subscribe(f"{MQTT_TOPIC_PREFIX}/+/battery_pct")
        mqtt_connected_event.set()  # Signal successful connection

def perform_startup_cleanup(client):
    """Purges ghost messages from the broker on startup"""
    for i in range(REGISTERS_TO_BRIDGE):
        addr = 40001 + i
        client.publish(f"{MQTT_TOPIC_PREFIX}/{addr}/status", "OFFLINE", retain=True)
        client.publish(f"{MQTT_TOPIC_PREFIX}/{addr}/power", "---", retain=True)
        client.publish(f"{MQTT_TOPIC_PREFIX}/{addr}/current1", "---", retain=True)
        client.publish(f"{MQTT_TOPIC_PREFIX}/{addr}/current2", "---", retain=True)
        client.publish(f"{MQTT_TOPIC_PREFIX}/{addr}/current3", "---", retain=True)
        client.publish(f"{MQTT_TOPIC_PREFIX}/{addr}/battery_pct", "---", retain=True)
        
        with data_lock:
            device_statuses[addr] = "OFFLINE"
            device_power_states[addr] = "---"
            device_current1[addr] = "---"
            device_current2[addr] = "---"
            device_current3[addr] = "---"
            device_batt_pct[addr] = "---"
        
    client.publish("metro/signage/scan", "PING", retain=False)
    time.sleep(2)

def bridge_and_display_loop(modbus_context, mqtt_client):
    last_known_values = [None] * REGISTERS_TO_BRIDGE
    last_ping_time = time.time()

    # Clear the screen entirely just ONCE before the loop starts
    if sys.stdout.isatty():
        sys.stdout.write('\033[2J')

    while True:
        try:
            # --- ESP32 WATCHDOG PING ---
            if time.time() - last_ping_time > 60:
                mqtt_client.publish("metro/signage/scan", "PING", retain=False)
                last_ping_time = time.time()

            # --- MUX LOGIC (Hand/Off/Auto) ---
            # Read Control Flag (43001 -> Address 3000)
            mux_flag = modbus_context[0].getValues(3, 3000, count=1)[0]

            if mux_flag == 1:
                # Manual/HMI Mode: Copy 41001+ (Address 1000) to 40001+ (Address 0)
                hmi_values = modbus_context[0].getValues(3, 1000, count=REGISTERS_TO_BRIDGE)
                modbus_context[0].setValues(3, 0, hmi_values)
            else:
                # Auto/SCADA Mode: Copy 42001+ (Address 2000) to 40001+ (Address 0)
                scada_values = modbus_context[0].getValues(3, 2000, count=REGISTERS_TO_BRIDGE)
                modbus_context[0].setValues(3, 0, scada_values)
            # -----------------------------

            # Now read the ACTUAL output zone (40001+)
            current_values = modbus_context[0].getValues(3, 0, count=REGISTERS_TO_BRIDGE)

            for i in range(REGISTERS_TO_BRIDGE):
                if current_values[i] != last_known_values[i]:
                    addr = 40001 + i
                    topic = f"{MQTT_TOPIC_PREFIX}/{addr}/value"
                    payload = str(current_values[i])
                    # Publish the Modbus integer exactly as-is (supports 0-4 and 10000+ offsets)
                    mqtt_client.publish(topic, payload, retain=True)
                    last_known_values[i] = current_values[i]
            
            # --- DOUBLE-BUFFERED UI DRAWING ---
            if sys.stdout.isatty():
                # Start the frame and move cursor to top-left
                frame = '\033[H'
                
                frame += "--- Metro Signage Modbus-MQTT Bridge ---\n"
                frame += "PLC -> Modbus -> MQTT -> ESP32s\n"
                
                mode_text = "HMI (MANUAL)" if mux_flag == 1 else "SCADA (AUTO)"
                frame += f"CURRENT MUX MODE: {mode_text} (Flag 43001: {mux_flag})\n"
                
                # UI Table adjusted to accommodate 11-character 4-state strings (e.g., FAIL_SHORT)
                frame += "+------------+---------+----------+-----------------+----------+-------------+-------------+-------------+--------+\n"
                frame += "| Register   |  Value  |  Status  |   IP Address    |  Power   |   LHS LED   |   RHS LED   | Static LED  | Batt % |\n"
                frame += "+------------+---------+----------+-----------------+----------+-------------+-------------+-------------+--------+\n"
                
                # Only display the first 15 in terminal so it doesn't overflow, but process all 100 for SCADA
                display_limit = min(REGISTERS_TO_BRIDGE, 15) 

                for i in range(REGISTERS_TO_BRIDGE):
                    addr = 40001 + i
                    val = current_values[i]
                    
                    with data_lock:
                        raw_status = device_statuses.get(addr, "UNKNOWN")
                        power_text = device_power_states.get(addr, "---")
                        c1_text = device_current1.get(addr, "---")
                        c2_text = device_current2.get(addr, "---")
                        c3_text = device_current3.get(addr, "---")
                        batt_text = device_batt_pct.get(addr, "---")

                    # --- SCADA MODBUS TRANSLATION BLOCK ---
                    # 1. Status: 1 = Online, 0 = Offline
                    modbus_status = 1 if raw_status.startswith("ONLINE:") else 0
                    
                    # 2. Power: 1 = OK, 0 = Fail
                    modbus_power = 1 if power_text == "OK" else 0
                    
                    # 3. LHS LED (Current 1): Uses 4-State mapping
                    modbus_c1 = map_discrepancy_to_int(c1_text)
                        
                    # 4. RHS LED (Current 2): Uses 4-State mapping
                    modbus_c2 = map_discrepancy_to_int(c2_text)
                    
                    # 5. Battery Percentage
                    try:
                        modbus_batt = int(batt_text)
                    except ValueError:
                        modbus_batt = 0
                        
                    # 6. Static LED (Current 3)
                    modbus_c3 = map_discrepancy_to_int(c3_text)

                    # Write the 6 diagnostic registers to Address 5000+ (Register 45001+)
                    # Node i starts at index 5000 + (i * 6)
                    diagnostic_base_index = 5000 + (i * 6) 
                    modbus_context[0].setValues(3, diagnostic_base_index, [modbus_status, modbus_power, modbus_c1, modbus_c2, modbus_batt, modbus_c3])
                    # ----------------------------------------

                    # Draw to terminal only if under the display limit
                    if i < display_limit:
                        if raw_status.startswith("ONLINE:"):
                            status_text = "ONLINE"
                            parts = raw_status.split(":", 1)
                            ip_text = parts[1] if len(parts) > 1 else "---"
                        elif raw_status == "OFFLINE":
                            status_text = "OFFLINE"
                            ip_text = "---"
                        else:
                            status_text = raw_status
                            ip_text = "---"
                            
                        batt_display = f"{batt_text}%" if batt_text != "---" else "---"

                        frame += f"| {addr:<10} | {val:>7} | {status_text:<8} | {ip_text:<15} | {power_text:<8} | {c1_text:<11} | {c2_text:<11} | {c3_text:<11} | {batt_display:>6} |\n"
                
                if REGISTERS_TO_BRIDGE > display_limit:
                    frame += f"| ...        | ...     | ...      | ...             | ...      | ...         | ...         | ...         | ...    |\n"
                    frame += f"| (Displaying {display_limit} of {REGISTERS_TO_BRIDGE} nodes. All {REGISTERS_TO_BRIDGE} mapping 6 registers/node to SCADA)                                         |\n"

                frame += "+------------+---------+----------+-----------------+----------+-------------+-------------+-------------+--------+\n"
                frame += "Monitoring... Press Ctrl+C to stop.\n"
                
                # Clear to end of screen to remove any ghosting
                frame += '\033[J'

                # Push the entire frame atomically
                sys.stdout.write(frame)
                sys.stdout.flush()

            time.sleep(1)
            
        except Exception as e:
            sys.stdout.write(f"\nError in bridge loop: {e}\n")
            sys.stdout.flush()
            time.sleep(1)
            break

if __name__ == '__main__':
    mqtt_client = None
    try:
        # Expanded Modbus memory block to 6000 to cover Addresses up to 45600+ for SCADA Diagnostics
        store = ModbusSlaveContext(hr=ModbusSequentialDataBlock(0, [0] * 6000))
        context = ModbusServerContext(slaves=store, single=True)

        mqtt_client = mqtt.Client("ModbusBridgeClient")
        mqtt_client.on_connect = on_connect
        mqtt_client.on_message = on_message
        mqtt_client.connect("localhost", MQTT_PORT, 60) # Internal Network
        mqtt_client.loop_start()

        sys.stdout.write("Waiting for MQTT connection to establish...\n")
        sys.stdout.flush()
        
        if mqtt_connected_event.wait(timeout=10):
            perform_startup_cleanup(mqtt_client)

        modbus_thread = Thread(target=StartTcpServer, kwargs={'context': context, 'address': (MQTT_BROKER_HOST, MODBUS_PORT)}, daemon=True)
        modbus_thread.start()

        bridge_and_display_loop(context, mqtt_client)
        
    except KeyboardInterrupt:
        sys.stdout.write("\nScript stopped by user.\n")
        sys.stdout.flush()
    finally:
        sys.stdout.write("Shutting down...\n")
        sys.stdout.flush()
        if mqtt_client:
            mqtt_client.loop_stop()