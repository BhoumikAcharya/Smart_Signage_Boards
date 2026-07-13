/*
 * ESP32 Enterprise Signage Controller (Firmware v2.0)
 * Architecture: MCP23017 I2C (10-Ch MOSFETs) + 3x ACS712
 * Features: Non-blocking animations, 4-State Discrepancy Logic, Modbus Offset, Dynamic Thresholds
 */

#include <ETH.h>
#include <PubSubClient.h>
#include <WiFi.h>
#include <Wire.h>
#include <Adafruit_MCP23X17.h>
#include <esp_task_wdt.h>
#include <freertos/FreeRTOS.h>
#include <freertos/task.h>
#include <freertos/queue.h>

// --- USER CONFIGURATION ----------------------------------------
const char* mqtt_server_ip = "192.168.1.10"; 
const int mqtt_port = 1883;
const int ASSIGNED_REGISTER = 40001;

IPAddress local_IP(192, 168, 1, 101); 
IPAddress gateway(192, 168, 1, 1);    
IPAddress subnet(255, 255, 255, 0);   
IPAddress primaryDNS(8, 8, 8, 8);

// --- HARDWARE PIN MAPPING (Finalized Architecture) ---
// I2C (MCP23017)
#define I2C_SDA 16
#define I2C_SCL 17
Adafruit_MCP23X17 mcp;

// Analog Sensors (ADC1 Only)
const int PIN_VOLT_PSU  = 36; 
const int PIN_VOLT_BATT = 39; 
const int PIN_CURR_LEFT = 34; // ACS712 #1 (Left Arrows)
const int PIN_CURR_RGHT = 35; // ACS712 #2 (Right Arrows)
const int PIN_CURR_ALWY = 32; // ACS712 #3 (Always ON)

// --- SENSOR CALIBRATION ---
const float ADC_REF = 3.3;
const float ALPHA   = 0.15;  // Low-pass filter

// Individual Sensor Calibration (Update these via the Calibration Tool)
const float SENS_LEFT = 0.146;
const float ZERO_LEFT = 2.400;

const float SENS_RGHT = 0.146;
const float ZERO_RGHT = 2.400;

const float SENS_ALWY = 0.146;
const float ZERO_ALWY = 2.400;

// --- BATTERY CALIBRATION ---
/*
const float BATT_R1 = 42000.0; 
const float BATT_R2 = 10000.0; 
const float K_CALIBRATION = 1.063; // Your exact multiplier from the standalone test
const float SAG_PER_STRIP = 0.05;  // CALIBRATE THIS: Voltage drop caused by 1 single LED strip
*/

// Dynamic Threshold Baseline (Minimum acceptable current for ONE single strip)
const float THRESH_PER_STRIP = 0.060; 
const float THRESHOLD_ALWY   = 0.080; // Always ON load is static

// Ethernet PHY Configuration (LAN8720)
#define ETH_PHY_ADDR  1
#define ETH_PHY_POWER -1
#define ETH_PHY_MDC   23
#define ETH_PHY_MDIO  18
#define ETH_PHY_TYPE  ETH_PHY_LAN8720
#define ETH_CLK_MODE  ETH_CLOCK_GPIO17_OUT

// --- MQTT TOPICS ---
char topic_cmd[50];
char topic_status[50];
char topic_power[50];
char topic_curr_l[50]; // Left Load
char topic_curr_r[50]; // Right Load
char topic_curr_a[50]; // Always On Load
// char topic_batt[50];          
const char* topic_scan = "metro/signage/scan"; 

WiFiClient ethClient;
PubSubClient client(ethClient);
bool eth_connected = false;

// --- STATE VARIABLES ---
volatile int currentCommand = 0; // The active MQTT integer
volatile bool expectLeftOn = false;
volatile bool expectRightOn = false;

// NEW: Track exactly how many strips are currently lit to calculate battery sag
// volatile int totalActiveStrips = 0; 

// Shared threshold variables that Core 1 updates and Core 0 reads
volatile float dynamicThreshLeft = THRESH_PER_STRIP;
volatile float dynamicThreshRight = THRESH_PER_STRIP;

// Mailbox Queue for Core 0 to pass 4-state strings to Core 1
struct NodeStateMsg {
    bool power_ok;
    // int batt_pct;
    const char* state_left;
    const char* state_right;
    const char* state_alwy;
};
QueueHandle_t sensorQueue;
NodeStateMsg networkState = {true, "---", "---", "---"};

// Animation Bitmasks
const uint8_t chaseFrames[5] = {0x07, 0x0E, 0x1C, 0x19, 0x13};
const int numFrames = 5;

