# Diagnostika Felicia 1.3 MPI (K-Line / KW1281)

Verze: **V0.3B**

Kompletní diagnostický systém pro vozy **Škoda Felicia 1.3 MPI (Siemens SIMOS 2P)** komunikující přes sběrnici K-line protokolem VAG KW1281.

Projekt se skládá z:
- **Windows aplikace (`FeliciaKLineDiagnostika.exe`):** Moderní české diagnostické rozhraní ve stylu VCDS / VAG s přehledem paměti závad, živými měřenými hodnotami, adaptacemi a testy akčních členů.
- **ESP32-C3 Firmware (`arduino/Diagnostika_Felicia_KLine_ESP32/`):** Rychlý 32-bitový převodník s hardwarovým UART a USB-CDC komunikací.
- **Arduino Nano / Uno Firmware (`arduino/Diagnostika_Felicia_KLine/`):** Klasická verze s knihovnou AltSoftSerial.

---

## ⚡ Podporovaný hardware

### 1. ESP32-C3 (SuperMini / DevKit) - Doporučeno
- **USB Serial:** 115200 baud (USB-CDC On Boot)
- **K-Line RX:** GPIO 5 (vstup z napěťového děliče)
- **K-Line TX:** GPIO 4 (výstup na bázi NPN tranzistoru)
- **Indikační LED:** GPIO 8 (modrá LED bliká při vysílání/příjmu)

### 2. Arduino Nano / Uno (5 V)
- **USB Serial:** 115200 baud
- **K-Line RX:** Pin D8 (AltSoftSerial)
- **K-Line TX:** Pin D9 (AltSoftSerial)
- **Indikační LED:** Pin D13

---

## 🛠️ Funkce verze V0.3B

1. **Paměť závad (Funkce 02):**
   - Čtení chybových kódů ECU s českým překladem VAG/OBD.
   - Mazání paměti závad (Funkce 05).
2. **Živá měřená data (Funkce 08):**
   - Otáčky motoru (RPM), napětí palubní sítě (V), úhel otevření klapky (°).
   - Teplota chladicí kapaliny (°C), doba vstřiku (ms), napětí lambda sondy (V).
3. **Konfigurace a servisní adaptace (Funkce 04 / 10):**
   - **Základní nastavení škrticí klapky (098):** Kalibrace dorazů servomotoru V60 a snímače G69.
   - **Vymazání adaptačních hodnot (Kanál 00):** Tovární reset korekcí směsi a volnoběhu z paměti RAM/EEPROM.
   - **Test akčních členů (Funkce 03):** Spínání ventilu odvětrání nádrže N80, relé palivového čerpadla a servomotoru klapky.
   - **Párování imobilizéru (Adresa 25 / Kanál 00).**
4. **Detailní identifikace ECU (Funkce 01):**
   - Číslo dílu VAG, typ systému (SIMOS 2P), verze softwaru a kódování.
5. **Hardware a konektivita:**
   - Automatická detekce a přehledné pojmenování COM portů.
   - Tlačítko pro vzdálený HW reset mikrokontroléru.
   - Světelná LED indikace komunikace na desce i v aplikaci.

---

## 🚀 Spuštění

1. Připoj ESP32 nebo Arduino k PC přes USB a k autu přes OBD zásuvku (Pin 7 = K-Line, Pin 16 = +12V, Pin 4/5 = GND).
2. Spusť [`FeliciaKLineDiagnostika.exe`](FeliciaKLineDiagnostika.exe).
3. Vyber odpovídající COM port a klikni na **Připojit**.
