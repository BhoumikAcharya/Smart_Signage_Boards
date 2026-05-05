**Modbus-to-MQTT Bridge for Metro Emergency Signage**

1. Overview Of the Product - 

  This project is a smart EROS, designed for reliable operation in public transit environments like metros and railways. It implements a robust communication bridge on a Raspberry Pi, designed to integrate a central industrial control system (using Modbus) with a distributed network of wireless emergency signs (using MQTT), all through ethernet connectivity. Making the product implemented on a complete local network.
  
  This architecture allows a PLC or a central control room to instantly and reliably activate or change emergency signage across multiple locations in a station or tunnel, using a combination of industrial-grade protocols and modern IoT technology.

2. System Architecture

  The project is built on a decoupled, hub-and-spoke architecture, which is ideal for critical systems:

  Central Control (PLC/Modbus Master): The primary industrial controller, located in a central control room. It initiates commands by sending standard Modbus "Write Multiple Holding Registers (16(0x10))" requests to the Raspberry Pi.
    
  2.1. Raspberry Pi (The Bridge & Broker): This is the core of the system and performs three critical roles simultaneously.
    
  2.2. Modbus TCP Server: A Python script runs continuously, listening for commands from the central controller and updating an internal data model.
    
  2.3. MQTT Broker: The industry-standard Mosquitto broker runs as a service, managing all communication with the wireless signage units.
    
  2.4. Industrial HMI: A 7inch Capacitive touch HMI is connected to the RaspberryPi with manual override.
    
  2.5. Emergency Signs (ESP32 Clients): Each emergency light signage unit is powered by an ESP32.
    
  2.6. The ESP32s connect to the Raspberry Pi over Ethernet with a ETH PHY module called the LAN8720 with MQTT communication.
    
  2.7. They subscribe to specific MQTT topics and wait for commands.
    
  2.8. They are responsible for the final physical action—activating the LEDs via a relay.

3. Features - 
  
  3.1. Protocol Bridging: Seamlessly translates industrial-grade Modbus TCP commands into lightweight MQTT messages suitable for wireless IoT devices.
  
  3.2. Real-Time, Reliable Control: Uses a multi-threaded approach on the Raspberry Pi to ensure low latency between receiving a Modbus command and publishing the corresponding MQTT message.
  
  3.3. Scalable: Easily add or remove signage units across a station without any changes to the central controller or bridge logic. Each sign only needs to know the broker's address and its unique topic.
  
  3.4. Decoupled & Robust: The central controller does not need to know anything about the individual signs, and the signs do not need to know about the controller. This separation makes the system highly reliable and easy to maintain.
  
  3.5. Custom Logic: The bridge script contains specific control logic, allowing complex actions to be easily implemented.
  
  3.6. Diagnostics data: Diagnostics data of the LEDs, ESP32, PSU Failure and the Battery(battery diagnostics still in development) are communicated through MQTT to the RaspberryPi.

4. Hardware Components - 

  This is the list of components that i have used (feel free to experiment on your own as well)
  
    Raspberry Pi 5(8GB RAM with Ethernet)
    
    ESP32 Development Board (DevKit 1)
    
    LAN8720 ETH PHY Module for the ESP32
    
    2x ACS712 5A/20A Current Sensor
    
    5V Dual Channel Relay Modules (to switch power to the LED signage)
    
    2x 12V 12W LEDs
    
    PLC or Modbus Master Simulator (like OpenModscan) for testing
    
    14.5V PSU (Preferrably a battery charger w/o XT60 connectors)
    
    12.8V LFP Battery (For ample battery backup)
    
    7inch Capacitive Touch Display for HMI

5. Software & Libraries - 

  5.1. Raspberry Pi:
  
    Raspberry Pi OS
  
  Python 3
  
    Mosquitto (MQTT Broker)
    
    Pymodbus (Python Modbus Library)
    
    Paho-MQTT (Python MQTT Client Library)
  
  5.2. ESP32:
  
    Arduino IDE or PlatformIO
    
    PubSubClient (Arduino MQTT Library)
  
  5.3. Modbus Master: 
  
    OpenModscan (For testing the control values in the Holding Registers of the PLC)

6. How It Works
  The central control room's PLC sends a Modbus command (e.g., writing a value from 0-3 in a holding regiter(40001-40010)) to the Raspberry Pi's IP address on port 502.

  The Python Modbus server script running on the Pi receives the command and updates its internal datastore.

  A dedicated "bridge" thread in the script detects this state change.

  The script, acting as an MQTT client, connects to the local Mosquitto broker and publishes a message (e.g., payload ON) to a predefined topic (e.g., metro/station1/platformA/emergency_exit/set).

  The Mosquitto broker forwards this message to all signs subscribed to that topic.

  The target ESP32 receives the ON message, and its code executes the physical action—toggling a GPIO pin to activate its connected relay and light up the sign.

7. Setup & Usage (Needs to be updated)
7.1. Raspberry Pi Setup
  Install the necessary software on your Raspberry Pi:

  # Install MQTT Broker
    sudo apt-get update
    sudo apt-get install mosquitto mosquitto-clients -y
    sudo systemctl enable mosquitto.service

    sudo systemctl restart mosquitto.serivce (To restart the mosquitto service)

# Install Python libraries
    pip3 install pymodbus paho-mqtt

  Set the static IP of the RaspberryPi

7.2. ESP32 Setup
  Open the esp32_mqtt_client.ino sketch in the Arduino IDE.

Update the Wi-Fi credentials and the Raspberry Pi's IP address in the configuration section.

Upload the sketch to each of your ESP32 boards. Ensure each sign that needs to be controlled independently is configured to subscribe to a unique MQTT topic.

3. Running the Bridge
Execute the main bridge script on the Raspberry Pi. It requires root privileges to bind to the privileged Modbus port (502).

sudo python3 modbus_mqtt_bridge.py

The system is now live. Commands sent from the PLC will be wirelessly relayed to the emergency signs.
