// --- HARDWARE PINS (Matching your project setup) ---
const int RELAY_1_PIN = 4;
const int RELAY_2_PIN = 13;

void setup() {
  Serial.begin(115200);
  delay(1000);
  
  // Configure the relay pins as outputs
  pinMode(RELAY_1_PIN, OUTPUT);
  pinMode(RELAY_2_PIN, OUTPUT);
  
  // Ensure they start in the OFF state (LOW)
  digitalWrite(RELAY_1_PIN, LOW);
  digitalWrite(RELAY_2_PIN, LOW);
  
  Serial.println("\n--- Relay / LED Hardware Test ---");
  Serial.println("Type '1' -> Turn Relay 1 ON");
  Serial.println("Type '2' -> Turn Relay 1 OFF");
  Serial.println("Type '3' -> Turn Relay 2 ON");
  Serial.println("Type '4' -> Turn Relay 2 OFF");
  Serial.println("---------------------------------");
}

void loop() {
  if (Serial.available() > 0) {
    char incomingChar = Serial.read();
    
    switch (incomingChar) {
      case '1':
        digitalWrite(RELAY_1_PIN, HIGH);
        Serial.println(">> Relay 1: ON");
        break;
      case '2':
        digitalWrite(RELAY_1_PIN, LOW);
        Serial.println(">> Relay 1: OFF");
        break;
      case '3':
        digitalWrite(RELAY_2_PIN, HIGH);
        Serial.println(">> Relay 2: ON");
        break;
      case '4':
        digitalWrite(RELAY_2_PIN, LOW);
        Serial.println(">> Relay 2: OFF");
        break;
    }
    
    // Clear any extra characters (like 'Enter' key presses) from the buffer
    while(Serial.available() > 0) {
      Serial.read();
    }
  }
}