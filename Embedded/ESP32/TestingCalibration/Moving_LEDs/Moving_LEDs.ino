/*
 * ESP32 5-Channel MOSFET Chase Controller
 * Physical Layout (Left to Right): D18, D17, D16, D19, D5
 * Function: Left-moving 3-LED chase effect with non-blocking Serial control
 */

// Define the pins in exact physical order from LEFT to RIGHT
const int ledPins[] = {18, 17, 16, 19, 5};
const int numLeds = 5;

// State Variables
bool isAnimating = false;
int currentHead = 4; // Start the "head" of our moving block at the far right

// Timing variables for the non-blocking stopwatch
unsigned long previousMillis = 0;
const long interval = 300; // Speed of the chase in milliseconds (lower = faster)

void setup() {
  Serial.begin(115200);
  
  // Initialize all pins as outputs and ensure they start OFF
  for (int i = 0; i < numLeds; i++) {
    pinMode(ledPins[i], OUTPUT);
    digitalWrite(ledPins[i], LOW);
  }
  
  delay(500);
  Serial.println("\n--- 5-Channel System Boot Complete ---");
  Serial.println("Type 'ON' to start the left-moving chase.");
  Serial.println("Type 'OFF' to stop and turn off all LEDs.");
}

// Helper function to quickly turn everything off
void turnAllOff() {
  for (int i = 0; i < numLeds; i++) {
    digitalWrite(ledPins[i], LOW);
  }
}

void loop() {
  // ==========================================
  // 1. LISTEN FOR COMMANDS (Runs constantly)
  // ==========================================
  if (Serial.available() > 0) {
    String input = Serial.readStringUntil('\n');
    input.trim();
    input.toUpperCase();
    
    if (input == "ON") {
      isAnimating = true;
      Serial.println("Status: Animation Started [<<< LEFTWARD CHASE <<<]");
    } 
    else if (input == "OFF") {
      isAnimating = false;
      turnAllOff();
      Serial.println("Status: Animation Stopped [ALL OFF]");
    }
  }

  // ==========================================
  // 2. RUN THE ANIMATION (Runs when allowed)
  // ==========================================
  if (isAnimating) {
    unsigned long currentMillis = millis();
    
    // Check if enough time has passed to move to the next frame
    if (currentMillis - previousMillis >= interval) {
      // Reset the stopwatch
      previousMillis = currentMillis;
      
      // Wipe the previous frame clean
      turnAllOff();
      
      // Calculate which 3 LEDs make up our moving block
      // 'currentHead' is the leading edge (left-most point of the block)
      // The other two LEDs trail behind it to the right
      int led1 = currentHead;
      int led2 = (currentHead + 1) % numLeds; // % numLeds ensures it safely wraps around
      int led3 = (currentHead + 2) % numLeds;
      
      // Light up the current frame
      digitalWrite(ledPins[led1], HIGH);
      digitalWrite(ledPins[led2], HIGH);
      digitalWrite(ledPins[led3], HIGH);
      
      // Move the head of the block one step to the LEFT for the next frame
      currentHead--;
      
      // If the head moves past the left edge (index 0), reset it to the right edge (index 4)
      if (currentHead < 0) {
        currentHead = numLeds - 1; 
      }
    }
  }
}