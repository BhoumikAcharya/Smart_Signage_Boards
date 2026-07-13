/*
 * ESP32 Dual ACS712 Manager with Relay Control and Status Logic
 * * WIRING:
 * - ACS712 #1 OUT -> GPIO 35
 * - ACS712 #2 OUT -> GPIO 36
 * - Relay #1      -> GPIO 4 (Changeable below)
 * - Relay #2      -> GPIO 13 (Changeable below)
 * * LOGIC:
 * - Calibration forces Relays HIGH, measures offset, then restores state.
 * - Smoothing and Sensitivity are independent per channel.
 * - Thresholds:
 * 0.00 - 0.10 A -> FAIL (Zero/Noise)
 * 0.10 - 0.38 A -> PARTIAL
 * > 0.38 A      -> OK
 */

// ================= CONFIGURATION =================
const float ADC_REF_VOLTAGE = 3.3; 
const int ADC_RESOLUTION = 4095;

// Status Thresholds (Amps)
const float THRESHOLD_LOW_CUTOFF = 0.100; // Below this is FAIL
const float THRESHOLD_OK_LIMIT   = 0.380; // Above this is OK, Between is PARTIAL

// Virtual "Holding Registers" to simulate external control (e.g., from Pi)
// These store the state the relays *should* be in during normal operation.
bool holdingRegRelayState1 = LOW; 
bool holdingRegRelayState2 = LOW;

// ================= CLASS DEFINITION =================
class CurrentChannel {
  private:
    int sensorPin;
    int relayPin;
    int sensitivity;        // mV per Amp (e.g., 185, 100, 66 or custom)
    float zeroPointVoltage; // The voltage reading when current is 0
    float smoothedCurrent;  // History for smoothing
    float alpha;            // Smoothing factor
    String name;

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
    }

    void setup() {
      pinMode(sensorPin, INPUT);
      pinMode(relayPin, OUTPUT);
      // Ensure specific attenuation for ESP32 ADC
      analogSetPinAttenuation(sensorPin, ADC_11db);
      
      // Set initial relay state to match holding register (default OFF/LOW)
      digitalWrite(relayPin, LOW); 
    }

    // Update relay based on the "Holding Register" control value
    void updateRelay(bool desiredState) {
      digitalWrite(relayPin, desiredState ? HIGH : LOW);
    }

    // Auto Calibration Routine
    void calibrate(bool returnToState) {
      Serial.print("["); Serial.print(name); Serial.println("] Starting Calibration...");
      
      // 1. Force Relay HIGH as requested
      digitalWrite(relayPin, HIGH);
      Serial.print("["); Serial.print(name); Serial.println("] Relay Forcibly set HIGH for Calibration.");
      
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

      Serial.print("["); Serial.print(name); Serial.print("] Calibrated Zero Point: ");
      Serial.print(zeroPointVoltage, 3); Serial.println(" V");
      Serial.print("["); Serial.print(name); Serial.println("] Relay Restored to Holding State.");
      Serial.println("--------------------------------");
    }

    // Setter for sensitivity dynamically
    void setSensitivity(int newSens) {
      sensitivity = newSens;
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
      // If current is extremely small (noise), force to 0 internally for smoothing calculation
      if (abs(rawCurrent) < 0.05) { 
        rawCurrent = 0.00;
      }

      // 4. Smoothing
      smoothedCurrent = (rawCurrent * alpha) + (smoothedCurrent * (1.0 - alpha));
    }

    // Get the status string based on thresholds
    String getStatus() {
      // Use absolute value in case of wiring polarity flip
      float absCurrent = abs(smoothedCurrent);

      if (absCurrent <= THRESHOLD_LOW_CUTOFF) {
        return "FAIL"; // Current is effectively zero
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

// ================= MAIN SETUP =================
void setup() {
  Serial.begin(115200);
  
  // Setup Hardware
  ch1.setup();
  ch2.setup();

  // Initial Control Values (Example: Let's say logic demands they start LOW)
  holdingRegRelayState1 = LOW;
  holdingRegRelayState2 = LOW;
  
  // --- INITIAL CALIBRATION ---
  // We calibrate them independently. 
  // Argument: The state to return to after calibration.
  Serial.println("=== SYSTEM STARTUP CALIBRATION ===");
  ch1.calibrate(holdingRegRelayState1);
  ch2.calibrate(holdingRegRelayState2);
  Serial.println("=== SYSTEM READY ===");
}

// ================= MAIN LOOP =================
void loop() {
  // 1. SIMULATE EXTERNAL CONTROL
  // In a real application, you might read Modbus registers or Serial commands here
  // to update holdingRegRelayState1 and holdingRegRelayState2.
  // For now, we enforce the current holding variables.
  ch1.updateRelay(holdingRegRelayState1);
  ch2.updateRelay(holdingRegRelayState2);

  // 2. UPDATE SENSORS
  ch1.update();
  ch2.update();

  // 3. DISPLAY OUTPUT
  printStatus();

  // 4. CHECK FOR USER INPUT (Serial) to trigger Recalibration
  if (Serial.available()) {
    char cmd = Serial.read();
    if (cmd == '1') {
      Serial.println("\n>>> Recalibrating Channel 1 Requested");
      ch1.calibrate(holdingRegRelayState1);
    }
    if (cmd == '2') {
      Serial.println("\n>>> Recalibrating Channel 2 Requested");
      ch2.calibrate(holdingRegRelayState2);
    }
    // Example to change holding register (Turn Relay 1 ON)
    if (cmd == 'A') { holdingRegRelayState1 = HIGH; Serial.println("Cmd: Relay 1 ON"); }
    if (cmd == 'a') { holdingRegRelayState1 = LOW;  Serial.println("Cmd: Relay 1 OFF"); }
  }

  delay(500); 
}

void printStatus() {
  // Format for clear reading or parsing by Raspberry Pi
  // Format: [CH] Current | Status
  
  Serial.print("CH1: ");
  Serial.print(ch1.getCurrent(), 3);
  Serial.print(" A [");
  Serial.print(ch1.getStatus());
  Serial.print("]  |  ");

  Serial.print("CH2: ");
  Serial.print(ch2.getCurrent(), 3);
  Serial.print(" A [");
  Serial.print(ch2.getStatus());
  Serial.println("]");
}