@echo off
chcp 65001 >nul
echo ============================================================
echo   Nahrávání projektu na GitHub (Felicia Diagnostika V0.3B)
echo ============================================================
echo.
"C:\Program Files\Git\cmd\git.exe" remote set-url origin https://github.com/lukasbertonecz007-gif/Diagnostika-Felicia-1.3MPI-arduino-.git
"C:\Program Files\Git\cmd\git.exe" branch -M main
"C:\Program Files\Git\cmd\git.exe" push -u origin main --force
echo.
echo Hotovo! Stiskni libovolnou klavesu pro ukonceni...
pause >nul