// ========================================================
// CORE 1: NETWORKING & MCP23017 ANIMATION MACHINE
// ========================================================

void eth_event_handler(arduino_event_id_t event) {
  switch (event) {
    case ARDUINO_EVENT_ETH_GOT_IP: eth_connected = true; break;
    case ARDUINO_EVENT_ETH_DISCONNECTED: eth_connected = false; break;
    default: break;
  }
}

void publish_state_msg(NodeStateMsg msg) {
  char onlineMsg[30];
  snprintf(onlineMsg, sizeof(onlineMsg), "ONLINE:%s", ETH.localIP().toString().c_str());
  client.publish(topic_status, onlineMsg, true);
  
  client.publish(topic_power, msg.power_ok ? "OK" : "FAIL", true);
  client.publish(topic_curr_l, msg.state_left, true);
  client.publish(topic_curr_r, msg.state_right, true);
  client.publish(topic_curr_a, msg.state_alwy, true);

  /*
  if (msg.batt_pct != -1) {
    char bStr[8];
    snprintf(bStr, sizeof(bStr), "%d", msg.batt_pct);
    client.publish(topic_batt, bStr, true);
  }
  */
}

void callback(char* topic, byte* payload, unsigned int length) {
  char msgBuffer[16]; 
  unsigned int copyLength = (length < sizeof(msgBuffer) - 1) ? length : (sizeof(msgBuffer) - 1);
  memcpy(msgBuffer, payload, copyLength);
  msgBuffer[copyLength] = '\0'; 

  if (strcmp(topic, topic_scan) == 0 && strcmp(msgBuffer, "PING") == 0) {
    publish_state_msg(networkState); 
    return;
  }

  // Update Global Command (The animation loop handles the actual hardware writing)
  currentCommand = atoi(msgBuffer); 
  Serial.printf("Command Updated: %d\n", currentCommand);
}

// Helper to mathematically count how many MOSFETs are active in the raw bitmask
int countActiveStrips(uint8_t portMask) {
  int count = 0;
  while (portMask) { count += portMask & 1; portMask >>= 1; }
  return count;
}

void runAnimationStateMachine() {
  static unsigned long previousMillis = 0;
  static int currentFrame = 0;      
  static int lastExecutedCommand = -1; 
  unsigned long currentMillis = millis();

  // --- MODE A: MACRO ANIMATIONS (0 to 4) ---
  if (currentCommand >= 0 && currentCommand <= 4) {
    if (currentMillis - previousMillis >= 300 || currentCommand != lastExecutedCommand) {
      previousMillis = currentMillis;
      lastExecutedCommand = currentCommand;

      switch (currentCommand) {
        case 0: 
          mcp.writeGPIOA(0x00); mcp.writeGPIOB(0x00); 
          expectLeftOn=false; expectRightOn=false; 
          // totalActiveStrips = 0;
          break;
        case 1: 
          mcp.writeGPIOA(chaseFrames[currentFrame]); mcp.writeGPIOB(0x00); 
          expectLeftOn=true; expectRightOn=false; 
          dynamicThreshLeft = THRESH_PER_STRIP * 2.5; // Expecting ~3 strips to be ON
          // totalActiveStrips = 3;
          break;
        case 2: 
          mcp.writeGPIOA(0x00); mcp.writeGPIOB(chaseFrames[currentFrame]); 
          expectLeftOn=false; expectRightOn=true; 
          dynamicThreshRight = THRESH_PER_STRIP * 2.5; // Expecting ~3 strips to be ON
          // totalActiveStrips = 3;
          break;
        case 3: 
          mcp.writeGPIOA(chaseFrames[currentFrame]); mcp.writeGPIOB(chaseFrames[currentFrame]); 
          expectLeftOn=true; expectRightOn=true; 
          dynamicThreshLeft = THRESH_PER_STRIP * 2.5; 
          dynamicThreshRight = THRESH_PER_STRIP * 2.5;
          // totalActiveStrips = 6;
          break;
        case 4: 
          mcp.writeGPIOA(0x1F); mcp.writeGPIOB(0x1F); 
          expectLeftOn=true; expectRightOn=true; 
          dynamicThreshLeft = THRESH_PER_STRIP * 4.5; // Solid ON expects all 5 strips
          dynamicThreshRight = THRESH_PER_STRIP * 4.5; 
          // totalActiveStrips = 10;
          break; 
      }
      currentFrame = (currentFrame + 1) % numFrames;
    }
  }
  
  // --- MODE B: RAW BITMASK (10000 to 11023) ---
  else if (currentCommand >= 10000 && currentCommand <= 11023) {
    if (currentCommand != lastExecutedCommand) {
      lastExecutedCommand = currentCommand;
      int bitmask = currentCommand - 10000; 
      
      uint8_t portA = bitmask & 0x1F;        
      uint8_t portB = (bitmask >> 5) & 0x1F; 
      
      mcp.writeGPIOA(portA);
      mcp.writeGPIOB(portB);

      // Inform Core 0 of our intentions for discrepancy logic
      expectLeftOn = (portA > 0);
      expectRightOn = (portB > 0);

      // Calculate exact dynamic thresholds based on how many bits are 1
      int activeLeft = countActiveStrips(portA);
      int activeRight = countActiveStrips(portB);
      
      // totalActiveStrips = activeLeft + activeRight;

      if (activeLeft > 0) dynamicThreshLeft = THRESH_PER_STRIP * (activeLeft - 0.5);
      if (activeRight > 0) dynamicThreshRight = THRESH_PER_STRIP * (activeRight - 0.5);
    }
  }
}


