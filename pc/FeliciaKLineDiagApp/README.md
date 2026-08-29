# Felicia K-Line Diagnostika pro Windows

Verze: **0.3**

Windows aplikace je jednoduchý WinForms program s HTML UI ve `WebBrowser` komponentě. Komunikuje s Arduino firmwarem přes USB Serial na `115200 baud`.

## Funkce

- Výběr nebo automatické hledání COM portu.
- Viditelný surový log Arduino / ECU.
- RX monitor: počet znaků, řádků a poslední přijatá zpráva.
- Čtení paměti závad.
- Mazání paměti závad s potvrzením.
- Živá data: otáčky, napětí, škrticí klapka.
- Základní česká databáze závad.

## Build

```powershell
& 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' /nologo /codepage:65001 /target:winexe /optimize+ /platform:anycpu /out:FeliciaKLineDiagnostika.exe /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll pc\FeliciaKLineDiagApp\FeliciaKLineDiagApp.cs
```
