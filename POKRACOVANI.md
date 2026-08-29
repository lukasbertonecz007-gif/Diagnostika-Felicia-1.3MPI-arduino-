# Pokračování projektu Felicia K-line diagnostika

Tento soubor slouží jako předání kontextu pro pokračování v jiné konverzaci nebo na jiném počítači.

## Projekt

Repozitář: `lukasbertonecz007-gif/Diagnostika-Felicia-1.3MPI-arduino-`

Aktuální verze v repozitáři: **0.3**

Cíl projektu: vlastní diagnostika pro **Škoda Felicia 1.3 MPI** přes **K-line** bez ELM327. Arduino komunikuje s ECU a Windows program poskytuje modernější české UI podobné VAG/VCDS.

## Auto a ECU

Ověřené auto:

- Škoda Felicia 1.3 MPI
- starší VAG/Škoda diagnostika přes K-line
- ECU odpovídá přes **KW1281 / KWP1281**
- adresa motorové jednotky: `0x01`
- rychlost po 5baud initu: `9600 baud`

Ověřená identifikace ECU:

- číslo jednotky: `047906030N`
- komponenta: `SIMOS 2P`
- další údaj: `7016`

Důležité: standardní OBD-II PIDy nejsou pro Felicii hlavní cesta. Pro tuhle ECU funguje VAG/KW1281 komunikace přes měřené bloky a VAG chybové kódy.

## Hardware

Použitý hardware:

- Arduino Nano / Uno 5 V
- vlastní K-line převodník z tranzistorů BC547
- AltSoftSerial
- USB Serial pro debug a komunikaci s PC aplikací: `115200 baud`

Piny Arduina:

- `D8` = K-line RX
- `D9` = K-line TX

Zapojení převodníku podle testované verze:

- Arduino TX přes `10k` na bázi Q1 BC547
- Q1 emitor na GND
- Q1 kolektor přes `3.3k` na +12 V
- Q1 kolektor přes `2.2k` na bázi Q2 BC547
- Q2 emitor na GND
- Q2 kolektor na K-line
- K-line přes `510 ohm / 0.5 W` na +12 V
- K-line na OBD pin 7
- OBD pin 16 = +12 V
- OBD pin 4/5 = GND
- Arduino GND společně s GND auta
- Arduino RX čte K-line přes dělič `47k / 20k`
- volitelně zenerka 5.1 V na RX node

Logika převodníku:

- Arduino TX HIGH = K-line HIGH / klid
- Arduino TX LOW = K-line stažená na GND
- K-line je half-duplex
- RX může vidět echo vlastního TX

## Co se už povedlo

Ověřená komunikace:

- Arduino se připojí na ECU přes KW1281 slow init.
- ECU odpověděla sync/keyword handshake.
- Identifikace ECU byla přečtena.
- Čtení paměti závad funguje.
- Mazání závad fungovalo a po smazání ECU vracela prázdnou paměť závad.
- Živá data fungovala pro 3 požadované hodnoty.

Ověřený výsledek po posledním testu závad:

```text
APP_BOOT|KLine_Felicia_Test|2
KW1281 init OK
APP_ID_FIELD|1|047906030N
APP_ID_FIELD|2|SIMOS 2P
APP_ID_FIELD|3|7016
KW1281 DATA len=3 : FF FF 88
APP_DTC_NONE
KW1281 ECU reports no fault codes: FF FF 88
```

Význam `FF FF 88`: ECU aktuálně nehlásí žádné chybové kódy.

Dříve nalezená závada před smazáním:

- VAG kód `00522`
- popis: čidlo teploty chladicí kapaliny G62
- raw: `02 0A 1E`
- status: `0x1E`

Po mazání se závada už v posledním testu nevrátila.

## Živá data

Uživatel chtěl jen tyto 3 hodnoty:

- otáčky motoru
- napětí baterie
- úhel škrticí klapky

Ověřené skupiny a převody pro SIMOS 2P:

