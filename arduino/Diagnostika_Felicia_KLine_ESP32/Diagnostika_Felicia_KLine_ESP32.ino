/*
  Diagnostika_Felicia_KLine_ESP32.ino
  Verze: 0.4 (ESP32-C3 Production)

  Kompletní diagnostický firmware pro Škoda Felicia 1.3 MPI (Siemens SIMOS 2P)
  s protokolem KW1281 / K-line na ESP32-C3.

  Zapojení pinů na tvé desce:
  - TX: GPIO 2 (výstup do báze Q1 přes odpor)
  - RX: GPIO 1 (vstup z napěťového děliče z K-line)
  - GND: Společná kostra (GND ESP32 + GND auta / baterie)
  - +12V: OBD pin 16 (přes pull-up odpory a dělič)
  - K-Line: OBD pin 7

  Rychlost USB: 115200 baud
  Rychlost K-line: 9600 baud (po 5-baud inicializaci na adrese 0x01)
*/

#include <Arduino.h>

// Piny K-Linky (výchozí pro ESP32-C3 SuperMini: RX=20, TX=21, nebo RX=1, TX=2)
uint8_t klineRxPin = 20;
uint8_t klineTxPin = 21;

// Vestavěná indikační LED na ESP32-C3 (SuperMini = GPIO 8, aktivní v LOW)
#ifndef LED_PIN
  #ifdef LED_BUILTIN
    const uint8_t LED_PIN = LED_BUILTIN;
  #else
    const uint8_t LED_PIN = 8;
  #endif
#endif
const bool LED_ACTIVE = LOW; // Active LOW na ESP32-C3 SuperMini

void setLed(bool on) {
  digitalWrite(LED_PIN, on ? LED_ACTIVE : !LED_ACTIVE);
}

void toggleLed() {
  digitalWrite(LED_PIN, !digitalRead(LED_PIN));
}

void blinkLed(uint8_t times = 1, uint16_t durationMs = 30) {
  for (uint8_t i = 0; i < times; i++) {
    setLed(true);
    delay(durationMs);
    setLed(false);
    if (times > 1) delay(durationMs);
  }
}

const unsigned long DEBUG_BAUD = 115200;
const unsigned long KW1281_BAUD = 9600;
const uint8_t VAG_ENGINE_ADDR = 0x01;

const uint16_t SLOW_BIT_MS = 200;
const uint16_t SLOW_IDLE_BEFORE_MS = 300;
const uint16_t AFTER_SERIAL_SWITCH_MS = 5;
const uint16_t FIRST_TIMEOUT_MS = 3000;
const uint16_t BYTE_TIMEOUT_MS = 120;
const uint16_t COMPLEMENT_TIMEOUT_MS = 90;
const uint16_t ECHO_TIMEOUT_MS = 35;

const uint8_t MAX_DATA = 64;
const uint8_t KWP_ACK = 0x09;
const uint8_t KWP_REFUSE = 0x0A;
const uint8_t KWP_CLEAR_FAULTS = 0x05;
const uint8_t KWP_REQUEST_FAULTS = 0x07;
const uint8_t KWP_REQUEST_GROUP = 0x29;
const uint8_t KWP_OUTPUT_TESTS = 0x04;
const uint8_t KWP_ADAPTATION = 0x21;
const uint8_t KWP_BASIC_SETTING = 0x28;
const uint8_t KWP_ID_DATA = 0xF6;
const uint8_t KWP_FAULT_DATA = 0xFC;
const uint8_t KWP_GROUP_DATA = 0xE7;

HardwareSerial kline(1);
uint8_t kwSeq = 0;

void printHexByte(uint8_t b) {
  if (b < 0x10) Serial.print('0');
  Serial.print(b, HEX);
}

void printFrame(const char *label, const uint8_t *buf, uint8_t len) {
  Serial.print(label);
  Serial.print(F(" len="));
  Serial.print(len);
  Serial.print(F(" :"));
  for (uint8_t i = 0; i < len; i++) {
    Serial.print(' ');
    printHexByte(buf[i]);
  }
  Serial.println();
}

