# Профили ниш

Сюда можно положить свои `*.json` профили (тот же формат, что во встроенных
`src/TgAutoposter.Infrastructure/Profiles/gaming.json` и `vtubing.json`).
Файл с тем же `key` переопределяет встроенный. Папка монтируется в API-контейнер
как `/app/profiles` (`Profiles__Path`).
