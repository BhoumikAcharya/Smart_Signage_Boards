#include <ETH.h>
#include <PubSubClient.h>
#include <WiFi.h>
#include <ArduinoJson.h>
#include <IPAddress.h>
#include <Preferences.h>
#include <WebServer.h>          // Built‑in HTTP server for OTA

// ===================== USER CONFIGURATION =====================
const char* mqtt_server_ip = "192.168.1.10";   // MQTT broker IP
const int mqtt_port = 1883;
int ASSIGNED_REGISTER = 40009;                 // will be recalculated after loadConfig()

// Static IP Settings (defaults, can be changed via JSON)
IPAddress local_IP(192, 168, 1, 109);
IPAddress gateway(192, 168, 1, 1);
IPAddress subnet(255, 255, 255, 0);
IPAddress primaryDNS(8, 8, 8, 8);
IPAddress secondaryDNS(8, 8, 4, 4);

// ============== CURRENT SENSOR CALIBRATION (defaults) =============
const float ADC_REF_VOLT = 3.3;
float SENSITIVITY_1 = 0.105;
float SENSITIVITY_2 = 0.105;
const float ZERO_VOLT_1  = 2.40;
const float ZERO_VOLT_2  = 2.4;
const float ALPHA        = 0.15;

// Variables managed via JSON (Panel Settings)
int Route = 0;
int panel_location = 0;
String panel_discription = "";
float battery_calibration = 0.0;
Preferences prefs;          // Flash storage

// Periodic status display
unsigned long lastPrintTime = 0;
const unsigned long printInterval = 5000;

// Current thresholds
const float CURRENT_THRESHOLD_1 = 0.100;
const float CURRENT_THRESHOLD_2 = 0.120;

// GPIO Pins
const int RELAY_1_PIN = 4;
const int RELAY_2_PIN = 13;
const int POWER_MONITOR_PIN = 34;
const int CURRENT_PIN_1 = 35;
const int CURRENT_PIN_2 = 36;

// Ethernet PHY Configuration (LAN8720)
#define ETH_PHY_ADDR  1
#define ETH_PHY_POWER -1
#define ETH_PHY_MDC   23
#define ETH_PHY_MDIO  18
#define ETH_PHY_TYPE  ETH_PHY_LAN8720
#define ETH_CLK_MODE  ETH_CLOCK_GPIO17_OUT

// MQTT Topics
char control_topic[50];
char status_topic[50];
char power_topic[50];
char current1_topic[50];
char current2_topic[50];
const char* scan_topic = "metro/signage/scan";

WiFiClient ethClient;
PubSubClient client(ethClient);
WebServer server(80);          // OTA HTTP server on port 80
long lastReconnectAttempt = 0;
static bool eth_connected = false;

// Power Monitoring Variables
const int POWER_THRESHOLD = 1800;
const unsigned long DEBOUNCE_DELAY = 50;
bool pwr_stableState = false;
bool pwr_lastReading = false;
unsigned long pwr_lastDebounceTime = 0;

// Current Monitoring Variables
float filteredCurrent1 = 0.0;
float filteredCurrent2 = 0.0;
const char* lastCurrentState1 = "---";
const char* lastCurrentState2 = "---";
unsigned long lastCurrentReadTime = 0;
const long CURRENT_READ_INTERVAL = 500;

// ========== Helper: Update IP from string ==========
void setIPAddress(IPAddress& ip, const char* ipStr) {
  byte parts[4] = {0,0,0,0};
  int i = 0;
  char buffer[16];
  strncpy(buffer, ipStr, 15);
  buffer[15] = '\0';
  char* token = strtok(buffer, ".");
  while (token != NULL && i < 4) {
    parts[i++] = atoi(token);
    token = strtok(NULL, ".");
  }
  if (i == 4) ip = IPAddress(parts[0], parts[1], parts[2], parts[3]);
}

// ========== Load saved configuration from Preferences ==========
void loadConfig() {
  prefs.begin("my-config", true);
  Route = prefs.getInt("Route", 0);
  panel_location = prefs.getInt("Panel", 9);
  panel_discription = prefs.getString("Desc", "");
  battery_calibration = prefs.getFloat("BattCal", 0.0f);
  SENSITIVITY_1 = prefs.getFloat("Sens1", 0.105f);
  SENSITIVITY_2 = prefs.getFloat("Sens2", 0.105f);
  String ipStr = prefs.getString("IP", "192.168.1.109");
  setIPAddress(local_IP, ipStr.c_str());
  prefs.end();
  Serial.println("Configuration loaded from flash.");
}

