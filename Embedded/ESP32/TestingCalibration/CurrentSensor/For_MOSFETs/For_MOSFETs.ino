#include <ETH.h>
#include <WiFi.h>
#include <Wire.h>
#include <Adafruit_MCP23X17.h>

// --- USER CONFIGURATION ----------------------------------------
IPAddress local_IP(192, 168, 1, 102); 
IPAddress gateway(192, 168, 1, 1);    
IPAddress subnet(255, 255, 255, 0);   
IPAddress primaryDNS(8, 8, 8, 8);

// --- HARDWARE PIN MAPPING ---
#define I2C_SDA 16
#define I2C_SCL 17
Adafruit_MCP23X17 mcp;

const int PIN_CURR_LEFT = 34; // ACS712 #1 (Left Arrows)
const int PIN_CURR_RGHT = 35; // ACS712 #2 (Right Arrows)
const int PIN_CURR_ALWY = 32; // ACS712 #3 (Always ON)

const float ADC_REF_VOLT = 3.3;
const float ADC_RESOLUTION = 4095.0;

// Ethernet PHY Configuration
#define ETH_PHY_ADDR  1
#define ETH_PHY_POWER -1
#define ETH_PHY_MDC   23
#define ETH_PHY_MDIO  18
#define ETH_PHY_TYPE  ETH_PHY_LAN8720
#define ETH_CLK_MODE  ETH_CLOCK_GPIO17_OUT

// --- CALIBRATION STATE MACHINE VARIABLES ---
enum CalibState { 
  IDLE, 
  CURR_WAIT_CURRENT, CURR_WAIT_ZERO, CURR_WAIT_LOAD
};
CalibState calibState = IDLE;

// Current Variables
float target_current = 0.0;
float v_zero_l = 0.0, v_zero_r = 0.0, v_zero_a = 0.0;
float v_load_l = 0.0, v_load_r = 0.0, v_load_a = 0.0;

// --- EMI MEDIAN FILTER ALGORITHM ---
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

float getStableVoltage(int pin) {
  float totalVoltage = 0;
  for(int i = 0; i < 10; i++) {
    int median = getMedianADC(pin);
    totalVoltage += (median / ADC_RESOLUTION) * ADC_REF_VOLT;
    delay(10);
  }
  return totalVoltage / 10.0;
}

void setup() {
  Serial.begin(115200);
  delay(1000);

  // Initialize I2C and MCP23017
  Wire.begin(I2C_SDA, I2C_SCL);
  if (!mcp.begin_I2C(0x20, &Wire)) {
    Serial.println("FATAL ERROR: MCP23017 not found!");
    while(1);
  }
  for (int i = 0; i < 16; i++) mcp.pinMode(i, OUTPUT);
  
  // Start with all LEDs OFF
  mcp.writeGPIOA(0x00); mcp.writeGPIOB(0x00);
  
  pinMode(PIN_CURR_LEFT, INPUT);
  pinMode(PIN_CURR_RGHT, INPUT);
  pinMode(PIN_CURR_ALWY, INPUT);

  analogReadResolution(12);
  analogSetAttenuation(ADC_11db);

  Serial.println("\n\n================================================");
  Serial.println("  ESP32 MCP23017 CALIBRATION TOOL (3x ACS712) ");
  Serial.println("================================================");
  Serial.println("Type 'C' -> Calibrate Current Sensors");
  Serial.print(">> ");
}