void printPaddedCode(uint16_t code) {
  if (code < 10000) Serial.print('0');
  if (code < 1000) Serial.print('0');
  if (code < 100) Serial.print('0');
  if (code < 10) Serial.print('0');
  Serial.print(code);
}

int readByteWithTimeout(uint16_t timeoutMs) {
  unsigned long started = millis();
  while ((uint16_t)(millis() - started) < timeoutMs) {
    if (kline.available() > 0) {
      toggleLed(); // Indikace příjmu bytu z linky
      return kline.read();
    }
    delay(1);
  }
  return -1;
}

void clearEchoByte() {
  (void)readByteWithTimeout(ECHO_TIMEOUT_MS);
}

void sendRaw(uint8_t b) {
  setLed(true); // Rozsvítit LED při vysílání
  kline.write(b);
  kline.flush();
  delay(2);
  clearEchoByte();
  setLed(false);
}

void sendComplement(uint8_t b) {
  uint8_t c = b ^ 0xFF;
  Serial.print(F("KW1281 ACK complement 0x"));
  printHexByte(b);
  Serial.print(F(" -> 0x"));
  printHexByte(c);
  Serial.println();
  sendRaw(c);
}

bool readKwByte(uint8_t *out, uint16_t timeoutMs, bool ack) {
  int b = readByteWithTimeout(timeoutMs);
  if (b < 0) {
    Serial.print(F("KW1281 timeout after "));
    Serial.print(timeoutMs);
    Serial.println(F(" ms"));
    setLed(false);
    return false;
  }
  *out = (uint8_t)b;
  Serial.print(F("RX: 0x"));
  printHexByte(*out);
  Serial.print(F(" DEC: "));
  Serial.println(*out, DEC);
  if (ack) sendComplement(*out);
  return true;
}

bool sendKwByte(uint8_t b, bool waitComplement) {
  Serial.print(F("TX: 0x"));
  printHexByte(b);
  Serial.print(F(" DEC: "));
  Serial.println(b, DEC);
  sendRaw(b);
  if (!waitComplement) return true;
  int c = readByteWithTimeout(COMPLEMENT_TIMEOUT_MS);
  if (c < 0) {
    Serial.println(F("KW1281 timeout waiting for ECU complement"));
    return false;
  }
  uint8_t expected = b ^ 0xFF;
  if ((uint8_t)c != expected) {
    Serial.print(F("KW1281 complement mismatch, expected 0x"));
    printHexByte(expected);
    Serial.println();
    return false;
  }
  return true;
}

bool sendBlock(uint8_t type, const uint8_t *data, uint8_t dataLen) {
  delay(12);
  uint8_t len = 3 + dataLen;
  uint8_t seq = kwSeq + 1;
  Serial.print(F("KW1281 sending block seq=0x"));
  printHexByte(seq);
  Serial.print(F(" type=0x"));
  printHexByte(type);
  Serial.println();
  if (!sendKwByte(len, true)) return false;
  if (!sendKwByte(seq, true)) return false;
  if (!sendKwByte(type, true)) return false;
  for (uint8_t i = 0; i < dataLen; i++) if (!sendKwByte(data[i], true)) return false;
  if (!sendKwByte(0x03, false)) return false;
  kwSeq = seq;
  return true;
}

