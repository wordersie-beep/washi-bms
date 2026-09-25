# PROJECT_STATE — QuantAI_Scalper_M1_MicroDepot_Pro

**Версия:** 1.2.0 (2026-09-25)

## Состояние
- Код готов: один файл `QuantAI_Scalper_M1_MicroDepot_Pro/QuantAI_Scalper_M1_MicroDepot_Pro.cs`, без заглушек.
- Готовый `release/QuantAI_Scalper_M1_MicroDepot_Pro.algo` (API 1.0.9): ставится с телефона, экземпляр сам
  открывается на EURUSD M1, все 60 параметров выставлены (проверено по метаданным `.algo`).
- Сборка: 0 ошибок, 0 предупреждений против cTrader.Automate 1.0.9, 1.0.14, 1.0.21 (cTrader 5.x); синтаксис C# 7.3.
  На API 4.x (1.0.8 и старше) не собирается — нет `DefaultSymbolName`/`DefaultTimeFrame`.
- Тесты: 98/98 (Naive Bayes, тиковый движок, калибратор, медиана спреда, комиссия, симулятор исходов, сквиз/свип, деление объёма).
- Брокер пользователя: Pepperstone (cTrader, облако, EU 1:30). Нужен счёт Razor; на Standard фильтр спреда блокирует входы.
- **Не проверено:** реальный запуск в терминале cTrader (в среде разработки нет терминала и доступа к брокеру).
  Первый прогон — демо-счёт в облаке.

## Стек
- C# 7.3, cTrader Automate API (`cAlgo.API`), без внешних библиотек, `AccessRights.None`.
- Таймзона бота — UTC (сессия, дневные лимиты, журнал).

## Структура
```
ctrader/
  QuantAI_Scalper_M1_MicroDepot_Pro/
    README.md, CHANGELOG.md, PROJECT_STATE.md
    release/QuantAI_Scalper_M1_MicroDepot_Pro.algo  <- готовый файл для установки (телефон/ПК)
    QuantAI_Scalper_M1_MicroDepot_Pro/
      QuantAI_Scalper_M1_MicroDepot_Pro.cs      <- весь cBot
      QuantAI_Scalper_M1_MicroDepot_Pro.csproj  <- сборка .algo вне cTrader
  tests/QuantAI_Scalper_Tests/                  <- юнит-тесты логики (dotnet run)
```

## Архитектура (кратко)
- `OnTick`: тиковый движок → калибровка → спред → смена дня → закрытые бары (AI-разметка, сетапы, лог бара)
  → дневной лимит → сопровождение позиций → проверка триггеров.
- `OnTimer` (1 с): сопровождение (выход по времени без тиков), HUD.
- Чистые классы без зависимостей от cTrader: `GaussianNaiveBayes`, `TickVelocityEngine`, `TickCalibrator`,
  `SpreadTracker`, `CommissionMath`, `OutcomeSimulator`, `VolmanSetups`, `SweepDetector`, `VolumeMath`, `AiFeatures`.
- `OnTimer` также пишет STATUS каждые 15 минут и события сессии.

## Известные ограничения
- 50 EUR при плече 1:30 → маржа позволяет ~0.01 лота → TP1 закрывает 100 % (0.01 лота не делится).
  Частичная фиксация 60 % начинается с 0.02 лота.
- Безубыток +0.1 пипса не покрывает комиссию ECN (~0.6–0.7 пипса за круг).
- Исторического спреда в барах нет: разметка истории использует медиану реальных спредов в сессию
  (после 300 тиков — автоматическое переобучение) или фиксированный `Training Spread Pips`.
- Фильтр спреда 0.12 × ATR рассчитан на Raw/ECN-счёт; на стандартном счёте (~1 пипс) сделок не будет.
- Новостного фильтра нет (в API cTrader нет экономического календаря).

## Открытые задачи
1. Первый запуск (пт 25.09, ~18–19 UTC) — ни одной сделки за час: поздняя пятница, ATR низкий, фильтр спреда. Собрать
   строки `STATUS` за понедельник (Лондон/Нью-Йорк) и по ним решить, какой фильтр ослабить, если сделок мало.
2. Демо-счёт в облаке 1–2 недели с параметрами по умолчанию; сверить журнал `SKIP`/`ENTRY`/`CLOSED`.
3. Бэктест EURUSD M1, «Tick data from server», 3–6 месяцев; по строкам `AI L/S` в журнале подобрать `Min Confidence`.
4. По итогам — решить про `If Volume Cannot Be Split` (CloseAllAtTp1 или TrailWholePosition) для 0.01 лота.
5. Опционально: новостные окна вручную (часы UTC), отдельные модели для SQZ и SWP.
