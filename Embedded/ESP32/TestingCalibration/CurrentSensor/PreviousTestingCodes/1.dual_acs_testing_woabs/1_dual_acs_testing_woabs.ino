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

// --- Current Monitoring Variables ---
// MODIFIED: Separate calibration for each sensor
// Sensor 1
const int SENSOR_1_ZERO_POINT = 3000; 
const float SENSOR_1_SENSITIVITY = 235.0; 

// Sensor 2
const int SENSOR_2_ZERO_POINT = 120; 
const float SENSOR_2_SENSITIVITY = 235.0; 

unsigned long lastCurrentCheck = 0;

// --- MODIFIED: Separate Thresholds for Status (in Amps) ---
// Sensor 1 Thresholds
const float SENSOR_1_THRESH_PARTIAL = 0.08; // Below this is FAIL
const float SENSOR_1_THRESH_OK = 0.30;      // Above this is OK

// Sensor 2 Thresholds
const float SENSOR_2_THRESH_PARTIAL = 0.05; // Below this is FAIL
const float SENSOR_2_THRESH_OK = 0.30;      // Above this is OK

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
  // Force a read to ensure we send current state
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
      digitalWrite(RELAY_1_PIN, LOW); digitalWrite(RELAY_2_PIN, LOW); break;
    case 1:
      digitalWrite(RELAY_1_PIN, HIGH); digitalWrite(RELAY_2_PIN, LOW); break;
    case 2:
      digitalWrite(RELAY_1_PIN, LOW); digitalWrite(RELAY_2_PIN, HIGH); break;
    case 3:
      digitalWrite(RELAY_1_PIN, HIGH); digitalWrite(RELAY_2_PIN, HIGH); break;
    default:
      digitalWrite(RELAY_1_PIN, LOW); digitalWrite(RELAY_2_PIN, LOW); break;
  }
}

// Helper function to read and calculate current status + return details by reference
// MODIFIED: Added specific threshold arguments
String determineCurrentStatus(int pin, int zeroPoint, float sensitivity, float threshOK, float threshPartial, float &outRaw, float &outAmps) {
  long sum = 0;
  for(int i=0; i<30; i++) {
    sum += analogRead(pin);
    delay(2);
  }
  float avgRaw = sum / 30.0;
  
  // Set the output raw value
  outRaw = avgRaw;

  // Detect disconnected sensor (Floating near 0)
  if (avgRaw < 100) {
    outAmps = 0.0;
    return "FAIL";
  }

  // MODIFIED: Removed abs() function.
  // Calculate raw difference from zero point.
  float currentAmps = (avgRaw - zeroPoint) / sensitivity;
  
  // If value is negative (reading below zero point), clamp it to 0.
  if (currentAmps < 0) {
    currentAmps = 0.0;
  }
  
  // Set the output amps value
  outAmps = currentAmps;

  // Determine Status based on specific thresholds passed in
  if (currentAmps >= threshOK) {
    return "OK";
  } else if (currentAmps > threshPartial) {
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

    // MODIFIED: Pass specific calibration constants AND thresholds for each sensor
    String status1 = determineCurrentStatus(CURRENT_SENSOR_PIN_1, SENSOR_1_ZERO_POINT, SENSOR_1_SENSITIVITY, SENSOR_1_THRESH_OK, SENSOR_1_THRESH_PARTIAL, raw1, amps1);
    String status2 = determineCurrentStatus(CURRENT_SENSOR_PIN_2, SENSOR_2_ZERO_POINT, SENSOR_2_SENSITIVITY, SENSOR_2_THRESH_OK, SENSOR_2_THRESH_PARTIAL, raw2, amps2);

    client.publish(current_topic_1, status1.c_str(), true);
    client.publish(current_topic_2, status2.c_str(), true);
    
    // --- DEBUG PRINTS to Serial Monitor ---
    Serial.println("--- Sensor Readings ---");
    Serial.printf("Power Monitor (Pin 34): Raw ADC = %d\n", analogValue);
    Serial.printf("Load 1 (Pin 35): Raw = %.2f, Amps = %.3f A, Status = %s\n", raw1, amps1, status1.c_str());
    Serial.printf("Load 2 (Pin 36): Raw = %.2f, Amps = %.3f A, Status = %s\n", raw2, amps2, status2.c_str());
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
  digitalWrite(RELAY_1_PIN, LOW);
  digitalWrite(RELAY_2_PIN, LOW);
  
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