bool readBlock(uint8_t *type, uint8_t *data, uint8_t *dataLen, uint16_t firstTimeout) {
  uint8_t len = 0;
  while (true) {
    if (!readKwByte(&len, firstTimeout, false)) return false;
    if (len == 0x55) {
      uint8_t k1 = 0, k2 = 0;
      if (!readKwByte(&k1, BYTE_TIMEOUT_MS, false)) return false;
      if (!readKwByte(&k2, BYTE_TIMEOUT_MS, false)) return false;
      delay(40);
      sendComplement(k2);
      continue;
    }
    break;
  }
  if (len < 3 || len > MAX_DATA + 3) {
    Serial.print(F("KW1281 invalid block length: "));
    Serial.println(len);
    return false;
  }
  sendComplement(len);
  uint8_t seq = 0;
  if (!readKwByte(&seq, BYTE_TIMEOUT_MS, true)) return false;
  kwSeq = seq;
  if (!readKwByte(type, BYTE_TIMEOUT_MS, true)) return false;
  *dataLen = len - 3;
  for (uint8_t i = 0; i < *dataLen; i++) if (!readKwByte(&data[i], BYTE_TIMEOUT_MS, true)) return false;
  uint8_t endByte = 0;
  if (!readKwByte(&endByte, BYTE_TIMEOUT_MS, false)) return false;
  if (endByte != 0x03) return false;
  printFrame("KW1281 DATA", data, *dataLen);
  return true;
}

void stopKLineSerialForBitBang() {
  kline.end();
  delay(20);
  pinMode(klineRxPin, INPUT);
  pinMode(klineTxPin, OUTPUT);
  digitalWrite(klineTxPin, HIGH);
}

unsigned long currentKLineBaud = 10400; // SIMOS 2P standard is 10400 or 9600

void startKLineSerial(unsigned long baud) {
  currentKLineBaud = baud;
  kline.begin(baud, SERIAL_8N1, klineRxPin, klineTxPin);
  delay(AFTER_SERIAL_SWITCH_MS);
}

void send5BaudByte(uint8_t b) {
  Serial.print(F("sending 5 baud init byte 0x"));
  printHexByte(b);
  Serial.println();
  
  // Parita pro KW1281: 7 datových bitů + 1 lichá parita (odd parity)
  uint8_t ones = 0;
  for (uint8_t bit = 0; bit < 7; bit++) {
    if (b & (1 << bit)) ones++;
  }
  bool parityOdd = ((ones % 2) == 0); // Pokud je počet 1 sudý, paritní bit musí být 1 pro lichý součet
  
  digitalWrite(klineTxPin, HIGH);
  delay(SLOW_IDLE_BEFORE_MS);
  
  // Start bit (LOW)
  digitalWrite(klineTxPin, LOW);
  delay(SLOW_BIT_MS);
  
  // 7 datových bitů (LSB first)
  for (uint8_t bit = 0; bit < 7; bit++) {
    bool bitVal = (b & (1 << bit)) != 0;
    digitalWrite(klineTxPin, bitVal ? HIGH : LOW);
    delay(SLOW_BIT_MS);
  }
  
  // 1 paritní bit (Odd)
  digitalWrite(klineTxPin, parityOdd ? HIGH : LOW);
  delay(SLOW_BIT_MS);
  
  // Stop bit (HIGH)
  digitalWrite(klineTxPin, HIGH);
  delay(SLOW_BIT_MS);
}

