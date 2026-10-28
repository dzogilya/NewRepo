# Демонстраційний скрипт тестування балансування (PowerShell Windows)
[System.Net.ServicePointManager]::ServerCertificateValidationCallback = {$true}

$baseUrl = "https://localhost"
$username = "demo_user_$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())"
$password = "DemoPassword123!"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " ДЕМОНСТРАЦІЯ БАЛАНСУВАННЯ НАВАНТАЖЕННЯ (FRACTALS .NET 8)" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Реєстрація
Write-Host "[1/4] Реєстрація користувача $username..."
$regBody = @{ username = $username; password = $password } | ConvertTo-Json
$null = Invoke-RestMethod -Uri "$baseUrl/api/auth/register" -Method Post -Body $regBody -ContentType "application/json" -SkipCertificateCheck

# 2. Авторизація
Write-Host "[2/4] Авторизація..."
$loginBody = @{ username = $username; password = $password } | ConvertTo-Json
$auth = Invoke-RestMethod -Uri "$baseUrl/api/auth/login" -Method Post -Body $loginBody -ContentType "application/json" -SkipCertificateCheck
$headers = @{ "Authorization" = "Bearer $($auth.token)" }

# 3. Відправка 4 задач
Write-Host "[3/4] Відправка 4 паралельних задач..."
$taskIds = @()
$taskPayload = @{ type = 0; width = 400; height = 300; maxIterations = 100 } | ConvertTo-Json

for ($i = 1; $i -le 4; $i++) {
    $response = Invoke-WebRequest -Uri "$baseUrl/api/tasks" -Method Post -Headers $headers -Body $taskPayload -ContentType "application/json" -SkipCertificateCheck
    $servedBy = $response.Headers["X-Served-By"]
    $taskData = $response.Content | ConvertFrom-Json
    $taskIds += $taskData.id

    Write-Host "  - Задача $i створена (ID: $($taskData.id)) через Web-інстанс: $servedBy" -ForegroundColor Yellow
}

# 4. Перевірка результату розподілу
Write-Host "[4/4] Очікування завершення обчислень та перевірка розподілу по App-серверах..."
Start-Sleep -Seconds 6

Write-Host "`n----------------------------------------------------------" -ForegroundColor Green
Write-Host " РЕЗУЛЬТАТ РОЗПОДІЛУ ЗАДАЧ МІЖ ОБЧИСЛЮВАЛЬНИМИ СЕРВЕРАМИ" -ForegroundColor Green
Write-Host "----------------------------------------------------------" -ForegroundColor Green
Write-Host ("{0,-38} | {1,-12} | {2,-15}" -f "Task ID", "Статус", "App Server ID")
Write-Host "----------------------------------------------------------"

foreach ($tid in $taskIds) {
    $info = Invoke-RestMethod -Uri "$baseUrl/api/tasks/$tid" -Headers $headers -SkipCertificateCheck
    $srv = if ($info.appServerId) { $info.appServerId } else { "В черзі" }
    Write-Host ("{0,-38} | {1,-12} | {2,-15}" -f $tid, $info.status, $srv)
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Демонстрація успішно завершена!" -ForegroundColor Cyan

