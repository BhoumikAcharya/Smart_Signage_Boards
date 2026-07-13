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

// ================= CURRENT SENSOR CONFIGURATION =================
const float ADC_REF_VOLTAGE = 3.3; 
const int ADC_RESOLUTION = 4095;

// Status Thresholds (Amps)
const float THRESHOLD_LOW_CUTOFF = 0.100; // Below this is FAIL
const float THRESHOLD_OK_LIMIT   = 0.380; // Above this is OK, Between is PARTIAL

// ================= CLASS DEFINITION =================
class CurrentChannel {
  private:
    int sensorPin;
    int relayPin;
    int sensitivity;        // mV per Amp
    float zeroPointVoltage; // The voltage reading when current is 0
    float smoothedCurrent;  // History for smoothing
    float alpha;            // Smoothing factor
    String name;
    bool currentRelayState; // Tracks state for calibration return

  public:
    // Constructor
    CurrentChannel(String chName, int sPin, int rPin, int sens, float smoothFactor) {
      name = chName;
      sensorPin = sPin;
      relayPin = rPin;
      sensitivity = sens;
      alpha = smoothFactor;
      zeroPointVoltage = 2.5; // Default safe start
      smoothedCurrent = 0.0;
      currentRelayState = LOW;
    }

    void setup() {
      pinMode(sensorPin, INPUT);
      pinMode(relayPin, OUTPUT);
      // Ensure specific attenuation for ESP32 ADC
      analogSetPinAttenuation(sensorPin, ADC_11db);
      
      // Set initial relay state (Default OFF)
      digitalWrite(relayPin, LOW); 
      currentRelayState = LOW;
    }

    // Update relay based on MQTT control
    void updateRelay(bool desiredState) {
      currentRelayState = desiredState;
      digitalWrite(relayPin, desiredState ? HIGH : LOW);
    }

    // Auto Calibration Routine
    void calibrate(bool returnToState) {
      Serial.print("["); Serial.print(name); Serial.println("] Starting Calibration...");
      
      // 1. Force Relay HIGH as requested
      digitalWrite(relayPin, HIGH);
      // Serial.print("["); Serial.print(name); Serial.println("] Relay Forcibly set HIGH for Calibration.");
      
      // 2. Wait for voltage to settle
      delay(1000); 

      // 3. Sample Data
      long totalRaw = 0;
      int sampleCount = 200;

      for (int i = 0; i < sampleCount; i++) {
        totalRaw += analogRead(sensorPin);
        delay(1);
      }

      // 4. Calculate Zero Point
      float averageRaw = totalRaw / (float)sampleCount;
      zeroPointVoltage = (averageRaw / (float)ADC_RESOLUTION) * ADC_REF_VOLTAGE;

      // 5. Restore Relay to "Control Value"
      digitalWrite(relayPin, returnToState ? HIGH : LOW);

      Serial.print("["); Serial.print(name); Serial.print("] Zero Point: ");
      Serial.print(zeroPointVoltage, 3); Serial.println(" V");
      Serial.println("--------------------------------");
    }

    // Main reading logic
    void update() {
      // 1. Raw Sampling
      long totalADC = 0;
      int samples = 100;
      for (int i = 0; i < samples; i++) {
        totalADC += analogRead(sensorPin);
        delayMicroseconds(100); 
      }
      float averageADC = totalADC / (float)samples;
      float inputVoltage = (averageADC / (float)ADC_RESOLUTION) * ADC_REF_VOLTAGE;

      // 2. Calculate Current
      // Power P = (V_read - V_zero) / Sensitivity
      float rawCurrent = (inputVoltage - zeroPointVoltage) / ((float)sensitivity / 1000.0);

      // 3. Dead Zone (Noise Filter)
      if (abs(rawCurrent) < 0.05) { 
        rawCurrent = 0.00;
      }

      // 4. Smoothing
      smoothedCurrent = (rawCurrent * alpha) + (smoothedCurrent * (1.0 - alpha));
    }

    // Get the status string based on thresholds
    String getStatus() {
      float absCurrent = abs(smoothedCurrent);
      if (absCurrent <= THRESHOLD_LOW_CUTOFF) {
        return "FAIL"; 
      } else if (absCurrent > THRESHOLD_LOW_CUTOFF && absCurrent <= THRESHOLD_OK_LIMIT) {
        return "PARTIAL";
      } else {
        return "OK";
      }
    }

    // Getter for printing
    float getCurrent() {
      return smoothedCurrent;
    }
};

// ================= INSTANTIATION =================
// Channel 1: Name "CH1", Sensor Pin 35, Relay Pin 4, Sens 780, Alpha 0.1
CurrentChannel ch1("CH1", 35, 4, 780, 0.1);

// Channel 2: Name "CH2", Sensor Pin 36, Relay Pin 13, Sens 780, Alpha 0.1
CurrentChannel ch2("CH2", 36, 13, 780, 0.1);

// ================= OTHER HARDWARE =================
const int POWER_MONITOR_PIN = 34; // Input-only pin for Voltage

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
char load1_topic[50]; // New topic for Load 1
char load2_topic[50]; // New topic for Load 2
const char* scan_topic = "metro/signage/scan"; // Broadcast topic

WiFiClient ethClient;
PubSubClient client(ethClient);
long lastReconnectAttempt = 0;
static bool eth_connected = false;

// State Tracking for Reporting (Only publish on change)
String last_load1_status = "";
String last_load2_status = "";

