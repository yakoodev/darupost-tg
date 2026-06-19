# TG Autoposter

MVP Telegram-автопостера по ТЗ: ASP.NET Core backend, PostgreSQL, Redis, React-админка и Docker.

## Быстрый запуск

```powershell
Copy-Item .env.example .env
docker compose up --build
```

Админка: http://localhost:8081 (порт задаётся `ADMIN_HOST_PORT`)  
API: http://localhost:5000  
Swagger в dev-режиме: http://localhost:5000/swagger

### Вход в админку

Админка закрыта авторизацией. При первом старте создаётся владелец из переменных `AUTH_OWNER_EMAIL` / `AUTH_OWNER_PASSWORD` (по умолчанию `owner@local` / `changeme123`). **Смените их перед любым реальным деплоем**, как и `AUTH_JWT_KEY`.

Роли (на уровне канала, по ТЗ): `Owner` — полный доступ ко всем каналам и пользователям; `ChannelAdmin` — настройки своего канала, источники, расписание, типы, промпты; `Moderator` — модерация очереди (публиковать/переписать/отклонить). Пользователями и ролями управляет владелец в разделе «Пользователи».

### База данных

Схема разворачивается через EF Core migrations (`Database.MigrateAsync` на старте). Новые миграции:

```powershell
dotnet dotnet-ef migrations add <Name> --project src/TgAutoposter.Infrastructure --startup-project src/TgAutoposter.Infrastructure --output-dir Persistence/Migrations
```

## Локальная разработка

Backend:

```powershell
dotnet build TgAutoposter.sln
dotnet run --project src/TgAutoposter.Api
```

Frontend:

```powershell
cd src/TgAutoposter.Admin
npm install
npm run dev
```

## Интеграции

Переменные окружения задаются в `.env`:

- `POLZA_ENABLED`, `POLZA_API_KEY`, `POLZA_DEFAULT_MODEL`
- `TELEGRAM_BOT_TOKEN`, `TELEGRAM_MODERATION_CHAT_ID`
- `WORKER_ENABLED`, `WORKER_INTERVAL_MINUTES`, `WORKER_MAX_POSTS_PER_RUN`

Если Polza.ai выключен или ключ не задан, генератор использует локальный fallback, чтобы очередь и админка работали в dev-режиме.

### Фактчек и дедупликация

- **Фактчек** — `AiFactCheckService`: при включённом Polza.ai отправляет инфоповод в модель и получает структурированный вердикт (passed / failed / needs_review) с учётом режима типа публикации (мягкий/средний/строгий/пользовательский). Политика по слухам (`RumorPolicy`) применяется жёстко поверх вердикта модели. Если Polza выключен или вызов упал — откат на эвристику `BasicFactCheckService`.
- **Дедупликация** — `AiDeduplicationService` поверх `BasicDeduplicationService`: сначала дешёвая эвристика (нормализованный URL/видео, Jaccard заголовков/тем, детекция «развития темы»); если эвристика говорит «уникально», но среди свежих постов есть пересечение по сущностям — модель решает, тот же это инфоповод/развитие/уникальное. Так ловятся дубли одной новости из разных изданий и на разных языках, которые токенная эвристика не видит. _Next step:_ перевод на эмбеддинги с векторным поиском для дедупа без AI-вызова на каждый кандидат.

Официальный endpoint Polza.ai для текстовой генерации: `https://polza.ai/api/v1/chat/completions`.

## Автопилот

В админке на `http://localhost:5173` есть кнопка `Автопилот`. Когда режим включён:

- канал переводится в `Automatic`;
- новости, дайджесты и мемы публикуются автоматически после дедупликации и мягкого фактчека;
- слухи остаются ручными и не уходят в канал без проверки;
- worker запускает автопостинг по интервалу из `.env`;
- кнопка `Запустить сейчас` принудительно собирает источники, создаёт один новый пост и сразу публикует его.
