/*
  Diagnostika_Felicia_KLine_ESP32.ino
  Verze: 0.5.0 (ESP32-C3, měřicí bloky SIMOS 2P)

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

// Skutečné zapojení tohoto K-line převodníku: RX=GPIO 5, TX=GPIO 4.
// Piny se za běhu nehledají ani nemění: autodetekce vytvářela aktivní pulzy
// na více GPIO a mohla vybrat jinou než skutečně zapojenou dvojici.
const uint8_t KLINE_RX_PIN = 5;
const uint8_t KLINE_TX_PIN = 4;

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
const bool VERBOSE_KW1281_LOG = false;

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
  if (VERBOSE_KW1281_LOG) {
    Serial.print(F("KW1281 ACK complement 0x"));
    printHexByte(b);
    Serial.print(F(" -> 0x"));
    printHexByte(c);
    Serial.println();
  }
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
  if (VERBOSE_KW1281_LOG) {
    Serial.print(F("RX: 0x"));
    printHexByte(*out);
    Serial.print(F(" DEC: "));
    Serial.println(*out, DEC);
  }
  if (ack) sendComplement(*out);
  return true;
}

bool sendKwByte(uint8_t b, bool waitComplement) {
  if (VERBOSE_KW1281_LOG) {
    Serial.print(F("TX: 0x"));
    printHexByte(b);
    Serial.print(F(" DEC: "));
    Serial.println(b, DEC);
  }
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
  if (VERBOSE_KW1281_LOG) {
    Serial.print(F("KW1281 sending block seq=0x"));
    printHexByte(seq);
    Serial.print(F(" type=0x"));
    printHexByte(type);
    Serial.println();
  }
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
  if (VERBOSE_KW1281_LOG) printFrame("KW1281 DATA", data, *dataLen);
  return true;
}

void stopKLineSerialForBitBang() {
  kline.end();
  delay(20);
  pinMode(KLINE_RX_PIN, INPUT);
  pinMode(KLINE_TX_PIN, OUTPUT);
  digitalWrite(KLINE_TX_PIN, HIGH);
}

void startKLineSerial() {
  // Stejný přenosový režim jako ve funkční variantě pro Arduino Nano.
  kline.begin(KW1281_BAUD, SERIAL_8N1, KLINE_RX_PIN, KLINE_TX_PIN);
  delay(AFTER_SERIAL_SWITCH_MS);
}

void send5BaudByte(uint8_t b) {
  Serial.print(F("sending 5 baud init byte 0x"));
  printHexByte(b);
  Serial.println();
  digitalWrite(KLINE_TX_PIN, HIGH);
  delay(SLOW_IDLE_BEFORE_MS);
  digitalWrite(KLINE_TX_PIN, LOW);
  delay(SLOW_BIT_MS);
  for (uint8_t bit = 0; bit < 8; bit++) {
    bool one = (b & (1 << bit)) != 0;
    digitalWrite(KLINE_TX_PIN, one ? HIGH : LOW);
    delay(SLOW_BIT_MS);
  }
  digitalWrite(KLINE_TX_PIN, HIGH);
  delay(SLOW_BIT_MS);
}

bool wakeKw1281() {
  Serial.println(F("starting slow init"));
  kwSeq = 0;
  stopKLineSerialForBitBang();
  send5BaudByte(VAG_ENGINE_ADDR);
  Serial.println(F("switching to 9600 baud"));
  startKLineSerial();
  Serial.println(F("waiting for ECU response"));
  uint8_t sync = 0, k1 = 0, k2 = 0;
  if (!readKwByte(&sync, FIRST_TIMEOUT_MS, false)) return false;
  if (!readKwByte(&k1, BYTE_TIMEOUT_MS, false)) return false;
  if (!readKwByte(&k2, BYTE_TIMEOUT_MS, false)) return false;
  if (sync != 0x55) {
    Serial.println(F("KW1281 invalid sync byte"));
    return false;
  }
  Serial.println(F("sync byte is OK"));
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
  Serial.println(F("APP_ID_DONE"));
}

bool readRawGroup(uint8_t group, uint8_t *body, uint8_t *bodyLen) {
  // 0x29 je standardní čtení normované skupiny: payload obsahuje jen číslo skupiny.
  uint8_t req[1] = { group };
  if (!sendBlock(KWP_REQUEST_GROUP, req, 1)) return false;
  uint8_t type = 0, data[MAX_DATA], len = 0;
  if (!readBlock(&type, data, &len, 2000)) return false;
  if (type != KWP_GROUP_DATA || len < 3 || (len % 3) != 0) return false;
  *bodyLen = len;
  for (uint8_t i = 0; i < len; i++) body[i] = data[i];
  return true;
}

void emitLiveField(uint8_t sample, uint8_t group, uint8_t cell, uint8_t formula, uint8_t a, uint8_t b) {
  // Každé pole E7 má přesně tři bajty: formát, A a B. Dekódování patří PC aplikaci,
  // aby se nezaměňoval formátovací bajt za naměřenou hodnotu.
  Serial.print(F("APP_LIVE_FIELD|"));
  Serial.print(sample); Serial.print('|');
  Serial.print(group); Serial.print('|');
  Serial.print(cell); Serial.print('|');
  Serial.print(formula); Serial.print('|');
  Serial.print(a); Serial.print('|');
  Serial.println(b);
}

void liveData() {
  Serial.println(F("KW1281 SIMOS 2P measuring blocks"));
  if (!wakeKw1281()) return;
  readIdentification();
  // Skupiny uvedené v dílenské příručce Felicia / SIMOS 2P. Jsou pouze čtené.
  const uint8_t groups[] = { 1, 3, 5, 7, 8, 9, 10, 11, 12, 15, 16, 17, 19, 20, 21, 97, 99 };
  for (uint8_t sample = 1; sample <= 10; sample++) {
    uint8_t groupsRead = 0;
    for (uint8_t g = 0; g < sizeof(groups); g++) {
      uint8_t body[MAX_DATA], bodyLen = 0;
      if (readRawGroup(groups[g], body, &bodyLen)) {
        groupsRead++;
        for (uint8_t pos = 0, cell = 1; pos < bodyLen; pos += 3, cell++) {
          emitLiveField(sample, groups[g], cell, body[pos], body[pos + 1], body[pos + 2]);
        }
      }
      delay(35);
    }
    if (groupsRead == 0) break;
    delay(650);
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
  uint8_t req[1] = { 98 };
  if (!sendBlock(KWP_BASIC_SETTING, req, 1)) {
    Serial.println(F("APP_ADP|FAIL_SEND"));
    return;
  }
  uint8_t type = 0, data[MAX_DATA], len = 0;
  if (!readBlock(&type, data, &len, 4000)) {
    Serial.println(F("APP_ADP|FAIL_RESP"));
    return;
  }
  if (type == KWP_GROUP_DATA) {
    Serial.println(F("APP_ADP|OK"));
    Serial.println(F("Throttle adaptation ADP OK"));
  } else if (type == KWP_REFUSE) {
    Serial.println(F("APP_ADP|REFUSED"));
  } else {
    Serial.println(F("APP_ADP|FAIL_UNKNOWN"));
  }
}

void hardwareTest() {
  stopKLineSerialForBitBang();
  digitalWrite(KLINE_TX_PIN, HIGH); delay(150); int hi = digitalRead(KLINE_RX_PIN);
  digitalWrite(KLINE_TX_PIN, LOW); delay(150); int lo = digitalRead(KLINE_RX_PIN);
  digitalWrite(KLINE_TX_PIN, HIGH); delay(150); int hi2 = digitalRead(KLINE_RX_PIN);
  if (hi == HIGH && lo == LOW && hi2 == HIGH) {
    Serial.println(F("APP_HW|OK"));
    Serial.println(F("hardware round-trip looks OK"));
  } else {
    Serial.println(F("APP_HW|FAIL"));
    Serial.println(F("check configured K-line GPIO, divider, GND and K-line pull-up"));
  }
}

void menu() {
  Serial.println();
  Serial.println(F("=================================================="));
  Serial.println(F("Diagnostika Felicia K-line (ESP32-C3 V0.5.0)"));
  Serial.println(F("f=závady, c=smazat, i=identifikace, l=měřicí bloky, a=klapka 098, t=test linky, ?=menu"));
  Serial.println(F("=================================================="));
}

void setup() {
  pinMode(LED_PIN, OUTPUT);
  setLed(false);
  Serial.begin(DEBUG_BAUD);
  delay(500); // Allow USB CDC / bridge to stabilize
  blinkLed(3, 70); // 3x bliknutí na znamení startu ESP32
  stopKLineSerialForBitBang();
  Serial.println();
  Serial.println(F("APP_BOOT|Diagnostika_Felicia_KLine_ESP32|V0.5.0"));
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
    else if (c == 't' || c == 'T') hardwareTest();
    else if (c == '?') menu();
  }
  delay(5);
}
