#include <ETH.h>
#include <PubSubClient.h>
#include <WiFi.h>

// --- USER CONFIGURATION ----------------------------------------
const char* mqtt_server_ip = "192.168.1.10"; // Raspberry Pi IP
const int mqtt_port = 1883;

// Unique Register Assignment (CHANGE THIS FOR EACH UNIT)
const int ASSIGNED_REGISTER = 40008;

// Static IP Settings (CHANGE THIS FOR EACH UNIT)
IPAddress local_IP(192, 168, 1, 108); // Unique Static IP
IPAddress gateway(192, 168, 1, 1);    // Router/Gateway IP
IPAddress subnet(255, 255, 255, 0);   // Subnet Mask
IPAddress primaryDNS(8, 8, 8, 8);
IPAddress secondaryDNS(8, 8, 4, 4);

// ---------------------------------------------------------------

// GPIO Pins
const int RELAY_1_PIN = 4;
const int RELAY_2_PIN = 13;
const int POWER_MONITOR_PIN = 34; // Input-only pin for Voltage
const int CURRENT_SENSOR_PIN_1 = 35; // Input-only pin for ACS712 Channel 1
const int CURRENT_SENSOR_PIN_2 = 36; // Input-only pin for ACS712 Channel 2 (Sensor VP)

// Ethernet PHY Configuration (LAN8720)
#define ETH_PHY_ADDR    1
#define ETH_PHY_POWER  -1
#define ETH_PHY_MDC     23
#define ETH_PHY_MDIO    18
#define ETH_PHY_TYPE    ETH_PHY_LAN8720
#define ETH_CLK_MODE    ETH_CLOCK_GPIO17_OUT

// MQTT Topics
char control_topic[50];
char status_topic[50];
char power_topic[50];
char current_topic_1[50]; // Topic for Load 1
char current_topic_2[50]; // Topic for Load 2
const char* scan_topic = "metro/signage/scan"; // Broadcast topic

WiFiClient ethClient;
PubSubClient client(ethClient);
long lastReconnectAttempt = 0;
static bool eth_connected = false;

// Power Monitoring Variables (Time-Based Debounce)
const int POWER_THRESHOLD = 2300;       // ~1.8V
const unsigned long DEBOUNCE_DELAY = 50; // ms
bool pwr_stableState = false;
bool pwr_lastReading = false;
unsigned long pwr_lastDebounceTime = 0;

// --- Current Monitoring Variables (Physics-Based) ---
// ESP32 ADC Reference Voltage (Max readable voltage with 11dB attenuation)
const float ADC_REF_VOLTAGE = 3.3; 
const int ADC_RESOLUTION = 4095;

// ACS712 Config (Powered by 5V)
const float ACS_VCC = 5.03; 
const float ACS_SENSITIVITY = 0.185; // 185mV/A for 5A module

// Offsets (Calibration) - Ideally VCC/2 = 2.50V
// You can tweak these slightly if your sensor rests at 2.48V or 2.52V
float offset_voltage_1 = 2.43; 
float offset_voltage_2 = 2.50; 

unsigned long lastCurrentCheck = 0;

// Thresholds for Status (in Amps)
const float THRESH_PARTIAL = 0.05; // Below this is FAIL
const float THRESH_OK = 0.30;      // Above this is OK

void eth_event_handler(WiFiEvent_t event) {
  switch (event) {
    case ARDUINO_EVENT_ETH_START:
      Serial.println("ETH Started");
      ETH.setHostname("esp32-signage");
      break;
    case ARDUINO_EVENT_ETH_CONNECTED:
      Serial.println("ETH Connected");
      break;
    case ARDUINO_EVENT_ETH_GOT_IP:
      Serial.print("ETH MAC: ");
      Serial.print(ETH.macAddress());
      Serial.print(", IPv4: ");
      Serial.print(ETH.localIP());
      Serial.println("Mbps");
      eth_connected = true;
      break;
    case ARDUINO_EVENT_ETH_DISCONNECTED:
      Serial.println("ETH Disconnected");
      eth_connected = false;
      break;
    case ARDUINO_EVENT_ETH_STOP:
      Serial.println("ETH Stopped");
      eth_connected = false;
      break;
    default:
      break;
  }
}