bool wakeKw1281(uint8_t ecuAddr = VAG_ENGINE_ADDR) {
  Serial.print(F("starting slow init for ECU address 0x"));
  printHexByte(ecuAddr);
  Serial.print(F(" on pins RX="));
  Serial.print(klineRxPin);
  Serial.print(F(", TX="));
  Serial.println(klineTxPin);
  kwSeq = 0;
  
  stopKLineSerialForBitBang();
  
  // Zkontrolujeme, zda je linka v klidu HIGH (musí mít 12V pullup z auta)
  if (digitalRead(klineRxPin) == LOW) {
    Serial.println(F("VAROVANI: K-linka je v LOW uz pred inicializaci! Chybí +12V na pinu 16 OBD nebo je linka zkratovana na kostru."));
  }
  
  send5BaudByte(ecuAddr);
  
  // Okamžitě přepneme na HW UART (9600 baud - standard pro Felicia SIMOS 2P)
  Serial.println(F("switching to 9600 baud UART"));
  startKLineSerial(9600);
  
  Serial.println(F("waiting for ECU response (Sync byte 0x55)..."));
  uint8_t sync = 0, k1 = 0, k2 = 0;
  
  if (!readKwByte(&sync, FIRST_TIMEOUT_MS, false)) {
    Serial.println(F("zkousim opakovat inicializaci na 10400 baud..."));
    stopKLineSerialForBitBang();
    send5BaudByte(ecuAddr);
    startKLineSerial(10400);
    if (!readKwByte(&sync, FIRST_TIMEOUT_MS, false)) {
      Serial.println(F("ECU neodpovedela na 5-baud inicializaci."));
      return false;
    }
  }
  
  if (!readKwByte(&k1, BYTE_TIMEOUT_MS, false)) return false;
  if (!readKwByte(&k2, BYTE_TIMEOUT_MS, false)) return false;
  if (sync == 0x55) Serial.println(F("sync byte is OK (0x55)"));
  Serial.print(F("KW1281 Key bytes: 0x")); printHexByte(k1); Serial.print(F(" 0x")); printHexByte(k2); Serial.println();
  
  delay(40);
  sendComplement(k2);
  Serial.println(F("KW1281 init OK"));
  return true;
}

void readIdentification() {
  uint8_t type = 0, data[MAX_DATA], len = 0;
  for (uint8_t block = 0; block < 4; block++) {
    if (!readBlock(&type, data, &len, 2000)) return;
    if (type != KWP_ID_DATA) return;
    Serial.print(F("APP_ID_FIELD|"));
    Serial.print(block + 1);
    Serial.print('|');
    for (uint8_t i = 0; i < len; i++) {
      char ch = (char)data[i];
      if (ch >= 32 && ch <= 126 && ch != '|') Serial.print(ch);
      else Serial.print('_');
    }
    Serial.println();
    if (!sendBlock(KWP_ACK, NULL, 0)) return;
  }
}

void printFault(uint8_t index, const uint8_t *t) {
  uint16_t code = ((uint16_t)t[0] << 8) | t[1];
  Serial.print(F("APP_DTC|"));
  printPaddedCode(code);
  Serial.print('|');
  printHexByte(t[0]); Serial.print(' '); printHexByte(t[1]); Serial.print(' '); printHexByte(t[2]);
  Serial.print('|');
  printHexByte(t[2]);
  Serial.println();
  Serial.print(F("DTC #"));
  Serial.print(index + 1);
  Serial.print(F(": raw="));
  printHexByte(t[0]); Serial.print(' '); printHexByte(t[1]); Serial.print(' '); printHexByte(t[2]);
  Serial.print(F(" VAG/decimal="));
  printPaddedCode(code);
  Serial.print(F(" status=0x"));
  printHexByte(t[2]);
  Serial.println();
}

void readFaultsBody() {
  if (!sendBlock(KWP_REQUEST_FAULTS, NULL, 0)) return;
  uint8_t type = 0, data[MAX_DATA], len = 0;
  uint8_t count = 0;
  while (true) {
    if (!readBlock(&type, data, &len, 2000)) return;
    if (type == KWP_REFUSE) {
      Serial.println(F("APP_DTC_REFUSED"));
      return;
    }
    if (type != KWP_FAULT_DATA) return;
    if (len == 3 && data[0] == 0xFF && data[1] == 0xFF) {
      Serial.println(F("APP_DTC_NONE"));
      Serial.println(F("KW1281 ECU reports no fault codes: FF FF 88"));
      return;
    }
    for (uint8_t i = 0; i + 2 < len; i += 3) printFault(count++, &data[i]);
    if (len < MAX_DATA) break;
    if (!sendBlock(KWP_ACK, NULL, 0)) return;
  }
  Serial.print(F("APP_DTC_DONE|"));
  Serial.println(count);
}

