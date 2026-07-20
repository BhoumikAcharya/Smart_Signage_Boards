#include <ETH.h>
#include <PubSubClient.h>
#include <WiFi.h>
#include <esp_task_wdt.h> // Hardware Watchdog Timer
#include <freertos/FreeRTOS.h>
#include <freertos/task.h>
#include <freertos/queue.h> // True Core Decoupling

// --- USER CONFIGURATION ----------------------------------------
const char* mqtt_server_ip = "192.168.1.10"; // Raspberry Pi IP
const int mqtt_port = 1883;

// Unique Register Assignment (CHANGE THIS FOR EACH UNIT)
const int ASSIGNED_REGISTER = 40001;

// Static IP Settings (CHANGE THIS FOR EACH UNIT)
IPAddress local_IP(192, 168, 1, 101); 
IPAddress gateway(192, 168, 1, 1);    
IPAddress subnet(255, 255, 255, 0);   
IPAddress primaryDNS(8, 8, 8, 8);
IPAddress secondaryDNS(8, 8, 4, 4);

// --- CURRENT SENSOR CALIBRATION ---
const float ADC_REF_VOLT = 3.3;
const float SENSITIVITY_1 = 0.192; 
const float SENSITIVITY_2 = 0.192; 
const float ZERO_VOLT_1  = 2.35; 
const float ZERO_VOLT_2  = 2.35; 
const float ALPHA        = 0.15;  

const float CURRENT_THRESHOLD_1 = 0.150;
const float CURRENT_THRESHOLD_2 = 0.150;

// --- BATTERY CALIBRATION ---
const float R1 = 4200.0;  // 4.2k Ohms
const float R2 = 1000.0;  // 1k Ohms
const float ADC_RESOLUTION = 4095.0; 
const float K_CALIBRATION = 1.057; // Calculated from physical measurements

// GPIO Pins
const int RELAY_1_PIN = 4;
const int RELAY_2_PIN = 13;
const int POWER_MONITOR_PIN = 34; 
const int CURRENT_PIN_1 = 35;     
const int CURRENT_PIN_2 = 36;     
const int BATTERY_PIN = 32;       

// Ethernet PHY Configuration
#define ETH_PHY_ADDR  1
#define ETH_PHY_POWER -1
#define ETH_PHY_MDC   23
#define ETH_PHY_MDIO  18
#define ETH_PHY_TYPE  ETH_PHY_LAN8720
#define ETH_CLK_MODE  ETH_CLOCK_GPIO17_OUT

// Watchdog & Fail-Safe Configuration
#define WDT_TIMEOUT_SECONDS 15            
const unsigned long FAIL_SAFE_TIMEOUT = 300000; // 5 Minutes (in milliseconds)

// MQTT Topics
char control_topic[50];
char status_topic[50];
char power_topic[50];
char current1_topic[50];
char current2_topic[50];
char batt_pct_topic[50];          
char relay_topic[50]; // SCADA State Sync Topic
const char* scan_topic = "metro/signage/scan"; 

WiFiClient ethClient;
PubSubClient client(ethClient);

// --- ENTERPRISE DATA SERIALIZATION ---
enum SensorState { SENS_UNKNOWN, SENS_OK, SENS_FAIL };

struct NodeStateMsg {
    bool power_ok;
    SensorState current1;
    SensorState current2;
    int batt_pct;
    int relay_state;
};

// True Decoupling: A Mailbox Queue between Core 0 and Core 1
QueueHandle_t sensorQueue;
TaskHandle_t SensorTaskHandle;

// The one single shared variable. Core 1 updates it on command, Core 0 reads it for Battery Math.
volatile int currentRelayState = 0; 

// Core 1 Network Variables
NodeStateMsg networkState = {true, SENS_UNKNOWN, SENS_UNKNOWN, -1, -1};
unsigned long lastReconnectAttempt = 0;
static bool eth_connected = false;
unsigned long lastCommsTime = 0; 
bool inFailSafeMode = false;

// --- EMI MEDIAN FILTER ALGORITHM ---
#define MAX_ADC_SAMPLES 51

