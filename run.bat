@echo off
chcp 65001 > nul
cd /d "%~dp0"

powershell -NoProfile -Command "Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'HeatLab.exe' -or ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -match 'HeatLab\.(dll|exe)') } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"

if /i "%1"=="fresh" (
    del /q storage\heatlab.sqlite* 2> nul
    echo База даних очищена.
)

echo Збірка...
dotnet build src\HeatLab\HeatLab.csproj -c Release -nologo -v q
if errorlevel 1 (
    echo Помилка збірки.
    pause
    exit /b 1
)

echo.
echo   Сайт:          https://localhost:7443
echo   Адмін-панель:  https://localhost:7443/admin.html  (admin / admin123)
echo   Сертифікат самопідписаний: у браузері "Додатково" - "Перейти на localhost".
echo.

src\HeatLab\bin\Release\net8.0\HeatLab.exe
pause