void readFaults() {
  Serial.println(F("KW1281 read fault codes"));
  if (!wakeKw1281()) {
    Serial.println(F("init failed"));
    return;
  }
  readIdentification();
  readFaultsBody();
}

void clearFaults() {
  Serial.println(F("KW1281 clear fault codes"));
  if (!wakeKw1281()) {
    Serial.println(F("init failed"));
    return;
  }
  readIdentification();
  if (!sendBlock(KWP_CLEAR_FAULTS, NULL, 0)) {
    Serial.println(F("APP_CLEAR|TIMEOUT"));
    return;
  }
  uint8_t type = 0, data[MAX_DATA], len = 0;
  if (!readBlock(&type, data, &len, 2000)) {
    Serial.println(F("APP_CLEAR|TIMEOUT"));
    return;
  }
  if (type == KWP_ACK) {
    Serial.println(F("APP_CLEAR|OK"));
    Serial.println(F("faults cleared successfully"));
  } else {
    Serial.println(F("APP_CLEAR|REFUSED"));
    Serial.println(F("clear faults refused by ECU"));
  }
}

void readOnlyIdentification() {
  Serial.println(F("KW1281 read identification"));
  if (!wakeKw1281()) {
    Serial.println(F("init failed"));
    return;
  }
  readIdentification();
}

void formatSimosValue(uint8_t type, uint8_t a, uint8_t b, char *out, size_t outLen) {
  float v = 0.0f;
  switch (type) {
    case 1:
      v = 0.2f * a * b;
      snprintf(out, outLen, "%.0f ot/min", v);
      break;
    case 5:
      v = a * (b - 100) * 0.1f;
      snprintf(out, outLen, "%.1f °C", v);
      break;
    case 7:
      v = 0.01f * 256 * a + 0.01f * b;
      snprintf(out, outLen, "%.2f V", v);
      break;
    case 14:
      v = 0.001f * 256 * a + 0.001f * b;
      snprintf(out, outLen, "%.3f V", v);
      break;
    case 15:
      v = 0.01f * 256 * a + 0.01f * b;
      snprintf(out, outLen, "%.2f ms", v);
      break;
    case 19:
      v = a * b * 0.01f;
      snprintf(out, outLen, "%.2f l", v);
      break;
    case 24:
      v = 0.001f * 256 * a + 0.001f * b;
      snprintf(out, outLen, "%.3f A", v);
      break;
    case 36:
      v = 2560.0f * a + 10.0f * b;
      snprintf(out, outLen, "%.0f km", v);
      break;
    case 44:
      snprintf(out, outLen, "%02d:%02d", a, b);
      break;
    default:
      snprintf(out, outLen, "%u / %u", a, b);
      break;
  }
}

void parseGroup(const uint8_t *data, uint8_t len, uint8_t sampleIdx) {
  if (len < 10) return;
  char r1[24], r2[24], r3[24], r4[24];
  formatSimosValue(data[0], data[1], data[2], r1, sizeof(r1));
  formatSimosValue(data[3], data[4], data[5], r2, sizeof(r2));
  formatSimosValue(data[6], data[7], data[8], r3, sizeof(r3));
  formatSimosValue(data[9], data[10], data[11], r4, sizeof(r4));

  float rpmVal = 0.0f;
  if (data[0] == 1) rpmVal = 0.2f * data[1] * data[2];
  float voltVal = 0.0f;
  if (data[3] == 7) voltVal = 0.01f * 256 * data[4] + 0.01f * data[5];
  float thrVal = 0.0f;
  if (data[6] == 7) thrVal = 0.01f * 256 * data[7] + 0.01f * data[8];

  Serial.print(F("APP_LIVE|"));
  Serial.print(sampleIdx);
  Serial.print('|');
  Serial.print(rpmVal, 0);
  Serial.print('|');
  Serial.print(voltVal, 2);
  Serial.print('|');
  Serial.print(thrVal, 1);
  Serial.print('|');
  Serial.print(r1);
  Serial.print('|');
  Serial.print(r2);
  Serial.print('|');
  Serial.print(r3);
  Serial.print('|');
  Serial.println(r4);
}