// Helper to publish current status (Used on Connect and on PING)
void publish_current_status() {
  // 1. Publish ONLINE status with IP
  String onlineMsg = "ONLINE:" + ETH.localIP().toString();
  client.publish(status_topic, onlineMsg.c_str(), true);
  Serial.print("Reported Status: ");
  Serial.println(onlineMsg);

  // 2. Publish Power Status
  int raw = analogRead(POWER_MONITOR_PIN);
  pwr_stableState = (raw > POWER_THRESHOLD);
  String pwrMsg = pwr_stableState ? "OK" : "FAIL";
  client.publish(power_topic, pwrMsg.c_str(), true);
  Serial.print("Reported Power: ");
  Serial.println(pwrMsg);
}

void callback(char* topic, byte* payload, unsigned int length) {
  String message;
  for (int i = 0; i < length; i++) {
    message += (char)payload[i];
  }
  
  // --- Check for Broadcast PING ---
  if (String(topic) == scan_topic && message == "PING") {
    Serial.println("\n[PING] Received Broadcast Ping from Pi.");
    Serial.println("[PING] Re-publishing status...");
    publish_current_status();
    return; // Don't process as relay control
  }

  // --- Relay Control Logic ---
  int control_value = message.toInt();
  Serial.printf("Received Relay Command: %d\n", control_value);

  // Active-High Logic (HIGH=ON, LOW=OFF)
  switch (control_value) {
    case 0:
      digitalWrite(RELAY_1_PIN, HIGH); digitalWrite(RELAY_2_PIN, HIGH); break;
    case 1:
      digitalWrite(RELAY_1_PIN, LOW); digitalWrite(RELAY_2_PIN, HIGH); break;
    case 2:
      digitalWrite(RELAY_1_PIN, HIGH); digitalWrite(RELAY_2_PIN, LOW); break;
    case 3:
      digitalWrite(RELAY_1_PIN, LOW); digitalWrite(RELAY_2_PIN, LOW); break;
    default:
      digitalWrite(RELAY_1_PIN, HIGH); digitalWrite(RELAY_2_PIN, HIGH); break;
  }
}

// Helper function to read and calculate current status + return details by reference
String determineCurrentStatus(int pin, float offsetVoltage, float &outRaw, float &outAmps) {
  long sum = 0;
  for(int i=0; i<30; i++) {
    sum += analogRead(pin);
    delay(50);
  }
  float avgRaw = sum / 30.0;
  outRaw = avgRaw;

  // Detect disconnected sensor (Floating near 0)
  if (avgRaw < 100) {
    outAmps = 0.0;
    return "FAIL";
  }

  // --- MODIFIED: Physics-Based Calculation ---
  // 1. Convert Raw Counts to Voltage (at ESP32 pin)
  float pinVoltage = (avgRaw / ADC_RESOLUTION) * ADC_REF_VOLTAGE;
  
  // 2. Calculate Current: (PinVoltage - Offset) / Sensitivity
  // Note: We do NOT use abs() here initially, to detect negative swing.
  float currentAmps = (pinVoltage - offsetVoltage) / ACS_SENSITIVITY;
  
  // 3. Negative Handling: If voltage dropped below offset (negative current), force to 0.
  if (currentAmps < 0) {
    currentAmps = 0.0;
  }
  
  outAmps = currentAmps;

  // Debug Print for Calibration
  // Serial.printf("Pin: %d | Raw: %.0f | Volts: %.3fV | Offset: %.2fV | Amps: %.3fA\n", 
  //                pin, avgRaw, pinVoltage, offsetVoltage, currentAmps);

  // Determine Status
  if (currentAmps >= THRESH_OK) {
    return "OK";
  } else if (currentAmps > THRESH_PARTIAL) {
    return "PARTIAL";
  } else {
    return "FAIL";
  }
}

