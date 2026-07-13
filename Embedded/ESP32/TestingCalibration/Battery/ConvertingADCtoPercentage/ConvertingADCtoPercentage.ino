#include <Arduino.h>

// --- HARDWARE CONFIGURATION ---
const int BATTERY_PIN = 32;       // The GPIO pin connected to the voltage divider

// --- VOLTAGE DIVIDER CALIBRATION ---
// Measure your actual resistors with a multimeter for maximum accuracy
// and update these values if they aren't exactly 47k and 10k.
const float R1 = 42000.0; 
const float R2 = 10000.0; 

const float V_REF = 3.3;          // ESP32 max ADC voltage
const float ADC_RESOLUTION = 4095.0; // 12-bit ADC

// --- BATTERY CHEMISTRY CURVE (12V Lead-Acid) ---
const float BATT_MAX_V = 13.6;    // Voltage at 100% Full
const float BATT_MIN_V = 11.0;    // Voltage at 0% Dead

void setup() {
  Serial.begin(115200);
  delay(1000);
  
  Serial.println("\n--- ESP32 Battery Monitor Standalone Test ---");
  
  // Configure the ADC
  pinMode(BATTERY_PIN, INPUT);
  analogReadResolution(12);       // 0-4095
  analogSetAttenuation(ADC_11db); // Allows reading voltages up to ~3.3V
}

void loop() {
  // 1. Take 100 samples to average out ADC noise
  long totalADC = 0;
  for (int i = 0; i < 100; i++) {
    totalADC += analogRead(BATTERY_PIN);
    delay(2); // tiny pause between samples
  }
  float avgADC = totalADC / 100.0;

  // 2. Calculate the voltage at the ESP32 GPIO pin
  float pinVoltage = (avgADC / ADC_RESOLUTION) * V_REF;

  // 3. Reverse the voltage divider math to find actual battery voltage
  // Formula: V_batt = V_pin * ((R1 + R2) / R2)
  float k = 1.0; //calibration facotr added according to the current values --- 07/04/2026
  float battVoltage = pinVoltage * ((R1 + R2) / R2) * k ;

  // 4. Calculate Discrete Battery Percentage (3 Tiers)
  int percentage = 0;
  
  if (battVoltage >= 12.1) {
    percentage = 100;
  } 
  else if (battVoltage >= 11.5) {
    percentage = 50;
  } 
  else {
    percentage = 0;
  }

  // 5. Print the Diagnostics to Serial Monitor
  Serial.println("====================================");
  Serial.print("Raw ADC Avg:    "); Serial.println(avgADC);
  Serial.print("Pin Voltage:    "); Serial.print(pinVoltage, 3); Serial.println(" V");
  Serial.print("Actual Battery: "); Serial.print(battVoltage, 2); Serial.println(" V");
  Serial.print("State of Charge:"); Serial.print(percentage); Serial.println(" %");
  
  // Safety Warning
  if (pinVoltage > 3.1) {
    Serial.println("!!! WARNING: PIN VOLTAGE DANGEROUSLY HIGH !!!");
  }

  delay(2000); // Wait 2 seconds before the next reading
}