void liveData() {
  Serial.println(F("KW1281 live data stream (Function 08, Group 001)"));
  if (!wakeKw1281()) {
    Serial.println(F("init failed"));
    return;
  }
  readIdentification();
  uint8_t grp[1] = { 0x01 };
  if (!sendBlock(KWP_REQUEST_GROUP, grp, 1)) return;
  for (uint8_t s = 1; s <= 20; s++) {
    uint8_t type = 0, data[MAX_DATA], len = 0;
    if (!readBlock(&type, data, &len, 2500)) return;
    if (type == KWP_GROUP_DATA) parseGroup(data, len, s);
    if (!sendBlock(KWP_ACK, NULL, 0)) return;
    delay(80);
  }
  Serial.println(F("APP_LIVE_DONE"));
}

void basicSetting098() {
  Serial.println(F("APP_ADP|START"));
  Serial.println(F("KW1281 basic setting (Function 04, Group 098 - Throttle Adaptation)"));
  if (!wakeKw1281()) {
    Serial.println(F("APP_ADP|FAIL_INIT"));
    return;
  }
  readIdentification();
  uint8_t grp[1] = { 0x62 }; // 0x62 = 98 dec
  if (!sendBlock(KWP_BASIC_SETTING, grp, 1)) {
    Serial.println(F("APP_ADP|FAIL_SEND"));
    return;
  }
  uint8_t type = 0, data[MAX_DATA], len = 0;
  if (!readBlock(&type, data, &len, 4000)) {
    Serial.println(F("APP_ADP|TIMEOUT"));
    return;
  }
  if (type == KWP_GROUP_DATA) {
    Serial.println(F("APP_ADP|OK"));
    Serial.println(F("Throttle adaptation ADP OK"));
  } else {
    Serial.println(F("APP_ADP|REFUSED"));
  }
  sendBlock(0x06, NULL, 0); // End output
}

void resetAdaptations() {
  Serial.println(F("APP_RESET_ADP|START"));
  Serial.println(F("KW1281 reset adaptations (Function 10, Channel 00)"));
  if (!wakeKw1281()) {
    Serial.println(F("APP_RESET_ADP|FAIL_INIT"));
    return;
  }
  readIdentification();
  uint8_t chan[1] = { 0x00 };
  if (!sendBlock(KWP_ADAPTATION, chan, 1)) {
    Serial.println(F("APP_RESET_ADP|FAIL_SEND"));
    return;
  }
  uint8_t type = 0, data[MAX_DATA], len = 0;
  if (!readBlock(&type, data, &len, 3000)) {
    Serial.println(F("APP_RESET_ADP|TIMEOUT"));
    return;
  }
  if (type == KWP_ACK || type == KWP_GROUP_DATA) {
    Serial.println(F("APP_RESET_ADP|OK"));
  } else {
    Serial.println(F("APP_RESET_ADP|REFUSED"));
  }
}

void testActuators() {
  Serial.println(F("APP_ACT|START"));
  Serial.println(F("KW1281 output tests (Function 03)"));
  if (!wakeKw1281()) {
    Serial.println(F("APP_ACT|FAIL_INIT"));
    return;
  }
  readIdentification();
  if (!sendBlock(KWP_OUTPUT_TESTS, NULL, 0)) {
    Serial.println(F("APP_ACT|FAIL_SEND"));
    return;
  }
  uint8_t type = 0, data[MAX_DATA], len = 0;
  if (readBlock(&type, data, &len, 3000)) {
    Serial.println(F("APP_ACT|RUNNING"));
  } else {
    Serial.println(F("APP_ACT|FAIL_RESP"));
  }
}

