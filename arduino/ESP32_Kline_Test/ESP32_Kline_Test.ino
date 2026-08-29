#include <Arduino.h>

const int TX_PIN = 2;
const int RX_PIN = 1;

const unsigned long BIT_US = 104; // 9600 baud = 104.16 µs / bit

void printHex(uint8_t b) {
  if (b < 0x10) Serial.print('0');
  Serial.print(b, HEX);
}

// Bitbang vysilani 1 bajtu (8N1: start bit LOW, 8 bitu LSB first, stop bit HIGH)
void bitbangSendByte(uint8_t b) {
  // Start bit (LOW)
  digitalWrite(TX_PIN, LOW);
  delayMicroseconds(BIT_US);

  // 8 datovych bitu
  for (int i = 0; i < 8; i++) {
    bool bitVal = (b & (1 << i)) != 0;
    digitalWrite(TX_PIN, bitVal ? HIGH : LOW);
    delayMicroseconds(BIT_US);
  }

  // Stop bit (HIGH)
  digitalWrite(TX_PIN, HIGH);
  delayMicroseconds(BIT_US);
}

// Bitbang prijem 1 bajtu s timeoutem
int bitbangReadByte(unsigned long timeoutMs) {
  unsigned long start = millis();
  // Cekame na Start bit (sestupna hrana z HIGH na LOW)
  while (digitalRead(RX_PIN) == HIGH) {
    if (millis() - start > timeoutMs) return -1;
  }

  // Jsme ve Start bitu -> pockame 1.5 bit periody do stredu 1. datoveho bitu
  delayMicroseconds(BIT_US + (BIT_US / 2));

  uint8_t result = 0;
  for (int i = 0; i < 8; i++) {
    if (digitalRead(RX_PIN) == HIGH) {
      result |= (1 << i);
    }
    delayMicroseconds(BIT_US);
  }

  // Pockame na stop bit
  delayMicroseconds(BIT_US / 2);
  return result;
}

void runBitbangTest() {
  Serial.println("\n==========================================");
  Serial.println("   BITBANG TEST 9600 BAUD (TX=2, RX=1)    ");
  Serial.println("==========================================");

  pinMode(TX_PIN, OUTPUT);
  pinMode(RX_PIN, INPUT);
  digitalWrite(TX_PIN, HIGH);
  delay(100);

  uint8_t testBytes[] = { 0x55, 0x01, 0xAA, 0xF6, 0xFC };
  int count = sizeof(testBytes);

  for (int i = 0; i < count; i++) {
    uint8_t b = testBytes[i];
    Serial.print("   Vysilam: 0x"); printHex(b);

    // Abychom zachytili echo pri half-duplexu, posleme bajt
    bitbangSendByte(b);
    delay(5);
    Serial.println(" -> Odeslano OK");
  }

  Serial.println("\n==========================================\n");
}

void setup() {
  Serial.begin(115200);
  delay(1500);
  runBitbangTest();
}

void loop() {
  if (Serial.available()) {
    char c = (char)Serial.read();
    if (c == 't' || c == 'T' || c == ' ') runBitbangTest();
  }
  delay(200);
}