int getMedianADC(int pin, int numSamples) {
  // Prevent buffer overflow and eliminate non-standard VLA
  if (numSamples > MAX_ADC_SAMPLES) {
    numSamples = MAX_ADC_SAMPLES;
  }
  
  int samples[MAX_ADC_SAMPLES]; 
  
  for (int i = 0; i < numSamples; i++) {
    samples[i] = analogRead(pin);
  }
  
  for (int i = 1; i < numSamples; i++) {
    int key = samples[i];
    int j = i - 1;
    while (j >= 0 && samples[j] > key) {
      samples[j + 1] = samples[j];
      j = j - 1;
    }
    samples[j + 1] = key;
  }
  return samples[numSamples / 2]; 
}

void eth_event_handler(arduino_event_id_t event) {
  switch (event) {
    case ARDUINO_EVENT_ETH_GOT_IP:
      Serial.print("ETH MAC: "); Serial.print(ETH.macAddress());
      Serial.print(", IPv4: "); Serial.println(ETH.localIP());
      eth_connected = true;
      break;
    case ARDUINO_EVENT_ETH_DISCONNECTED:
    case ARDUINO_EVENT_ETH_STOP:
      eth_connected = false;
      break;
    default:
      break;
  }
}

// --- CORE 1: PUBLISH FORMATTER ---
void publish_state_msg(NodeStateMsg msg) {
  char onlineMsg[30];
  snprintf(onlineMsg, sizeof(onlineMsg), "ONLINE:%s", ETH.localIP().toString().c_str());
  client.publish(status_topic, onlineMsg, true);
  
  client.publish(power_topic, msg.power_ok ? "OK" : "FAIL", true);

  if (msg.current1 != SENS_UNKNOWN)
    client.publish(current1_topic, msg.current1 == SENS_OK ? "OK" : "FAIL", true);
  if (msg.current2 != SENS_UNKNOWN)
    client.publish(current2_topic, msg.current2 == SENS_OK ? "OK" : "FAIL", true);

  if (msg.batt_pct != -1) {
    char bStr[8];
    snprintf(bStr, sizeof(bStr), "%d", msg.batt_pct);
    client.publish(batt_pct_topic, bStr, true);
  }

  // Eradicating the SCADA blind spot
  if (msg.relay_state != -1) {
    char rStr[8];
    snprintf(rStr, sizeof(rStr), "%d", msg.relay_state);
    client.publish(relay_topic, rStr, true);
  }
}

void callback(char* topic, byte* payload, unsigned int length) {
  lastCommsTime = millis();
  
  if (inFailSafeMode) {
    Serial.println(">> [RECOVERY] Valid Command Received. Exiting Fail-Safe Mode.");
    inFailSafeMode = false;
  }

  char msgBuffer[16]; 
  unsigned int copyLength = (length < sizeof(msgBuffer) - 1) ? length : (sizeof(msgBuffer) - 1);
  memcpy(msgBuffer, payload, copyLength);
  msgBuffer[copyLength] = '\0'; 

  if (strcmp(topic, scan_topic) == 0 && strcmp(msgBuffer, "PING") == 0) {
    publish_state_msg(networkState); // Reply to PING with last known mailbox state
    return;
  }

  int temp_command = atoi(msgBuffer); 
  if (temp_command >= 0 && temp_command <= 3) {
    currentRelayState = temp_command; 
    Serial.printf("Received Valid Relay Command: %d\n", currentRelayState);

    switch (currentRelayState) {
      case 0: digitalWrite(RELAY_1_PIN, HIGH); digitalWrite(RELAY_2_PIN, HIGH); break;
      case 1: digitalWrite(RELAY_1_PIN, LOW); digitalWrite(RELAY_2_PIN, HIGH); break;
      case 2: digitalWrite(RELAY_1_PIN, HIGH); digitalWrite(RELAY_2_PIN, LOW); break;
      case 3: digitalWrite(RELAY_1_PIN, LOW); digitalWrite(RELAY_2_PIN, LOW); break;
    }
  } else {
    Serial.println("[ERROR] Malformed Payload Received.");
  }
}

void checkFailSafe() {
  if ((millis() - lastCommsTime) > FAIL_SAFE_TIMEOUT) {
    if (!inFailSafeMode) {
      inFailSafeMode = true;
      Serial.println("\n>> [EMERGENCY] COMMS LOST FOR 5 MINS! ENTERING FAIL-SAFE MODE!");
      
      digitalWrite(RELAY_1_PIN, LOW);
      digitalWrite(RELAY_2_PIN, LOW);
      currentRelayState = 3; // Core 0 will automatically catch this and inform SCADA later!
    }
  }
}