- skupina `003`, pole 1: otáčky = `raw * 32`
- skupina `003`, pole 3: úhel škrticí klapky = `raw * 0.5`
- skupina `005`, pole 2: napětí baterie = `raw * 0.1`

Arduino posílá živá data do PC aplikace ve formátu:

```text
APP_LIVE|vzorek|rpm|napeti|klapka
```

## Arduino firmware

V repozitáři je Arduino firmware zde:

```text
arduino/Diagnostika_Felicia_KLine/Diagnostika_Felicia_KLine.ino
```

Knihovna:

```text
AltSoftSerial
```

Příkazy přes Serial:

- `?` = menu
- `f` = číst paměť závad
- `c` = smazat paměť závad
- `l` = živá data
- `t` = test K-line převodníku

Bezpečnost:

- čtení závad je read-only
- živá data jsou read-only
- test převodníku neposílá ECU diagnostický příkaz
- `c` je jediný zapisovací příkaz, maže paměť závad ECU

## PC aplikace

V repozitáři je PC aplikace zde:

```text
pc/FeliciaKLineDiagApp/FeliciaKLineDiagApp.cs
```

Typ aplikace:

- C# WinForms
- HTML UI uvnitř komponenty `WebBrowser`
- komunikace přes USB Serial 115200 baud

Funkce aplikace ve verzi 0.3:

- české UI
- připojení na COM port
- automatické hledání portu
- viditelný surový log Arduino / ECU
- RX monitor: počet znaků, počet řádků, poslední zpráva
- čtení závad
- mazání závad s potvrzením
- živá data: RPM, napětí, klapka
- základní česká databáze závad

Build příkaz pro Windows:

```powershell
& 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' /nologo /codepage:65001 /target:winexe /optimize+ /platform:anycpu /out:FeliciaKLineDiagnostika.exe /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll pc\FeliciaKLineDiagApp\FeliciaKLineDiagApp.cs
```

## Důležitá poznámka k repozitáři

Do GitHubu byla přes GitHub plugin nahraná verze zdrojů `0.3`. Lokálně na notebooku existovala i delší pracovní verze Windows aplikace a hotové `.exe`, ale kvůli chybějícímu lokálnímu `git/gh` přihlášení nebyla hotová binárka nahraná jako release.

Další krok bude dodělat normální GitHub release:

- sestavit `FeliciaKLineDiagnostika.exe`
- přidat ho do GitHub Releases jako `v0.3`
- případně přidat ZIP balík s Arduino + PC aplikací

## Co dělat příště

Doporučené další kroky:

1. Doma otevřít repo a pokračovat od tohoto souboru.
2. Ověřit, že Arduino sketch v repu jde zkompilovat v Arduino IDE.
3. Ověřit, že PC aplikace jde sestavit přes `csc.exe`.
4. Dodělat GitHub Release `v0.3` s `.exe` souborem.
5. Přidat do PC aplikace export reportu do souboru, pokud v repozitářové kompaktní verzi ještě není.
6. Sladit repozitářovou PC aplikaci s plnou lokální pracovní verzí z notebooku, pokud bude notebook k dispozici.
7. Přidat větší databázi VAG závad.
8. Přidat ukládání logů podle data a času.
9. Přidat obrazovku „stav připojení“ s jasným rozlišením:
   - Arduino připojeno
   - ECU odpověděla
   - KW1281 init OK
   - závady přečteny
   - živá data běží

## Jak navázat v nové konverzaci

V nové konverzaci stačí napsat:

```text
Pokračujeme na projektu Diagnostika Felicia 1.3 MPI Arduino.
Načti si POKRACOVANI.md z repa lukasbertonecz007-gif/Diagnostika-Felicia-1.3MPI-arduino- a pokračuj podle něj.
```

Pak pokračovat konkrétním požadavkem, například:

```text
Dodělej GitHub release v0.3 s exe souborem.
```

nebo:

```text
Srovnej Arduino firmware a PC aplikaci, ať spolu mluví přes APP_* protokol.
```
