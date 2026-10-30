# Dota Helper

Dota 2 Draft Assistant — настольная программа для Windows на C# / .NET 10 / WPF.

## Версия 0.2 — тестовый прототип

- Драфт 4 союзников и 5 противников, выбор своей позиции 1–5.
- Отметка противников на линии (повышенный вес в ранжировании).
- Контрпики на основе агрегированных OpenDota matchup-данных с консервативным сглаживанием.
- Общий винрейт и приблизительная частота выбора героев по доступным рейтинговым категориям OpenDota.
- Фильтр «Без редких (25%)», «Все герои» и «Только избранные».
- Избранные герои и настройки сохраняются между версиями.
- Дисковый кэш (каталог 7 дней; heroStats 3 часа; матчапы 24 часа).

**ВАЖНО:** 0–100 — эвристический рейтинг, НЕ вероятность победы. Общие теги (Carry/Support) **не равны позиции**; матчапы **не являются результатами линии**. OpenDota API не гарантирует фильтр по текущему патчу для используемых агрегатов. Рейтинг /heroStats и контрпики /matchups относятся к разным выборкам. Синергия союзников пока НЕ оценивается. Статистика Immortal может быть недоступна, поэтому в интерфейсе только доступные категории от Рекрута до Божества.

Фильтр «Без редких» скрывает нижние 25% по числу пиков выбранного ранга. Редкий герой не значит слабый: включи «Все герои», чтобы увидеть полный список.

## Запуск на Windows

Если используешь числовые папки (1, 2, 3…) в `E:\Work\Dota Helper`, клонируй новую копию:

    $root = "E:\Work\Dota Helper"
    $nums = @(Get-ChildItem $root -Directory | Where-Object { $_.Name -match '^\d+$' } | ForEach-Object { [int]$_.Name })
    $n = if ($nums.Count) { ($nums | Measure-Object -Maximum).Maximum + 1 } else { 1 }
    $folder = Join-Path $root $n
    New-Item -ItemType Directory -Path $folder | Out-Null
    git clone https://github.com/Freep-o-0-rn/Dota-Helper.git "$folder\Dota-Helper"
    cd "$folder\Dota-Helper"
    dotnet run --project .\src\DotaHelper\DotaHelper.csproj

Нужен .NET 10 SDK. Visual Studio 2026 поддерживает .NET 10 WPF.

Есть также автономный EXE: вкладка Actions → успешная Windows build → Artifacts (архив `DotaHelper-0.1-win-x64`; имя артефакта пока прежнее). ZIP распаковать перед запуском.

## Проверка

    dotnet build src/DotaHelper/DotaHelper.csproj
    dotnet run --project tests/DotaHelper.SmokeTests/DotaHelper.SmokeTests.csproj

Исходники разрабатываются в ветке main, результаты автоматической сборки доступны в GitHub Actions.

План и ограничения: [docs/PLAN.md](docs/PLAN.md)