// ========== Save current configuration to Preferences ==========
void saveConfig() {
  prefs.begin("my-config", false);
  prefs.putInt("Route", Route);
  prefs.putInt("Panel", panel_location);
  prefs.putString("Desc", panel_discription);
  prefs.putFloat("BattCal", battery_calibration);
  prefs.putFloat("Sens1", SENSITIVITY_1);
  prefs.putFloat("Sens2", SENSITIVITY_2);
  prefs.putString("IP", local_IP.toString());
  prefs.end();
  Serial.println("Configuration saved to flash.");
}

// ========== Process JSON payload (used by both serial and HTTP) ==========
// Returns true if a restart was requested
bool processJsonPayload(const String& jsonLine) {
  StaticJsonDocument<512> doc;
  DeserializationError err = deserializeJson(doc, jsonLine);
  if (err) {
    Serial.print("JSON parse error: ");
    Serial.println(err.c_str());
    return false;
  }

  bool configChanged = false;

  if (doc.containsKey("Route"))              { Route = doc["Route"]; configChanged = true; }
  if (doc.containsKey("PanelLocation"))      { panel_location = doc["PanelLocation"]; configChanged = true; }
  if (doc.containsKey("Description"))        { panel_discription = doc["Description"].as<String>(); configChanged = true; }
  if (doc.containsKey("IPAddress"))          { setIPAddress(local_IP, doc["IPAddress"]); configChanged = true; }
  if (doc.containsKey("ACS_Sensitivity1"))   { SENSITIVITY_1 = doc["ACS_Sensitivity1"]; configChanged = true; }
  if (doc.containsKey("ACS_Sensitivity2"))   { SENSITIVITY_2 = doc["ACS_Sensitivity2"]; configChanged = true; }
  if (doc.containsKey("Battery_Calibration")){ battery_calibration = doc["Battery_Calibration"]; configChanged = true; }

  if (configChanged) {
    saveConfig();
    // Update register based on panel location
    ASSIGNED_REGISTER = 40000 + panel_location;
    // Rebuild MQTT topics (they depend on ASSIGNED_REGISTER)
    sprintf(control_topic, "metro/signage/register/%d/value", ASSIGNED_REGISTER);
    sprintf(status_topic, "metro/signage/register/%d/status", ASSIGNED_REGISTER);
    sprintf(power_topic, "metro/signage/register/%d/power", ASSIGNED_REGISTER);
    sprintf(current1_topic, "metro/signage/register/%d/current1", ASSIGNED_REGISTER);
    sprintf(current2_topic, "metro/signage/register/%d/current2", ASSIGNED_REGISTER);
    // Reconnect MQTT with new topics
    if (client.connected()) client.disconnect();
  }

  // Return whether restart was requested (but do NOT restart here)
  return (doc.containsKey("Restart") && doc["Restart"] == true);
}

// ========== HTTP handler for /config ==========
void handleConfigPost() {
  if (server.method() != HTTP_POST) {
    server.send(405, "text/plain", "Method Not Allowed");
    return;
  }
  if (!server.hasArg("plain")) {
    server.send(400, "text/plain", "Body missing");
    return;
  }
  String body = server.arg("plain");
  bool needRestart = processJsonPayload(body);
  server.send(200, "text/plain", "OK");   // Always respond BEFORE restart
  Serial.println("OTA config received and applied.");

  if (needRestart) {
    Serial.println("Restarting in 1 second...");
    delay(1000);
    ESP.restart();
  }
}

// ========== Ethernet event handler ==========
void eth_event_handler(arduino_event_id_t event) {
  switch (event) {
    case ARDUINO_EVENT_ETH_START:    Serial.println("ETH Started"); break;
    case ARDUINO_EVENT_ETH_CONNECTED: Serial.println("ETH Connected"); break;
    case ARDUINO_EVENT_ETH_GOT_IP:
      Serial.print("ETH MAC: "); Serial.print(ETH.macAddress());
      Serial.print(", IPv4: "); Serial.println(ETH.localIP());
      eth_connected = true;
      break;
    case ARDUINO_EVENT_ETH_DISCONNECTED: Serial.println("ETH Disconnected"); eth_connected = false; break;
    case ARDUINO_EVENT_ETH_STOP: Serial.println("ETH Stopped"); eth_connected = false; break;
    default: break;
  }
}