// Power Monitoring Variables (Time-Based Debounce)
const int POWER_THRESHOLD = 2300;       // ~1.8V
const unsigned long DEBOUNCE_DELAY = 50; // ms
bool pwr_stableState = false;
bool pwr_lastReading = false;
unsigned long pwr_lastDebounceTime = 0;

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

// Helper to publish all current statuses (Used on Connect and on PING)
void publish_full_status() {
  // 1. Publish ONLINE status
  String onlineMsg = "ONLINE:" + ETH.localIP().toString();
  client.publish(status_topic, onlineMsg.c_str(), true);

  // 2. Publish Power Status
  int raw = analogRead(POWER_MONITOR_PIN);
  pwr_stableState = (raw > POWER_THRESHOLD);
  String pwrMsg = pwr_stableState ? "OK" : "FAIL";
  client.publish(power_topic, pwrMsg.c_str(), true);

  // 3. Publish Load Statuses
  client.publish(load1_topic, ch1.getStatus().c_str(), true);
  client.publish(load2_topic, ch2.getStatus().c_str(), true);
  
  Serial.println(">> Full Status Report Sent.");
}

void callback(char* topic, byte* payload, unsigned int length) {
  String message;
  for (int i = 0; i < length; i++) {
    message += (char)payload[i];
  }
  
  // --- Check for Broadcast PING ---
  if (String(topic) == scan_topic && message == "PING") {
    Serial.println("\n[PING] Received Broadcast Ping. Reporting...");
    publish_full_status();
    return;
  }

  // --- Relay Control Logic ---
  int control_value = message.toInt();
  Serial.printf("Received Relay Command: %d\n", control_value);

  // Using Class Methods to update Relays
  switch (control_value) {
    case 0:
      ch1.updateRelay(LOW); ch2.updateRelay(LOW); break;
    case 1:
      ch1.updateRelay(HIGH); ch2.updateRelay(LOW); break;
    case 2:
      ch1.updateRelay(LOW); ch2.updateRelay(HIGH); break;
    case 3:
      ch1.updateRelay(HIGH); ch2.updateRelay(HIGH); break;
    default:
      ch1.updateRelay(LOW); ch2.updateRelay(LOW); break;
  }
}

// Check Voltage at Pin 34
void checkVoltageMonitor() {
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
      Serial.printf(">> Power Changed: %s\n", payload.c_str());
    }
  }
}

// Check Current Sensors (Pins 35 & 36)
void checkCurrentSensors() {
  // Update Physics
  ch1.update();
  ch2.update();

  // Get Status Strings
  String s1 = ch1.getStatus();
  String s2 = ch2.getStatus();

  // Publish if changed
  if (s1 != last_load1_status) {
    client.publish(load1_topic, s1.c_str(), true);
    Serial.printf(">> Load 1 Changed: %s (%.2f A)\n", s1.c_str(), ch1.getCurrent());
    last_load1_status = s1;
  }

  if (s2 != last_load2_status) {
    client.publish(load2_topic, s2.c_str(), true);
    Serial.printf(">> Load 2 Changed: %s (%.2f A)\n", s2.c_str(), ch2.getCurrent());
    last_load2_status = s2;
  }
}

boolean mqtt_connect() {
  if (!eth_connected) return false;
  
  Serial.print("Attempting MQTT connection...");
  String clientId = "ESP32Eth-" + ETH.macAddress();

  if (client.connect(clientId.c_str(), status_topic, 1, true, "OFFLINE")) {
    Serial.println(" Connected!");
    client.subscribe(control_topic);
    client.subscribe(scan_topic); 
    publish_full_status();
  } else {
    Serial.printf("failed, rc=%d try 5s\n", client.state());
  }
  return client.connected();
}

void setup() {
  Serial.begin(115200);
  delay(100);

  Serial.printf("\n--- ESP32 Signage Controller (%d) ---\n", ASSIGNED_REGISTER);

  // Setup Hardware (Current Channels handle their own pins)
  ch1.setup();
  ch2.setup();
  
  pinMode(POWER_MONITOR_PIN, INPUT);
  analogSetAttenuation(ADC_11db); // Global setting 0-3.3V

  // --- STARTUP CALIBRATION ---
  // Relays will click HIGH then LOW to find Zero Point
  Serial.println("Performing Startup Calibration...");
  ch1.calibrate(LOW);
  ch2.calibrate(LOW);
  Serial.println("Calibration Complete.");

  // Setup Strings
  sprintf(control_topic, "metro/signage/register/%d/value", ASSIGNED_REGISTER);
  sprintf(status_topic, "metro/signage/register/%d/status", ASSIGNED_REGISTER);
  sprintf(power_topic, "metro/signage/register/%d/power", ASSIGNED_REGISTER);
  sprintf(load1_topic, "metro/signage/register/%d/load1", ASSIGNED_REGISTER);
  sprintf(load2_topic, "metro/signage/register/%d/load2", ASSIGNED_REGISTER);

  // Initialize Ethernet
  WiFi.onEvent(eth_event_handler);
  ETH.begin(ETH_PHY_TYPE, ETH_PHY_ADDR, ETH_PHY_MDC, ETH_PHY_MDIO, ETH_PHY_POWER, ETH_CLK_MODE);
  ETH.config(local_IP, gateway, subnet, primaryDNS, secondaryDNS);

  // Setup MQTT
  client.setServer(mqtt_server_ip, mqtt_port);
  client.setCallback(callback);
  client.setKeepAlive(4); 
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
    checkVoltageMonitor();
    checkCurrentSensors();
  }
}