// ========================================================
// CORE 0: HARDWARE DSP & 4-STATE DISCREPANCY LOGIC
// ========================================================

int getMedianADC(int pin) {
  int samples[51]; 
  for (int i = 0; i < 51; i++) samples[i] = analogRead(pin);
  for (int i = 1; i < 51; i++) {
    int key = samples[i]; int j = i - 1;
    while (j >= 0 && samples[j] > key) { samples[j + 1] = samples[j]; j = j - 1; }
    samples[j + 1] = key;
  }
  return samples[25]; 
}

// The core engine for the 4-State Output
const char* getDiscrepancyState(bool expectedOn, float actualCurrent, float threshold) {
  if (expectedOn && actualCurrent >= threshold) return "ON";
  if (!expectedOn && actualCurrent < threshold) return "OFF";
  if (expectedOn && actualCurrent < threshold) return "FAIL_OPEN";
  return "FAIL_SHORT"; 
}

void SensorTask(void * parameter) {
  esp_task_wdt_add(NULL); 
  NodeStateMsg currentState = {true, "---", "---", "---"};
  
  float filt_l = 0.0, filt_r = 0.0, filt_a = 0.0;
  unsigned long lastReadTime = 0;

  for(;;) {
    esp_task_wdt_reset(); 
    bool changed = false;

    if (millis() - lastReadTime >= 500) {
      lastReadTime = millis();

      // 1. Power Monitor
      bool pwrRead = (getMedianADC(PIN_VOLT_PSU) > 1800);
      if (currentState.power_ok != pwrRead) { currentState.power_ok = pwrRead; changed = true; }

      // 2. Read ACS712 Sensors with Individual Calibration Math
      float curr_l = abs((((getMedianADC(PIN_CURR_LEFT) / 4095.0) * ADC_REF) - ZERO_LEFT) / SENS_LEFT);
      float curr_r = abs((((getMedianADC(PIN_CURR_RGHT) / 4095.0) * ADC_REF) - ZERO_RGHT) / SENS_RGHT);
      float curr_a = abs((((getMedianADC(PIN_CURR_ALWY) / 4095.0) * ADC_REF) - ZERO_ALWY) / SENS_ALWY);
      
      filt_l = (curr_l < 0.06) ? 0 : (curr_l * ALPHA) + (filt_l * (1 - ALPHA));
      filt_r = (curr_r < 0.06) ? 0 : (curr_r * ALPHA) + (filt_r * (1 - ALPHA));
      filt_a = (curr_a < 0.06) ? 0 : (curr_a * ALPHA) + (filt_a * (1 - ALPHA));

      // 3. Apply 4-State Logic using DYNAMIC thresholds
      // Note: Always On is expected to always be true!
      const char* state_l = getDiscrepancyState(expectLeftOn, filt_l, dynamicThreshLeft);
      const char* state_r = getDiscrepancyState(expectRightOn, filt_r, dynamicThreshRight);
      const char* state_a = getDiscrepancyState(true, filt_a, THRESHOLD_ALWY);

      if (strcmp(currentState.state_left, state_l) != 0 || 
          strcmp(currentState.state_right, state_r) != 0 || 
          strcmp(currentState.state_alwy, state_a) != 0) {
        
        currentState.state_left = state_l;
        currentState.state_right = state_r;
        currentState.state_alwy = state_a;
        changed = true;
      }

      /* --- DYNAMIC BATTERY SAG COMPENSATION (TEMPORARILY DISABLED) ---
      float pinVoltage = (getMedianADC(PIN_VOLT_BATT) / 4095.0) * ADC_REF;
      float rawBattVoltage = pinVoltage * ((BATT_R1 + BATT_R2) / BATT_R2) * K_CALIBRATION;
      
      // Add compensation: total active I2C strips + 1 for the Always ON strip
      float compBattVoltage = rawBattVoltage + ((totalActiveStrips + 1) * SAG_PER_STRIP);
      
      int pct = 0;
      if (compBattVoltage >= 12.1) pct = 100;
      else if (compBattVoltage >= 11.5) pct = 50;
      else pct = 0;

      if (currentState.batt_pct != pct) {
        currentState.batt_pct = pct;
        changed = true;
      }
      */

      if (changed) xQueueOverwrite(sensorQueue, &currentState); 
    }
    vTaskDelay(pdMS_TO_TICKS(10)); 
  }
}


