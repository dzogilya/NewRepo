@echo off
chcp 65001 > nul
echo =======================================================
echo    Запуск системи генерації фракталів (Fractals Web)
echo    Адреса в браузері: http://localhost:5119
echo =======================================================
cd /d "%~dp0"
dotnet run --project src/Fractals.Api/Fractals.Api.csproj --launch-profile "http"
pause