boolean mqtt_connect() {
  if (!eth_connected) return false;
  
  char clientId[30];
  snprintf(clientId, sizeof(clientId), "ESP32EthClient-%s", ETH.macAddress().c_str());

  if (client.connect(clientId, status_topic, 1, true, "OFFLINE")) {
    Serial.println("MQTT Connected!");
    client.subscribe(control_topic, 1);
    client.subscribe(scan_topic, 0);
    
    // Publish initial baseline immediately
    publish_state_msg(networkState);
    lastCommsTime = millis(); 
  } 
  return client.connected();
}

// --- CORE 0 HARDWARE ENGINE ---
// No networking. No Strings. No Blocking. Just raw EMI filtering.
void SensorTask(void * parameter) {
  esp_task_wdt_add(NULL); 

  NodeStateMsg currentState = {true, SENS_UNKNOWN, SENS_UNKNOWN, -1, -1};
  bool firstRun = true;

  // Local Debounce/Interval tracking isolated to Core 0
  const int POWER_THRESHOLD = 1800;  
  const unsigned long DEBOUNCE_DELAY = 50; 
  bool pwr_lastReading = false;
  unsigned long pwr_lastDebounceTime = 0;

  float filteredCurrent1 = 0.0;
  float filteredCurrent2 = 0.0;
  unsigned long lastCurrentReadTime = 0;
  const unsigned long CURRENT_READ_INTERVAL = 500; 

  unsigned long lastBattReadTime = 0;
  const unsigned long BATT_READ_INTERVAL = 5000; 

  for(;;) {
    esp_task_wdt_reset(); 
    bool changed = firstRun;
    firstRun = false;

    // 1. POWER MONITOR
    int pwrAdc = getMedianADC(POWER_MONITOR_PIN, 11);
    bool pwrRead = (pwrAdc > POWER_THRESHOLD);
    if (pwrRead != pwr_lastReading) pwr_lastDebounceTime = millis();
    pwr_lastReading = pwrRead;

    if ((millis() - pwr_lastDebounceTime) > DEBOUNCE_DELAY) {
      if (currentState.power_ok != pwrRead) {
        currentState.power_ok = pwrRead;
        changed = true;
      }
    }

    // 2. CURRENT SENSORS
    if (millis() - lastCurrentReadTime >= CURRENT_READ_INTERVAL || lastCurrentReadTime == 0) {
      lastCurrentReadTime = millis();
      
      int medianADC1 = getMedianADC(CURRENT_PIN_1, 51);
      float voltage1 = (medianADC1 / 4095.0) * ADC_REF_VOLT;
      float rawCurrent1 = (voltage1 - ZERO_VOLT_1) / SENSITIVITY_1;
      if (abs(rawCurrent1) < 0.06) rawCurrent1 = 0; 
      filteredCurrent1 = abs((rawCurrent1 * ALPHA) + (filteredCurrent1 * (1 - ALPHA)));
      SensorState c1State = (filteredCurrent1 > CURRENT_THRESHOLD_1) ? SENS_OK : SENS_FAIL;
      if (currentState.current1 != c1State) { currentState.current1 = c1State; changed = true; }

      int medianADC2 = getMedianADC(CURRENT_PIN_2, 51);
      float voltage2 = (medianADC2 / 4095.0) * ADC_REF_VOLT;
      float rawCurrent2 = (voltage2 - ZERO_VOLT_2) / SENSITIVITY_2;
      if (abs(rawCurrent2) < 0.06) rawCurrent2 = 0; 
      filteredCurrent2 = abs((rawCurrent2 * ALPHA) + (filteredCurrent2 * (1 - ALPHA)));
      SensorState c2State = (filteredCurrent2 > CURRENT_THRESHOLD_2) ? SENS_OK : SENS_FAIL;
      if (currentState.current2 != c2State) { currentState.current2 = c2State; changed = true; }
    }

    // 3. BATTERY COMPENSATION
    if (millis() - lastBattReadTime >= BATT_READ_INTERVAL || lastBattReadTime == 0) {
      lastBattReadTime = millis();
      
      int medianADC = getMedianADC(BATTERY_PIN, 51);
      float pinVoltage = (medianADC / ADC_RESOLUTION) * ADC_REF_VOLT; 
      float battVoltage = pinVoltage * ((R1 + R2) / R2) * K_CALIBRATION;

      // Pull currentRelayState atomically from Core 1
      int activeRelays = currentRelayState; 
      if (activeRelays == 1 || activeRelays == 2) battVoltage += 0.40; 
      else if (activeRelays == 3) battVoltage += 0.58; 

      int percentage = 0;
      if (battVoltage >= 12.1) percentage = 100;
      else if (battVoltage >= 11.5) percentage = 50;
      else percentage = 0;

      if (currentState.batt_pct != percentage) {
        currentState.batt_pct = percentage;
        changed = true;
      }
    }

    // 4. RELAY REPORTING
    if (currentState.relay_state != currentRelayState) {
      currentState.relay_state = currentRelayState;
      changed = true;
    }

    // 5. TRUE DECOUPLING: Post to Mailbox Queue
    if (changed) {
      xQueueOverwrite(sensorQueue, &currentState); 
    }

    vTaskDelay(pdMS_TO_TICKS(10)); // Yield to WDT
  }
}

