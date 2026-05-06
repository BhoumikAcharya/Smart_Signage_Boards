## ESP32 Signage Firmware (Enterprise Edition)

Name:Bhoumik Acharya

Date: 05/05/2026

Version: 1.1.0

| Version | Date | Author | Description |
| ------- | ---- | ------ | ----------- |
| 1.0 | May 2026 | Core Engineering | Initial V1.1.0 Baseline creation. |

Version: 1.0.1
Role: Hardware Edge Controller

Description: This is the baseline code for the ESP32

### 1. Architectural Overview

This firmware utilizes FreeRTOS to strictly decouple network operations from hardware operations via a Dual-Core architecture. This ensures that severe network latency, dropped packets, or broker restarts never interrupt the reading of critical analog sensors or battery compensation math.

#### Core Separation

- Core 1 (Application/Network): Handles *setup()*, the *main loop()*, Ethernet events, and MQTT payload parsing.

- Core 0 (Hardware DSP): Runs a dedicated *SensorTask* pinned to the core. Handles ADC reads, median filtering, and battery math.

#### Inter-Process Communication (IPC)

The cores share data using a FreeRTOS QueueHandle_t named sensorQueue configured with a length of 1.

- It utilizes the Mailbox Pattern. Core 0 uses *xQueueOverwrite()* to blindly drop a serialized *NodeStateMsg* struct into the queue. Core 1 checks the queue non-blockingly using *xQueueReceive()*. This eliminates mutex bottlenecks and race conditions.

### 2. Core Security & Resilience

#### Hardware Watchdog Timer (WDT)

Configured using the modern ESP-IDF v5 (*esp_task_wdt_config_t*) struct. The WDT timeout is set to 15 seconds. Both the main *loop()* (Core 1) and *SensorTask* (Core 0) must call *esp_task_wdt_reset()*. If either core freezes in an infinite loop, the hardware physical resets the chip.

#### 5-Minute Fail-Safe (Dead-Man Switch)

Core 1 tracks *lastCommsTime*. This is updated whenever any valid MQTT message arrives on the control topic, OR when the Raspberry Pi broadcasts the *PING* string to the scan topic.

- If *lastCommsTime* exceeds 300,000ms (5 minutes), the ESP32 assumes total network blackout.

- It enters *inFailSafeMode = true*, forces physical relays 1 and 2 *LOW* (Signage ON), and sets *currentRelayState = 3*.

#### EMI Immune ADC Filtering

    Instead of simple averaging, the system uses *getMedianADC()*. It takes a fixed-size *int samples[51]* buffer directly on the task stack, reads 51 consecutive ADC values, and runs an Insertion Sort algorithm. It returns the absolute middle value, effectively ignoring 100% of high-voltage transient spikes caused by train EMI.

### 3. Sensor & Math Algorithms

#### Dynamic Battery Compensation

The *checkBattery()* logic executes every 5000ms. Because activating the relays causes a physical voltage drop across the wiring harness, the software dynamically compensates:

- Reads the *BATTERY_PIN* via the median filter.

- Applies the *K_CALIBRATION* multiplier to the hardware voltage divider math.

- Checks *currentRelayState* (pulled atomically from Core 1).

- If State = 1 or 2 (One Load): Adds *+0.40V* to the calculation.

- If State = 3 (Both Loads): Adds *+0.58V* to the calculation.

- Calculates discrete quartiles (100%, 50%, 0%) to prevent fluctuating UX.

#### Command Payload Validation

In the MQTT *callback()* function, incoming payloads are copied into a *static char msgBuffer[16]* using *memcpy* to prevent buffer overflow attacks or memory leaks from Arduino Strings. The integer is validated using *if (temp_command >= 0 && temp_command <= 3)* before hardware is actuated.

### 4. The SCADA Blind Spot Fix

In addition to reporting current and voltage, the ESP32 publishes to a *.../relay_status* topic. Core 0 detects if the physical relays were overridden by the 5-minute fail-safe and passes this state to Core 1 via the Mailbox. Core 1 publishes this to SCADA, ensuring the Master PLC always knows the actual physical state of the relays regardless of what command was previously sent.