bool autoDetectPins() {
  struct PinPair { uint8_t rx; uint8_t tx; const char* name; };
  PinPair pairs[] = {
    { 20, 21, "ESP32-C3 SuperMini (RX=20, TX=21)" },
    { 1, 2, "ESP32-C3 DevKit (RX=1, TX=2)" },
    { 4, 5, "ESP32-C3 Alt1 (RX=4, TX=5)" },
    { 6, 7, "ESP32-C3 Alt2 (RX=6, TX=7)" },
    { 0, 1, "ESP32-C3 Alt3 (RX=0, TX=1)" }
  };
  
  for (uint8_t i = 0; i < sizeof(pairs)/sizeof(pairs[0]); i++) {
    uint8_t rx = pairs[i].rx;
    uint8_t tx = pairs[i].tx;
    
    pinMode(rx, INPUT);
    pinMode(tx, OUTPUT);
    digitalWrite(tx, HIGH);
    delay(30);
    int hi = digitalRead(rx);
    
    digitalWrite(tx, LOW);
    delay(30);
    int lo = digitalRead(rx);
    
    digitalWrite(tx, HIGH);
    delay(30);
    int hi2 = digitalRead(rx);
    
    if (hi == HIGH && lo == LOW && hi2 == HIGH) {
      klineRxPin = rx;
      klineTxPin = tx;
      Serial.print(F(">>> AUTO-DETECT: Nalezen aktivni obvod na: "));
      Serial.println(pairs[i].name);
      return true;
    }
  }
  return false;
}

void hardwareTest() {
  stopKLineSerialForBitBang();
  Serial.println(F("\n--- DIAGNOSTIKA FYZICKEHO HARDWARE K-LINKY ---"));
  
  bool detected = autoDetectPins();
  
  digitalWrite(klineTxPin, HIGH);
  delay(150);
  int rxIdle = digitalRead(klineRxPin);
  
  digitalWrite(klineTxPin, LOW);
  delay(150);
  int rxActive = digitalRead(klineRxPin);
  
  digitalWrite(klineTxPin, HIGH);
  delay(150);
  int rxRestored = digitalRead(klineRxPin);
  
  Serial.print(F("1. Stav linky v klidu (TX=HIGH na GPIO "));
  Serial.print(klineTxPin);
  Serial.print(F("): RX (GPIO "));
  Serial.print(klineRxPin);
  Serial.print(F(") = "));
  Serial.println(rxIdle == HIGH ? F("HIGH (3.3V) -> OK (12V pull-up je pritomen)") : F("LOW (0V) -> CHYBA!"));
  
  Serial.print(F("2. Stav linky pri stazeni (TX=LOW): RX = "));
  Serial.println(rxActive == LOW ? F("LOW (0V) -> OK (Tranzistor stahuje k zemi)") : F("HIGH (3.3V) -> CHYBA!"));
  
  Serial.print(F("3. Navrat do klidoveho stavu (TX=HIGH): RX = "));
  Serial.println(rxRestored == HIGH ? F("HIGH (3.3V) -> OK") : F("LOW (0V) -> CHYBA!"));
  
  Serial.println(F("\n--- STAV VSECH GPIO PINU NA ESP32-C3 ---"));
  uint8_t probePins[] = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 20, 21 };
  for (uint8_t i = 0; i < sizeof(probePins); i++) {
    uint8_t p = probePins[i];
    if (p == klineTxPin || p == LED_PIN) continue;
    pinMode(p, INPUT);
    int val = digitalRead(p);
    Serial.print(F(" GPIO "));
    if (p < 10) Serial.print(' ');
    Serial.print(p);
    Serial.print(F(": "));
    Serial.println(val == HIGH ? F("HIGH (3.3V)") : F("LOW (0V)"));
  }
  Serial.println(F("----------------------------------------\n"));
  
  if (rxIdle == HIGH && rxActive == LOW && rxRestored == HIGH) {
    Serial.println(F("APP_HW|OK"));
    Serial.println(F("VYSLEDEK: Hardwarovy obvod prevodniku je 100% FUNKCNI!"));
  } else {
    Serial.println(F("APP_HW|FAIL"));
    if (rxIdle == LOW && rxActive == LOW) {
      Serial.println(F("PRICINA CHYBY: K-linka je trvale na 0V (GND). Zkontrolujte:"));
      Serial.println(F(" - Zda je zapalovani vozu ZAPNUTO (kontrolky sviti)"));
      Serial.println(F(" - Zda je privedeno +12V z OBD pinu 16 na pull-up odpor prevodniku (510R / 1k)"));
      Serial.println(F(" - Zda je napetovy delic z K-linky spravne pripojen"));
    } else if (rxIdle == HIGH && rxActive == HIGH) {
      Serial.println(F("PRICINA CHYBY: Tranzistor nestahuje K-linku k zemi. Zkontrolujte:"));
      Serial.println(F(" - Zapojeni baze tranzistoru (GPIO pres odpor cca 1k-4.7k)"));
      Serial.println(F(" - Zapojeni emitoru tranzistoru na spolecne GND"));
    }
  }
  Serial.println(F("----------------------------------------------\n"));
}

