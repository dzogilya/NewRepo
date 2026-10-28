#!/bin/bash
# Демонстраційний скрипт тестування балансування (Linux / Git Bash)
set -e

BASE_URL="https://localhost"
USERNAME="demo_user_$(date +%s)"
PASSWORD="DemoPassword123!"

echo "=========================================================="
echo " ДЕМОНСТРАЦІЯ БАЛАНСУВАННЯ НАВАНТАЖЕННЯ (FRACTALS .NET 8)"
echo "=========================================================="

echo "[1/4] Реєстрація користувача $USERNAME..."
curl -k -s -X POST "$BASE_URL/api/auth/register" \
  -H "Content-Type: application/json" \
  -d "{\"username\":\"$USERNAME\",\"password\":\"$PASSWORD\"}" > /dev/null

echo "[2/4] Авторизація..."
LOGIN_RESP=$(curl -k -s -X POST "$BASE_URL/api/auth/login" \
  -H "Content-Type: application/json" \
  -d "{\"username\":\"$USERNAME\",\"password\":\"$PASSWORD\"}")

TOKEN=$(echo $LOGIN_RESP | grep -o '"token":"[^"]*' | cut -d'"' -f4)

echo "[3/4] Відправка 4 паралельних задач..."
TASK_IDS=()

for i in {1..4}; do
  RESP=$(curl -k -s -i -X POST "$BASE_URL/api/tasks" \
    -H "Content-Type: application/json" \
    -H "Authorization: Bearer $TOKEN" \
    -d '{"type":0,"width":400,"height":300,"maxIterations":100}')
  
  SERVED_BY=$(echo "$RESP" | grep -i "X-Served-By:" | awk '{print $2}' | tr -d '\r')
  TASK_ID=$(echo "$RESP" | grep -o '"id":"[^"]*' | cut -d'"' -f4)
  
  TASK_IDS+=($TASK_ID)
  echo "  - Задача $i створена (ID: $TASK_ID) через Web-інстанс: $SERVED_BY"
done

echo "[4/4] Очікування завершення обчислень та перевірка розподілу по App-серверах..."
sleep 5

echo "----------------------------------------------------------"
echo " РЕЗУЛЬТАТ РОЗПОДІЛУ ЗАДАЧ МІЖ ОБЧИСЛЮВАЛЬНИМИ СЕРВЕРАМИ"
echo "----------------------------------------------------------"
printf "%-38s | %-12s | %-15s\n" "Task ID" "Статус" "App Server ID"
echo "----------------------------------------------------------"

for TID in "${TASK_IDS[@]}"; do
  STATUS_RESP=$(curl -k -s "$BASE_URL/api/tasks/$TID" -H "Authorization: Bearer $TOKEN")
  STATUS=$(echo $STATUS_RESP | grep -o '"status":"[^"]*' | cut -d'"' -f4)
  SERVER=$(echo $STATUS_RESP | grep -o '"appServerId":"[^"]*' | cut -d'"' -f4)
  printf "%-38s | %-12s | %-15s\n" "$TID" "$STATUS" "${SERVER:-В черзі}"
done

echo "=========================================================="
echo " Демонстрація успішно завершена!"
