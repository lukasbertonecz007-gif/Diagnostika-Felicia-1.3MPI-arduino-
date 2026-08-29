# 🛠️ Zprovoznění K-Line Diagnostického Převodníku na ESP32-C3

**Projekt:** Diagnostika řídicí jednotky motoru Siemens SIMOS 2P (Škoda Felicia 1.3 MPI)  
**Hardware:** ESP32-C3 SuperMini / DevKit + K-Line převodník (L9637D / MC33290 / Si9243 / diskrétní tranzistorový obvod)  
**Komunikační protokol:** ISO 9141-2 / VAG KW1281 (5-Baud Slow Init, poté 9 600 Bd)

---

## 1. 📋 Přehled toho, co se podařilo zprovoznit

1. **Plná hardwarová komunikace na ESP32-C3:**
   * Firmware v mikrokontroléru ESP32-C3 úspěšně řídí pomalou 5-baudovou inicializaci (`5-baud slow init`), generuje synchronizační pulzy (WGB) a navazuje relaci s řídicí jednotkou **Siemens SIMOS 2P (047906030N)**.
   * Převodník úspěšně prošel hardwarovým round-trip testem (`APP_HW|OK`), což potvrzuje, že vysílač (TX) i přijímač (RX) na K-lince jsou elektricky i logicky funkční.

2. **Oddělení časování a protokolu (ESP32 Firmware):**
   * Časově kritická komunikace (5-baudový slow-init a následný přenos 9 600 Bd) běží přímo v reálném čase na ESP32.
   * Počítač s Windows komunikuje s ESP32 po standardní USB sériové lince rychlostí **115 200 Bd**, takže nedochází k chybám v časování způsobeným Windows.

3. **Implementované a funkční funkce protokolu KW1281:**
   * **Identifikace jednotky:** Vyčtení čísla dílu (`047906030N`), softwaru a typu systému (`SIMOS 2P`).
   * **Čtení paměti závad (DTC / Blok 02):** Dekódování VAG chybových kódů (např. `00513` snímač otáček G28, `00518` škrticí klapka G69, `00522` čidlo teploty G62, `17978` imobilizér atd.).
   * **Mazání paměti závad (Blok 05):** Odeslání požadavku na smazání chyb v RAM a EEPROM.
   * **Živá měřená data (Blok 003 a 005):** Čtení otáček motoru (ot/min), palubního napětí (V) a úhlu otevření škrticí klapky (°).
   * **Hardwarový self-test sběrnice:** Příkaz `t` otestuje fyzickou odezvu K-linky.

---

## 2. 🔌 Zapojení pinů (Pinout ESP32-C3)

| Signál | ESP32-C3 Pin | Popis / Připojení na převodník |
| :--- | :--- | :--- |
| **K-Line TX** | `GPIO 4` | Výstup z ESP32 do vstupu TXD budiče K-linky |
| **K-Line RX** | `GPIO 5` | Vstup do ESP32 z výstupu RXD budiče K-linky |
| **GND** | `GND` | Společná kostra (propojeno s pinem 4 a 5 na OBD zásuvce) |
| **Napájení ESP32** | `5V / USB` | Napájení mikrokontroléru z USB PC |
| **K-Line Signál** | *OBD Pin 7* | K-line sběrnice vozidla (pullována na +12V přes 510R odpor) |
| **+12V Vozidlo** | *OBD Pin 16* | Trvalé napájení pro budič K-linky (svorka 30 / 15) |

---

## 3. 🖥️ Příkazy sériového rozhraní (Protokol mezi PC a ESP32)

ESP32 naslouchá na sériovém portu (rychlost **115 200 Bd**, 8N1). Odesláním jednoduchého znaku s koncem řádku `\n` lze ovládat veškerou diagnostiku:

| Příkaz | Popis funkce | Odpověď ESP32 |
| :---: | :--- | :--- |
| `?` | Zobrazí nápovědu a stav jednotky | Textové menu a stav relace |
| `t` | **Hardwarový test K-linky** (round-trip ověření) | `APP_HW\|OK` nebo `APP_HW\|FAIL` |
| `f` | **Vyčíst paměť závad (DTC)** | `APP_DTC\|kód\|hex\|status` nebo `APP_DTC_NONE` |
| `c` | **Smazat paměť závad** | `APP_CLEAR\|OK` |
| `l` | **Čtení živých dat** (Blok 003 / 005) | `APP_LIVE\|vzorek\|otáčky\|napětí\|klapka` |
| `0` | **Odpojit / Ukončit relaci** s ECU | `APP_DISCONNECTED` |

---

## 4. 📂 Umístění zdrojových kódů v projektu

* **Arduino Firmware pro ESP32:**  
  📁 `arduino/Diagnostika_Felicia_KLine_ESP32/Diagnostika_Felicia_KLine_ESP32.ino`
* **Zálohy a verze PC aplikace:**  
  📁 `pc/versions/` (všechny verze od `V0.1` po `V0.6`)
* **Aktuální spustitelný PC program:**  
  📁 `pc/FeliciaKLineDiagnostika.exe`

---

## 5. 🚀 Jak s převodníkem pracovat (Rychlý návod)

1. Připoj K-line diagnostický převodník do OBD-II zásuvky Felicie (nebo na žlutý konektor u pojistkové skříňky).
2. Připoj USB kabel z ESP32 do počítače.
3. Zapni zapalování ve Felicii (kontrolky na palubce se rozsvítí).
4. Otevři libovolný sériový terminál (např. Serial Monitor v Arduino IDE, PuTTY, nebo aplikaci `pc/FeliciaKLineDiagnostika.exe`) na portu **COM6** (nebo tvůj detekovaný COM port) rychlostí **115 200 Bd**.
5. Pošli znak `t` pro ověření hardware (odpoví `APP_HW|OK`).
6. Pošli znak `?` nebo `l` a jednotka SIMOS 2P okamžitě odpoví a začne posílat data.
