### 1. Power Tree Connections
* **Main Input:** 14.5V DC entering via terminal block. Connect the positive terminal to the anode of a reverse-polarity protection/isolation diode.
* **Battery:** Connect the backup battery positive terminal in parallel to the cathode of the isolation diode (the 14.5V system bus).
* **Buck Converters (5x MP1584):**
    * **IN (All 5 units):** Connect to the 14.5V system bus.
    * **Buck 1 OUT (12V):** Connect to the positive terminals for the LHS and RHS LEDs.
    * **Buck 2 OUT (12V):** Connect to the positive terminals for the Static LEDs (Zone A).
    * **Buck 3 OUT (12V):** Connect to the positive terminals for the Static LEDs (Zone B).
    * **Buck 4 OUT (12V):** Float/No Connection (Expansion buffer).
    * **Buck 5 OUT (5.0V):** Connect to the VCC pins of the 4x ACS712 sensors and the IN pin of the AMS1117-3.3 linear regulator.
* **Linear Regulator (AMS1117-3.3):**
    * **OUT (3.3V Rail):** Connect to the ESP32 (3V3 pin), LAN8720 VCC, MCP23017 VCC, and the I2C Pull-up resistors.

### 2. Common Ground Net
* **GND:** Tie the ground of the 14.5V input, the battery, all 5x MP1584 converters, the AMS1117, the ESP32, the LAN8720, the MCP23017, all ACS712 sensors, and all MOSFET Sources to a single continuous Common GND net.

### 3. ESP32 & Ethernet Connections
* **Power:** 3V3 pin to 3.3V Rail. GND to Common GND.
* **Ethernet PHY (LAN8720):**
    * GPIO 17 ➔ ETH_CLK
    * GPIO 18 ➔ ETH_MDIO
    * GPIO 23 ➔ ETH_MDC
    * GPIO 0, 19, 21, 22, 25, 26, 27 ➔ Ethernet MAC Pins

### 4. Sensor Connections (ADC1)
* **Power Supply Monitor (GPIO 36 / VP):** Connect to the center point of a voltage divider stepping down the 14.5V system bus to a maximum of 3.0V.
* **Battery Monitor (GPIO 39 / VN):** Connect to the center point of a voltage divider stepping down the Battery positive terminal to a maximum of 3.0V.
* **Current Sensors (ACS712):**
    * GPIO 34 ➔ ACS712 VOUT (LHS Load)
    * GPIO 35 ➔ ACS712 VOUT (RHS Load)
    * GPIO 32 ➔ ACS712 VOUT (Static Load)

### 5. I2C Expansion Connections (MCP23017)
* **VCC:** Connect to 3.3V Rail.
* **A0, A1, A2:** Connect all three directly to Common GND.
* **RESET:** Connect directly to 3.3V Rail.
* **SDA:** Connect to ESP32 GPIO 4. Connect a 4.7kΩ pull-up resistor from this net to the 3.3V Rail.
* **SCL:** Connect to ESP32 GPIO 13. Connect a 4.7kΩ pull-up resistor from this net to the 3.3V Rail.

### 6. MOSFET Output Connections (10x Logic-Level N-Channel)
* **LHS LEDs (5x MOSFETs):**
    * **Gate:** Connect to MCP23017 pins A0 through A4. Insert a 100Ω series resistor between the MCP23017 pin and the Gate. Connect a 10kΩ pull-down resistor from the Gate to Common GND.
* **RHS LEDs (5x MOSFETs):**
    * **Gate:** Connect to MCP23017 pins B0 through B4. Insert a 100Ω series resistor between the MCP23017 pin and the Gate. Connect a 10kΩ pull-down resistor from the Gate to Common GND.
* **Sources (All 10x MOSFETs):** Connect directly to Common GND.
* **Drains (All 10x MOSFETs):** Connect to the respective Negative (-) output terminals for the LED strips.