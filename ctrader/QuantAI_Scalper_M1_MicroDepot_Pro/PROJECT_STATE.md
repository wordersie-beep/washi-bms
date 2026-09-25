# PROJECT_STATE — QuantAI_Scalper_M1_MicroDepot_Pro

**Версия:** 1.0.0 (2026-09-25)

## Состояние
- Код готов: один файл `QuantAI_Scalper_M1_MicroDepot_Pro/QuantAI_Scalper_M1_MicroDepot_Pro.cs`, без заглушек.
- Сборка: 0 ошибок, 0 предупреждений против cTrader.Automate 1.0.0 (cTrader 4.2), 1.0.8 (последняя 4.x),
  1.0.21 (5.x); цели .NET 6 и .NET Framework 4.5 (legacy); синтаксис ограничен C# 7.3.
- `.algo` собирается командой `dotnet build -c Release` в папке проекта (API 1.0.0 — открывается в cTrader 4.2+ и 5.x).
- Тесты: 82/82 (Naive Bayes, тиковый движок, калибратор, симулятор исходов, сквиз/свип, деление объёма).
- **Не проверено:** реальный запуск в терминале cTrader (в среде разработки нет терминала и доступа к брокеру).
  Первый прогон — бэктест на тиковых данных, затем демо.

## Стек
- C# 7.3, cTrader Automate API (`cAlgo.API`), без внешних библиотек, `AccessRights.None`.
- Таймзона бота — UTC (сессия, дневные лимиты, журнал).

## Структура
```
ctrader/
  QuantAI_Scalper_M1_MicroDepot_Pro/
    README.md, CHANGELOG.md, PROJECT_STATE.md
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
  `OutcomeSimulator`, `VolmanSetups`, `SweepDetector`, `VolumeMath`, `AiFeatures`.

## Известные ограничения
- 50 EUR при плече 1:30 → маржа позволяет ~0.01 лота → TP1 закрывает 100 % (0.01 лота не делится).
  Частичная фиксация 60 % начинается с 0.02 лота.
- Безубыток +0.1 пипса не покрывает комиссию ECN (~0.6–0.7 пипса за круг).
- Разметка истории использует спред на момент старта (или `Training Spread Pips`), так как исторического
  спреда в барах нет; дальше окно обучения обновляется реальным средним спредом.
- Новостного фильтра нет (в API cTrader нет экономического календаря).

## Открытые задачи
1. Бэктест EURUSD M1, «Tick data from server», 3–6 месяцев; по строкам `AI L/S` в журнале подобрать `Min Confidence`.
2. Демо-счёт 1–2 недели при тех же параметрах; сверить журнал `SKIP`/`ENTRY`/`CLOSED`.
3. По итогам — решить про `If Volume Cannot Be Split` (CloseAllAtTp1 или TrailWholePosition) для 0.01 лота.
4. Опционально: новостные окна вручную (часы UTC), отдельные модели для SQZ и SWP.
