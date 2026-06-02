/*
  Diagnostika_Felicia_KLine.ino
  Verze: 0.3

  Testovací firmware pro Škoda Felicia 1.3 MPI / SIMOS 2P přes K-line.

  K-line:
  - klidový stav linky je HIGH,
  - logická 0 se dělá stažením K-line na GND,
  - sběrnice je jeden vodič a half-duplex,
  - jednoduchý tranzistorový převodník může vracet echo vlastního TX na RX.

  Hardware:
  - Arduino Nano / Uno 5 V,
  - AltSoftSerial RX = D8, TX = D9,
  - USB Serial Monitor / PC aplikace = 115200 baud,
  - K-line ECU pin OBD 7.

  Příkazy přes Serial:
  f = číst závady, c = smazat závady, l = živá data, t = test převodníku, ? = menu.
*/

#include <Arduino.h>
#include <AltSoftSerial.h>

const uint8_t KLINE_RX_PIN = 8;
const uint8_t KLINE_TX_PIN = 9;
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
const uint8_t KWP_ID_DATA = 0xF6;
const uint8_t KWP_FAULT_DATA = 0xFC;
const uint8_t KWP_GROUP_DATA = 0xE7;

AltSoftSerial kline;
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
    if (kline.available() > 0) return kline.read();
  }
  return -1;
}

void clearEchoByte() {
  (void)readByteWithTimeout(ECHO_TIMEOUT_MS);
}

void sendRaw(uint8_t b) {
  kline.write(b);
  kline.flush();
  delay(2);
  clearEchoByte();
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
  pinMode(KLINE_RX_PIN, INPUT);
  pinMode(KLINE_TX_PIN, OUTPUT);
  digitalWrite(KLINE_TX_PIN, HIGH);
}

void startKLineSerial() {
  kline.begin(KW1281_BAUD);
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
  if (sync == 0x55) Serial.println(F("sync byte is OK"));
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
  Serial.println(F("APP_CLEAR|REQUEST"));
  if (!wakeKw1281()) {
    Serial.println(F("APP_CLEAR|TIMEOUT"));
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
  if (type == KWP_ACK) Serial.println(F("APP_CLEAR|OK"));
  else if (type == KWP_REFUSE) Serial.println(F("APP_CLEAR|REFUSED"));
  else Serial.println(F("APP_CLEAR|UNKNOWN"));
  delay(1200);
  readFaults();
}

bool readRawGroup(uint8_t group, uint8_t *body, uint8_t *bodyLen) {
  uint8_t req[3] = { group, 0x03, 0x00 };
  if (!sendBlock(KWP_REQUEST_GROUP, req, 3)) return false;
  uint8_t type = 0, data[MAX_DATA], len = 0;
  if (!readBlock(&type, data, &len, 2000)) return false;
  if (type != KWP_GROUP_DATA || len < 3) return false;
  *bodyLen = len;
  for (uint8_t i = 0; i < len; i++) body[i] = data[i];
  return true;
}

void liveData() {
  Serial.println(F("KW1281 live data"));
  if (!wakeKw1281()) return;
  readIdentification();
  uint8_t g3[MAX_DATA], g5[MAX_DATA], g3len = 0, g5len = 0;
  for (uint8_t sample = 1; sample <= 20; sample++) {
    bool a = readRawGroup(3, g3, &g3len);
    delay(80);
    bool b = readRawGroup(5, g5, &g5len);
    if (!a || !b || g3len < 3 || g5len < 2) break;
    double rpm = g3[0] * 32.0;
    double throttle = g3[2] * 0.5;
    double voltage = g5[1] * 0.1;
    Serial.print(F("APP_LIVE|"));
    Serial.print(sample); Serial.print('|');
    Serial.print(rpm, 0); Serial.print('|');
    Serial.print(voltage, 1); Serial.print('|');
    Serial.println(throttle, 1);
    Serial.print(F("LIVE ")); Serial.print(sample);
    Serial.print(F(" RPM=")); Serial.print(rpm, 0);
    Serial.print(F(" rpm Voltage=")); Serial.print(voltage, 1);
    Serial.print(F(" V Throttle=")); Serial.print(throttle, 1);
    Serial.println(F(" deg"));
    delay(850);
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
    Serial.println(F("check D8/D9, divider, GND and K-line pull-up"));
  }
}

void menu() {
  Serial.println();
  Serial.println(F("=================================================="));
  Serial.println(F("Diagnostika Felicia K-line v0.3"));
  Serial.println(F("f = read faults, c = clear faults, l = live data, t = hardware test, ? = menu"));
  Serial.println(F("=================================================="));
}

void setup() {
  Serial.begin(DEBUG_BAUD);
  stopKLineSerialForBitBang();
  Serial.println();
  Serial.println(F("APP_BOOT|Diagnostika_Felicia_KLine|0.3"));
  Serial.println(F("Boot OK"));
  menu();
}

void loop() {
  while (Serial.available() > 0) {
    char c = (char)Serial.read();
    if (c == 'f' || c == 'F') readFaults();
    else if (c == 'c' || c == 'C') clearFaults();
    else if (c == 'l' || c == 'L') liveData();
    else if (c == 't' || c == 'T') hardwareTest();
    else if (c == '?') menu();
  }
}
