@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo ==========================================
echo  Sestavuji Felicia K-Line Diagnostika...
echo ==========================================

dotnet build -c Release
if %ERRORLEVEL% equ 0 (
    echo.
    echo [OK] Aplikace byla uspesne sestavena pomoci .NET!
    echo Spustit ji muzes prikazem 'dotnet run' nebo souborem bin\Release\net10.0-windows\FeliciaKLineDiagApp.exe
    goto end
)

echo.
echo Zkousim zalozni sestaveni pres .NET Framework csc.exe...
C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /nologo /codepage:65001 /target:winexe /optimize+ /platform:anycpu /out:FeliciaKLineDiagnostika.exe /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll FeliciaKLineDiagApp.cs

if %ERRORLEVEL% equ 0 (
    echo.
    echo [OK] Aplikace FeliciaKLineDiagnostika.exe byla uspesne vytvorena!
) else (
    echo.
    echo [CHYBA] Sestaveni selhalo.
)

:end
pause