// ========================================================
// SETUP & MAIN LOOP
// ========================================================

void setup() {
  Serial.begin(115200);
  
  // Initialize I2C and MCP23017
  Wire.begin(I2C_SDA, I2C_SCL);
  if (!mcp.begin_I2C(0x20, &Wire)) {
    Serial.println("FATAL ERROR: MCP23017 not found!");
    while(1);
  }
  for (int i = 0; i < 16; i++) mcp.pinMode(i, OUTPUT);
  mcp.writeGPIOA(0x00); mcp.writeGPIOB(0x00);

  // Initialize Analog Pins
  pinMode(PIN_VOLT_PSU, INPUT); pinMode(PIN_VOLT_BATT, INPUT);
  pinMode(PIN_CURR_LEFT, INPUT); pinMode(PIN_CURR_RGHT, INPUT); pinMode(PIN_CURR_ALWY, INPUT);
  analogReadResolution(12); analogSetAttenuation(ADC_11db);

  // Networking Topic Generation
  sprintf(topic_cmd, "metro/signage/register/%d/value", ASSIGNED_REGISTER);
  sprintf(topic_status, "metro/signage/register/%d/status", ASSIGNED_REGISTER);
  sprintf(topic_power, "metro/signage/register/%d/power", ASSIGNED_REGISTER);
  sprintf(topic_curr_l, "metro/signage/register/%d/current1", ASSIGNED_REGISTER);
  sprintf(topic_curr_r, "metro/signage/register/%d/current2", ASSIGNED_REGISTER);
  sprintf(topic_curr_a, "metro/signage/register/%d/current3", ASSIGNED_REGISTER); // The 3rd load
  // sprintf(topic_batt, "metro/signage/register/%d/battery_pct", ASSIGNED_REGISTER); 

  // Initialize Ethernet
  WiFi.onEvent(eth_event_handler);
  ETH.begin(ETH_PHY_TYPE, ETH_PHY_ADDR, ETH_PHY_MDC, ETH_PHY_MDIO, ETH_PHY_POWER, ETH_CLK_MODE);
  ETH.config(local_IP, gateway, subnet, primaryDNS);
  client.setServer(mqtt_server_ip, mqtt_port);
  client.setCallback(callback);

  // Create the Mailbox for IPC
  sensorQueue = xQueueCreate(1, sizeof(NodeStateMsg));
  
  // Initialize Watchdog Timer
  esp_task_wdt_config_t wdt_config = { .timeout_ms = 15000, .idle_core_mask = (1<<portNUM_PROCESSORS)-1, .trigger_panic = true };
  esp_task_wdt_init(&wdt_config);
  esp_task_wdt_add(NULL); 

  // Spin up Core 0
  xTaskCreatePinnedToCore(SensorTask, "SensorTask", 10000, NULL, 1, NULL, 0);
}

void loop() {
  esp_task_wdt_reset();

  if (eth_connected) {
    if (!client.connected()) {
      static unsigned long lastReconnect = 0;
      if (millis() - lastReconnect > 5000) {
        lastReconnect = millis();
        char clientId[30]; snprintf(clientId, sizeof(clientId), "ESP32-%s", ETH.macAddress().c_str());
        
        // Connect with Last Will and Testament
        if (client.connect(clientId, topic_status, 1, true, "OFFLINE")) {
          client.subscribe(topic_cmd, 1);
          client.subscribe(topic_scan, 0);
          publish_state_msg(networkState);
        }
      }
    } else {
      client.loop(); 
      runAnimationStateMachine(); // Core 1 cleanly executes the LED sequence
      
      // Check the mailbox for new sensor updates from Core 0
      NodeStateMsg tempMsg;
      if (xQueueReceive(sensorQueue, &tempMsg, 0) == pdTRUE) {
        networkState = tempMsg;
        publish_state_msg(networkState);
      }
    }
  }
}