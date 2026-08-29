# Diagnostika Felicia 1.3 MPI Arduino

Verze: **0.3**

Testovací diagnostika pro Škodu Felicia 1.3 MPI / SIMOS 2P přes K-line. Projekt má dvě části:

- Arduino firmware pro K-line komunikaci přes tranzistorový převodník BC547.
- Windows aplikaci `FeliciaKLineDiagnostika.exe` s modernějším českým UI ve stylu VAG/VCDS.

## Ověřená konfigurace

- Arduino Nano / Uno 5 V
- AltSoftSerial: RX = D8, TX = D9
- USB Serial: 115200 baud
- ECU: VAG/KW1281, adresa `0x01`
- Ověřená jednotka: `047906030N`, `SIMOS 2P`, `7016`

## Funkce ve verzi 0.3

- Čistý komunikační protokol mezi Arduinem a Windows aplikací přes řádky `APP_*`.
- Viditelný surový log Arduino / ECU v aplikaci.
- Automatické hledání COM portu.
- Čtení paměti závad ECU.
- Mazání paměti závad s potvrzením a historií před/po.
- Živá data: otáčky, napětí baterie, úhel škrticí klapky.
- Graf živých dat.
- Český diagnostický report.
- Rozšířená databáze běžných VAG/OBD závad.

## Soubory

- `arduino/Diagnostika_Felicia_KLine/Diagnostika_Felicia_KLine.ino` - Arduino firmware.
- `pc/FeliciaKLineDiagApp/FeliciaKLineDiagApp.cs` - zdroj Windows aplikace.
- `VERSION` - číslo verze.

## Knihovny

V Arduino IDE nainstaluj knihovnu:

- `AltSoftSerial`

Na Arduino Nano / Uno používá AltSoftSerial pevné piny:

- RX = D8
- TX = D9

## Build Windows aplikace

Na Windows lze aplikaci sestavit přes .NET Framework C# compiler:

```powershell
& 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' /nologo /codepage:65001 /target:winexe /optimize+ /platform:anycpu /out:FeliciaKLineDiagnostika.exe /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll pc\FeliciaKLineDiagApp\FeliciaKLineDiagApp.cs
```

## Bezpečnost

Program není ELM327 a nepoužívá AT příkazy. Adaptace a kódování jsou vypnuté. Jediný zapisovací příkaz je potvrzované smazání paměti závad ECU.

Před mazáním závad si vždy ulož report nebo si opiš nalezené kódy.
