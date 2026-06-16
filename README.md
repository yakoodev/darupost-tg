# TG Autoposter

MVP Telegram-автопостера по ТЗ: ASP.NET Core backend, PostgreSQL, Redis, React-админка и Docker.

## Быстрый запуск

```powershell
Copy-Item .env.example .env
docker compose up --build
```

Админка: http://localhost:5173  
API: http://localhost:5000  
Swagger в dev-режиме: http://localhost:5000/swagger

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

Официальный endpoint Polza.ai для текстовой генерации: `https://polza.ai/api/v1/chat/completions`.

## Автопилот

В админке на `http://localhost:5173` есть кнопка `Автопилот`. Когда режим включён:

- канал переводится в `Automatic`;
- новости, дайджесты и мемы публикуются автоматически после дедупликации и мягкого фактчека;
- слухи остаются ручными и не уходят в канал без проверки;
- worker запускает автопостинг по интервалу из `.env`;
- кнопка `Запустить сейчас` принудительно собирает источники, создаёт один новый пост и сразу публикует его.
