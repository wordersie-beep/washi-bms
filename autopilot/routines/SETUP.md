# Как включить автопилот в облаке

Claude Code Routines работают на серверах Anthropic по расписанию, по API-вызову или по событию
GitHub. Ноутбук и телефон можно выключить. Каждый запуск — отдельная сессия, её видно
в claude.ai/code. Документация: https://code.claude.com/docs/en/routines

## Шаг 0. Приватный репозиторий (агент по твоему «да» или ты за 2 минуты)
Репо, где сейчас лежит эта папка, **публичный**, а в HQ будут выручка, метрики и планы.
Нужен приватный `autopilot-hq`: содержимое папки `autopilot/` в корне.
Если агент не может запушить в новый репо — открой claude.ai/connect-github и дай
приложению Claude доступ к `autopilot-hq`.
**Не включай защиту ветки `main` в `autopilot-hq`**: ночная смена сохраняет туда состояние
(бэклог, журнал). Если ветка защищена, следующий запуск не увидит сделанного.
Продуктовые репо, наоборот, можно защищать: туда код идёт только через PR.

## Шаг 1. Окружение «Autopilot» (ты, ≈ 10 мин)
claude.ai/code → меню окружения в заголовке сессии → новое окружение (окружение Default
не трогаем: на нём работают другие твои Routines).
- **Name:** `Autopilot`
- **Network access:** `Custom` + галочка «Also include default list of common package managers».
  Домены:
  ```
  api.apify.com
  docs.apify.com
  overpass-api.de
  download.geofabrik.de
  wiki.openstreetmap.org
  www.irishrail.ie
  re.jrc.ec.europa.eu
  api.ted.europa.eu
  api.openchargemap.io
  ec.europa.eu
  api.netlify.com
  api.appstoreconnect.apple.com
  ```
  WebSearch работает при любой политике. Упрётся в новый домен — агент запишет его в
  `OWNER_INBOX.md`, ты добавишь одной строкой. `Full` проще, но открывает агенту весь интернет —
  не нужно.
- **API credentials** (не обычные переменные окружения: их видит любой, кто пользуется окружением):
  - `APIFY_TOKEN` — после регистрации в Apify (Settings → API & Integrations);
  - позже — `ASC_KEY_ID`, `ASC_ISSUER_ID`, `ASC_PRIVATE_KEY` (App Store Connect API: метрики,
    отзывы, ASO) и `LEMONSQUEEZY_API_KEY`, если будут цифровые товары.
- **Никогда не вставляй ключи в чат** — только в настройки окружения.

## Шаг 2. Три Routines (агент создаст по «да», или вручную в claude.ai/code/routines → New routine)

| Routine | Когда (Дублин) | Промпт | Модель | Коннекторы | Уведомления |
|---|---|---|---|---|---|
| Автопилот — ночная смена | ежедневно 02:52 | `routines/nightly-shift.md` | экономичная | Claude Code Remote, Netlify | push, только если есть что сказать |
| Автопилот — недельный отчёт | воскресенье 18:52 | `routines/weekly-review.md` | сильная | Claude Code Remote, Netlify | push |
| Автопилот — стратегия месяца | 1-е число, 09:52 | `routines/monthly-strategy.md` | самая сильная | Claude Code Remote | push + e-mail |

Везде: **Repository** — `autopilot-hq`, **Environment** — `Autopilot`. Cron в CLI и API:
`CRON_TZ=Europe/Dublin 52 2 * * *`, `CRON_TZ=Europe/Dublin 52 18 * * 0`,
`CRON_TZ=Europe/Dublin 52 9 1 * *`.

**Коннекторы — по минимуму.** Всё, что подключено к routine, агент может использовать без
вопросов, включая запись. Gmail, Drive, IFTTT и Home Assistant автопилоту на старте не нужны.

## Шаг 3. Лимиты — чтобы не упереться
- Дневной лимит запусков на аккаунт (research preview): Pro — 5, Max — 15, Team/Enterprise — 25.
  Твои Gmail- и HA-routines уже тратят ≈ 7 в день, автопилот добавит ≈ 1,2.
- Разовые запуски (one-off, «напомни через N дней») в дневной лимит не входят.
- Запуски тратят обычные лимиты подписки, поэтому тяжёлая работа — ночью.
- Минимальный интервал — 1 час.

## Шаг 4. Как проверять
- claude.ai/code/routines → routine → **Runs**: каждый запуск открывается как обычная сессия.
- **Зелёный статус ≠ задача выполнена**: он значит только «сессия отработала без сбоя».
  Правду смотри в `RUNLOG.md` и отчётах.
- **Run now** — запустить вне расписания, можно с текстом-пояснением.

## Позже, когда пойдут продажи
- **API-триггер:** вебхук продажи (Lemon Squeezy или App Store Server Notifications) → Netlify-функция
  → `POST /fire` routine «новая продажа» → метрики и черновик благодарности. Токен routine
  хранится только в переменных Netlify.
- **GitHub-триггер:** PR от ночной смены → routine-ревьюер (второй взгляд на код). Стоит
  1 запуск на PR — включать, когда PR станет больше одного в день.
- **IFTTT:** публикация **одобренных тобой** постов в твои каналы.
- **Artifact-дашборд:** приватная страница с KPI, которую недельный отчёт обновляет сам.
