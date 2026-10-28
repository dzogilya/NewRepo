Write-Host "=======================================================" -ForegroundColor Cyan
Write-Host "   Запуск системи генерації фракталів (Fractals Web)" -ForegroundColor Green
Write-Host "   Адреса в браузері: http://localhost:5119" -ForegroundColor Yellow
Write-Host "=======================================================" -ForegroundColor Cyan

Set-Location -Path $PSScriptRoot
dotnet run --project src/Fractals.Api/Fractals.Api.csproj --launch-profile "http"