// ========== MQTT helpers ==========
void publish_all_status() {
  char onlineMsg[30];
  snprintf(onlineMsg, sizeof(onlineMsg), "ONLINE:%s", ETH.localIP().toString().c_str());
  client.publish(status_topic, onlineMsg, true);
  int raw = analogRead(POWER_MONITOR_PIN);
  pwr_stableState = (raw > POWER_THRESHOLD);
  client.publish(power_topic, pwr_stableState ? "OK" : "FAIL", true);
  client.publish(current1_topic, lastCurrentState1, true);
  client.publish(current2_topic, lastCurrentState2, true);
  Serial.println(">>> Reported Full Status");
}

void callback(char* topic, byte* payload, unsigned int length) {
  char msgBuffer[16];
  unsigned int copyLength = (length < sizeof(msgBuffer) - 1) ? length : (sizeof(msgBuffer) - 1);
  memcpy(msgBuffer, payload, copyLength);
  msgBuffer[copyLength] = '\0';

  if (strcmp(topic, scan_topic) == 0 && strcmp(msgBuffer, "PING") == 0) {
    publish_all_status();
    return;
  }

  int control_value = atoi(msgBuffer);
  switch (control_value) {
    case 0: digitalWrite(RELAY_1_PIN, HIGH); digitalWrite(RELAY_2_PIN, HIGH); break;
    case 1: digitalWrite(RELAY_1_PIN, LOW);  digitalWrite(RELAY_2_PIN, HIGH); break;
    case 2: digitalWrite(RELAY_1_PIN, HIGH); digitalWrite(RELAY_2_PIN, LOW);  break;
    case 3: digitalWrite(RELAY_1_PIN, LOW);  digitalWrite(RELAY_2_PIN, LOW);   break;
    default: digitalWrite(RELAY_1_PIN, HIGH); digitalWrite(RELAY_2_PIN, HIGH); break;
  }
}

// ========== Sensor Monitoring ==========
void checkCurrentSensors() {
  if (millis() - lastCurrentReadTime < CURRENT_READ_INTERVAL) return;
  lastCurrentReadTime = millis();

  long totalADC1 = 0;
  for (int i = 0; i < 150; i++) totalADC1 += analogRead(CURRENT_PIN_1);
  float avgADC1 = totalADC1 / 150.0;
  float voltage1 = (avgADC1 / 4095.0) * ADC_REF_VOLT;
  float rawCurrent1 = (voltage1 - ZERO_VOLT_1) / SENSITIVITY_1;
  if (abs(rawCurrent1) < 0.06) rawCurrent1 = 0;
  filteredCurrent1 = abs((rawCurrent1 * ALPHA) + (filteredCurrent1 * (1 - ALPHA)));
  const char* currentState1 = (filteredCurrent1 > CURRENT_THRESHOLD_1) ? "OK" : "FAIL";
  if (strcmp(currentState1, lastCurrentState1) != 0) {
    lastCurrentState1 = currentState1;
    client.publish(current1_topic, currentState1, true);
  }

  long totalADC2 = 0;
  for (int i = 0; i < 150; i++) totalADC2 += analogRead(CURRENT_PIN_2);
  float avgADC2 = totalADC2 / 150.0;
  float voltage2 = (avgADC2 / 4095.0) * ADC_REF_VOLT;
  float rawCurrent2 = (voltage2 - ZERO_VOLT_2) / SENSITIVITY_2;
  if (abs(rawCurrent2) < 0.06) rawCurrent2 = 0;
  filteredCurrent2 = abs((rawCurrent2 * ALPHA) + (filteredCurrent2 * (1 - ALPHA)));
  const char* currentState2 = (filteredCurrent2 > CURRENT_THRESHOLD_2) ? "OK" : "FAIL";
  if (strcmp(currentState2, lastCurrentState2) != 0) {
    lastCurrentState2 = currentState2;
    client.publish(current2_topic, currentState2, true);
  }
}

void checkPowerMonitor() {
  int analogValue = analogRead(POWER_MONITOR_PIN);
  bool currentReading = (analogValue > POWER_THRESHOLD);
  if (currentReading != pwr_lastReading) pwr_lastDebounceTime = millis();
  pwr_lastReading = currentReading;
  if ((millis() - pwr_lastDebounceTime) > DEBOUNCE_DELAY) {
    if (currentReading != pwr_stableState) {
      pwr_stableState = currentReading;
      client.publish(power_topic, pwr_stableState ? "OK" : "FAIL", true);
      Serial.printf(">> Power Changed: %s\n", pwr_stableState ? "OK" : "FAIL");
    }
  }
}