void checkSensors() {
  // --- 1. Power Voltage Monitoring (Debounce) ---
  int analogValue = analogRead(POWER_MONITOR_PIN);
  bool currentReading = (analogValue > POWER_THRESHOLD);

  if (currentReading != pwr_lastReading) {
    pwr_lastDebounceTime = millis();
  }
  pwr_lastReading = currentReading;

  if ((millis() - pwr_lastDebounceTime) > DEBOUNCE_DELAY) {
    if (currentReading != pwr_stableState) {
      pwr_stableState = currentReading;
      String payload = pwr_stableState ? "OK" : "FAIL";
      client.publish(power_topic, payload.c_str(), true);
      Serial.printf(">> Power Changed: %s (Raw Pin 34: %d)\n", payload.c_str(), analogValue);
    }
  }

  // --- 2. Current Monitoring (Two Channels) ---
  if (millis() - lastCurrentCheck > 2000) { 
    float raw1, amps1;
    float raw2, amps2;

    // Pass specific OFFSET voltage for each sensor
    String status1 = determineCurrentStatus(CURRENT_SENSOR_PIN_1, offset_voltage_1, raw1, amps1);
    String status2 = determineCurrentStatus(CURRENT_SENSOR_PIN_2, offset_voltage_2, raw2, amps2);

    client.publish(current_topic_1, status1.c_str(), true);
    client.publish(current_topic_2, status2.c_str(), true);
    
    // --- DEBUG PRINTS ---
    Serial.println("--- Sensor Readings ---");
    Serial.printf("Power Monitor (Pin 34): Raw ADC = %d\n", analogValue);
    Serial.printf("Load 1 (Pin 35): Raw=%.0f, Volts=%.2fV, Amps=%.3f A, Status=%s\n", 
                  raw1, (raw1/4095.0)*3.3, amps1, status1.c_str());
    Serial.printf("Load 2 (Pin 36): Raw=%.0f, Volts=%.2fV, Amps=%.3f A, Status=%s\n", 
                  raw2, (raw2/4095.0)*3.3, amps2, status2.c_str());
    Serial.println("-----------------------");

    lastCurrentCheck = millis();
  }
}

boolean mqtt_connect() {
  if (!eth_connected) return false;
  
  Serial.print("Attempting MQTT connection...");

  String clientId = "ESP32EthClient-";
  clientId += ETH.macAddress();

  if (client.connect(clientId.c_str(), status_topic, 1, true, "OFFLINE")) {
    Serial.println(">>> Connected! <<<");
    
    client.subscribe(control_topic);
    client.subscribe(scan_topic); // Listen for the PING
    
    publish_current_status();

  } else {
    Serial.printf("failed, rc=%d try again in 5s\n", client.state());
  }
  return client.connected();
}

void setup() {
  Serial.begin(115200);
  delay(100);

  // Setup Hardware
  pinMode(RELAY_1_PIN, OUTPUT);
  pinMode(RELAY_2_PIN, OUTPUT);
  // Active-High Initialization: LOW = OFF
  digitalWrite(RELAY_1_PIN, HIGH);
  digitalWrite(RELAY_2_PIN, HIGH);
  
  pinMode(POWER_MONITOR_PIN, INPUT);
  pinMode(CURRENT_SENSOR_PIN_1, INPUT);
  pinMode(CURRENT_SENSOR_PIN_2, INPUT);

  // CRITICAL: Set ADC to read up to ~3.3V
  analogSetAttenuation(ADC_11db);

  // Setup Strings
  sprintf(control_topic, "metro/signage/register/%d/value", ASSIGNED_REGISTER);
  sprintf(status_topic, "metro/signage/register/%d/status", ASSIGNED_REGISTER);
  sprintf(power_topic, "metro/signage/register/%d/power", ASSIGNED_REGISTER);
  sprintf(current_topic_1, "metro/signage/register/%d/current1", ASSIGNED_REGISTER);
  sprintf(current_topic_2, "metro/signage/register/%d/current2", ASSIGNED_REGISTER);

  Serial.printf("\n--- ESP32 Ethernet Signage Controller (%d) ---\n", ASSIGNED_REGISTER);

  // Initialize Ethernet
  WiFi.onEvent(eth_event_handler);
  ETH.begin(ETH_PHY_TYPE, ETH_PHY_ADDR, ETH_PHY_MDC, ETH_PHY_MDIO, ETH_PHY_POWER, ETH_CLK_MODE);
  
  // Configure Static IP (After begin)
  if (!ETH.config(local_IP, gateway, subnet, primaryDNS, secondaryDNS)) {
    Serial.println("ETH Config Failed");
  }

  // Setup MQTT
  client.setServer(mqtt_server_ip, mqtt_port);
  client.setCallback(callback);
  client.setKeepAlive(4); // Short keep-alive for faster offline detection
  lastReconnectAttempt = 0;
}

void loop() {
  if (!eth_connected) {
      lastReconnectAttempt = 0;
      delay(1000);
      return;
  }
  
  if (!client.connected()) {
    long now = millis();
    if (now - lastReconnectAttempt > 5000) {
      lastReconnectAttempt = now;
      if (mqtt_connect()) {
        lastReconnectAttempt = 0;
      }
    }
  } else {
    client.loop();
    checkSensors();
  }
}