void scanAllEcus() {
  Serial.println(F("\n--- HLEDANI VSECH JEDNOTEK VE VOZU (SCAN) ---"));
  uint8_t addrs[] = { 0x01, 0x25, 0x15, 0x17 };
  const char* names[] = { "Motor SIMOS 2P (0x01)", "Imobilizer (0x25)", "Airbag (0x15)", "Pristrojova deska (0x17)" };
  
  for (uint8_t i = 0; i < 4; i++) {
    Serial.print(F("Zkousim se pripojit k: "));
    Serial.println(names[i]);
    if (wakeKw1281(addrs[i])) {
      Serial.print(F(">>> JEDNOTKA NALEZENA A ODPOVÍDÁ: "));
      Serial.println(names[i]);
      readIdentification();
      sendBlock(0x06, NULL, 0); // End session
      delay(500);
    } else {
      Serial.print(F("Bez odezvy: "));
      Serial.println(names[i]);
    }
    delay(400);
  }
  Serial.println(F("--- SCAN DOKONCEN ---\n"));
}

void menu() {
  Serial.println();
  Serial.println(F("=================================================="));
  Serial.println(F("Diagnostika Felicia K-line (ESP32-C3 V0.3B)"));
  Serial.println(F("f=závady, c=smazat, i=identifikace, l=živá data, a=klapka 098, r=reset adaptací, k=akční členy, s=scan všech ECU, t=test linky, ?=menu"));
  Serial.println(F("=================================================="));
}

void setup() {
  pinMode(LED_PIN, OUTPUT);
  setLed(false);
  Serial.begin(DEBUG_BAUD);
  delay(500); // Allow USB CDC / bridge to stabilize
  blinkLed(3, 70); // 3x bliknutí na znamení startu ESP32
  autoDetectPins();
  stopKLineSerialForBitBang();
  Serial.println();
  Serial.println(F("APP_BOOT|Diagnostika_Felicia_KLine_ESP32|V0.3B"));
  Serial.println(F("Boot OK - ESP32 Ready"));
  menu();
}

void loop() {
  while (Serial.available() > 0) {
    char c = (char)Serial.read();
    if (c == '\r' || c == '\n' || c == ' ' || c == '\t') continue;
    if (c == 'f' || c == 'F') readFaults();
    else if (c == 'c' || c == 'C') clearFaults();
    else if (c == 'i' || c == 'I') readOnlyIdentification();
    else if (c == 'l' || c == 'L') liveData();
    else if (c == 'a' || c == 'A') basicSetting098();
    else if (c == 'r' || c == 'R') resetAdaptations();
    else if (c == 'k' || c == 'K') testActuators();
    else if (c == 's' || c == 'S') scanAllEcus();
    else if (c == 't' || c == 'T') hardwareTest();
    else if (c == '?') menu();
  }
  delay(5);
}