boolean mqtt_connect() {
  if (!eth_connected) return false;
  Serial.print("Attempting MQTT connection...");
  char clientId[30];
  snprintf(clientId, sizeof(clientId), "ESP32EthClient-%s", ETH.macAddress().c_str());
  if (client.connect(clientId, status_topic, 1, true, "OFFLINE")) {
    Serial.println("Connected!");
    client.subscribe(control_topic);
    client.subscribe(scan_topic);
    publish_all_status();
  } else {
    Serial.printf("failed, rc=%d try again in 5s\n", client.state());
  }
  return client.connected();
}

void clearPreferences() {
  prefs.begin("my-config", false);
  prefs.clear();               // remove all keys in the namespace
  prefs.end();
  Serial.println("Preferences cleared. Restarting...");
  delay(500);
  ESP.restart();
}

// ========== Setup ==========
void setup() {
  Serial.begin(115200);
  delay(500);

  loadConfig();
  // Ensure register reflects the loaded panel location
  ASSIGNED_REGISTER = 40000 + panel_location;
  Serial.println("ESP32 ready.");

  pinMode(RELAY_1_PIN, OUTPUT);
  pinMode(RELAY_2_PIN, OUTPUT);
  digitalWrite(RELAY_1_PIN, HIGH);
  digitalWrite(RELAY_2_PIN, HIGH);
  pinMode(POWER_MONITOR_PIN, INPUT);
  pinMode(CURRENT_PIN_1, INPUT);
  pinMode(CURRENT_PIN_2, INPUT);
  analogReadResolution(12);
  analogSetAttenuation(ADC_11db);

  // Build MQTT topics based on register
  sprintf(control_topic, "metro/signage/register/%d/value", ASSIGNED_REGISTER);
  sprintf(status_topic, "metro/signage/register/%d/status", ASSIGNED_REGISTER);
  sprintf(power_topic, "metro/signage/register/%d/power", ASSIGNED_REGISTER);
  sprintf(current1_topic, "metro/signage/register/%d/current1", ASSIGNED_REGISTER);
  sprintf(current2_topic, "metro/signage/register/%d/current2", ASSIGNED_REGISTER);

  Serial.printf("\n--- ESP32 Ethernet Signage Controller (%d) ---\n", ASSIGNED_REGISTER);

  WiFi.onEvent(eth_event_handler);
  ETH.begin(ETH_PHY_TYPE, ETH_PHY_ADDR, ETH_PHY_MDC, ETH_PHY_MDIO, ETH_PHY_POWER, ETH_CLK_MODE);
  ETH.config(local_IP, gateway, subnet, primaryDNS, secondaryDNS);

  // Start HTTP server for OTA
  server.on("/config", HTTP_POST, handleConfigPost);
  server.begin();
  Serial.println("HTTP server started on port 80");

  client.setServer(mqtt_server_ip, mqtt_port);
  client.setCallback(callback);
  client.setKeepAlive(4);
}

// ========== Loop ==========
void loop() {
  if (!eth_connected) {
    lastReconnectAttempt = 0;
    delay(1000);
    return;
  }

  // Serve HTTP requests (OTA)
  server.handleClient();

  // MQTT reconnect loop
  if (!client.connected()) {
    long now = millis();
    if (now - lastReconnectAttempt > 5000) {
      lastReconnectAttempt = now;
      if (mqtt_connect()) lastReconnectAttempt = 0;
    }
  } else {
    client.loop();
    checkPowerMonitor();
    checkCurrentSensors();
  }

  // Serial JSON input (USB configuration)
  if (Serial.available()) {
    String jsonLine = Serial.readStringUntil('\n');
    jsonLine.trim();

    if (jsonLine == "RESET_CONFIG") {
        clearPreferences();
      }

    if (jsonLine.length() > 0) {
      bool needRestart = processJsonPayload(jsonLine);
      if (needRestart) {
        Serial.println("Restarting in 1 second...");
        delay(1000);
        ESP.restart();
      }
    }
  }

  // Periodic status print
  if (millis() - lastPrintTime >= printInterval) {
    lastPrintTime = millis();
    Serial.println("===== Current Configuration =====");
    Serial.print("Route: "); Serial.println(Route);
    Serial.print("Panel Location: "); Serial.println(panel_location);
    Serial.print("Description: "); Serial.println(panel_discription);
    Serial.print("IP Address: "); Serial.println(local_IP);
    Serial.print("Battery Calibration: "); Serial.println(battery_calibration, 3);
    Serial.print("Sensitivity 1: "); Serial.println(SENSITIVITY_1, 3);
    Serial.print("Sensitivity 2: "); Serial.println(SENSITIVITY_2, 3);
    Serial.println("=================================");
  }
}

