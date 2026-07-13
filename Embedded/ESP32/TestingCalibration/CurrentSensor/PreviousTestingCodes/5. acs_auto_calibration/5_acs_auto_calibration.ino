/*
 * ESP32 + ACS712 (Auto-Calibrated)
 * Setup:
 * - ACS712 VCC -> 5V
 * - ACS712 OUT -> ESP32 Pin D35
 */

const int sensorPin = 35;  

// ================= CONFIGURATION =================
// ACS712-05B = 185, ACS712-20A = 100, ACS712-30A = 66
const float sensitivity = 0.502; 

// This is no longer 'const' because we calculate it in setup
float zeroPointVoltage = 2.50; 

// ESP32 ADC Reference Voltage
const float adcRefVoltage = 3.3; 
// =================================================

void setup() {
  Serial.begin(115200);
  pinMode(sensorPin, INPUT);
  analogSetPinAttenuation(sensorPin, ADC_11db);

  Serial.println("--------------------------------");
  Serial.println("Starting Auto-Calibration...");
  Serial.println("Ensure NO current is flowing through the sensor!");
  delay(1000); // Give you a moment to ensure load is off

  // --- CALIBRATION ROUTINE ---
  long totalRaw = 0;
  int sampleCount = 100;

  for (int i = 0; i < sampleCount; i++) {
    totalRaw += analogRead(sensorPin);
    delay(10);
  }

  float averageRaw = totalRaw / (float)sampleCount;
  zeroPointVoltage = (averageRaw / 4095.0) * adcRefVoltage;
  
  Serial.print("Calibration Complete. New Zero Point: ");
  Serial.print(zeroPointVoltage, 3);
  Serial.println(" V");
  Serial.println("--------------------------------");
}

void loop() {
  // 1. Averaging for stability
  long totalADC = 0;
  int samples = 100; 

  for (int i = 0; i < samples; i++) {
    totalADC += analogRead(sensorPin);
    delay(1); 
  }
  
  float averageADC = totalADC / (float)samples;
  float inputVoltage = (averageADC / 4095.0) * adcRefVoltage;

  // 2. Calculate Current
  float current = (inputVoltage - zeroPointVoltage) / ((float)sensitivity);

  // 3. "Dead Zone" Filter
  // If current is very tiny (noise), force it to zero
  if (abs(current) < 0.12) { 
    current = 0.00;
  }

  // 4. Output
  Serial.print("ADC: ");
  Serial.print(averageADC);
  Serial.print(" |V_Out: ");
  Serial.print(inputVoltage, 3);
  Serial.print("V | Current: ");
  Serial.print(current, 3); 
  Serial.println(" A");

  delay(500); 
}