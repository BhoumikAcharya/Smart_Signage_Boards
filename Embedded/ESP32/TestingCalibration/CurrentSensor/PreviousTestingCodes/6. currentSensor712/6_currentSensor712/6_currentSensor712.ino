/*
 * ESP32 + ACS712 (Smoothed Output)
 * Setup:
 * - ACS712 VCC -> 5V
 * - ACS712 OUT -> ESP32 Pin D35
 */

const int sensorPin = 36;  

// ================= CONFIGURATION =================
const int sensitivity = 220; // Your calculated value
const float adcRefVoltage = 3.3; 

// SMOOTHING FACTOR (0.01 to 1.0)
// Lower = Smoother but slower to react
// Higher = Faster but more jittery
const float alpha = 0.1; 

// Variables
float zeroPointVoltage = 2.50; 
float smoothedCurrent = 0.0; // Stores the history
// =================================================

void setup() {
  Serial.begin(115200);
  pinMode(sensorPin, INPUT);
  analogSetPinAttenuation(sensorPin, ADC_11db);

  Serial.println("--------------------------------");
  Serial.println("Starting Auto-Calibration...");
  Serial.println("Ensure NO current is flowing!");
  delay(1000); 

  // --- CALIBRATION ROUTINE ---
  long totalRaw = 0;
  int sampleCount = 100;

  for (int i = 0; i < sampleCount; i++) {
    totalRaw += analogRead(sensorPin);
    delay(1); 
  }

  float averageRaw = totalRaw / (float)sampleCount;
  zeroPointVoltage = (averageRaw / 4095.0) * adcRefVoltage;
  
  Serial.print("Calibration Complete. Zero Point: ");
  Serial.print(zeroPointVoltage, 3);
  Serial.println(" V");
  Serial.println("--------------------------------");
}

void loop() {
  // 1. RAW SAMPLING
  long totalADC = 0;
  int samples = 100; 

  for (int i = 0; i < samples; i++) {
    totalADC += analogRead(sensorPin);
    delay(1); 
  }
  
  float averageADC = totalADC / (float)samples;
  float inputVoltage = (averageADC / 4095.0) * adcRefVoltage;

  // 2. Calculate Raw Current
  float rawCurrent = (inputVoltage - zeroPointVoltage) / ((float)sensitivity / 1000.0);

  // 3. Noise Filter (Dead Zone)
  if (abs(rawCurrent) < 0.12) { 
    rawCurrent = 0.00;
  }

  // 4. APPLY SMOOTHING (Exponential Moving Average)
  // Formula: (New * alpha) + (Old * (1 - alpha))
  smoothedCurrent = (rawCurrent * alpha) + (smoothedCurrent * (1.0 - alpha));

  // 5. Output
  // We print both so you can compare the jittery raw vs the smooth final
  Serial.print("ADC: ");
  Serial.print(averageADC);
  Serial.print(" | V_Out: ");
  Serial.print(inputVoltage);
  Serial.print(" | Raw: ");
  Serial.print(rawCurrent, 3);
  Serial.print(" A | Smoothed: ");
  Serial.print(smoothedCurrent, 3); 
  Serial.println(" A");

  delay(500); 
}