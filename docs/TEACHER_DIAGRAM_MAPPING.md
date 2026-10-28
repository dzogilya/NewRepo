# Відповідність архітектури еталонній схемі викладача

## 1. Схема викладача (з дошки)

```text
Клієнти ⇄ Web Server ──► DB ⇄ App Server 1
                         │  ⇄ App Server 2
                         ▼
           Таблиця Tasks: id | params | progress | app_server_id | is_canceled | ...
```

## 2. Mermaid-діаграма реалізованої системи

```mermaid
flowchart TD
    subgraph Clients["Клієнти (Браузери)"]
        UI["SPA Клієнт (HTML5/CSS/ES Modules Canvas)"]
        Admin["Панель адміністратора"]
    end

    subgraph LoadBalancer["Web Server & Балансувальник"]
        Nginx["Nginx Reverse Proxy & SSL (least_conn, X-Served-By)"]
        Api1["Fractals.Api (Інстанс 1, stateless)"]
        Api2["Fractals.Api (Інстанс 2, stateless)"]
    end

    subgraph Storage["Сховище даних (Єдине джерело істини)"]
        Postgres[("PostgreSQL 16 (Таблиця tasks + advisory locks)")]
        Redis[("Redis 7 (Pub/Sub поштовхи, SSE стрім, Heartbeat)")]
        SharedVol[("Спільний том (PNG файли / результати)")]
    end

    subgraph Workers["Обчислювальні сервери (App Servers)"]
        App1["Fractals.AppServer 1 (Worker Service)"]
        App2["Fractals.AppServer 2 (Worker Service)"]
        AppN["Fractals.AppServer N (Динамічно масштабовані)"]
    end

    subgraph Scaling["Автомасштабування"]
        Autoscaler["Fractals.Autoscaler (Docker.DotNet)"]
    end

    UI ⇄|HTTPS / SSE| Nginx
    Admin ⇄|HTTPS / REST| Nginx
    Nginx -->|Балансування HTTP| Api1
    Nginx -->|Балансування HTTP| Api2

    Api1 -->|Створення tasks status=QUEUED| Postgres
    Api2 -->|Створення tasks status=QUEUED| Postgres
    Api1 -.->|PUBLISH tasks:poke| Redis
    Api2 -.->|PUBLISH tasks:poke| Redis

    App1 -->|Атомарний claim: FOR UPDATE SKIP LOCKED| Postgres
    App2 -->|Атомарний claim: FOR UPDATE SKIP LOCKED| Postgres
    AppN -->|Атомарний claim: FOR UPDATE SKIP LOCKED| Postgres

    App1 -.->|SUBSCRIBE tasks:poke + Heartbeat| Redis
    App2 -.->|SUBSCRIBE tasks:poke + Heartbeat| Redis

    App1 -->|Оновлення progress & перевірка is_canceled| Postgres
    App2 -->|Оновлення progress & перевірка is_canceled| Postgres

    App1 -->|Запис готового PNG| SharedVol
    App2 -->|Запис готового PNG| SharedVol

    Autoscaler -->|Моніторинг черги| Postgres
    Autoscaler -->|Scale UP/DOWN контейнерів| AppN
```

## 3. Таблиця мапінгу компонентів викладача на код

| Елемент на дошці викладача | Реалізація в проєкті (.NET 8 / Інфраструктура) | Відповідний файл / клас | Роль та принципи |
| :--- | :--- | :--- | :--- |
| **Клієнти** | Чистий HTML5 Canvas + SSE Client | `frontend/index.html`, `frontend/js/app.js` | Відображення форми, валідація, прогресивний рендер смуг на Canvas, SSE оновлення |
| **Web Server** | Nginx 1.25 + 2 екземпляри `Fractals.Api` | `nginx/nginx.conf`, `src/Fractals.Api/Program.cs` | **Нічого не обчислює!** Приймає запит, валідує ліміти, вставляє запис у DB, повертає статус, передає SSE стрім від Redis |
| **DB** | PostgreSQL 16 + EF Core 8 (`UseSnakeCaseNamingConvention`) | `src/Fractals.Data/FractalsDbContext.cs` | **Єдине джерело істини.** Координує завдання, гарантує консистентність, чергу та блокування |
| **Таблиця Tasks** | Сутність `TaskEntity`, таблиця `tasks` | `src/Fractals.Data/Entities/TaskEntity.cs` | Містить усі обов'язкові колонки викладача (деталі нижче) |
| **App Server 1, 2** | `Fractals.AppServer` (.NET 8 BackgroundService) | `src/Fractals.AppServer/Worker.cs` | Окремі контейнери. Самі забирають задачі з БД через `SKIP LOCKED`, оновлюють прогрес, перевіряють `is_canceled` |

## 4. Явний мапінг обов'язкових колонок таблиці `tasks`

| Колонка на дошці | Тип у PostgreSQL | Властивість C# (`TaskEntity`) | Опис використання |
| :--- | :--- | :--- | :--- |
| `id` | `uuid` (Primary Key) | `Guid Id` | Унікальний ідентифікатор задачі |
| `params` | `jsonb` | `string Params` | Параметри генерації фрактала (координати, ітерації, палітра) |
| `progress` | `integer` (0-100) | `int Progress` | Поточний відсоток виконання обчислення |
| `app_server_id` | `varchar(100)` (NULLABLE) | `string? AppServerId` | ID сервера, що зарезервував і рахує задачу (NULL поки в черзі) |
| `is_canceled` | `boolean` (default false) | `bool IsCanceled` | Прапорець скасування задачі користувачем або адміністратором |
| `status` | `varchar(30)` | `string Status` | Стан кінцевого автомата: `Queued`, `Running`, `Assembling`, `Success`, `Failed`, `Cancelled` |
| `stage` | `varchar(100)` | `string Stage` | Текстовий етап українською мовою для користувача |
| `attempts` | `integer` (default 0) | `int Attempts` | Кількість спроб виконання (для watchdog у разі падіння сервера) |
| `heartbeat_at` | `timestamptz` | `DateTimeOffset? HeartbeatAt` | Час останнього сигналу життя від app-сервера |