void loop() {
  if (Serial.available() > 0) {
    String input = Serial.readStringUntil('\n');
    input.trim();
    if (input.length() == 0) return;

    switch (calibState) {
      
      case IDLE:
        if (input.equalsIgnoreCase("C")) {
          Serial.println("\n--- CURRENT: STEP 1: TARGET LOAD ---");
          Serial.println("What is the Amp rating of the LED strips you are testing with?");
          Serial.println("Note: Since we will turn ON all 5 arrows per side, input the total current for 5 strips.");
          Serial.println("(e.g., type 1.50 and press ENTER)");
          Serial.print(">> ");
          calibState = CURR_WAIT_CURRENT;
        } 
        break;

      case CURR_WAIT_CURRENT:
        target_current = input.toFloat();
        if (target_current <= 0) {
          Serial.println("Invalid current. Try again:");
          Serial.print(">> ");
        } else {
          Serial.printf("\nTarget Load Current set to: %.3f A\n", target_current);
          Serial.println("\n--- CURRENT: STEP 2: ZERO VOLTAGE ---");
          Serial.println("1. Left/Right Arrows are already forced OFF.");
          Serial.println("2. PLEASE UNPLUG THE 'ALWAYS ON' LEDs so no current is flowing through Sensor #3.");
          Serial.println("Press ENTER when ready to measure baseline Zero Voltages.");
          Serial.print(">> ");
          calibState = CURR_WAIT_ZERO;
        }
        break;

      case CURR_WAIT_ZERO:
        Serial.println("\nMeasuring Zero Voltages...");
        v_zero_l = getStableVoltage(PIN_CURR_LEFT);
        v_zero_r = getStableVoltage(PIN_CURR_RGHT);
        v_zero_a = getStableVoltage(PIN_CURR_ALWY);
        
        Serial.printf("Sensor 1 (Left)  ZERO_VOLT: %.3f V\n", v_zero_l);
        Serial.printf("Sensor 2 (Right) ZERO_VOLT: %.3f V\n", v_zero_r);
        Serial.printf("Sensor 3 (Alwy)  ZERO_VOLT: %.3f V\n", v_zero_a);

        Serial.println("\n--- CURRENT: STEP 3: LOAD VOLTAGE ---");
        Serial.println("1. PLEASE RE-PLUG THE 'ALWAYS ON' LEDs.");
        Serial.println("2. Press ENTER to automatically turn ON all Left and Right LEDs via the MCP23017 and measure.");
        Serial.print(">> ");
        calibState = CURR_WAIT_LOAD;
        break;

      case CURR_WAIT_LOAD:
        // Force all MOSFETs ON (0x1F = 00011111)
        mcp.writeGPIOA(0x1F);
        mcp.writeGPIOB(0x1F);
        
        Serial.println("\nLEDs ON! Waiting 2 seconds for current to stabilize...");
        delay(2000);
        
        Serial.println("Measuring Load Voltages...");
        v_load_l = getStableVoltage(PIN_CURR_LEFT);
        v_load_r = getStableVoltage(PIN_CURR_RGHT);
        v_load_a = getStableVoltage(PIN_CURR_ALWY);
        
        // Turn them back off to save eyes/power
        mcp.writeGPIOA(0x00);
        mcp.writeGPIOB(0x00);
        
        Serial.printf("Sensor 1 Active Voltage: %.3f V\n", v_load_l);
        Serial.printf("Sensor 2 Active Voltage: %.3f V\n", v_load_r);
        Serial.printf("Sensor 3 Active Voltage: %.3f V\n", v_load_a);

        Serial.println("\n--- COPY AND PASTE THIS INTO main.cpp ---");
        float sens_l = abs(v_load_l - v_zero_l) / target_current;
        float sens_r = abs(v_load_r - v_zero_r) / target_current;
        float sens_a = abs(v_load_a - v_zero_a) / target_current;

        Serial.printf("const float SENS_LEFT = %.4f;\n", sens_l);
        Serial.printf("const float ZERO_LEFT = %.3f;\n\n", v_zero_l);
        
        Serial.printf("const float SENS_RGHT = %.4f;\n", sens_r);
        Serial.printf("const float ZERO_RGHT = %.3f;\n\n", v_zero_r);
        
        Serial.printf("const float SENS_ALWY = %.4f;\n", sens_a);
        Serial.printf("const float ZERO_ALWY = %.3f;\n", v_zero_a);
        
        Serial.println("\nType 'C' to recalibrate.");
        Serial.print(">> ");
        calibState = IDLE;
        break;
    }
  }
}