void setup() {
  Serial.begin(115200);
  delay(100);

  // Initialize length-1 Mailbox Queue
  sensorQueue = xQueueCreate(1, sizeof(NodeStateMsg));

  // --- NEW ESP32 CORE v3.x WDT INITIALIZATION ---
  esp_task_wdt_config_t wdt_config = {
    .timeout_ms = WDT_TIMEOUT_SECONDS * 1000,
    .idle_core_mask = (1 << portNUM_PROCESSORS) - 1, 
    .trigger_panic = true                            
  };
  esp_task_wdt_init(&wdt_config);
  esp_task_wdt_add(NULL); 

  pinMode(RELAY_1_PIN, OUTPUT);
  pinMode(RELAY_2_PIN, OUTPUT);
  digitalWrite(RELAY_1_PIN, HIGH);
  digitalWrite(RELAY_2_PIN, HIGH);
  
  pinMode(POWER_MONITOR_PIN, INPUT);
  pinMode(CURRENT_PIN_1, INPUT);
  pinMode(CURRENT_PIN_2, INPUT);
  pinMode(BATTERY_PIN, INPUT); 

  analogReadResolution(12);
  analogSetAttenuation(ADC_11db);

  sprintf(control_topic, "metro/signage/register/%d/value", ASSIGNED_REGISTER);
  sprintf(status_topic, "metro/signage/register/%d/status", ASSIGNED_REGISTER);
  sprintf(power_topic, "metro/signage/register/%d/power", ASSIGNED_REGISTER);
  sprintf(current1_topic, "metro/signage/register/%d/current1", ASSIGNED_REGISTER);
  sprintf(current2_topic, "metro/signage/register/%d/current2", ASSIGNED_REGISTER);
  sprintf(batt_pct_topic, "metro/signage/register/%d/battery_pct", ASSIGNED_REGISTER); 
  sprintf(relay_topic, "metro/signage/register/%d/relay_status", ASSIGNED_REGISTER); 

  WiFi.onEvent(eth_event_handler);
  
  ETH.begin(ETH_PHY_TYPE, ETH_PHY_ADDR, ETH_PHY_MDC, ETH_PHY_MDIO, ETH_PHY_POWER, ETH_CLK_MODE);
  ETH.config(local_IP, gateway, subnet, primaryDNS, secondaryDNS);

  client.setServer(mqtt_server_ip, mqtt_port);
  client.setCallback(callback);
  
  // Industrial Keep-Alive Standard (30s) prevents network flap
  client.setKeepAlive(30); 
  
  lastCommsTime = millis(); 

  xTaskCreatePinnedToCore(SensorTask, "SensorTask", 10000, NULL, 1, &SensorTaskHandle, 0);
}

void loop() {
  esp_task_wdt_reset();

  if (eth_connected) {
    if (!client.connected()) {
      if (millis() - lastReconnectAttempt > 5000) { 
        lastReconnectAttempt = millis();
        mqtt_connect();
      }
    } else {
      client.loop(); 
      
      // CHECK MAILBOX
      NodeStateMsg tempMsg;
      bool stateUpdated = false;
      
      // Pull data from queue. Timeout is 0 (Non-Blocking).
      if (xQueueReceive(sensorQueue, &tempMsg, 0) == pdTRUE) {
        networkState = tempMsg;
        stateUpdated = true;
      }

      if (stateUpdated) {
        publish_state_msg(networkState);
      }
    }
  }

  checkFailSafe();
}