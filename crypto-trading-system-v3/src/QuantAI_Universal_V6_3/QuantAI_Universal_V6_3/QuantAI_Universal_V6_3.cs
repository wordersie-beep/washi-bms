// =====================================================================================================
//  QuantAI_Universal_V6_3 (версия 6.3) — адаптивный AI-cBot для cTrader (API 4.x / cTrader.Automate)
//
//  ИСТОРИЯ ВЕРСИЙ
//   6.3 — выходные: крипта торгует ВСЕГДА, всё остальное закрывается до выходных.
//         • Закрытие перед выходными/праздником делает сам робот по таймеру (раз в 20 с) для ВСЕХ позиций бота —
//           любой версии (QAIv6, QAIv5, QAIv5T) и любого таймфрейма, даже если для них нет потока. Решение — по
//           расписанию рынка каждого инструмента у брокера: закрывается за N минут до закрытия, если потом рынок
//           закрыт 6+ часов. Страховка: в пятницу с 20:45 UTC закрывается всё, что не крипта.
//         • Параметр «Закрывать перед выходными»: только позиции бота (по умолчанию) или все позиции счёта.
//         • Крипта получает потоки первой (сразу после инструмента графика) — лимит потоков её не отрежет.
//           Если у брокера не нашлось ни одной криптовалюты, журнал сразу предупреждает.
//   6.2 — потоки распределяются по инструментам честно: сначала КАЖДЫЙ инструмент получает таймфрейм графика,
//         затем следующий таймфрейм для всех и т. д., пока не кончится лимит 24. В 6.1 порядок был «инструмент
//         за инструментом», и при 4 таймфреймах × 11 инструментах последние в списке (BTC, ETH, LTC, XRP, GER40)
//         не получали ни одного потока — в выходные бот не торговал бы вовсе. Пропущенные потоки перечисляются.
//   6.1 — круглосуточная торговля 24/7: форекс, металлы и индексы — всё время, пока открыт их рынок (24/5),
//         крипта — и в выходные. Вместо фиксированных часов работают фильтры по фактам: спред к ATR,
//         доля издержек в прибыли, EV после комиссии и уровни доверия стратегий.
//         • Пауза на ролловер (17:00 Нью-Йорка, резкое расширение спреда) теперь следует летнему времени США:
//           летом 20:50–21:20 UTC, зимой 21:50–22:20 UTC (в 5.x–6.0 окно было зимним круглый год). Крипту не касается.
//         • Перед долгим закрытием рынка (выходные, праздник) позиции не-крипто закрываются за 15 минут до
//           закрытия ИМЕННО ЭТОГО инструмента по его расписанию у брокера; за час до закрытия новые не открываются.
//         • Больше крипты для выходных: LTCUSD и XRPUSD (в группе связанных с BTC/ETH).
//         • Исправлено: в 6.0 журнал печатал версию «5.9».
//   6.0 — торговля по ВСЕМ инструментам с уровнями доверия к стратегиям (как «инкубация» в фондах:
//         новая стратегия сначала торгуется малым капиталом и получает полный только после подтверждения).
//         • Уровни по теневому счёту стратегии (после спреда и комиссии):
//             ✓ ДОКАЗАН — среднее − σ·ошибка > 0 и плюс в обеих половинах периода → полный риск;
//             ? ПРОБНЫЙ — плюс ещё не доказан → «Риск пробной сделки» (по умолчанию 25% обычного);
//             ✗ УБЫТОЧЕН — среднее + σ·ошибка < 0 → сделки запрещены, стратегия только учится.
//         • Больше инструментов по умолчанию: EURUSD, GBPUSD, USDJPY, XAUUSD, US500, NAS100, GER40, BTCUSD,
//           ETHUSD на ТФ графика и h1 (до 24 потоков). Имена у брокеров разные — можно перечислить
//           варианты через «|» (US500|SPX500): берётся первый существующий.
//         • Лимит суммарного риска открытых позиций (по умолчанию 2%): позиция в безубытке риска не несёт,
//           поэтому освобождает место для новых сделок. Объём новой сделки урезается под остаток лимита.
//         • Группы связанных инструментов (EURUSD/GBPUSD, US500/NAS100, BTC/ETH): в одну сторону в группе —
//           не больше одной позиции, чтобы не ставить дважды на одно и то же движение.
//         • Форекс, металлы и индексы закрываются в пятницу в 19:45 UTC — без риска гэпа через выходные.
//         • Журнал: по умолчанию только значимые пропуски (сигнал был, но отклонён), рутина — в сводке.
//   5.9 — РАБОЧАЯ версия (не тест): сделка открывается только при доказанном преимуществе.
//         • Теневые сделки ведутся ТАК ЖЕ, как реальные: частичное закрытие и безубыток на +1R,
//           ATR-трейлинг, тейк, спред и КОМИССИЯ брокера. Раньше комиссия не учитывалась нигде, а на
//           форексе m5/m15 это 5–10% риска каждой сделки.
//         • У каждой из 5 стратегий свой теневой счёт. Торгуются только стратегии, у которых на последних
//           (до 300) теневых сделках ожидание в R положительно с запасом надёжности (среднее − σ·ошибка
//           среднего > 0) и положительно в ОБЕИХ половинах периода — проверка на устойчивость во времени,
//           как walk-forward. Остальные только учатся (SKIP: No Edge).
//         • Ожидание сделки (EV) — по фактическим средним выигрыша и проигрыша теневых сделок потока
//           за вычетом комиссии. Объём учитывает комиссию: стоп + комиссия ≈ заданный риск.
//         • Форекс и золото — только в ликвидные часы (по умолчанию 07:00–19:00 UTC: Лондон и Нью-Йорк);
//           крипта — круглосуточно. Не больше одной позиции на инструмент на всех таймфреймах.
//         • По умолчанию: EURUSD (график) + XAUUSD + BTCUSD × (ТФ графика, m30, h1). Фильтры рабочие:
//           EV > 0, адаптивный порог, уверенность 0.30, пауза 2 бара, спред ≤ 30% ATR и ≤ 15% прибыли.
//         • Позиции тестовых версий (метка QAIv5T) на тех же инструменте и таймфрейме подхватываются.
//   5.8-TEST — исправлен лимит залога: NormalizeVolumeInUnits не опускает объём ниже минимального лота,
//         поэтому 0.83 лота «округлялось вниз» до 1 и сделка по золоту открылась с залогом 29% при
//         лимите 25%. Теперь сравнивается объём ДО округления, а после округления залог проверяется
//         ещё раз: больше лимита — вход пропускается (SKIP: Margin Limit).
//   5.7-TEST — защита маржи надёжнее: в 5.6 залог считался только оценкой брокера (GetEstimatedMargin),
//         а если она недоступна (NaN или 0), проверка молча пропускалась и сделка шла полным объёмом.
//         Теперь берётся САМАЯ БОЛЬШАЯ из трёх оценок: брокера, собственного расчёта (стоимость позиции в валюте
//         счёта / плечо инструмента) и фактического залога уже открытых позиций по инструменту. Если
//         оценить залог нельзя — вход пропускается. В журнале ENTRY — залог сделки и свободная маржа,
//         после входа — фактический залог; при старте каждый поток пишет залог на минимальный лот.
//   5.6-TEST — защита маржи: объём сделки уменьшается так, чтобы залог (оценка брокера через
//         Symbol.GetEstimatedMargin) не превышал «Макс. маржа на сделку» от свободной маржи (25%). Без
//         этого крипта при плече 1:2 забирала 40% счёта одной сделкой, и следующие входы брокер отклонял.
//   5.5-TEST — инструменты по умолчанию: XAUUSD, BTCUSD, ETHUSD к инструменту графика (EURUSD) — 8 потоков
//         на m5 и m15. Крипта торгуется и в выходные в том же экземпляре; форекс и золото в это время
//         ждут открытия рынка. Новый параметр «Макс. позиций всего» (4): при 8 потоках без него разом
//         могли бы открыться 8 сделок, а EURUSD m5/m15 и BTC/ETH часто движутся вместе.
//   5.4-TEST — несколько потоков в ОДНОМ экземпляре: облако cTrader на демо запускает только один
//         экземпляр, поэтому M5 и M15 (и при желании другие инструменты) теперь ведутся внутри одного
//         cBot. У каждого потока свои индикаторы, AI, память, позиции и метка ордеров; дневной лимит
//         убытка и просадка — общие для счёта. Параметры «Доп. таймфреймы» (по умолчанию m15) и
//         «Доп. инструменты».
//   5.3-TEST — тестовая сборка для демо: фильтры ослаблены ПО УМОЛЧАНИЮ, чтобы сделки открывались часто
//         и можно было проверить механику (вход, частичное закрытие, безубыток, трейлинг). Требование
//         EV > 0 выключено, пороги 0.05 / 0.10, адаптивный порог выключен, пауза 0, спред до 50% ATR.
//         Своя память AI (ключ QAIv5T) и своя метка ордеров (QAIv5T) — тест не портит рабочую версию.
//         Прибыльность в этом режиме НЕ цель: бот входит и туда, где сам оценивает сделку как убыточную.
//   5.2 — каждый экземпляр видит только свои позиции: к метке ордеров автоматически добавляется
//         таймфрейм (QAIv5 m5, QAIv5 h1). Раньше два экземпляра на одном инструменте с разными
//         таймфреймами считали позиции друг друга своими.
//   5.1 — исправлен ключ памяти AI в LocalStorage (cTrader допускает только латиницу, цифры и пробелы;
//         прежний ключ с символом '|' отвергался, и память не читалась и не сохранялась).
//         Номер версии теперь в имени файла, в названии бота и в журнале.
//   5.0 — первая версия: ансамбль из 5 стратегий, Naive Bayes, RL-веса, фильтр режима.
//
//  Один файл. Работает на любом инструменте графика: BTCUSD, ETHUSD, XAUUSD, EURUSD и т. д.
//  Все расстояния (стоп, цель, трейлинг, допустимый спред) выражены в ATR(14) — поэтому один и тот же
//  набор настроек применим к инструментам с совершенно разной ценой и волатильностью.
//
//  СОСТАВ
//   1. Ансамбль из 5 подстратегий: TrendFollowing (EMA 50/200), Breakout (Donchian 20 + Keltner),
//      MeanReversion (RSI + Bollinger), VolatilityExpansion (всплеск ATR), LiquidityGrab (снятие
//      ликвидности с микро-экстремумов). Сигнал — взвешенная сумма голосов с учётом AI-весов.
//   2. AI-модуль:
//      • онлайн Gaussian Naive Bayes по вектору [ATR_Ratio, RSI_Slope, ADX, Spread_Cost, Vol_Percentile]
//        оценивает вероятность того, что сделка закроется в плюс после спреда и комиссии;
//      • обучение с подкреплением: каждая закрытая сделка (Win/Loss, в R) меняет веса подстратегий,
//        голосовавших за неё; веса ограничены [0.25 … 3.0] — ни одна стратегия не может ни исчезнуть,
//        ни захватить ансамбль;
//      • вероятностный фильтр режима: P(тренд) и P(возврат к среднему) — эвристика по ADX и Efficiency
//        Ratio, дообучаемая онлайн-логистической регрессией на исходах рынка;
//      • итоговый Signal Confidence 0.00–1.00 и адаптивный порог входа (тренд / флэт).
//   3. Холодный старт: при запуске модель обучается на загруженной истории графика через виртуальные
//      (теневые) сделки — по тем же правилам сопровождения, со спредом и комиссией. Далее теневые сделки
//      продолжаются онлайн, поэтому модель учится и тогда, когда реальных сделок нет.
//   3a. Доказательность (5.9): у каждой стратегии свой теневой счёт; в реальную сделку идут только голоса
//      стратегий, чьё ожидание после издержек положительно с запасом надёжности и в обеих половинах периода.
//   4. Риск: лот от % Equity/Balance и ATR-стопа, жёсткий потолок риска, дневной лимит убытка,
//      R:R не ниже заданного минимума, EV-фильтр (спред не более 15% потенциальной прибыли),
//      блокировка межсессионного окна 21:50–22:20 UTC.
//   5. Сопровождение: закрытие 50% объёма на +1.0R, перенос стопа в безубыток + 0.1R, ATR-трейлинг.
//   6. Журнал: причина каждого пропуска (SKIP), режим рынка, просадка, состояние AI.
//
//  ЧЕГО ЗДЕСЬ НЕТ И НЕ БУДЕТ: мартингейла, усреднения, увеличения лота после убытка. Размер позиции
//  может только уменьшаться относительно заданного риска (кроме округления до минимального лота брокера,
//  которое разрешено лишь в пределах «Потолка риска»).
//
//  ВАЖНО: ни один алгоритм не гарантирует прибыль. Модель учится на прошлом; рынок может измениться.
//  Сначала — демо-счёт.
// =====================================================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    /// <summary>От чего считать риск на сделку.</summary>
    public enum RiskBaseMode
    {
        Equity,
        Balance,
    }

    /// <summary>Фильтр ложных пробоев (снятие ликвидности). Auto — включается для золота (XAU*).</summary>
    public enum SweepProtectionMode
    {
        Auto,
        On,
        Off,
    }

    /// <summary>Какие пропуски печатать: значимые — был сигнал, но его отклонили; все; никакие.</summary>
    public enum SkipLogMode
    {
        Important,
        All,
        None,
    }

    /// <summary>Какие позиции закрывать перед выходными и праздниками.</summary>
    public enum WeekendCloseScope
    {
        BotPositions,
        AllPositions,
    }

    /// <summary>Уровень доверия к стратегии по её теневому счёту.</summary>
    internal enum StrategyTier
    {
        Proven,
        Probation,
        Blocked,
    }

    // =================================================================================================
    //  ВСПОМОГАТЕЛЬНЫЕ ТИПЫ
    // =================================================================================================

    /// <summary>Причины отказа от входа — для журнала и сводки.</summary>
    internal enum SkipReason
    {
        None = 0,
        Warmup,
        NoSignal,
        UnclearRegime,
        LowConfidence,
        NegativeEV,
        HighSpread,
        CostVsProfit,
        SessionBlock,
        DailyLossLimit,
        PositionOpen,
        Cooldown,
        SweepBlock,
        AccountTooSmall,
        MarginLimit,
        MarketClosed,
        OrderFailed,
        NoEdge,
        OffHours,
        Correlated,
        RiskBudget,
    }

    /// <summary>Голос одной подстратегии.</summary>
    internal struct StrategyVote
    {
        public int Direction;   // +1 покупка, -1 продажа, 0 нет мнения
        public double Strength; // 0..1
    }

    /// <summary>Всё, что известно о закрытом баре после анализа.</summary>
    internal sealed class BarAnalysis
    {
        public int Index;
        public double Close;
        public double Atr;
        public double AtrLongAverage;
        public double Adx;
        public double Rsi;
        public double RsiSlope;          // пункты RSI за бар, без знака направления
        public double VolPercentile;     // 0..1
        public double EfficiencyRatio;   // 0..1
        public double TrendSlopeSign;    // знак наклона EMA20
        public double PTrend;            // вероятность трендового режима
        public double RegimeConfidence;  // |2·PTrend − 1|
        public readonly StrategyVote[] Votes = new StrategyVote[QuantAI_Universal_V6_3.StrategyCount];
        public int EnsembleDirection;
        public double EnsembleScore;     // 0..1
        public int AgreeingCount;
        public bool BullishSweepRecent;  // снятие ликвидности снизу (ловушка для продавцов)
        public bool BearishSweepRecent;  // снятие ликвидности сверху (ловушка для покупателей)
    }

    /// <summary>
    /// Взвешенная онлайн-статистика признака (алгоритм Уэста): среднее и дисперсия без хранения выборки.
    /// </summary>
    internal sealed class GaussianStat
    {
        public double W;
        public double Mean;
        public double M2;

        public void Add(double x, double weight)
        {
            if (weight <= 0 || double.IsNaN(x) || double.IsInfinity(x)) return;
            double newW = W + weight;
            double delta = x - Mean;
            double r = delta * weight / newW;
            Mean += r;
            M2 += W * delta * r;
            W = newW;
        }

        public double Variance => W > 0 ? Math.Max(0, M2 / W) : 0;

        /// <summary>Объединяет две статистики (для совмещения реальных и теневых наблюдений).</summary>
        public static void Merge(GaussianStat a, GaussianStat b, out double w, out double mean, out double variance)
        {
            w = a.W + b.W;
            if (w <= 0)
            {
                mean = 0;
                variance = 0;
                return;
            }

            mean = ((a.W * a.Mean) + (b.W * b.Mean)) / w;
            double delta = a.Mean - b.Mean;
            double m2 = a.M2 + b.M2 + (a.W * b.W / w * delta * delta);
            variance = Math.Max(0, m2 / w);
        }
    }

    /// <summary>
    /// Онлайн Gaussian Naive Bayes: класс 1 — сделка закрылась в плюс после спреда и комиссии, класс 0 — нет.
    /// </summary>
    internal sealed class NaiveBayesModel
    {
        public const int FeatureCount = 5;

        public readonly GaussianStat[,] Stats = new GaussianStat[2, FeatureCount];
        public readonly double[] ClassWeight = new double[2];

        public NaiveBayesModel()
        {
            for (int c = 0; c < 2; c++)
            {
                for (int f = 0; f < FeatureCount; f++) Stats[c, f] = new GaussianStat();
            }
        }

        public double TotalWeight => ClassWeight[0] + ClassWeight[1];

        public void Add(double[] x, bool win, double weight)
        {
            if (x == null || x.Length != FeatureCount || weight <= 0) return;
            int c = win ? 1 : 0;
            ClassWeight[c] += weight;
            for (int f = 0; f < FeatureCount; f++) Stats[c, f].Add(x[f], weight);
        }

        public void Clear()
        {
            ClassWeight[0] = 0;
            ClassWeight[1] = 0;
            for (int c = 0; c < 2; c++)
            {
                for (int f = 0; f < FeatureCount; f++) Stats[c, f] = new GaussianStat();
            }
        }

        /// <summary>
        /// Апостериорная вероятность выигрыша по объединённым реальным и теневым наблюдениям.
        /// Пока данных мало, ответ сжимается к нейтральным 0.5: модель без доказательств не имеет права
        /// ни разгонять, ни душить вход.
        /// </summary>
        public static double PredictCombined(NaiveBayesModel real, NaiveBayesModel shadow, double[] x, double[] varianceFloor, double trustSamples)
        {
            double totalW = real.TotalWeight + shadow.TotalWeight;
            if (x == null || totalW <= 0) return 0.5;

            double[] logp = new double[2];
            for (int c = 0; c < 2; c++)
            {
                double classW = real.ClassWeight[c] + shadow.ClassWeight[c];
                logp[c] = Math.Log((classW + 1.0) / (totalW + 2.0));

                for (int f = 0; f < FeatureCount; f++)
                {
                    GaussianStat.Merge(real.Stats[c, f], shadow.Stats[c, f], out double w, out double mean, out double variance);
                    if (w < 3) continue; // признак без данных не голосует

                    double v = Math.Max(variance, varianceFloor[f]);
                    double d = x[f] - mean;
                    logp[c] += (-0.5 * Math.Log(2.0 * Math.PI * v)) - (d * d / (2.0 * v));
                }
            }

            double diff = logp[0] - logp[1];
            double raw = diff > 50 ? 0.0 : diff < -50 ? 1.0 : 1.0 / (1.0 + Math.Exp(diff));

            // Naive Bayes известен самоуверенностью: доверие растёт с выборкой, крайние значения срезаны.
            double trust = totalW / (totalW + Math.Max(1.0, trustSamples));
            double p = 0.5 + ((raw - 0.5) * trust);
            return Math.Max(0.10, Math.Min(0.90, p));
        }
    }

    /// <summary>Онлайн-логистическая регрессия (SGD с L2) — дообучение вероятности трендового режима.</summary>
    internal sealed class OnlineLogistic
    {
        private readonly double[] _w;

        public OnlineLogistic(int inputs)
        {
            _w = new double[inputs];
        }

        public int Updates { get; private set; }

        public double Predict(double[] x)
        {
            double z = 0;
            for (int k = 0; k < _w.Length && k < x.Length; k++) z += _w[k] * x[k];
            if (z > 30) return 1.0;
            if (z < -30) return 0.0;
            return 1.0 / (1.0 + Math.Exp(-z));
        }

        public void Update(double[] x, double y, double learningRate, double l2)
        {
            double error = y - Predict(x);
            for (int k = 0; k < _w.Length && k < x.Length; k++)
            {
                _w[k] += learningRate * ((error * x[k]) - (l2 * _w[k]));
            }
            Updates++;
        }
    }

    /// <summary>Правила сопровождения — одни и те же для реальных позиций и теневых сделок.</summary>
    internal struct ExitRules
    {
        public bool UsePartial;
        public double PartialFraction;   // доля объёма, закрываемая на уровне безубытка
        public double TriggerR;          // уровень частичного закрытия и переноса в безубыток, R
        public double BreakevenOffsetR;  // куда переносится стоп, R от входа
        public double TrailAtr;          // трейлинг: ATR от закрытия бара
        public int MaxHoldBars;          // теневая сделка закрывается по времени
    }

    /// <summary>
    /// Виртуальная (теневая) сделка. Ведётся ТАК ЖЕ, как реальная: стоп, частичное закрытие и безубыток
    /// на +TriggerR, ATR-трейлинг по закрытию бара, тейк; спред — в ценах входа и выхода, комиссия — в CostR.
    /// Порядок цен внутри бара неизвестен: если бар задел и стоп, и цель, считается стоп. После касания
    /// уровня безубытка путь бара принимается стандартным: бычий бар — открытие→минимум→максимум→закрытие,
    /// медвежий — открытие→максимум→минимум→закрытие.
    /// </summary>
    internal sealed class ShadowTrade
    {
        public int OpenIndex;
        public int Direction;
        public int Strategy = -1;     // -1 — сделка ансамбля; 0..4 — сделка одной стратегии
        public double Entry;
        public double RiskDistance;   // 1R в цене
        public double Stop;
        public double TriggerLevel;
        public double Target;
        public double CostR;          // комиссия туда-обратно в R
        public double Remaining = 1.0;
        public double RealizedR;
        public bool Protected;
        public double[] Features;
        public double[] VoteStrengths; // сила голоса каждой стратегии в направлении сделки (0, если не голосовала)

        /// <summary>Итог в R без комиссии (спред уже учтён в ценах).</summary>
        public double GrossR { get; private set; }

        /// <summary>Итог в R после комиссии.</summary>
        public double NetR => GrossR - CostR;

        /// <summary>Один закрытый бар (цены Bid). Покупка закрывается по Bid, продажа — по Ask = Bid + спред. true — сделка закрыта.</summary>
        public bool Step(double open, double high, double low, double close, double spread, double atr, int barsHeld, ExitRules rules)
        {
            int d = Direction;
            double worst = d > 0 ? low : high + spread;
            double best = d > 0 ? high : low + spread;

            // 1. Стоп — первым. При гэпе за стоп исполнение хуже стопа — по цене открытия.
            if ((worst - Stop) * d <= 0)
            {
                double openExit = d > 0 ? open : open + spread;
                return Close((openExit - Stop) * d < 0 ? openExit : Stop);
            }

            // 2. Частичное закрытие и перенос стопа в безубыток.
            bool triggeredNow = false;
            if (!Protected && (best - TriggerLevel) * d >= 0)
            {
                if (rules.UsePartial && rules.PartialFraction > 0 && rules.PartialFraction < 1)
                {
                    RealizedR += rules.PartialFraction * rules.TriggerR;
                    Remaining -= rules.PartialFraction;
                }

                double breakeven = Entry + (d * rules.BreakevenOffsetR * RiskDistance);
                if ((breakeven - Stop) * d > 0) Stop = breakeven;
                Protected = true;
                triggeredNow = true;
            }

            // 3. Тейк: на пути к экстремуму бара он достигается раньше любого последующего отката.
            if ((best - Target) * d >= 0) return Close(Target);

            // 4. Остаток бара после касания уровня безубытка — мог задеть новый стоп.
            if (triggeredNow)
            {
                bool bullish = close >= open;
                double after = d > 0 ? (bullish ? close : low) : (bullish ? high + spread : close + spread);
                if ((after - Stop) * d <= 0) return Close(Stop);
            }

            // 5. Выход по времени.
            double exitNow = d > 0 ? close : close + spread;
            if (barsHeld >= rules.MaxHoldBars) return Close(exitNow);

            // 6. Трейлинг по закрытию бара — как у реальной позиции: только подтягивается и не ниже безубытка.
            if (Protected && atr > 0)
            {
                double breakeven = Entry + (d * rules.BreakevenOffsetR * RiskDistance);
                double candidate = close - (d * rules.TrailAtr * atr);
                candidate = d > 0 ? Math.Max(candidate, breakeven) : Math.Min(candidate, breakeven);
                if ((candidate - Stop) * d > 0 && (exitNow - candidate) * d > 0) Stop = candidate;
            }

            return false;
        }

        private bool Close(double exitPrice)
        {
            RealizedR += Remaining * (exitPrice - Entry) * Direction / RiskDistance;
            Remaining = 0;
            GrossR = RealizedR;
            return true;
        }
    }

    /// <summary>Сводка доказательств по стратегии: ожидание в R и его надёжность.</summary>
    internal struct EdgeStats
    {
        public int Count;
        public double Mean;        // среднее R на сделку после спреда и комиссии
        public double StdError;    // стандартная ошибка среднего
        public double LowerBound;  // Mean − σ·StdError
        public double UpperBound;  // Mean + σ·StdError
        public double OlderMean;   // среднее в старшей половине периода
        public double NewerMean;   // среднее в новой половине периода

        /// <summary>Плюс доказан: хватает сделок, нижняя граница > 0 и обе половины периода в плюсе.</summary>
        public bool Proven(int minTrades) => Count >= minTrades && LowerBound > 0 && OlderMean > 0 && NewerMean > 0;

        /// <summary>Минус доказан: хватает сделок и даже верхняя граница ожидания ниже нуля.</summary>
        public bool ProvenLoss(int minTrades) => Count >= minTrades && UpperBound < 0;
    }

    /// <summary>Скользящее окно последних исходов стратегии (в R) — без хранения лишней истории.</summary>
    internal sealed class EdgeTracker
    {
        private readonly double[] _values;
        private int _count;
        private int _next;

        public EdgeTracker(int capacity)
        {
            _values = new double[Math.Max(2, capacity)];
        }

        public int Count => _count;

        public void Add(double r)
        {
            if (double.IsNaN(r) || double.IsInfinity(r)) return;
            _values[_next] = r;
            _next = (_next + 1) % _values.Length;
            if (_count < _values.Length) _count++;
        }

        /// <summary>k-й по порядку поступления исход в окне: 0 — самый старый.</summary>
        private double At(int k) => _values[(((_next - _count + k) % _values.Length) + _values.Length) % _values.Length];

        public EdgeStats Compute(double sigma)
        {
            var st = new EdgeStats { Count = _count };
            if (_count == 0) return st;

            int half = _count / 2;
            double sum = 0;
            double sumSq = 0;
            double older = 0;
            double newer = 0;
            for (int k = 0; k < _count; k++)
            {
                double v = At(k);
                sum += v;
                sumSq += v * v;
                if (k < half) older += v;
                else newer += v;
            }

            st.Mean = sum / _count;
            double variance = _count > 1 ? Math.Max(0, (sumSq - (_count * st.Mean * st.Mean)) / (_count - 1)) : 0;
            st.StdError = Math.Sqrt(variance / _count);
            st.LowerBound = st.Mean - (sigma * st.StdError);
            st.UpperBound = st.Mean + (sigma * st.StdError);
            st.OlderMean = half > 0 ? older / half : 0;
            st.NewerMean = _count - half > 0 ? newer / (_count - half) : 0;
            return st;
        }
    }

    /// <summary>Отложенная метка режима: исход становится известен через горизонт баров.</summary>
    internal sealed class RegimeSample
    {
        public int Index;
        public double[] Input;
        public int Direction;
        public double Close;
        public double Atr;
    }

    /// <summary>Сведения о реальной позиции, нужные для сопровождения и обучения.</summary>
    internal sealed class TradeMeta
    {
        public int Direction;
        public double EntryPrice;
        public double InitialRiskDistance; // 1R в цене
        public double RiskMoney;
        public bool PartialDone;
        public bool BreakevenDone;
        public double RealizedPartialProfit;
        public double[] Features;
        public double[] VoteStrengths;
        public int OpenBarIndex;
    }

    // =================================================================================================
    //  РОБОТ
    // =================================================================================================

    [Robot(AccessRights = AccessRights.None, TimeZone = TimeZones.UTC, AddIndicators = true)]
    public class QuantAI_Universal_V6_3 : Robot
    {
        /// <summary>Версия бота — печатается в журнале при запуске. Меняется с каждой выпущенной версией.</summary>
        public const string BotVersion = "6.3";

        public const int StrategyCount = 5;

        internal static readonly string[] StrategyNames =
        {
            "TrendFollowing", "Breakout", "MeanReversion", "VolExpansion", "LiquidityGrab",
        };

        // Какие стратегии относятся к трендовому режиму (остальные — к режиму возврата к среднему).
        internal static readonly bool[] IsTrendStrategy = { true, true, false, true, false };

        /// <summary>Больше потоков облако не вытянет: каждый — это свой ряд баров и 7 индикаторов.</summary>
        private const int MaxStreams = 24;

        /// <summary>Метка ордеров по умолчанию; метки прошлых версий, чьи позиции подхватываются; префикс памяти AI.</summary>
        internal const string DefaultLabel = "QAIv6";
        internal static readonly string[] LegacyLabels = { "QAIv5", "QAIv5T" };
        internal const string MemoryPrefix = "QAIv5";

        // ----- Параметры: потоки ------------------------------------------------------------------

        [Parameter("Доп. таймфреймы", Group = "Потоки", DefaultValue = "h1")]
        public string ExtraTimeFrames { get; set; }

        [Parameter("Доп. инструменты", Group = "Потоки", DefaultValue = "GBPUSD, USDJPY, XAUUSD, US500|SPX500, NAS100|USTEC, GER40|GER30|DE40, BTCUSD, ETHUSD, LTCUSD, XRPUSD")]
        public string ExtraSymbols { get; set; }

        [Parameter("Макс. позиций всего", Group = "Потоки", DefaultValue = 8, MinValue = 1, MaxValue = 30)]
        public int MaxTotalPositions { get; set; }

        [Parameter("Макс. позиций на инструмент", Group = "Потоки", DefaultValue = 1, MinValue = 1, MaxValue = 10)]
        public int MaxPositionsPerSymbol { get; set; }

        [Parameter("Группы связанных инструментов", Group = "Потоки", DefaultValue = "EURUSD GBPUSD; US500 SPX500 NAS100 USTEC US30; BTCUSD ETHUSD LTCUSD XRPUSD")]
        public string CorrelationGroups { get; set; }

        [Parameter("Макс. позиций в группе в одну сторону", Group = "Потоки", DefaultValue = 1, MinValue = 1, MaxValue = 10)]
        public int MaxGroupSameDirection { get; set; }

        // ----- Параметры: доказательность ----------------------------------------------------------

        [Parameter("Риск пробной сделки, % от обычного", Group = "Доказательность", DefaultValue = 25.0, MinValue = 0.0, MaxValue = 100.0, Step = 5.0)]
        public double ProbationRiskPercent { get; set; }

        [Parameter("Блокировать доказанно убыточные", Group = "Доказательность", DefaultValue = true)]
        public bool BlockProvenLosers { get; set; }

        [Parameter("Мин. теневых сделок стратегии", Group = "Доказательность", DefaultValue = 40, MinValue = 10, MaxValue = 300)]
        public int EvidenceMinTrades { get; set; }

        [Parameter("Запас надёжности, σ", Group = "Доказательность", DefaultValue = 1.5, MinValue = 0.0, MaxValue = 4.0, Step = 0.1)]
        public double EvidenceSigma { get; set; }

        // ----- Параметры: AI и пороги --------------------------------------------------------------

        [Parameter("Мин. уверенность режима", Group = "AI и пороги", DefaultValue = 0.30, MinValue = 0.0, MaxValue = 1.0, Step = 0.01)]
        public double MinRegimeConfidence { get; set; }

        [Parameter("Мин. уверенность ансамбля", Group = "AI и пороги", DefaultValue = 0.30, MinValue = 0.0, MaxValue = 1.0, Step = 0.01)]
        public double MinEnsembleConfidence { get; set; }

        [Parameter("Адаптивный порог AI", Group = "AI и пороги", DefaultValue = true)]
        public bool AdaptiveThreshold { get; set; }

        [Parameter("ADX сильного тренда", Group = "AI и пороги", DefaultValue = 25.0, MinValue = 10.0, MaxValue = 60.0, Step = 1.0)]
        public double TrendAdxLevel { get; set; }

        [Parameter("Порог в тренде", Group = "AI и пороги", DefaultValue = 0.28, MinValue = 0.0, MaxValue = 1.0, Step = 0.01)]
        public double TrendThreshold { get; set; }

        [Parameter("ADX флэта (ниже)", Group = "AI и пороги", DefaultValue = 20.0, MinValue = 5.0, MaxValue = 40.0, Step = 1.0)]
        public double FlatAdxLevel { get; set; }

        [Parameter("Порог во флэте", Group = "AI и пороги", DefaultValue = 0.40, MinValue = 0.0, MaxValue = 1.0, Step = 0.01)]
        public double FlatThreshold { get; set; }

        [Parameter("Требовать EV > 0", Group = "AI и пороги", DefaultValue = true)]
        public bool RequirePositiveEv { get; set; }

        [Parameter("Скорость RL-обучения", Group = "AI и пороги", DefaultValue = 0.10, MinValue = 0.0, MaxValue = 0.5, Step = 0.01)]
        public double RlLearningRate { get; set; }

        [Parameter("Обучение на истории (баров)", Group = "AI и пороги", DefaultValue = 5000, MinValue = 400, MaxValue = 20000, Step = 100)]
        public int HistoryBars { get; set; }

        [Parameter("Сбросить память AI", Group = "AI и пороги", DefaultValue = false)]
        public bool ResetAiMemory { get; set; }

        // ----- Параметры: риск ---------------------------------------------------------------------

        [Parameter("Риск на сделку, %", Group = "Риск", DefaultValue = 0.5, MinValue = 0.01, MaxValue = 5.0, Step = 0.05)]
        public double RiskPercent { get; set; }

        [Parameter("База риска", Group = "Риск", DefaultValue = RiskBaseMode.Equity)]
        public RiskBaseMode RiskBase { get; set; }

        [Parameter("Потолок риска (мин. лот), %", Group = "Риск", DefaultValue = 1.5, MinValue = 0.05, MaxValue = 10.0, Step = 0.05)]
        public double MaxRiskCapPercent { get; set; }

        [Parameter("Макс. маржа на сделку, % свободной", Group = "Риск", DefaultValue = 25.0, MinValue = 1.0, MaxValue = 100.0, Step = 1.0)]
        public double MaxMarginPercent { get; set; }

        [Parameter("Дневной лимит убытка, %", Group = "Риск", DefaultValue = 3.0, MinValue = 0.1, MaxValue = 50.0, Step = 0.1)]
        public double DailyLossLimitPercent { get; set; }

        [Parameter("Макс. позиций", Group = "Риск", DefaultValue = 1, MinValue = 1, MaxValue = 10)]
        public int MaxOpenPositions { get; set; }

        [Parameter("Пауза после сделки, баров", Group = "Риск", DefaultValue = 2, MinValue = 0, MaxValue = 100)]
        public int CooldownBars { get; set; }

        [Parameter("Мин. комиссия за лот туда-обратно", Group = "Риск", DefaultValue = 0.0, MinValue = 0.0, MaxValue = 1000.0, Step = 0.1)]
        public double MinCommissionPerLot { get; set; }

        [Parameter("Макс. суммарный риск открытых, %", Group = "Риск", DefaultValue = 2.0, MinValue = 0.1, MaxValue = 20.0, Step = 0.1)]
        public double MaxOpenRiskPercent { get; set; }

        // ----- Параметры: выходы -------------------------------------------------------------------

        [Parameter("Стоп, ATR", Group = "Выходы", DefaultValue = 1.5, MinValue = 0.3, MaxValue = 10.0, Step = 0.1)]
        public double SlAtrMultiplier { get; set; }

        [Parameter("Тейк, ATR", Group = "Выходы", DefaultValue = 3.0, MinValue = 0.3, MaxValue = 30.0, Step = 0.1)]
        public double TpAtrMultiplier { get; set; }

        [Parameter("Мин. Risk/Reward", Group = "Выходы", DefaultValue = 1.5, MinValue = 1.0, MaxValue = 10.0, Step = 0.1)]
        public double MinRewardRisk { get; set; }

        [Parameter("Безубыток при, R", Group = "Выходы", DefaultValue = 1.0, MinValue = 0.2, MaxValue = 5.0, Step = 0.1)]
        public double BreakevenTriggerR { get; set; }

        [Parameter("Смещение безубытка, R", Group = "Выходы", DefaultValue = 0.1, MinValue = 0.0, MaxValue = 1.0, Step = 0.05)]
        public double BreakevenOffsetR { get; set; }

        [Parameter("Частичное закрытие", Group = "Выходы", DefaultValue = true)]
        public bool UsePartialClose { get; set; }

        [Parameter("Доля частичного закрытия, %", Group = "Выходы", DefaultValue = 50.0, MinValue = 10.0, MaxValue = 90.0, Step = 5.0)]
        public double PartialClosePercent { get; set; }

        [Parameter("Трейлинг, ATR", Group = "Выходы", DefaultValue = 1.5, MinValue = 0.3, MaxValue = 10.0, Step = 0.1)]
        public double TrailAtrMultiplier { get; set; }

        // ----- Параметры: фильтры ------------------------------------------------------------------

        [Parameter("Макс. спред, доля ATR", Group = "Фильтры", DefaultValue = 0.30, MinValue = 0.01, MaxValue = 2.0, Step = 0.01)]
        public double MaxSpreadAtr { get; set; }

        [Parameter("Макс. спред, доля прибыли", Group = "Фильтры", DefaultValue = 0.15, MinValue = 0.01, MaxValue = 1.0, Step = 0.01)]
        public double MaxSpreadCostOfProfit { get; set; }

        [Parameter("Блок сессии с (UTC или «авто»)", Group = "Фильтры", DefaultValue = "авто")]
        public string SessionBlockStart { get; set; }

        [Parameter("Блок сессии до (UTC или «авто»)", Group = "Фильтры", DefaultValue = "авто")]
        public string SessionBlockEnd { get; set; }

        [Parameter("Защита от ложных пробоев", Group = "Фильтры", DefaultValue = SweepProtectionMode.Auto)]
        public SweepProtectionMode SweepProtection { get; set; }

        [Parameter("Торговые часы не-крипто с (UTC)", Group = "Фильтры", DefaultValue = "00:00")]
        public string TradeHoursStart { get; set; }

        [Parameter("Торговые часы не-крипто до (UTC)", Group = "Фильтры", DefaultValue = "00:00")]
        public string TradeHoursEnd { get; set; }

        [Parameter("Закрывать перед выходными, мин до закрытия", Group = "Фильтры", DefaultValue = 15, MinValue = 0, MaxValue = 240)]
        public int WeekendCloseMinutes { get; set; }

        [Parameter("Закрывать перед выходными", Group = "Фильтры", DefaultValue = WeekendCloseScope.BotPositions)]
        public WeekendCloseScope WeekendCloseWhat { get; set; }

        // ----- Параметры: журнал -------------------------------------------------------------------

        [Parameter("Журнал пропусков", Group = "Журнал", DefaultValue = SkipLogMode.Important)]
        public SkipLogMode SkipLog { get; set; }

        [Parameter("Сводка каждые N баров", Group = "Журнал", DefaultValue = 12, MinValue = 1, MaxValue = 1000)]
        public int StatusEveryBars { get; set; }

        [Parameter("Метка ордеров", Group = "Журнал", DefaultValue = "QAIv6")]
        public string OrderLabel { get; set; }

        // ----- Общее для всех потоков: счёт целиком ------------------------------------------------

        private readonly List<TradingStream> _streams = new List<TradingStream>();

        private DateTime _dayKey = DateTime.MinValue;
        private double _dayStartEquity;
        private bool _dailyLocked;
        private double _peakEquity;

        private TimeSpan _blockStart;
        private TimeSpan _blockEnd;
        private bool _sessionBlockEnabled;

        private TimeSpan _hoursStart;
        private TimeSpan _hoursEnd;
        private bool _tradeHoursEnabled;
        private bool _sessionBlockAuto;
        private double _effectiveTpAtr;

        /// <summary>Группы связанных инструментов (имена в верхнем регистре).</summary>
        private readonly List<HashSet<string>> _groups = new List<HashSet<string>>();

        internal bool DailyLocked => _dailyLocked;
        internal double EffectiveTpAtr => _effectiveTpAtr;

        // =============================================================================================
        //  ЖИЗНЕННЫЙ ЦИКЛ
        // =============================================================================================

        protected override void OnStart()
        {
            ValidateGlobalSettings();
            Print("QuantAI_Universal " + BotVersion + " — торговля по всем инструментам с уровнями доверия: стратегия с доказанным " +
                  "плюсом торгуется полным риском, ещё не доказанная — пробным (" + F(ProbationRiskPercent) + "% обычного), " +
                  (BlockProvenLosers ? "доказанно убыточная не торгуется. " : "доказанно убыточные НЕ блокируются. ") +
                  "Доказательства — теневые сделки по правилам реальных, со спредом и комиссией. Прибыль не гарантируется: " +
                  "доказательства берутся из прошлого, рынок может измениться.");

            _peakEquity = Account.Equity;
            RollDay(Server.TimeInUtc);

            BuildStreams();
            foreach (TradingStream s in _streams) s.Start();

            bool anyCrypto = false;
            foreach (TradingStream s in _streams) if (IsCrypto(s.SymbolName)) anyCrypto = true;
            if (!anyCrypto)
            {
                Print("ВНИМАНИЕ: нет ни одного потока по криптовалюте — в выходные торговать будет нечем. Добавьте BTCUSD/ETHUSD в «Доп. инструменты».");
            }

            Timer.Start(TimeSpan.FromSeconds(20));

            Positions.Closed += OnPositionClosed;

            var tiers = new StringBuilder();
            foreach (TradingStream s in _streams)
            {
                if (tiers.Length > 0) tiers.Append(", ");
                tiers.Append(s.Tag).Append(' ').Append(s.TierSummary());
            }
            Print("Уровни стратегий по потокам (✓ доказан / ? пробный / ✗ убыточен): " + tiers + ".");

            var names = new StringBuilder();
            foreach (TradingStream s in _streams)
            {
                if (names.Length > 0) names.Append(", ");
                names.Append(s.Tag);
            }
            Print("QuantAI_Universal версия " + BotVersion + " запущена. Потоков: " + _streams.Count + " — " + names +
                  ". Все работают внутри ОДНОГО экземпляра cBot. Одновременно открыто не больше " + MaxTotalPositions +
                  " позиций на весь счёт, " + MaxPositionsPerSymbol + " на инструмент и " + MaxGroupSameDirection +
                  " в одну сторону в группе связанных; суммарный риск открытых ≤ " + F(MaxOpenRiskPercent) + "%, залог одной сделки ≤ " +
                  F(MaxMarginPercent) + "% свободной маржи. Форекс, металлы и индексы — " +
                  (_tradeHoursEnabled ? TradeHoursStart + "–" + TradeHoursEnd + " UTC" : "круглосуточно, пока открыт их рынок (24/5)") +
                  (WeekendCloseMinutes > 0 ? ", " + (WeekendCloseWhat == WeekendCloseScope.AllPositions ? "ВСЕ позиции счёта" : "все позиции бота любой версии") +
                      " по ним закрываются за " + WeekendCloseMinutes + " мин до закрытия рынка на выходные/праздник (страховка — пятница 20:45 UTC)" : "") +
                  "; крипта — 24/7, включая выходные.");
        }

        protected override void OnTimer()
        {
            CloseBeforeLongClosures();
        }

        /// <summary>Страховка на случай неполного расписания у брокера: в пятницу с этого времени (UTC) закрывается всё, кроме крипты.</summary>
        private static readonly TimeSpan FridayFallbackClose = new TimeSpan(20, 45, 0);

        private readonly Dictionary<int, DateTime> _closeAttempts = new Dictionary<int, DateTime>();

        /// <summary>
        /// Рынок инструмента закрывается в пределах window и потом закрыт не меньше 6 часов (выходные, праздник) —
        /// по расписанию брокера; либо пятница после 20:45 UTC для всего, кроме крипты. Короткие ежедневные перерывы
        /// сюда не попадают; рынок, который не закрывается (крипта), — тоже.
        /// </summary>
        internal bool LongClosureWithin(Symbol symbol, string symbolName, TimeSpan window)
        {
            if (symbol == null || window <= TimeSpan.Zero) return false;
            DateTime utc = Server.TimeInUtc;
            if (!IsCrypto(symbolName) && utc.DayOfWeek == DayOfWeek.Friday && utc.TimeOfDay >= FridayFallbackClose - (window - TimeSpan.FromMinutes(WeekendCloseMinutes)))
            {
                return true;
            }

            try
            {
                MarketHours hours = symbol.MarketHours;
                if (!hours.IsOpened()) return false;
                TimeSpan left = hours.TimeTillClose();
                if (left <= TimeSpan.Zero || left > window) return false;
                DateTime closeAt = Server.Time + left;
                return !hours.IsOpened(closeAt.AddHours(6));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Перед выходными и праздниками закрываются позиции по рынкам, которые не работают в это время: все позиции
        /// бота любой версии и таймфрейма (или все позиции счёта — по параметру). Крипта не закрывается.
        /// </summary>
        private void CloseBeforeLongClosures()
        {
            if (WeekendCloseMinutes <= 0) return;
            TimeSpan window = TimeSpan.FromMinutes(WeekendCloseMinutes);
            DateTime now = Server.TimeInUtc;

            var candidates = new List<Position>();
            foreach (Position p in Positions)
            {
                if (WeekendCloseWhat == WeekendCloseScope.AllPositions || IsOwnLabel(p.Label)) candidates.Add(p);
            }

            var verdicts = new Dictionary<string, bool>();
            foreach (Position p in candidates)
            {
                if (!verdicts.TryGetValue(p.SymbolName, out bool closing))
                {
                    closing = LongClosureWithin(Symbols.GetSymbol(p.SymbolName), p.SymbolName, window);
                    verdicts[p.SymbolName] = closing;
                }
                if (!closing) continue;
                if (_closeAttempts.TryGetValue(p.Id, out DateTime last) && (now - last).TotalSeconds < 60) continue;
                _closeAttempts[p.Id] = now;

                double profit = p.NetProfit;
                string label = p.Label ?? "";
                TradeResult result = ClosePosition(p);
                Print("WEEKEND CLOSE " + p.SymbolName + " " + p.Id + " «" + label + "»: " + (result.IsSuccessful
                    ? "закрыта перед выходными/праздником, результат " + profit.ToString("F2", CultureInfo.InvariantCulture)
                    : "не удалось закрыть (" + result.Error + "), повтор через минуту"));
            }
        }

        protected override void OnStop()
        {
            foreach (TradingStream s in _streams)
            {
                s.SaveMemory();
                Print("[" + s.Tag + "] " + s.BuildStatsLine());
            }
            Print("Версия " + BotVersion + " остановлена. Память AI всех потоков сохранена.");
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            foreach (TradingStream s in _streams) s.OnPositionClosed(args);
        }

        /// <summary>
        /// Потоки = (инструмент графика + доп. инструменты) × (таймфрейм графика + доп. таймфреймы).
        /// Недоступные инструменты и нераспознанные таймфреймы пропускаются с сообщением.
        /// </summary>
        private void BuildStreams()
        {
            var symbols = new List<string> { SymbolName };
            foreach (string raw in SplitList(ExtraSymbols))
            {
                // «US500|SPX500» — варианты имени у разных брокеров: берётся первый существующий.
                string found = null;
                foreach (string candidate in raw.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string name = candidate.Trim();
                    if (name.Length > 0 && Symbols.Exists(name))
                    {
                        found = name;
                        break;
                    }
                }

                if (found == null)
                {
                    Print("Инструмент «" + raw + "» у брокера не найден — пропущен.");
                    continue;
                }
                if (ContainsIgnoreCase(symbols, found)) continue;
                symbols.Add(found);
            }

            // Крипта — сразу после инструмента графика: на выходных торгует только она, её лимит потоков не отрежет.
            var ordered = new List<string> { symbols[0] };
            foreach (string name in symbols) if (name != symbols[0] && IsCrypto(name)) ordered.Add(name);
            foreach (string name in symbols) if (name != symbols[0] && !IsCrypto(name)) ordered.Add(name);
            symbols = ordered;

            var frames = new List<TimeFrame> { TimeFrame };
            foreach (string raw in SplitList(ExtraTimeFrames))
            {
                TimeFrame tf = ParseTimeFrame(raw);
                if (tf == null)
                {
                    Print("Таймфрейм «" + raw + "» не распознан — пропущен. Допустимо: m1, m3, m5, m10, m15, m30, h1, h4, d1.");
                    continue;
                }
                if (!frames.Contains(tf)) frames.Add(tf);
            }

            // Сначала каждый инструмент получает таймфрейм графика, потом следующий таймфрейм для всех —
            // при нехватке места отсекаются лишние таймфреймы, а не целые инструменты.
            var skipped = new List<string>();
            foreach (TimeFrame tf in frames)
            {
                foreach (string symbolName in symbols)
                {
                    if (_streams.Count >= MaxStreams)
                    {
                        skipped.Add(symbolName + " " + ShortTf(tf));
                        continue;
                    }

                    bool isChart = symbolName == SymbolName && tf.Equals(TimeFrame);
                    Bars bars = isChart ? Bars : MarketData.GetBars(tf, symbolName);
                    Symbol symbol = isChart ? Symbol : Symbols.GetSymbol(symbolName);
                    if (bars == null || symbol == null)
                    {
                        Print("Не удалось получить данные для " + symbolName + " " + tf + " — поток пропущен.");
                        continue;
                    }

                    _streams.Add(new TradingStream(this, bars, symbol, symbolName, tf));
                }
            }

            if (skipped.Count > 0)
            {
                Print("Потоков больше " + MaxStreams + " — пропущены: " + string.Join(", ", skipped) +
                      ". Чтобы они работали, уберите лишние таймфреймы или инструменты.");
            }
        }

        private static string ShortTf(TimeFrame tf)
        {
            string t = tf.ToString();
            if (t.StartsWith("Minute", StringComparison.Ordinal)) return "m" + (t.Length > 6 ? t.Substring(6) : "1");
            if (t.StartsWith("Hour", StringComparison.Ordinal)) return "h" + (t.Length > 4 ? t.Substring(4) : "1");
            return t;
        }

        private static IEnumerable<string> SplitList(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;
            foreach (string part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = part.Trim();
                if (t.Length > 0) yield return t;
            }
        }

        private static bool ContainsIgnoreCase(List<string> list, string value)
        {
            foreach (string s in list)
            {
                if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        internal static TimeFrame ParseTimeFrame(string text)
        {
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "m1": return TimeFrame.Minute;
                case "m3": return TimeFrame.Minute3;
                case "m5": return TimeFrame.Minute5;
                case "m10": return TimeFrame.Minute10;
                case "m15": return TimeFrame.Minute15;
                case "m30": return TimeFrame.Minute30;
                case "h1": return TimeFrame.Hour;
                case "h4": return TimeFrame.Hour4;
                case "d1": return TimeFrame.Daily;
                default: return null;
            }
        }

        // =============================================================================================
        //  ОБЩИЕ НАСТРОЙКИ И РИСК СЧЁТА
        // =============================================================================================

        private void ValidateGlobalSettings()
        {
            // R:R — жёсткое требование: при нарушении цель поднимается, а не игнорируется молча.
            _effectiveTpAtr = TpAtrMultiplier;
            if (TpAtrMultiplier / SlAtrMultiplier < MinRewardRisk)
            {
                _effectiveTpAtr = SlAtrMultiplier * MinRewardRisk;
                Print("ВНИМАНИЕ: Тейк " + F(TpAtrMultiplier) + " ATR при стопе " + F(SlAtrMultiplier) +
                      " ATR даёт R:R ниже " + F(MinRewardRisk) + ". Тейк поднят до " + F(_effectiveTpAtr) + " ATR.");
            }

            _sessionBlockAuto = IsAuto(SessionBlockStart) || IsAuto(SessionBlockEnd);
            _sessionBlockEnabled = _sessionBlockAuto ||
                                   (TryParseTime(SessionBlockStart, out _blockStart) & TryParseTime(SessionBlockEnd, out _blockEnd));
            if (!_sessionBlockEnabled && !(string.IsNullOrWhiteSpace(SessionBlockStart) && string.IsNullOrWhiteSpace(SessionBlockEnd)))
            {
                Print("ВНИМАНИЕ: не удалось прочитать окно блокировки сессии ('" + SessionBlockStart + "'–'" +
                      SessionBlockEnd + "'). Формат ЧЧ:ММ или «авто». Блокировка отключена.");
            }

            // Одинаковые «с» и «до» (по умолчанию 00:00–00:00) или пустые поля — круглосуточно.
            bool hoursParsed = TryParseTime(TradeHoursStart, out _hoursStart) & TryParseTime(TradeHoursEnd, out _hoursEnd);
            _tradeHoursEnabled = hoursParsed && _hoursStart != _hoursEnd;
            if (!hoursParsed && !(string.IsNullOrWhiteSpace(TradeHoursStart) && string.IsNullOrWhiteSpace(TradeHoursEnd)))
            {
                Print("ВНИМАНИЕ: не удалось прочитать торговые часы ('" + TradeHoursStart + "'–'" + TradeHoursEnd +
                      "'). Формат ЧЧ:ММ. Форекс, металлы и индексы торгуются круглосуточно, пока открыт рынок.");
            }

            _groups.Clear();
            foreach (string part in (CorrelationGroups ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var group = new HashSet<string>(StringComparer.Ordinal);
                foreach (string name in SplitList(part)) group.Add(name.ToUpperInvariant());
                if (group.Count > 1) _groups.Add(group);
            }

            if (MaxRiskCapPercent < RiskPercent)
            {
                Print("ВНИМАНИЕ: Потолок риска " + F(MaxRiskCapPercent) + "% ниже риска на сделку " + F(RiskPercent) +
                      "% — позиция будет ограничена потолком.");
            }

            // Никаких скрытых порогов: печатается ровно то, что будет применяться.
            Print("Пороги (все из параметров UI): режим ≥ " + F(MinRegimeConfidence) +
                  "; вход ≥ " + (AdaptiveThreshold
                      ? F(TrendThreshold) + " при ADX > " + F(TrendAdxLevel) + ", " + F(FlatThreshold) +
                        " при ADX < " + F(FlatAdxLevel) + ", иначе " + F(MinEnsembleConfidence)
                      : F(MinEnsembleConfidence) + " (адаптивный порог выключен)") +
                  ". Риск " + F(RiskPercent) + "% от " + RiskBase + " на каждую сделку каждого потока, стоп " + F(SlAtrMultiplier) +
                  " ATR, тейк " + F(_effectiveTpAtr) + " ATR, дневной лимит " + F(DailyLossLimitPercent) + "% на весь счёт." +
                  (_sessionBlockEnabled
                      ? (_sessionBlockAuto
                          ? " Пауза на ролловер (не-крипто): 16:50–17:20 Нью-Йорка, сейчас " + RolloverWindowText(Server.TimeInUtc) + " UTC."
                          : " Пауза (не-крипто) " + SessionBlockStart + "–" + SessionBlockEnd + " UTC.")
                      : "") +
                  (RequirePositiveEv ? " Требуется EV > 0 (после комиссии)." : " EV не проверяется.") +
                  " Уровни стратегий: ✓ доказан — не меньше " + EvidenceMinTrades + " теневых сделок, среднее − " + F(EvidenceSigma) +
                  "σ > 0 и плюс в обеих половинах периода → полный риск; ? пробный → " + F(ProbationRiskPercent) + "% риска" +
                  (BlockProvenLosers ? "; ✗ убыточен — среднее + " + F(EvidenceSigma) + "σ < 0 → не торгуется." : "; убыточные не блокируются."));
        }

        private static bool IsAuto(string text)
        {
            string t = (text ?? "").Trim().ToLowerInvariant();
            return t == "авто" || t == "auto";
        }

        private static DateTime NthSunday(int year, int month, int n)
        {
            var first = new DateTime(year, month, 1);
            int offset = ((int)DayOfWeek.Sunday - (int)first.DayOfWeek + 7) % 7;
            return first.AddDays(offset + (7 * (n - 1)));
        }

        /// <summary>
        /// Летнее время в США: со второго воскресенья марта 02:00 (EST = 07:00 UTC) до первого воскресенья
        /// ноября 02:00 (EDT = 06:00 UTC). От него зависит время ролловера — 17:00 Нью-Йорка.
        /// </summary>
        internal static bool IsUsDaylightTime(DateTime utc)
        {
            DateTime start = NthSunday(utc.Year, 3, 2).AddHours(7);
            DateTime end = NthSunday(utc.Year, 11, 1).AddHours(6);
            return utc >= start && utc < end;
        }

        /// <summary>Ролловер 17:00 Нью-Йорка в UTC: 21:00 летом, 22:00 зимой.</summary>
        internal static TimeSpan RolloverUtc(DateTime utc) => IsUsDaylightTime(utc) ? new TimeSpan(21, 0, 0) : new TimeSpan(22, 0, 0);

        private static string RolloverWindowText(DateTime utc)
        {
            TimeSpan r = RolloverUtc(utc);
            return (r - TimeSpan.FromMinutes(10)).ToString(@"hh\:mm", CultureInfo.InvariantCulture) + "–" +
                   (r + TimeSpan.FromMinutes(20)).ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        }

        private static bool TryParseTime(string text, out TimeSpan value)
        {
            value = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;
            return TimeSpan.TryParseExact(text.Trim(), "h\\:mm", CultureInfo.InvariantCulture, out value) ||
                   TimeSpan.TryParseExact(text.Trim(), "hh\\:mm", CultureInfo.InvariantCulture, out value);
        }

        /// <summary>Вызывается каждым потоком перед решением: сутки и дневной лимит — общие для счёта.</summary>
        internal void UpdateGlobal()
        {
            DateTime nowUtc = Server.TimeInUtc;
            RollDay(nowUtc);
            UpdateEquityPeak();
        }

        private void RollDay(DateTime nowUtc)
        {
            if (nowUtc.Date == _dayKey) return;

            _dayKey = nowUtc.Date;
            _dayStartEquity = Account.Equity;
            if (_dailyLocked) Print("Новые сутки: блокировка по дневному лимиту снята.");
            _dailyLocked = false;
        }

        private void UpdateEquityPeak()
        {
            double equity = Account.Equity;
            if (equity > _peakEquity) _peakEquity = equity;

            if (!_dailyLocked && _dayStartEquity > 0 && equity <= _dayStartEquity * (1.0 - (DailyLossLimitPercent / 100.0)))
            {
                _dailyLocked = true;
                Print("DAILY LOSS LIMIT: капитал " + equity.ToString("F2", CultureInfo.InvariantCulture) + " против " +
                      _dayStartEquity.ToString("F2", CultureInfo.InvariantCulture) + " на начало суток. Новые входы во ВСЕХ потоках запрещены до конца суток UTC.");
            }
        }

        internal string BaseLabel => string.IsNullOrWhiteSpace(OrderLabel) ? DefaultLabel : OrderLabel.Trim();

        /// <summary>Позиция этого бота: метка «база ТФ» или метка прошлых версий («QAIv5 ТФ», «QAIv5T ТФ»).</summary>
        internal bool IsOwnLabel(string label)
        {
            if (label == null) return false;
            if (label.StartsWith(BaseLabel + " ", StringComparison.Ordinal)) return true;
            foreach (string legacy in LegacyLabels)
            {
                if (label.StartsWith(legacy + " ", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// Сколько позиций бота уже открыто в ту же сторону в группах, куда входит инструмент (максимум по группам).
        /// Инструменты группы движутся вместе, поэтому вторая такая позиция — это та же ставка ещё раз.
        /// </summary>
        internal int GroupSameDirection(string symbolName, TradeType type)
        {
            string me = (symbolName ?? "").ToUpperInvariant();
            int worst = 0;
            foreach (HashSet<string> group in _groups)
            {
                if (!group.Contains(me)) continue;
                int n = 0;
                foreach (Position p in Positions)
                {
                    if (p.TradeType == type && IsOwnLabel(p.Label) && group.Contains((p.SymbolName ?? "").ToUpperInvariant())) n++;
                }
                worst = Math.Max(worst, n);
            }
            return worst;
        }

        /// <summary>
        /// Суммарный риск открытых позиций бота, % капитала: сколько будет потеряно, если сработают все стопы.
        /// Позиция со стопом в безубытке риска не несёт. Позиция без стопа считается по «Потолку риска».
        /// </summary>
        internal double OpenRiskPercent()
        {
            double equity = Account.Equity;
            if (equity <= 0) return 100.0;

            double total = 0;
            foreach (Position p in Positions)
            {
                if (!IsOwnLabel(p.Label)) continue;
                if (p.StopLoss == null)
                {
                    total += equity * MaxRiskCapPercent / 100.0;
                    continue;
                }

                int dir = p.TradeType == TradeType.Buy ? 1 : -1;
                double distance = (p.EntryPrice - p.StopLoss.Value) * dir;
                if (distance <= 0) continue;

                Symbol symbol = Symbols.GetSymbol(p.SymbolName);
                if (symbol == null || !(symbol.PipSize > 0)) continue;
                double risk = symbol.AmountRisked(p.VolumeInUnits, distance / symbol.PipSize);
                if (risk > 0 && !double.IsInfinity(risk)) total += risk;
            }
            return total / equity * 100.0;
        }


        /// <summary>Позиции этого бота на всех инструментах.</summary>
        internal int TotalOwnPositions()
        {
            int n = 0;
            foreach (Position p in Positions)
            {
                if (IsOwnLabel(p.Label)) n++;
            }
            return n;
        }

        /// <summary>Позиции этого бота на инструменте — по всем таймфреймам.</summary>
        internal int OwnPositionsOnSymbol(string symbolName)
        {
            int n = 0;
            foreach (Position p in Positions)
            {
                if (p.SymbolName == symbolName && IsOwnLabel(p.Label)) n++;
            }
            return n;
        }

        private static readonly string[] CryptoPrefixes =
        {
            "BTC", "ETH", "LTC", "XRP", "BCH", "SOL", "ADA", "DOGE", "DOT", "LINK", "XLM", "EOS", "UNI", "AVAX",
            "MATIC", "TRX", "BNB", "SHIB", "XTZ", "ATOM", "ALGO", "NEAR", "FIL", "ETC", "AAVE",
        };

        /// <summary>Криптовалюта торгуется круглосуточно — для неё торговые часы не применяются.</summary>
        internal static bool IsCrypto(string symbolName)
        {
            string name = (symbolName ?? "").ToUpperInvariant();
            foreach (string prefix in CryptoPrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Ликвидные часы для форекса и золота; крипта — всегда.</summary>
        internal bool InTradingHours(string symbolName, DateTime utc)
        {
            if (!_tradeHoursEnabled || IsCrypto(symbolName)) return true;
            TimeSpan t = utc.TimeOfDay;
            return _hoursStart <= _hoursEnd
                ? t >= _hoursStart && t < _hoursEnd
                : t >= _hoursStart || t < _hoursEnd;
        }

        internal double DrawdownPercent() => _peakEquity > 0 ? Math.Max(0, (_peakEquity - Account.Equity) / _peakEquity * 100.0) : 0;

        internal double DailyPnlPercent() => _dayStartEquity > 0 ? (Account.Equity - _dayStartEquity) / _dayStartEquity * 100.0 : 0;

        /// <summary>Пауза на ролловер: «авто» — от 10 минут до и до 20 минут после 17:00 Нью-Йорка.</summary>
        internal bool IsSessionBlocked(DateTime nowUtc)
        {
            if (!_sessionBlockEnabled) return false;
            TimeSpan t = nowUtc.TimeOfDay;
            if (_sessionBlockAuto)
            {
                TimeSpan rollover = RolloverUtc(nowUtc);
                return t >= rollover - TimeSpan.FromMinutes(10) && t < rollover + TimeSpan.FromMinutes(20);
            }
            return _blockStart <= _blockEnd
                ? t >= _blockStart && t < _blockEnd
                : t >= _blockStart || t < _blockEnd;
        }

        internal string SessionBlockText(DateTime utc) =>
            _sessionBlockAuto ? RolloverWindowText(utc) + " UTC (ролловер 17:00 Нью-Йорка)" : SessionBlockStart + "–" + SessionBlockEnd + " UTC";

        private static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    }

    // =================================================================================================
    //  ПОТОК: один инструмент на одном таймфрейме — со своими индикаторами, AI, памятью и позициями
    // =================================================================================================

    internal sealed class TradingStream
    {
        // ----- Константы алгоритма (структура, а не настройки) -------------------------------------

        private const int AtrPeriod = 14;
        private const int AtrLongPeriod = 100;
        private const int VolPercentileWindow = 200;
        private const int DonchianPeriod = 20;
        private const int MicroExtremePeriod = 10;
        private const int EfficiencyPeriod = 20;
        private const int RegimeHorizon = 12;
        private const int ShadowMaxHoldBars = 60;
        private const double ShadowMinScore = 0.15;
        private const double ShadowWeight = 0.5;
        private const double NaiveBayesTrustSamples = 40.0;
        private const int WarmupIndex = 320;
        private const int EvidenceWindow = 300;     // сколько последних теневых сделок стратегии оценивается
        private const double EvPriorTrades = 5.0;   // вес априорных средних выигрыша/проигрыша, пока своих данных мало

        private static readonly double[] VarianceFloor = { 0.0025, 0.25, 1.0, 0.00005, 0.004 };

        // ----- Состояние потока ---------------------------------------------------------------------

        private readonly QuantAI_Universal_V6_3 _r;
        private readonly Bars _bars;
        private readonly Symbol _symbol;
        private readonly string _symbolName;
        private readonly TimeFrame _timeFrame;

        private AverageTrueRange _atr;
        private RelativeStrengthIndex _rsi;
        private DirectionalMovementSystem _dms;
        private ExponentialMovingAverage _ema20;
        private ExponentialMovingAverage _ema50;
        private ExponentialMovingAverage _ema200;
        private BollingerBands _bb;

        private readonly NaiveBayesModel _nbReal = new NaiveBayesModel();
        private readonly NaiveBayesModel _nbShadow = new NaiveBayesModel();
        private readonly OnlineLogistic _regimeModel = new OnlineLogistic(5);

        private readonly double[] _realWeights = { 1.0, 1.0, 1.0, 1.0, 1.0 };
        private readonly double[] _shadowMultipliers = { 1.0, 1.0, 1.0, 1.0, 1.0 };

        private readonly List<ShadowTrade> _shadowTrades = new List<ShadowTrade>();
        private readonly List<RegimeSample> _regimeSamples = new List<RegimeSample>();
        private readonly Dictionary<int, TradeMeta> _meta = new Dictionary<int, TradeMeta>();
        private readonly Dictionary<SkipReason, int> _skipCounts = new Dictionary<SkipReason, int>();

        private int _lastProcessedIndex = -1;
        private int _lastTradeCloseIndex = -1000000;
        private int _barsSinceStatus;
        private int _shadowResolved;
        private int _shadowWins;
        private int _realTrades;
        private int _realWins;
        private double _shadowSumR;

        // Средние исходы теневых сделок ансамбля (R без комиссии) — для EV по фактам, а не по допущениям.
        private double _winGrossSum;
        private double _winCount;
        private double _lossGrossSum;
        private double _lossCount;

        // Теневой счёт каждой стратегии: исходы в R после спреда и комиссии.
        private readonly EdgeTracker[] _edge = new EdgeTracker[QuantAI_Universal_V6_3.StrategyCount];

        private ExitRules _rules;

        /// <summary>Комиссия туда-обратно на единицу объёма (валюта счёта) по закрытым сделкам счёта; 0 — нет данных.</summary>
        private double _historyCommissionPerUnit;

        /// <summary>Комиссия в цене для теневых сделок: пересчитывается раз в бар, а не на каждую сделку.</summary>
        private double _commissionPriceCached;

        private bool _sweepActive;
        private string _storageKey;

        /// <summary>Фактический залог на единицу объёма по последней своей сделке (0 — ещё не известен).</summary>
        private double _observedMarginPerUnit;

        /// <summary>Метка ордеров потока: метка из параметров + таймфрейм. Инструмент различается сам — позиции ищутся по метке И символу.</summary>
        private string _label;

        /// <summary>Метка тестовых версий на том же таймфрейме: такие позиции тоже сопровождаются.</summary>
        private string[] _legacyLabels;

        public TradingStream(QuantAI_Universal_V6_3 robot, Bars bars, Symbol symbol, string symbolName, TimeFrame timeFrame)
        {
            _r = robot;
            _bars = bars;
            _symbol = symbol;
            _symbolName = symbolName;
            _timeFrame = timeFrame;
            Tag = symbolName + " " + ShortTimeFrame();
        }

        /// <summary>Имя потока в журнале: «EURUSD m5».</summary>
        public string Tag { get; }

        public string SymbolName => _symbolName;

        private void Log(string message) => _r.Print("[" + Tag + "] " + message);

        public void Start()
        {
            _atr = _r.Indicators.AverageTrueRange(_bars, AtrPeriod, MovingAverageType.Simple);
            _rsi = _r.Indicators.RelativeStrengthIndex(_bars.ClosePrices, 14);
            _dms = _r.Indicators.DirectionalMovementSystem(_bars, 14);
            _ema20 = _r.Indicators.ExponentialMovingAverage(_bars.ClosePrices, 20);
            _ema50 = _r.Indicators.ExponentialMovingAverage(_bars.ClosePrices, 50);
            _ema200 = _r.Indicators.ExponentialMovingAverage(_bars.ClosePrices, 200);
            _bb = _r.Indicators.BollingerBands(_bars.ClosePrices, 20, 2.0, MovingAverageType.Simple);

            _label = _r.BaseLabel + " " + ShortTimeFrame();
            _legacyLabels = new string[QuantAI_Universal_V6_3.LegacyLabels.Length];
            for (int k = 0; k < _legacyLabels.Length; k++) _legacyLabels[k] = QuantAI_Universal_V6_3.LegacyLabels[k] + " " + ShortTimeFrame();

            for (int s = 0; s < QuantAI_Universal_V6_3.StrategyCount; s++) _edge[s] = new EdgeTracker(EvidenceWindow);
            _rules = new ExitRules
            {
                UsePartial = _r.UsePartialClose,
                PartialFraction = _r.PartialClosePercent / 100.0,
                TriggerR = _r.BreakevenTriggerR,
                BreakevenOffsetR = _r.BreakevenOffsetR,
                TrailAtr = _r.TrailAtrMultiplier,
                MaxHoldBars = ShadowMaxHoldBars,
            };
            _historyCommissionPerUnit = HistoryCommissionPerUnit();
            _commissionPriceCached = CommissionPrice();

            _sweepActive = _r.SweepProtection == SweepProtectionMode.On ||
                           (_r.SweepProtection == SweepProtectionMode.Auto &&
                            _symbolName.IndexOf("XAU", StringComparison.OrdinalIgnoreCase) >= 0);

            // LocalStorage cTrader принимает в ключе только латиницу, цифры и пробелы (без пробелов по краям).
            _storageKey = StorageKey(QuantAI_Universal_V6_3.MemoryPrefix + " " + _symbolName + " " + _timeFrame);
            if (_r.ResetAiMemory)
            {
                Log("Память AI сброшена по параметру — модель начинает с нуля.");
            }
            else
            {
                LoadMemory();
            }

            EnsureHistory();
            PretrainOnHistory();

            _bars.BarOpened += OnBarOpened;
            _symbol.Tick += OnSymbolTick;
            RebuildMetaForOpenPositions();

            Log("поток запущен: метка ордеров «" + _label + "», баров истории " + _bars.Count +
                (_sweepActive ? ", фильтр ложных пробоев включён" : "") + ".");
            LogMarginProbe();
            PrintStatus(_bars.Count - 2, null);
        }

        /// <summary>Позиция этого потока: тот же инструмент и метка потока либо метка тестовых версий на том же ТФ.</summary>
        private bool IsMine(Position p)
        {
            if (p.SymbolName != _symbolName || p.Label == null) return false;
            if (p.Label == _label) return true;
            foreach (string legacy in _legacyLabels)
            {
                if (p.Label == legacy) return true;
            }
            return false;
        }

        private List<Position> OwnPositions()
        {
            var list = new List<Position>();
            foreach (Position p in _r.Positions)
            {
                if (IsMine(p)) list.Add(p);
            }
            return list;
        }

        /// <summary>Рынок инструмента скоро закрывается надолго (выходные, праздник) — решение робота по расписанию брокера.</summary>
        private bool LongClosureWithin(TimeSpan window) => _r.LongClosureWithin(_symbol, _symbolName, window);

        private void OnSymbolTick(SymbolTickEventArgs args)
        {
            ManagePositionsOnTick();
        }

        private void OnBarOpened(BarOpenedEventArgs args)
        {
            int closedIndex = _bars.Count - 2;
            if (closedIndex < 0) return;

            _r.UpdateGlobal();
            DateTime nowUtc = _r.Server.TimeInUtc;
            _commissionPriceCached = CommissionPrice();

            // Все бары, которые ещё не были обработаны (обычно ровно один).
            BarAnalysis last = null;
            for (int i = Math.Max(_lastProcessedIndex + 1, 0); i <= closedIndex; i++)
            {
                last = ProcessClosedBar(i);
                _lastProcessedIndex = i;
            }

            ManageTrailingOnBar(closedIndex);

            if (last == null)
            {
                RegisterSkip(SkipReason.Warmup, "недостаточно истории для индикаторов", null);
            }
            else
            {
                TryEnter(last, nowUtc);
            }

            _barsSinceStatus++;
            if (_barsSinceStatus >= _r.StatusEveryBars)
            {
                _barsSinceStatus = 0;
                PrintStatus(closedIndex, last);
            }

            if (closedIndex % 50 == 0) SaveMemory();
        }

        private string ShortTimeFrame()
        {
            string tf = _timeFrame.ToString();
            if (tf.StartsWith("Minute", StringComparison.Ordinal)) return "m" + (tf.Length > 6 ? tf.Substring(6) : "1");
            if (tf.StartsWith("Hour", StringComparison.Ordinal)) return "h" + (tf.Length > 4 ? tf.Substring(4) : "1");
            if (tf.StartsWith("Daily", StringComparison.Ordinal)) return "d1";
            return tf;
        }

        private void EnsureHistory()
        {
            int attempts = 0;
            while (_bars.Count < _r.HistoryBars && attempts < 40)
            {
                attempts++;
                int loaded = _bars.LoadMoreHistory();
                if (loaded <= 0) break;
            }
        }

        // =============================================================================================
        //  ОБУЧЕНИЕ НА ИСТОРИИ (холодный старт)
        // =============================================================================================

        private void PretrainOnHistory()
        {
            int lastClosed = _bars.Count - 2;
            int from = Math.Max(WarmupIndex, lastClosed - _r.HistoryBars);
            int processed = 0;

            for (int i = from; i <= lastClosed; i++)
            {
                ProcessClosedBar(i);
                _lastProcessedIndex = i;
                processed++;
            }

            if (processed == 0)
            {
                _lastProcessedIndex = lastClosed;
                Log("Обучение на истории пропущено: баров " + _bars.Count + ", нужно больше " + WarmupIndex +
                      ". Модель начнёт с нейтральной оценки и будет учиться онлайн.");
                return;
            }

            double rate = _shadowResolved > 0 ? (double)_shadowWins / _shadowResolved : 0;
            double avg = _shadowResolved > 0 ? _shadowSumR / _shadowResolved : 0;
            Log("AI обучен на истории: " + processed + " баров. Теневые сделки ансамбля (по правилам реальных: частичное " +
                  "закрытие, безубыток, трейлинг, спред и комиссия): " + _shadowResolved + ", в плюс " +
                  (rate * 100).ToString("F1", CultureInfo.InvariantCulture) + "%, в среднем " + SignedR(avg) +
                  " на сделку. Обновлений модели режима: " + _regimeModel.Updates + ".");
            Log(CommissionText());
            Log(EdgeReport());
        }

        /// <summary>
        /// Обработка одного закрытого бара: закрываются созревшие теневые сделки и метки режима,
        /// выполняется анализ, открывается новая теневая сделка. Реальных ордеров здесь нет.
        /// </summary>
        private BarAnalysis ProcessClosedBar(int i)
        {
            ResolveShadowTrades(i);
            ResolveRegimeSamples(i);

            if (i < WarmupIndex) return null;

            BarAnalysis a = Analyze(i);
            if (a == null) return null;

            QueueRegimeSample(a);

            if (a.EnsembleDirection != 0 && a.EnsembleScore >= ShadowMinScore)
            {
                OpenShadowTrade(a);
            }

            // Теневой счёт каждой стратегии — только в часы, когда поток вообще может торговать.
            DateTime entryTime = EntryTime(i);
            bool rolloverPause = !QuantAI_Universal_V6_3.IsCrypto(_symbolName) && _r.IsSessionBlocked(entryTime);
            if (_r.InTradingHours(_symbolName, entryTime) && !rolloverPause) OpenStrategyShadows(a);

            return a;
        }

        /// <summary>Время входа по сигналу бара i — открытие следующего бара.</summary>
        private DateTime EntryTime(int i) => i + 1 < _bars.Count ? _bars.OpenTimes[i + 1] : _r.Server.TimeInUtc;

        // =============================================================================================
        //  АНАЛИЗ РЫНКА
        // =============================================================================================

        private BarAnalysis Analyze(int i)
        {
            double atr = _atr.Result[i];
            if (double.IsNaN(atr) || atr <= 0) return null;

            var a = new BarAnalysis
            {
                Index = i,
                Close = _bars.ClosePrices[i],
                Atr = atr,
                Adx = _dms.ADX[i],
                Rsi = _rsi.Result[i],
            };

            if (double.IsNaN(a.Adx) || double.IsNaN(a.Rsi)) return null;

            // Средний ATR и перцентиль волатильности.
            double sum = 0;
            int n = 0;
            for (int k = 0; k < AtrLongPeriod; k++)
            {
                double v = _atr.Result[i - k];
                if (double.IsNaN(v)) continue;
                sum += v;
                n++;
            }
            a.AtrLongAverage = n > 0 ? sum / n : atr;

            int below = 0;
            int total = 0;
            for (int k = 1; k <= VolPercentileWindow; k++)
            {
                double v = _atr.Result[i - k];
                if (double.IsNaN(v)) continue;
                total++;
                if (v < atr) below++;
            }
            a.VolPercentile = total > 0 ? (double)below / total : 0.5;

            a.RsiSlope = (_rsi.Result[i] - _rsi.Result[i - 3]) / 3.0;

            // Efficiency Ratio Кауфмана: доля направленного движения в пройденном пути.
            double path = 0;
            for (int k = 0; k < EfficiencyPeriod; k++)
            {
                path += Math.Abs(_bars.ClosePrices[i - k] - _bars.ClosePrices[i - k - 1]);
            }
            double net = Math.Abs(_bars.ClosePrices[i] - _bars.ClosePrices[i - EfficiencyPeriod]);
            a.EfficiencyRatio = path > 0 ? net / path : 0;

            a.TrendSlopeSign = Math.Sign(_ema20.Result[i] - _ema20.Result[i - 5]);

            ComputeRegime(a);
            EvaluateStrategies(a);
            ComputeEnsemble(a);
            DetectSweeps(a);
            return a;
        }

        private static double Sigmoid(double z)
        {
            if (z > 30) return 1.0;
            if (z < -30) return 0.0;
            return 1.0 / (1.0 + Math.Exp(-z));
        }

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        private double[] RegimeInput(BarAnalysis a) => new[]
        {
            1.0,
            (a.Adx - 22.0) / 10.0,
            (a.EfficiencyRatio - 0.30) * 3.0,
            (a.AtrLongAverage > 0 ? a.Atr / a.AtrLongAverage : 1.0) - 1.0,
            a.VolPercentile - 0.5,
        };

        /// <summary>
        /// P(тренд): эвристика по ADX и Efficiency Ratio, смешанная с онлайн-моделью, которая учится на
        /// том, продолжился ли рынок в направлении наклона EMA20 на горизонте в 12 баров.
        /// </summary>
        private void ComputeRegime(BarAnalysis a)
        {
            double heuristic = Sigmoid(((a.Adx - 22.0) / 5.0) + (6.0 * (a.EfficiencyRatio - 0.30)));

            double learnedWeight = Math.Min(0.5, _regimeModel.Updates / (_regimeModel.Updates + 500.0));
            double learned = _regimeModel.Updates > 0 ? _regimeModel.Predict(RegimeInput(a)) : heuristic;

            // Модель предсказывает вероятность продолжения движения на 0.75 ATR — её базовая частота ниже 0.5,
            // поэтому она растягивается к шкале эвристики, а не подменяет её.
            double learnedScaled = Clamp01(learned * 2.0);
            a.PTrend = Clamp01(((1.0 - learnedWeight) * heuristic) + (learnedWeight * learnedScaled));
            a.RegimeConfidence = Math.Abs((2.0 * a.PTrend) - 1.0);
        }

        private void QueueRegimeSample(BarAnalysis a)
        {
            if (a.TrendSlopeSign == 0) return;
            _regimeSamples.Add(new RegimeSample
            {
                Index = a.Index,
                Input = RegimeInput(a),
                Direction = (int)a.TrendSlopeSign,
                Close = a.Close,
                Atr = a.Atr,
            });
        }

        private void ResolveRegimeSamples(int i)
        {
            for (int k = _regimeSamples.Count - 1; k >= 0; k--)
            {
                RegimeSample s = _regimeSamples[k];
                if (i - s.Index < RegimeHorizon) continue;

                double move = (_bars.ClosePrices[i] - s.Close) * s.Direction / Math.Max(s.Atr, 1e-12);
                double y = move >= 0.75 ? 1.0 : 0.0;
                _regimeModel.Update(s.Input, y, 0.02, 0.0005);
                _regimeSamples.RemoveAt(k);
            }
        }

        // ----- Подстратегии ------------------------------------------------------------------------

        private void EvaluateStrategies(BarAnalysis a)
        {
            int i = a.Index;
            double atr = a.Atr;
            double c = _bars.ClosePrices[i];
            double o = _bars.OpenPrices[i];
            double h = _bars.HighPrices[i];
            double l = _bars.LowPrices[i];

            // 0. TrendFollowing: EMA50/EMA200, цена по тренду, наклон EMA50; откат к EMA50 усиливает сигнал.
            {
                double e50 = _ema50.Result[i];
                double e200 = _ema200.Result[i];
                double e50Prev = _ema50.Result[i - 5];
                double minLow = Math.Min(Math.Min(_bars.LowPrices[i], _bars.LowPrices[i - 1]), Math.Min(_bars.LowPrices[i - 2], _bars.LowPrices[i - 3]));
                double maxHigh = Math.Max(Math.Max(_bars.HighPrices[i], _bars.HighPrices[i - 1]), Math.Max(_bars.HighPrices[i - 2], _bars.HighPrices[i - 3]));
                double separation = Math.Abs(e50 - e200) / atr;

                if (e50 > e200 && c > e50 && e50 > e50Prev)
                {
                    bool pullback = minLow <= e50 + (0.5 * atr);
                    a.Votes[0] = new StrategyVote { Direction = 1, Strength = Clamp01(0.35 + (0.15 * Math.Min(separation, 3.0))) * (pullback ? 1.0 : 0.7) };
                }
                else if (e50 < e200 && c < e50 && e50 < e50Prev)
                {
                    bool pullback = maxHigh >= e50 - (0.5 * atr);
                    a.Votes[0] = new StrategyVote { Direction = -1, Strength = Clamp01(0.35 + (0.15 * Math.Min(separation, 3.0))) * (pullback ? 1.0 : 0.7) };
                }
            }

            // 1. Breakout: пробой канала Дончиана (20 баров без текущего) и/или канала Кельтнера (EMA20 ± 2 ATR).
            {
                double donHigh = double.MinValue;
                double donLow = double.MaxValue;
                for (int k = 1; k <= DonchianPeriod; k++)
                {
                    donHigh = Math.Max(donHigh, _bars.HighPrices[i - k]);
                    donLow = Math.Min(donLow, _bars.LowPrices[i - k]);
                }
                double kelTop = _ema20.Result[i] + (2.0 * atr);
                double kelBottom = _ema20.Result[i] - (2.0 * atr);

                if (c > donHigh)
                {
                    a.Votes[1] = new StrategyVote { Direction = 1, Strength = Clamp01(0.40 + (0.30 * ((c - donHigh) / atr)) + (c > kelTop ? 0.20 : 0.0)) };
                }
                else if (c < donLow)
                {
                    a.Votes[1] = new StrategyVote { Direction = -1, Strength = Clamp01(0.40 + (0.30 * ((donLow - c) / atr)) + (c < kelBottom ? 0.20 : 0.0)) };
                }
                else if (c > kelTop)
                {
                    a.Votes[1] = new StrategyVote { Direction = 1, Strength = 0.35 };
                }
                else if (c < kelBottom)
                {
                    a.Votes[1] = new StrategyVote { Direction = -1, Strength = 0.35 };
                }
            }

            // 2. MeanReversion: RSI в зоне перекупленности/перепроданности и закрытие за полосой Боллинджера.
            {
                double rsi = a.Rsi;
                double bbTop = _bb.Top[i];
                double bbBottom = _bb.Bottom[i];

                if (rsi < 30 && c <= bbBottom)
                {
                    a.Votes[2] = new StrategyVote { Direction = 1, Strength = Clamp01(0.40 + ((30.0 - rsi) / 25.0) + (0.20 * ((bbBottom - c) / atr))) };
                }
                else if (rsi > 70 && c >= bbTop)
                {
                    a.Votes[2] = new StrategyVote { Direction = -1, Strength = Clamp01(0.40 + ((rsi - 70.0) / 25.0) + (0.20 * ((c - bbTop) / atr))) };
                }
            }

            // 3. VolatilityExpansion: диапазон бара намного больше предыдущего ATR, закрытие у края диапазона.
            {
                double range = h - l;
                double prevAtr = _atr.Result[i - 1];
                if (range > 0 && prevAtr > 0 && range > 1.8 * prevAtr && atr > a.AtrLongAverage)
                {
                    double closeLocation = (c - l) / range;
                    double strength = Clamp01(0.60 + (0.25 * ((range / prevAtr) - 1.8)));
                    if (c > o && closeLocation >= 0.70)
                    {
                        a.Votes[3] = new StrategyVote { Direction = 1, Strength = strength };
                    }
                    else if (c < o && closeLocation <= 0.30)
                    {
                        a.Votes[3] = new StrategyVote { Direction = -1, Strength = strength };
                    }
                }
            }

            // 4. LiquidityGrab: прокол микро-экстремума (10 баров) с возвратом внутрь и длинной тенью.
            {
                double microHigh = double.MinValue;
                double microLow = double.MaxValue;
                for (int k = 1; k <= MicroExtremePeriod; k++)
                {
                    microHigh = Math.Max(microHigh, _bars.HighPrices[i - k]);
                    microLow = Math.Min(microLow, _bars.LowPrices[i - k]);
                }

                double upperWick = h - Math.Max(o, c);
                double lowerWick = Math.Min(o, c) - l;
                bool grabHigh = h > microHigh && c < microHigh && upperWick >= 0.4 * atr;
                bool grabLow = l < microLow && c > microLow && lowerWick >= 0.4 * atr;

                if (grabHigh && (!grabLow || upperWick >= lowerWick))
                {
                    a.Votes[4] = new StrategyVote { Direction = -1, Strength = Clamp01(0.35 + (0.30 * (upperWick / atr))) };
                }
                else if (grabLow)
                {
                    a.Votes[4] = new StrategyVote { Direction = 1, Strength = Clamp01(0.35 + (0.30 * (lowerWick / atr))) };
                }
            }
        }

        private double EffectiveWeight(int s)
        {
            double w = _realWeights[s] * _shadowMultipliers[s];
            return Math.Max(0.20, Math.Min(3.0, w));
        }

        private double RegimeFactor(int s, double pTrend) => QuantAI_Universal_V6_3.IsTrendStrategy[s] ? pTrend : 1.0 - pTrend;

        /// <summary>
        /// Взвешенная сумма голосов. Итоговая оценка 0..1 — сила согласных голосов, умноженная на степень
        /// согласия, на соответствие режиму и на средний AI-вес согласных стратегий.
        /// </summary>
        private void ComputeEnsemble(BarAnalysis a, bool[] allowed = null)
        {
            double netSum = 0;
            double gross = 0;

            for (int s = 0; s < QuantAI_Universal_V6_3.StrategyCount; s++)
            {
                StrategyVote v = a.Votes[s];
                if (v.Direction == 0 || v.Strength <= 0) continue;
                if (allowed != null && !allowed[s]) continue;
                double contribution = EffectiveWeight(s) * RegimeFactor(s, a.PTrend) * v.Strength;
                netSum += v.Direction * contribution;
                gross += contribution;
            }

            if (gross <= 1e-12 || Math.Abs(netSum) <= 1e-12)
            {
                a.EnsembleDirection = 0;
                a.EnsembleScore = 0;
                return;
            }

            int dir = netSum > 0 ? 1 : -1;
            double sumWrs = 0;   // Σ w·r·s согласных
            double sumWr = 0;    // Σ w·r
            double sumW = 0;     // Σ w
            double sumRs = 0;    // Σ r·s
            int agreeing = 0;

            for (int s = 0; s < QuantAI_Universal_V6_3.StrategyCount; s++)
            {
                StrategyVote v = a.Votes[s];
                if (v.Direction != dir || v.Strength <= 0) continue;
                if (allowed != null && !allowed[s]) continue;
                double w = EffectiveWeight(s);
                double r = RegimeFactor(s, a.PTrend);
                sumWrs += w * r * v.Strength;
                sumWr += w * r;
                sumW += w;
                sumRs += r * v.Strength;
                agreeing++;
            }

            double strength = sumWr > 0 ? sumWrs / sumWr : 0;
            double consensus = Math.Abs(netSum) / gross;
            double regimeFit = sumW > 0 ? sumWr / sumW : 0;
            double weightFactor = sumRs > 0 ? Math.Min(1.25, sumWrs / sumRs) : 0;
            double multiVote = 1.0 + (0.10 * Math.Max(0, agreeing - 1));

            a.EnsembleDirection = dir;
            a.AgreeingCount = agreeing;
            a.EnsembleScore = Clamp01(strength * consensus * (0.5 + (0.5 * regimeFit)) * weightFactor * multiVote);
        }

        /// <summary>
        /// Снятие ликвидности за последние 3 бара: прокол экстремума 20 баров с закрытием обратно внутри
        /// и длинной тенью. Для золота это частая ловушка — пробой, который сразу возвращается.
        /// </summary>
        private void DetectSweeps(BarAnalysis a)
        {
            for (int j = a.Index - 2; j <= a.Index; j++)
            {
                double hh = double.MinValue;
                double ll = double.MaxValue;
                for (int k = 1; k <= DonchianPeriod; k++)
                {
                    hh = Math.Max(hh, _bars.HighPrices[j - k]);
                    ll = Math.Min(ll, _bars.LowPrices[j - k]);
                }

                double atrJ = _atr.Result[j];
                if (double.IsNaN(atrJ) || atrJ <= 0) continue;

                double o = _bars.OpenPrices[j];
                double c = _bars.ClosePrices[j];
                double upperWick = _bars.HighPrices[j] - Math.Max(o, c);
                double lowerWick = Math.Min(o, c) - _bars.LowPrices[j];

                if (_bars.HighPrices[j] > hh && c < hh && upperWick >= 0.3 * atrJ) a.BearishSweepRecent = true;
                if (_bars.LowPrices[j] < ll && c > ll && lowerWick >= 0.3 * atrJ) a.BullishSweepRecent = true;
            }
        }

        // ----- Признаки для Naive Bayes ------------------------------------------------------------

        private double CurrentSpread() => Math.Max(0, _symbol.Spread);

        private double[] Features(BarAnalysis a, int direction) => new[]
        {
            a.AtrLongAverage > 0 ? a.Atr / a.AtrLongAverage : 1.0, // ATR_Ratio
            a.RsiSlope * direction,                                 // RSI_Slope в направлении сделки
            a.Adx,                                                  // ADX
            CurrentSpread() / a.Atr,                                // Spread_Cost
            a.VolPercentile,                                        // Volatility_Percentile
        };

        private double WinProbability(double[] features) =>
            NaiveBayesModel.PredictCombined(_nbReal, _nbShadow, features, VarianceFloor, NaiveBayesTrustSamples);

        /// <summary>
        /// Signal Confidence: сила ансамбля, скорректированная AI-вероятностью выигрыша.
        /// При нейтральной вероятности 0.5 равна силе ансамбля.
        /// </summary>
        private static double SignalConfidence(double ensembleScore, double pWin) => Clamp01(ensembleScore * 2.0 * pWin);

        /// <summary>
        /// Ожидание сделки в R после комиссии: P(win) от AI × средний выигрыш + P(loss) × средний проигрыш.
        /// Средние — фактические по теневым сделкам потока (те же правила сопровождения, спред в ценах).
        /// Пока своих сделок мало, к ним подмешано априорное значение с весом EvPriorTrades.
        /// </summary>
        private double ExpectedValueR(double pWin, double costR)
        {
            double rr = _r.EffectiveTpAtr / _r.SlAtrMultiplier;
            double part = _r.UsePartialClose ? _r.PartialClosePercent / 100.0 : 0.0;
            double priorWin = (part * _r.BreakevenTriggerR) + ((1.0 - part) * (_r.BreakevenOffsetR + rr) / 2.0);
            double avgWin = (_winGrossSum + (priorWin * EvPriorTrades)) / (_winCount + EvPriorTrades);
            double avgLoss = (_lossGrossSum - EvPriorTrades) / (_lossCount + EvPriorTrades);
            return (pWin * avgWin) + ((1.0 - pWin) * avgLoss) - costR;
        }

        private double EntryThreshold(double adx)
        {
            if (!_r.AdaptiveThreshold) return _r.MinEnsembleConfidence;
            if (adx > _r.TrendAdxLevel) return _r.TrendThreshold;
            if (adx < _r.FlatAdxLevel) return _r.FlatThreshold;
            return _r.MinEnsembleConfidence;
        }

        // =============================================================================================
        //  ТЕНЕВЫЕ СДЕЛКИ
        // =============================================================================================

        private ShadowTrade NewShadow(BarAnalysis a, int dir, int strategy)
        {
            double spread = CurrentSpread();
            double risk = a.Atr * _r.SlAtrMultiplier;

            // Бары строятся по Bid: покупка входит по Ask (Bid + спред), продажа — по Bid.
            double entry = dir > 0 ? a.Close + spread : a.Close;
            return new ShadowTrade
            {
                OpenIndex = a.Index,
                Direction = dir,
                Strategy = strategy,
                Entry = entry,
                RiskDistance = risk,
                Stop = entry - (dir * risk),
                TriggerLevel = entry + (dir * _r.BreakevenTriggerR * risk),
                Target = entry + (dir * a.Atr * _r.EffectiveTpAtr),
                CostR = _commissionPriceCached / risk,
            };
        }

        private bool HasOpenShadow(int strategy, int dir)
        {
            for (int k = 0; k < _shadowTrades.Count; k++)
            {
                if (_shadowTrades[k].Strategy == strategy && _shadowTrades[k].Direction == dir) return true;
            }
            return false;
        }

        /// <summary>Теневая сделка ансамбля: учит Naive Bayes, RL-множители и средние выигрыша/проигрыша.</summary>
        private void OpenShadowTrade(BarAnalysis a)
        {
            // Одна теневая сделка на направление одновременно — иначе серия похожих баров считалась бы
            // многократно одним и тем же событием.
            int dir = a.EnsembleDirection;
            if (HasOpenShadow(-1, dir)) return;

            ShadowTrade t = NewShadow(a, dir, -1);
            t.Features = Features(a, dir);
            t.VoteStrengths = new double[QuantAI_Universal_V6_3.StrategyCount];
            for (int s = 0; s < QuantAI_Universal_V6_3.StrategyCount; s++)
            {
                if (a.Votes[s].Direction == dir) t.VoteStrengths[s] = a.Votes[s].Strength;
            }
            _shadowTrades.Add(t);
        }

        /// <summary>Теневые сделки каждой стратегии отдельно: из них складывается доказательство её плюса или минуса.</summary>
        private void OpenStrategyShadows(BarAnalysis a)
        {
            for (int s = 0; s < QuantAI_Universal_V6_3.StrategyCount; s++)
            {
                int dir = a.Votes[s].Direction;
                if (dir == 0 || a.Votes[s].Strength <= 0 || HasOpenShadow(s, dir)) continue;
                _shadowTrades.Add(NewShadow(a, dir, s));
            }
        }

        private void ResolveShadowTrades(int i)
        {
            if (_shadowTrades.Count == 0) return;

            double spread = CurrentSpread();
            double open = _bars.OpenPrices[i];
            double high = _bars.HighPrices[i];
            double low = _bars.LowPrices[i];
            double close = _bars.ClosePrices[i];
            double atr = _atr.Result[i];
            if (double.IsNaN(atr)) atr = 0;

            for (int k = _shadowTrades.Count - 1; k >= 0; k--)
            {
                ShadowTrade t = _shadowTrades[k];
                if (i <= t.OpenIndex) continue;
                if (!t.Step(open, high, low, close, spread, atr, i - t.OpenIndex, _rules)) continue;

                _shadowTrades.RemoveAt(k);
                double netR = t.NetR;

                if (t.Strategy >= 0)
                {
                    _edge[t.Strategy].Add(netR);
                    continue;
                }

                bool win = netR > 0;
                _nbShadow.Add(t.Features, win, ShadowWeight);
                _shadowResolved++;
                _shadowSumR += netR;
                if (win)
                {
                    _shadowWins++;
                    _winGrossSum += t.GrossR;
                    _winCount++;
                }
                else
                {
                    _lossGrossSum += t.GrossR;
                    _lossCount++;
                }

                // RL на теневых исходах — в пять раз медленнее, чем на реальных.
                for (int s = 0; s < QuantAI_Universal_V6_3.StrategyCount; s++)
                {
                    if (t.VoteStrengths[s] <= 0) continue;
                    double step = _r.RlLearningRate / 5.0 * t.VoteStrengths[s] * Math.Max(-1.5, Math.Min(1.5, netR));
                    _shadowMultipliers[s] = Math.Max(0.5, Math.Min(1.5, _shadowMultipliers[s] * Math.Exp(step)));
                }
            }
        }

        // =============================================================================================
        //  ДОКАЗАТЕЛЬСТВА И ИЗДЕРЖКИ
        // =============================================================================================

        private EdgeStats Edge(int s) => _edge[s].Compute(_r.EvidenceSigma);

        private bool IsProven(int s) => Edge(s).Proven(_r.EvidenceMinTrades);

        /// <summary>Уровень доверия стратегии: доказан — полный риск; пробный — доля риска; убыточен — не торгуется.</summary>
        private StrategyTier TierOf(int s)
        {
            EdgeStats st = Edge(s);
            if (st.Proven(_r.EvidenceMinTrades)) return StrategyTier.Proven;
            if (_r.BlockProvenLosers && st.ProvenLoss(_r.EvidenceMinTrades)) return StrategyTier.Blocked;
            return _r.ProbationRiskPercent > 0 ? StrategyTier.Probation : StrategyTier.Blocked;
        }

        private static string TierMark(StrategyTier t) => t == StrategyTier.Proven ? "✓" : t == StrategyTier.Probation ? "?" : "✗";

        /// <summary>Коротко: сколько стратегий на каждом уровне — для сводки при старте.</summary>
        public string TierSummary()
        {
            int proven = 0, probation = 0, blocked = 0;
            for (int s = 0; s < QuantAI_Universal_V6_3.StrategyCount; s++)
            {
                StrategyTier t = TierOf(s);
                if (t == StrategyTier.Proven) proven++;
                else if (t == StrategyTier.Probation) probation++;
                else blocked++;
            }
            return "✓" + proven + " ?" + probation + " ✗" + blocked;
        }

        private static string SignedR(double r) => (r >= 0 ? "+" : "−") + Math.Abs(r).ToString("0.00", CultureInfo.InvariantCulture) + "R";

        private string EdgeText(int s)
        {
            EdgeStats st = Edge(s);
            string name = QuantAI_Universal_V6_3.StrategyNames[s];
            string mark = " " + TierMark(TierOf(s));
            if (st.Count < _r.EvidenceMinTrades) return name + " мало данных (" + st.Count + " из " + _r.EvidenceMinTrades + ")" + mark;
            return name + " " + SignedR(st.Mean) + " (n=" + st.Count + ", " + SignedR(st.LowerBound) + "…" + SignedR(st.UpperBound) +
                   ", половины " + SignedR(st.OlderMean) + "/" + SignedR(st.NewerMean) + ")" + mark;
        }

        /// <summary>Какие стратегии этого потока доказали плюс — печатается при старте и в сводке.</summary>
        private string EdgeReport()
        {
            var sb = new StringBuilder("Уровни стратегий по теневым сделкам (последние до " + EvidenceWindow + ", после спреда и комиссии; ожидание, диапазон ±" +
                                       F(_r.EvidenceSigma) + "σ): ");
            var proven = new List<string>();
            var probation = new List<string>();
            var blocked = new List<string>();
            for (int s = 0; s < QuantAI_Universal_V6_3.StrategyCount; s++)
            {
                if (s > 0) sb.Append("; ");
                sb.Append(EdgeText(s));
                StrategyTier t = TierOf(s);
                string name = QuantAI_Universal_V6_3.StrategyNames[s];
                if (t == StrategyTier.Proven) proven.Add(name);
                else if (t == StrategyTier.Probation) probation.Add(name);
                else blocked.Add(name);
            }

            sb.Append(". Полный риск: ").Append(proven.Count > 0 ? string.Join(", ", proven) : "нет");
            sb.Append("; пробный (").Append(F(_r.ProbationRiskPercent)).Append("% риска): ").Append(probation.Count > 0 ? string.Join(", ", probation) : "нет");
            sb.Append("; не торгуются: ").Append(blocked.Count > 0 ? string.Join(", ", blocked) : "нет").Append('.');
            return sb.ToString();
        }

        /// <summary>Сколько единиц валюты счёта стоит движение цены на 1.0 для одной единицы объёма.</summary>
        private double ValuePerPriceUnit()
        {
            double volume = Math.Max(_symbol.VolumeInUnitsMin, 1e-9);
            double pips = 1000.0; // крупное расстояние — чтобы округления и возможные издержки внутри расчёта не влияли
            double money = _symbol.AmountRisked(volume, pips);
            double distance = pips * _symbol.PipSize;
            return money > 0 && distance > 0 ? money / (volume * distance) : 0;
        }

        /// <summary>Комиссия туда-обратно за закрытые сделки счёта по инструменту (последние до 20), на единицу объёма.</summary>
        private double HistoryCommissionPerUnit()
        {
            double best = 0;
            try
            {
                History history = _r.History;
                int seen = 0;
                for (int k = history.Count - 1; k >= 0 && k >= history.Count - 2000 && seen < 20; k--)
                {
                    HistoricalTrade t = history[k];
                    if (t.SymbolName != _symbolName || !(t.VolumeInUnits > 0)) continue;
                    seen++;
                    double perUnit = Math.Abs(t.Commissions) / t.VolumeInUnits;
                    if (perUnit > best && !double.IsInfinity(perUnit)) best = perUnit;
                }
            }
            catch (Exception)
            {
                // История недоступна — остаются другие источники.
            }
            return best;
        }

        /// <summary>
        /// Комиссия туда-обратно на единицу объёма в валюте счёта — наибольшая из: закрытых сделок счёта,
        /// открытых позиций (Position.Commissions — одна сторона, поэтому ×2) и параметра «Мин. комиссия за лот».
        /// </summary>
        private double CommissionPerUnit()
        {
            double best = _historyCommissionPerUnit;
            foreach (Position p in _r.Positions)
            {
                if (p.SymbolName != _symbolName || !(p.VolumeInUnits > 0)) continue;
                double perUnit = 2.0 * Math.Abs(p.Commissions) / p.VolumeInUnits;
                if (perUnit > best && !double.IsInfinity(perUnit)) best = perUnit;
            }

            if (_r.MinCommissionPerLot > 0 && _symbol.LotSize > 0)
            {
                best = Math.Max(best, _r.MinCommissionPerLot / _symbol.LotSize);
            }
            return best;
        }

        /// <summary>Комиссия туда-обратно, выраженная в цене: на сколько должна пройти цена, чтобы её окупить.</summary>
        private double CommissionPrice()
        {
            double commission = CommissionPerUnit();
            if (commission <= 0) return 0;
            double value = ValuePerPriceUnit();
            return value > 0 ? commission / value : 0;
        }

        private string CommissionText()
        {
            double perUnit = CommissionPerUnit();
            if (perUnit <= 0)
            {
                return "Комиссия по " + _symbolName + ": не найдена в сделках счёта (у Pepperstone на индексах и крипте её обычно нет — только спред) — " +
                       "учитывается только спред. Если комиссия есть, задайте «Мин. комиссия за лот туда-обратно».";
            }

            double price = CommissionPrice();
            double atr = _atr.Result[Math.Max(0, _bars.Count - 2)];
            double costR = !double.IsNaN(atr) && atr > 0 ? price / (atr * _r.SlAtrMultiplier) : 0;
            return "Комиссия по " + _symbolName + " туда-обратно: " + (perUnit * _symbol.LotSize).ToString("F2", CultureInfo.InvariantCulture) +
                   " за лот = " + (price / _symbol.PipSize).ToString("F2", CultureInfo.InvariantCulture) + " п. цены ≈ " +
                   (costR * 100).ToString("F1", CultureInfo.InvariantCulture) + "% риска сделки при текущем ATR. Учтена в теневых сделках, EV и объёме.";
        }

        // =============================================================================================
        //  ВХОД
        // =============================================================================================

        private void TryEnter(BarAnalysis a, DateTime nowUtc)
        {
            string regimeText = RegimeLabel(a);

            if (_r.DailyLocked)
            {
                RegisterSkip(SkipReason.DailyLossLimit, "дневной лимит убытка " + F(_r.DailyLossLimitPercent) + "% достигнут, торговля до конца суток закрыта", a);
                return;
            }

            if (!QuantAI_Universal_V6_3.IsCrypto(_symbolName) && _r.IsSessionBlocked(nowUtc))
            {
                RegisterSkip(SkipReason.SessionBlock, "пауза " + _r.SessionBlockText(nowUtc) + ": резкое расширение спреда", a);
                return;
            }

            if (!_symbol.MarketHours.IsOpened())
            {
                RegisterSkip(SkipReason.MarketClosed, "рынок закрыт", a);
                return;
            }

            if (!_r.InTradingHours(_symbolName, nowUtc))
            {
                RegisterSkip(SkipReason.OffHours, "вне торговых часов " + _r.TradeHoursStart + "–" + _r.TradeHoursEnd +
                    " UTC, заданных в параметрах", a);
                return;
            }

            if (_r.WeekendCloseMinutes > 0 && LongClosureWithin(TimeSpan.FromMinutes(Math.Max(60, _r.WeekendCloseMinutes))))
            {
                RegisterSkip(SkipReason.OffHours, "рынок " + _symbolName + " меньше чем через час закрывается на выходные/праздник — новые сделки не открываются", a);
                return;
            }

            if (OwnPositions().Count >= _r.MaxOpenPositions)
            {
                RegisterSkip(SkipReason.PositionOpen, "уже открыто позиций: " + _r.MaxOpenPositions, a);
                return;
            }

            if (_r.OwnPositionsOnSymbol(_symbolName) >= _r.MaxPositionsPerSymbol)
            {
                RegisterSkip(SkipReason.PositionOpen, "по " + _symbolName + " уже открыта позиция на другом таймфрейме (лимит на инструмент: " +
                    _r.MaxPositionsPerSymbol + ")", a);
                return;
            }

            if (_r.TotalOwnPositions() >= _r.MaxTotalPositions)
            {
                RegisterSkip(SkipReason.PositionOpen, "на счёте уже " + _r.MaxTotalPositions + " позиций бота (лимит на все потоки)", a);
                return;
            }

            if (a.Index - _lastTradeCloseIndex < _r.CooldownBars)
            {
                RegisterSkip(SkipReason.Cooldown, "пауза после закрытия сделки", a);
                return;
            }

            if (a.EnsembleDirection == 0)
            {
                RegisterSkip(SkipReason.NoSignal, "ни одна стратегия не дала сигнала", a);
                return;
            }

            // Уровни доверия: голоса доказанно убыточных стратегий не учитываются вовсе.
            var tiers = new StrategyTier[QuantAI_Universal_V6_3.StrategyCount];
            var allowed = new bool[QuantAI_Universal_V6_3.StrategyCount];
            var blockedText = new StringBuilder();
            bool anyAllowed = false;
            for (int s = 0; s < QuantAI_Universal_V6_3.StrategyCount; s++)
            {
                if (a.Votes[s].Direction == 0) continue;
                tiers[s] = TierOf(s);
                if (tiers[s] == StrategyTier.Blocked)
                {
                    if (blockedText.Length > 0) blockedText.Append("; ");
                    blockedText.Append(EdgeText(s));
                    continue;
                }
                allowed[s] = true;
                anyAllowed = true;
            }

            if (!anyAllowed)
            {
                RegisterSkip(SkipReason.NoEdge, (_r.ProbationRiskPercent > 0 ? "сигнал только от доказанно убыточных стратегий: " : "сигнал от стратегий без доказанного плюса (пробные сделки выключены): ") + blockedText, a);
                return;
            }

            ComputeEnsemble(a, allowed);
            if (a.EnsembleDirection == 0)
            {
                RegisterSkip(SkipReason.NoSignal, "допущенные стратегии не дают общего направления", a);
                return;
            }

            // Полный риск — если направление поддерживает хотя бы одна доказанная стратегия, иначе пробный.
            var supportText = new StringBuilder();
            bool provenSupport = false;
            for (int s = 0; s < QuantAI_Universal_V6_3.StrategyCount; s++)
            {
                if (!allowed[s] || a.Votes[s].Direction != a.EnsembleDirection) continue;
                if (tiers[s] == StrategyTier.Proven) provenSupport = true;
                if (supportText.Length > 0) supportText.Append("; ");
                supportText.Append(EdgeText(s));
            }
            double riskScale = provenSupport ? 1.0 : _r.ProbationRiskPercent / 100.0;
            string edgeNote = provenSupport
                ? " | уровень ✓ ДОКАЗАН (полный риск): " + supportText
                : " | уровень ? ПРОБНЫЙ (" + F(_r.ProbationRiskPercent) + "% риска): " + supportText;

            if (a.RegimeConfidence < _r.MinRegimeConfidence)
            {
                RegisterSkip(SkipReason.UnclearRegime,
                    "режим неясен: уверенность " + F(a.RegimeConfidence) + " < " + F(_r.MinRegimeConfidence) + " (" + regimeText + ")", a);
                return;
            }

            double[] features = Features(a, a.EnsembleDirection);
            double pWin = WinProbability(features);
            double confidence = SignalConfidence(a.EnsembleScore, pWin);
            double threshold = EntryThreshold(a.Adx);

            if (confidence < threshold)
            {
                RegisterSkip(SkipReason.LowConfidence,
                    "Low Confidence " + F(confidence) + " < " + F(threshold) + " (ансамбль " + F(a.EnsembleScore) + ", AI P(win) " + F(pWin) + ")", a);
                return;
            }

            double spread = CurrentSpread();
            if (spread / a.Atr > _r.MaxSpreadAtr)
            {
                RegisterSkip(SkipReason.HighSpread, "High Spread: " + Pct(spread / a.Atr) + " ATR при пределе " + Pct(_r.MaxSpreadAtr), a);
                return;
            }

            double slDistance = a.Atr * _r.SlAtrMultiplier;
            double tpDistance = a.Atr * _r.EffectiveTpAtr;
            if (spread / tpDistance > _r.MaxSpreadCostOfProfit)
            {
                RegisterSkip(SkipReason.CostVsProfit, "спред съедает " + Pct(spread / tpDistance) + " потенциальной прибыли при пределе " + Pct(_r.MaxSpreadCostOfProfit), a);
                return;
            }

            double costR = CommissionPrice() / slDistance;
            double ev = ExpectedValueR(pWin, costR);
            if (_r.RequirePositiveEv && ev <= 0)
            {
                RegisterSkip(SkipReason.NegativeEV, "ожидание " + SignedR(ev) + " ≤ 0 при AI P(win) " + F(pWin) + " (комиссия " + F(costR) + "R)", a);
                return;
            }

            if (_sweepActive)
            {
                bool trapForLongs = a.EnsembleDirection > 0 && a.BearishSweepRecent && a.Votes[4].Direction <= 0;
                bool trapForShorts = a.EnsembleDirection < 0 && a.BullishSweepRecent && a.Votes[4].Direction >= 0;
                if (trapForLongs || trapForShorts)
                {
                    RegisterSkip(SkipReason.SweepBlock, "ложный пробой: недавно снята ликвидность против направления входа", a);
                    return;
                }
            }

            TradeType type = a.EnsembleDirection > 0 ? TradeType.Buy : TradeType.Sell;

            int sameDirection = _r.GroupSameDirection(_symbolName, type);
            if (sameDirection >= _r.MaxGroupSameDirection)
            {
                RegisterSkip(SkipReason.Correlated, "в группе связанных с " + _symbolName + " инструментов уже " + sameDirection + " позиц. в сторону " +
                    (type == TradeType.Buy ? "покупки" : "продажи") + " — это была бы та же ставка ещё раз", a);
                return;
            }

            // Суммарный риск открытых позиций: новая сделка получает не больше остатка лимита.
            double openRisk = _r.OpenRiskPercent();
            double riskRoom = _r.MaxOpenRiskPercent - openRisk;
            double riskPercent = Math.Min(_r.RiskPercent * riskScale, riskRoom);
            if (riskPercent <= 0.005)
            {
                RegisterSkip(SkipReason.RiskBudget, "суммарный риск открытых позиций " + F(openRisk) + "% при лимите " + F(_r.MaxOpenRiskPercent) +
                    "% — место освободится, когда стопы уйдут в безубыток или позиции закроются", a);
                return;
            }

            double riskBase = _r.RiskBase == RiskBaseMode.Equity ? _r.Account.Equity : _r.Account.Balance;
            if (!TryComputeVolume(type, riskBase, slDistance, costR, riskPercent, riskRoom, out double volume, out double riskMoney, out string sizeNote))
            {
                RegisterSkip(sizeNote.StartsWith("Margin", StringComparison.Ordinal) ? SkipReason.MarginLimit : SkipReason.AccountTooSmall, sizeNote, a);
                return;
            }

            double slPips = slDistance / _symbol.PipSize;
            double tpPips = tpDistance / _symbol.PipSize;

            double freeMarginBefore = _r.Account.FreeMargin;
            TradeResult result = _r.ExecuteMarketOrder(type, _symbolName, volume, _label, slPips, tpPips);
            if (!result.IsSuccessful || result.Position == null)
            {
                RegisterSkip(SkipReason.OrderFailed, "брокер отклонил ордер: " + result.Error, a);
                return;
            }

            Position p = result.Position;
            var votes = new double[QuantAI_Universal_V6_3.StrategyCount];
            for (int s = 0; s < QuantAI_Universal_V6_3.StrategyCount; s++)
            {
                if (allowed != null && !allowed[s]) continue;
                if (a.Votes[s].Direction == a.EnsembleDirection) votes[s] = a.Votes[s].Strength;
            }

            _meta[p.Id] = new TradeMeta
            {
                Direction = a.EnsembleDirection,
                EntryPrice = p.EntryPrice,
                InitialRiskDistance = slDistance,
                RiskMoney = riskMoney,
                Features = features,
                VoteStrengths = votes,
                OpenBarIndex = a.Index,
            };

            Log("ENTRY " + type + " " + _symbolName + " объём " + volume.ToString("0.########", CultureInfo.InvariantCulture) +
                  " @ " + p.EntryPrice.ToString("F" + _symbol.Digits, CultureInfo.InvariantCulture) +
                  " | SL " + F(_r.SlAtrMultiplier) + " ATR, TP " + F(_r.EffectiveTpAtr) + " ATR | риск " + riskMoney.ToString("F2", CultureInfo.InvariantCulture) +
                  " (" + F(riskMoney / riskBase * 100.0) + "% вместе с комиссией; открытый риск был " + F(openRisk) + "% из " + F(_r.MaxOpenRiskPercent) + "%)" +
                  " | Confidence " + F(confidence) + " (порог " + F(threshold) + "), P(win) " + F(pWin) + ", EV " + SignedR(ev) +
                  " (комиссия " + F(costR) + "R)" + edgeNote +
                  " | " + regimeText + " | голоса: " + VotesText(a) + (sizeNote.Length > 0 ? " | " + sizeNote : ""));
            LogActualMargin(p, freeMarginBefore);
        }

        /// <summary>
        /// Объём от риска через штатные функции платформы (VolumeForFixedRisk / AmountRisked): они
        /// пересчитывают валюту инструмента в валюту счёта по текущему курсу. _symbol.PipValue для этого
        /// не годится — по документации SDK он фиксируется при запуске и не обновляется.
        /// Ниже минимального лота брокера — округление вверх только если минимальный лот укладывается
        /// в «Потолок риска»; иначе вход отклоняется с объяснением.
        /// </summary>
        private bool TryComputeVolume(TradeType type, double riskBase, double slDistance, double costR, double riskPercent, double riskRoom,
                                      out double volume, out double riskMoney, out string note)
        {
            volume = 0;
            riskMoney = 0;
            note = "";

            double effectiveRisk = Math.Min(riskPercent, _r.MaxRiskCapPercent);
            // Стоп + комиссия туда-обратно ≈ заданный риск: на стоп остаётся бюджет / (1 + комиссия в R).
            double budget = riskBase * effectiveRisk / 100.0 / (1.0 + Math.Max(0, costR));
            double slPips = slDistance / _symbol.PipSize;

            if (budget <= 0 || slPips <= 0)
            {
                note = "не удалось рассчитать риск: бюджет " + budget.ToString("F2", CultureInfo.InvariantCulture) + ", стоп " + slPips.ToString("F1", CultureInfo.InvariantCulture) + " п.";
                return false;
            }

            double raw = _symbol.VolumeForFixedRisk(budget, slPips, RoundingMode.Down);
            double normalized = _symbol.NormalizeVolumeInUnits(Math.Min(raw, _symbol.VolumeInUnitsMax), RoundingMode.Down);

            if (normalized < _symbol.VolumeInUnitsMin)
            {
                double minRisk = RiskWithCosts(_symbol.VolumeInUnitsMin, slPips);
                double minRiskPercent = minRisk / riskBase * 100.0;
                // Округление вверх до минимального лота — не больше чем втрое к задуманному риску, в пределах
                // «Потолка риска» и остатка суммарного лимита: пробная сделка не должна раздуваться до полной.
                double roundUpLimit = Math.Min(_r.MaxRiskCapPercent, Math.Min(3.0 * effectiveRisk, riskRoom));
                if (minRisk > 0 && minRiskPercent <= roundUpLimit)
                {
                    volume = _symbol.VolumeInUnitsMin;
                    riskMoney = minRisk;
                    note = "объём поднят до минимального лота: риск " + minRiskPercent.ToString("F2", CultureInfo.InvariantCulture) + "% (потолок " + F(_r.MaxRiskCapPercent) + "%)";
                    return ApplyMarginLimit(type, slPips, ref volume, ref riskMoney, ref note);
                }

                note = "Account Too Small: минимальный лот рискует " + minRisk.ToString("F2", CultureInfo.InvariantCulture) + " (" +
                       minRiskPercent.ToString("F2", CultureInfo.InvariantCulture) + "% счёта), а допустимо " + F(roundUpLimit) +
                       "% (задуманный риск " + F(effectiveRisk) + "% ×3, потолок " + F(_r.MaxRiskCapPercent) + "%, остаток лимита " + F(riskRoom) + "%).";
                return false;
            }

            volume = normalized;
            riskMoney = RiskWithCosts(normalized, slPips);
            return ApplyMarginLimit(type, slPips, ref volume, ref riskMoney, ref note);
        }

        /// <summary>Потеря при срабатывании стопа вместе с комиссией туда-обратно — в валюте счёта.</summary>
        private double RiskWithCosts(double volume, double slPips) => _symbol.AmountRisked(volume, slPips) + (CommissionPerUnit() * volume);

        /// <summary>
        /// Залог сделки не должен превышать «Макс. маржа на сделку» от СВОБОДНОЙ маржи. Берётся самая
        /// большая из трёх оценок (см. EstimateMargin): в 5.6 при недоступной оценке брокера проверка
        /// пропускалась целиком. Объём уменьшается пропорционально; если
        /// не укладывается даже минимальный лот или залог оценить нельзя — вход пропускается.
        /// </summary>
        private bool ApplyMarginLimit(TradeType type, double slPips, ref double volume, ref double riskMoney, ref string note)
        {
            double freeMargin = _r.Account.FreeMargin;
            double budget = freeMargin * _r.MaxMarginPercent / 100.0;
            double margin = EstimateMargin(type, volume, out string detail);

            if (margin <= 0)
            {
                note = "Margin: не удалось оценить залог (" + detail + ") — вход пропущен";
                return false;
            }

            if (margin > budget)
            {
                // Объём сравнивается с минимальным лотом ДО округления: NormalizeVolumeInUnits не опускает
                // объём ниже минимального лота даже при RoundingMode.Down (0.83 → 1).
                double target = budget > 0 ? volume * budget / margin : 0;
                double scaled = target >= _symbol.VolumeInUnitsMin ? _symbol.NormalizeVolumeInUnits(target, RoundingMode.Down) : 0;
                double scaledMargin = scaled > 0 ? EstimateMargin(type, scaled, out detail) : 0;
                if (scaled <= 0 || scaledMargin <= 0 || scaledMargin > budget)
                {
                    double minMargin = EstimateMargin(type, _symbol.VolumeInUnitsMin, out _);
                    note = "Margin: залог минимального лота " + M(minMargin) + " превышает " + F(_r.MaxMarginPercent) +
                           "% свободной маржи (" + M(freeMargin) + ", лимит " + M(budget) + ")";
                    return false;
                }

                double before = volume;
                volume = scaled;
                riskMoney = RiskWithCosts(scaled, slPips);
                margin = scaledMargin;
                note = (note.Length > 0 ? note + "; " : "") + "объём уменьшен " + before.ToString("0.########", CultureInfo.InvariantCulture) +
                       "→" + scaled.ToString("0.########", CultureInfo.InvariantCulture) + " по марже";
            }

            note = (note.Length > 0 ? note + "; " : "") + "залог ≈" + M(margin) + " (" + detail + ") из свободных " + M(freeMargin) +
                   ", лимит " + F(_r.MaxMarginPercent) + "% = " + M(budget);
            return true;
        }

        /// <summary>
        /// Залог объёма в валюте счёта — максимум из доступных оценок:
        ///  • брокер: Symbol.GetEstimatedMargin;
        ///  • расчёт: стоимость позиции в валюте счёта / плечо (меньшее из плеча инструмента и счёта).
        ///    Стоимость в валюте счёта = AmountRisked(объём, 1 пункт) / размер пункта × цена — пересчёт
        ///    валют делает сама платформа по текущему курсу;
        ///  • факт: залог уже открытых позиций по этому инструменту (Position.Margin) на единицу объёма.
        /// 0 — если ни одна оценка недоступна.
        /// </summary>
        private double EstimateMargin(TradeType type, double volume, out string detail)
        {
            double broker = Valid(_symbol.GetEstimatedMargin(type, volume));
            double own = Valid(OwnMarginEstimate(type, volume));
            double perUnit = ObservedMarginPerUnit();
            double observed = perUnit > 0 ? Valid(perUnit * volume) : 0;

            detail = "брокер " + (broker > 0 ? M(broker) : "н/д") + ", расчёт " + (own > 0 ? M(own) : "н/д") +
                     (observed > 0 ? ", факт " + M(observed) : "");
            return Math.Max(broker, Math.Max(own, observed));
        }

        private double OwnMarginEstimate(TradeType type, double volume)
        {
            double price = type == TradeType.Buy ? _symbol.Ask : _symbol.Bid;
            double perPip = _symbol.AmountRisked(volume, 1.0);
            double leverage = EffectiveLeverage();
            if (!(price > 0) || !(perPip > 0) || !(_symbol.PipSize > 0) || !(leverage > 0)) return 0;
            double notional = perPip / _symbol.PipSize * price;
            return notional / leverage;
        }

        private double EffectiveLeverage()
        {
            double leverage = _r.Account.PreciseLeverage;
            if (_symbol.DynamicLeverage != null && _symbol.DynamicLeverage.Count > 0)
            {
                double symbolLeverage = _symbol.DynamicLeverage[0].Leverage;
                if (symbolLeverage > 0) leverage = leverage > 0 ? Math.Min(leverage, symbolLeverage) : symbolLeverage;
            }
            return leverage;
        }

        /// <summary>Наибольший фактический залог на единицу объёма среди открытых позиций счёта по инструменту либо по последней своей сделке.</summary>
        private double ObservedMarginPerUnit()
        {
            double best = _observedMarginPerUnit;
            foreach (Position p in _r.Positions)
            {
                if (p.SymbolName != _symbolName || !(p.VolumeInUnits > 0)) continue;
                double perUnit = Valid(p.Margin) / p.VolumeInUnits;
                if (perUnit > best) best = perUnit;
            }
            return best;
        }

        /// <summary>После входа — фактический залог от брокера; если он заметно больше лимита, это видно в журнале.</summary>
        private void LogActualMargin(Position p, double freeMarginBefore)
        {
            double actual = Valid(p.Margin);
            if (actual <= 0 || !(p.VolumeInUnits > 0)) return;
            _observedMarginPerUnit = actual / p.VolumeInUnits;
            double limit = freeMarginBefore * _r.MaxMarginPercent / 100.0;
            double share = freeMarginBefore > 0 ? actual / freeMarginBefore * 100.0 : 0;
            Log((actual > limit * 1.10 ? "ВНИМАНИЕ: " : "") + "фактический залог позиции " + p.Id + ": " + M(actual) +
                " = " + share.ToString("F1", CultureInfo.InvariantCulture) + "% свободной маржи до входа (лимит " + F(_r.MaxMarginPercent) + "%)" +
                (actual > limit * 1.10 ? " — следующие входы по " + _symbolName + " будут считаться по этому факту" : "") + ".");
        }

        /// <summary>При старте: залог минимального лота по всем оценкам — чтобы сразу было видно, чему верить.</summary>
        private void LogMarginProbe()
        {
            double min = _symbol.VolumeInUnitsMin;
            double margin = EstimateMargin(TradeType.Buy, min, out string detail);
            Log("залог минимального лота " + _symbol.VolumeInUnitsToQuantity(min).ToString("0.########", CultureInfo.InvariantCulture) +
                " лота: " + (margin > 0 ? M(margin) : "н/д") + " (" + detail + "), плечо 1:" + EffectiveLeverage().ToString("0.##", CultureInfo.InvariantCulture) +
                ", свободная маржа " + M(_r.Account.FreeMargin) + ".");
        }

        private static double Valid(double v) => double.IsNaN(v) || double.IsInfinity(v) || v <= 0 ? 0 : v;

        private static string M(double v) => v.ToString("F2", CultureInfo.InvariantCulture);


        // =============================================================================================
        //  СОПРОВОЖДЕНИЕ
        // =============================================================================================

        /// <summary>На каждом тике: при +1R — частичное закрытие и перенос стопа в безубыток + 0.1R.</summary>
        private void ManagePositionsOnTick()
        {
            foreach (Position p in OwnPositions())
            {
                if (!_meta.TryGetValue(p.Id, out TradeMeta m) || m.BreakevenDone) continue;

                int dir = p.TradeType == TradeType.Buy ? 1 : -1;
                double price = dir > 0 ? _symbol.Bid : _symbol.Ask;
                double r = (price - m.EntryPrice) * dir / m.InitialRiskDistance;
                if (r < _r.BreakevenTriggerR) continue;

                if (_r.UsePartialClose && !m.PartialDone)
                {
                    double part = _symbol.NormalizeVolumeInUnits(p.VolumeInUnits * _r.PartialClosePercent / 100.0, RoundingMode.Down);
                    double rest = p.VolumeInUnits - part;
                    if (part >= _symbol.VolumeInUnitsMin && rest >= _symbol.VolumeInUnitsMin)
                    {
                        double estimatedProfit = p.NetProfit * part / p.VolumeInUnits;
                        TradeResult closeResult = _r.ClosePosition(p, part);
                        if (closeResult.IsSuccessful)
                        {
                            m.RealizedPartialProfit += estimatedProfit;
                            Log("PARTIAL " + _symbolName + ": закрыто " + F(_r.PartialClosePercent) + "% на +" + F(r) + "R, зафиксировано ~" +
                                  estimatedProfit.ToString("F2", CultureInfo.InvariantCulture));
                        }
                    }
                    else
                    {
                        Log("PARTIAL пропущено: объём " + p.VolumeInUnits + " не делится на части не меньше минимального лота " + _symbol.VolumeInUnitsMin + ".");
                    }
                    m.PartialDone = true;
                }

                double breakeven = m.EntryPrice + (dir * _r.BreakevenOffsetR * m.InitialRiskDistance);
                bool improves = p.StopLoss == null || (dir > 0 ? breakeven > p.StopLoss.Value : breakeven < p.StopLoss.Value);
                bool valid = dir > 0 ? breakeven < _symbol.Bid : breakeven > _symbol.Ask;

                if (improves && valid)
                {
                    TradeResult mod = _r.ModifyPosition(p, breakeven, p.TakeProfit, ProtectionType.Absolute);
                    if (mod.IsSuccessful)
                    {
                        m.BreakevenDone = true;
                        Log("BREAKEVEN " + _symbolName + ": стоп перенесён на " + breakeven.ToString("F" + _symbol.Digits, CultureInfo.InvariantCulture) +
                              " (вход + " + F(_r.BreakevenOffsetR) + "R).");
                    }
                }
                else if (!improves)
                {
                    m.BreakevenDone = true;
                }
            }
        }

        /// <summary>На каждом баре: ATR-трейлинг для позиций, уже переведённых в безубыток.</summary>
        private void ManageTrailingOnBar(int closedIndex)
        {
            double atr = _atr.Result[closedIndex];
            if (double.IsNaN(atr) || atr <= 0) return;

            foreach (Position p in OwnPositions())
            {
                if (!_meta.TryGetValue(p.Id, out TradeMeta m) || !m.BreakevenDone) continue;

                int dir = p.TradeType == TradeType.Buy ? 1 : -1;
                double close = _bars.ClosePrices[closedIndex];
                double candidate = close - (dir * _r.TrailAtrMultiplier * atr);
                double breakeven = m.EntryPrice + (dir * _r.BreakevenOffsetR * m.InitialRiskDistance);

                // Трейлинг только подтягивает стоп и никогда не опускает его ниже безубытка.
                candidate = dir > 0 ? Math.Max(candidate, breakeven) : Math.Min(candidate, breakeven);
                bool improves = p.StopLoss == null || (dir > 0 ? candidate > p.StopLoss.Value + _symbol.TickSize : candidate < p.StopLoss.Value - _symbol.TickSize);
                bool valid = dir > 0 ? candidate < _symbol.Bid : candidate > _symbol.Ask;
                if (!improves || !valid) continue;

                _r.ModifyPosition(p, candidate, p.TakeProfit, ProtectionType.Absolute);
            }
        }

        /// <summary>После перезапуска: восстанавливает сведения об уже открытых позициях бота.</summary>
        private void RebuildMetaForOpenPositions()
        {
            foreach (Position p in OwnPositions())
            {
                if (_meta.ContainsKey(p.Id) || p.StopLoss == null) continue;

                int dir = p.TradeType == TradeType.Buy ? 1 : -1;
                double distance = Math.Abs(p.EntryPrice - p.StopLoss.Value);
                bool alreadyProtected = (p.StopLoss.Value - p.EntryPrice) * dir >= 0;

                double atr = _atr.Result[Math.Max(0, _bars.Count - 2)];
                if (alreadyProtected || distance <= 0) distance = double.IsNaN(atr) ? _symbol.PipSize * 10 : atr * _r.SlAtrMultiplier;

                _meta[p.Id] = new TradeMeta
                {
                    Direction = dir,
                    EntryPrice = p.EntryPrice,
                    InitialRiskDistance = distance,
                    RiskMoney = _symbol.AmountRisked(p.VolumeInUnits, distance / _symbol.PipSize),
                    PartialDone = alreadyProtected,
                    BreakevenDone = alreadyProtected,
                    OpenBarIndex = _bars.Count - 2,
                };
                Log("Позиция " + p.Id + " подхвачена после перезапуска (сопровождение продолжается, обучение по ней не ведётся).");
            }
        }

        // =============================================================================================
        //  ОБУЧЕНИЕ НА РЕАЛЬНЫХ СДЕЛКАХ
        // =============================================================================================

        public void OnPositionClosed(PositionClosedEventArgs args)
        {
            Position p = args.Position;
            if (!IsMine(p)) return;

            _lastTradeCloseIndex = _bars.Count - 2;
            _historyCommissionPerUnit = Math.Max(_historyCommissionPerUnit, HistoryCommissionPerUnit());

            if (!_meta.TryGetValue(p.Id, out TradeMeta m))
            {
                Log("CLOSE " + p.Id + ": " + p.NetProfit.ToString("F2", CultureInfo.InvariantCulture) + " (без данных для обучения).");
                return;
            }

            _meta.Remove(p.Id);

            double totalProfit = p.NetProfit + m.RealizedPartialProfit;
            double resultR = m.RiskMoney > 0 ? totalProfit / m.RiskMoney : 0;
            bool win = m.PartialDone || totalProfit > 0;

            _realTrades++;
            if (win) _realWins++;

            if (m.Features != null && m.VoteStrengths != null)
            {
                _nbReal.Add(m.Features, win, 1.0);

                // Обучение с подкреплением: веса стратегий, голосовавших за сделку, меняются по её результату.
                var changes = new StringBuilder();
                for (int s = 0; s < QuantAI_Universal_V6_3.StrategyCount; s++)
                {
                    if (m.VoteStrengths[s] <= 0) continue;
                    double before = _realWeights[s];
                    double step = _r.RlLearningRate * m.VoteStrengths[s] * Math.Max(-2.0, Math.Min(3.0, resultR)) * 0.5;
                    _realWeights[s] = Math.Max(0.25, Math.Min(3.0, before * Math.Exp(step)));
                    changes.Append(QuantAI_Universal_V6_3.StrategyNames[s]).Append(' ').Append(F(before)).Append("→").Append(F(_realWeights[s])).Append("; ");
                }

                Log("CLOSE " + _symbolName + " " + (win ? "WIN" : "LOSS") + " " + F(resultR) + "R (" +
                      totalProfit.ToString("F2", CultureInfo.InvariantCulture) + ") | RL-веса: " + changes);
            }

            SaveMemory();
        }

        // =============================================================================================
        //  РИСК: СУТКИ И ПРОСАДКА
        // =============================================================================================





        // =============================================================================================
        //  ПАМЯТЬ AI (LocalStorage)
        // =============================================================================================

        /// <summary>
        /// Сохраняются только знания из РЕАЛЬНЫХ сделок: RL-веса и статистика Naive Bayes. Теневые знания
        /// заново строятся из истории при каждом запуске — иначе одни и те же бары засчитывались бы
        /// повторно после каждого перезапуска.
        /// </summary>
        /// <summary>Память на уровне устройства: её видит и следующая версия бота, а не только эта сборка.</summary>
        private const LocalStorageScope MemoryScope = LocalStorageScope.Device;

        public void SaveMemory()
        {
            try
            {
                var sb = new StringBuilder("v5;");
                for (int s = 0; s < QuantAI_Universal_V6_3.StrategyCount; s++) sb.Append(R(_realWeights[s])).Append(';');
                sb.Append(R(_nbReal.ClassWeight[0])).Append(';').Append(R(_nbReal.ClassWeight[1])).Append(';');
                for (int c = 0; c < 2; c++)
                {
                    for (int f = 0; f < NaiveBayesModel.FeatureCount; f++)
                    {
                        GaussianStat g = _nbReal.Stats[c, f];
                        sb.Append(R(g.W)).Append(';').Append(R(g.Mean)).Append(';').Append(R(g.M2)).Append(';');
                    }
                }
                sb.Append(_realTrades).Append(';').Append(_realWins);

                _r.LocalStorage.SetString(_storageKey, sb.ToString(), MemoryScope);
                _r.LocalStorage.Flush(MemoryScope);
            }
            catch (Exception ex)
            {
                Log("Не удалось сохранить память AI: " + ex.Message);
            }
        }

        private void LoadMemory()
        {
            try
            {
                string text = _r.LocalStorage.GetString(_storageKey, MemoryScope);
                if (string.IsNullOrEmpty(text) || !text.StartsWith("v5;", StringComparison.Ordinal))
                {
                    Log("Память AI для " + _symbolName + " " + _timeFrame + " не найдена — начинаю с нейтральных весов.");
                    return;
                }

                string[] parts = text.Split(';');
                int expected = 1 + QuantAI_Universal_V6_3.StrategyCount + 2 + (2 * NaiveBayesModel.FeatureCount * 3) + 2;
                if (parts.Length < expected)
                {
                    Log("Память AI повреждена или устарела — игнорирую.");
                    return;
                }

                int idx = 1;
                for (int s = 0; s < QuantAI_Universal_V6_3.StrategyCount; s++) _realWeights[s] = Math.Max(0.25, Math.Min(3.0, P(parts[idx++], 1.0)));
                _nbReal.ClassWeight[0] = Math.Max(0, P(parts[idx++], 0));
                _nbReal.ClassWeight[1] = Math.Max(0, P(parts[idx++], 0));
                for (int c = 0; c < 2; c++)
                {
                    for (int f = 0; f < NaiveBayesModel.FeatureCount; f++)
                    {
                        GaussianStat g = _nbReal.Stats[c, f];
                        g.W = Math.Max(0, P(parts[idx++], 0));
                        g.Mean = P(parts[idx++], 0);
                        g.M2 = Math.Max(0, P(parts[idx++], 0));
                    }
                }
                _realTrades = (int)P(parts[idx++], 0);
                _realWins = (int)P(parts[idx], 0);

                Log("Память AI загружена: реальных сделок " + _realTrades + ", веса " + WeightsText() + ".");
            }
            catch (Exception ex)
            {
                _nbReal.Clear();
                for (int s = 0; s < QuantAI_Universal_V6_3.StrategyCount; s++) _realWeights[s] = 1.0;
                Log("Не удалось прочитать память AI (" + ex.Message + ") — начинаю с нуля.");
            }
        }

        private static string StorageKey(string raw)
        {
            var sb = new StringBuilder(raw.Length);
            foreach (char ch in raw)
            {
                bool latin = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9');
                sb.Append(latin ? ch : ' ');
            }
            string key = sb.ToString().Trim();
            while (key.Contains("  ")) key = key.Replace("  ", " ");
            return key.Length > 0 ? key : "QAIv5";
        }

        private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        private static double P(string s, double fallback) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && !double.IsNaN(v) && !double.IsInfinity(v) ? v : fallback;

        // =============================================================================================
        //  ЖУРНАЛ
        // =============================================================================================

        private void RegisterSkip(SkipReason reason, string detail, BarAnalysis a)
        {
            _skipCounts.TryGetValue(reason, out int n);
            _skipCounts[reason] = n + 1;

            if (_r.SkipLog == SkipLogMode.None) return;
            if (_r.SkipLog == SkipLogMode.Important && IsRoutine(reason)) return;

            string context = a == null
                ? ""
                : " | " + RegimeLabel(a) + " | ADX " + a.Adx.ToString("F1", CultureInfo.InvariantCulture) +
                  " | DD " + _r.DrawdownPercent().ToString("F2", CultureInfo.InvariantCulture) + "%";
            Log("SKIP: " + SkipTitle(reason) + " — " + detail + context);
        }

        /// <summary>Рутинные пропуски (сигнала нет, рынок закрыт, позиция уже есть) — только в сводке, не в журнале.</summary>
        private static bool IsRoutine(SkipReason r) =>
            r == SkipReason.NoSignal || r == SkipReason.PositionOpen || r == SkipReason.MarketClosed || r == SkipReason.OffHours ||
            r == SkipReason.Warmup || r == SkipReason.Cooldown || r == SkipReason.SessionBlock ||
            r == SkipReason.UnclearRegime || r == SkipReason.LowConfidence;

        private static string SkipTitle(SkipReason r)
        {
            switch (r)
            {
                case SkipReason.NoSignal: return "No Signal";
                case SkipReason.LowConfidence: return "Low Confidence";
                case SkipReason.HighSpread: return "High Spread";
                case SkipReason.CostVsProfit: return "High Spread vs Profit";
                case SkipReason.NegativeEV: return "Negative EV";
                case SkipReason.UnclearRegime: return "Unclear Regime";
                case SkipReason.SessionBlock: return "Session Block";
                case SkipReason.DailyLossLimit: return "Daily Loss Limit";
                case SkipReason.PositionOpen: return "Position Open";
                case SkipReason.Cooldown: return "Cooldown";
                case SkipReason.SweepBlock: return "Liquidity Sweep";
                case SkipReason.AccountTooSmall: return "Account Too Small";
                case SkipReason.MarginLimit: return "Margin Limit";
                case SkipReason.MarketClosed: return "Market Closed";
                case SkipReason.OrderFailed: return "Order Failed";
                case SkipReason.Warmup: return "Warmup";
                case SkipReason.NoEdge: return "No Edge";
                case SkipReason.OffHours: return "Off Hours";
                case SkipReason.Correlated: return "Correlated";
                case SkipReason.RiskBudget: return "Risk Budget";
                default: return r.ToString();
            }
        }

        private string RegimeLabel(BarAnalysis a)
        {
            string kind;
            if (a.PTrend >= 0.5 + (_r.MinRegimeConfidence / 2.0))
            {
                kind = a.TrendSlopeSign > 0 ? "ТРЕНД↑" : a.TrendSlopeSign < 0 ? "ТРЕНД↓" : "ТРЕНД";
            }
            else if (a.PTrend <= 0.5 - (_r.MinRegimeConfidence / 2.0))
            {
                kind = "ФЛЭТ";
            }
            else
            {
                kind = "ПЕРЕХОД";
            }
            return kind + " P(тренд) " + F(a.PTrend);
        }

        private string VotesText(BarAnalysis a)
        {
            var sb = new StringBuilder();
            for (int s = 0; s < QuantAI_Universal_V6_3.StrategyCount; s++)
            {
                StrategyVote v = a.Votes[s];
                if (v.Direction == 0) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(QuantAI_Universal_V6_3.StrategyNames[s]).Append(v.Direction > 0 ? " ▲" : " ▼").Append(F(v.Strength)).Append("×w").Append(F(EffectiveWeight(s)));
            }
            return sb.Length > 0 ? sb.ToString() : "нет";
        }

        private string WeightsText()
        {
            var sb = new StringBuilder();
            for (int s = 0; s < QuantAI_Universal_V6_3.StrategyCount; s++)
            {
                if (s > 0) sb.Append(", ");
                sb.Append(QuantAI_Universal_V6_3.StrategyNames[s]).Append(' ').Append(F(EffectiveWeight(s)));
            }
            return sb.ToString();
        }

        public string BuildStatsLine()
        {
            double realRate = _realTrades > 0 ? (double)_realWins / _realTrades * 100.0 : 0;
            double shadowRate = _shadowResolved > 0 ? (double)_shadowWins / _shadowResolved * 100.0 : 0;
            double shadowAvg = _shadowResolved > 0 ? _shadowSumR / _shadowResolved : 0;
            return "Реальных сделок " + _realTrades + " (в плюс " + realRate.ToString("F0", CultureInfo.InvariantCulture) +
                   "%), теневых " + _shadowResolved + " (в плюс " + shadowRate.ToString("F0", CultureInfo.InvariantCulture) +
                   "%, в среднем " + SignedR(shadowAvg) + ").";
        }

        private void PrintStatus(int closedIndex, BarAnalysis a)
        {
            var sb = new StringBuilder();
            sb.Append("STATUS ").Append(_symbolName).Append(' ').Append(_timeFrame);
            if (a != null)
            {
                sb.Append(" | ").Append(RegimeLabel(a))
                  .Append(" | ADX ").Append(a.Adx.ToString("F1", CultureInfo.InvariantCulture))
                  .Append(" | ATR ").Append(a.Atr.ToString("F" + _symbol.Digits, CultureInfo.InvariantCulture))
                  .Append(" | спред/ATR ").Append(Pct(CurrentSpread() / a.Atr))
                  .Append(" | порог входа ").Append(F(EntryThreshold(a.Adx)));
            }
            sb.Append(" | капитал ").Append(_r.Account.Equity.ToString("F2", CultureInfo.InvariantCulture))
              .Append(" | за сутки ").Append(_r.DailyPnlPercent().ToString("F2", CultureInfo.InvariantCulture)).Append('%')
              .Append(" | просадка ").Append(_r.DrawdownPercent().ToString("F2", CultureInfo.InvariantCulture)).Append('%')
              .Append(" | риск открытых ").Append(F(_r.OpenRiskPercent())).Append("% из ").Append(F(_r.MaxOpenRiskPercent)).Append('%')
              .Append(_r.DailyLocked ? " | ДНЕВНОЙ ЛИМИТ" : "");
            Log(sb.ToString());

            Log("  AI: " + BuildStatsLine() + " Веса: " + WeightsText() + ". Открытых теневых: " + _shadowTrades.Count + ".");
            Log("  " + EdgeReport());

            if (_skipCounts.Count > 0)
            {
                var list = new List<KeyValuePair<SkipReason, int>>(_skipCounts);
                list.Sort((x, y) => y.Value.CompareTo(x.Value));
                var skips = new StringBuilder("  Причины пропусков: ");
                for (int k = 0; k < list.Count && k < 6; k++)
                {
                    if (k > 0) skips.Append(", ");
                    skips.Append(SkipTitle(list[k].Key)).Append(' ').Append(list[k].Value);
                }
                Log(skips.ToString());
            }
        }

        private static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

        private static string Pct(double fraction) => (fraction * 100.0).ToString("F1", CultureInfo.InvariantCulture) + "%";
    }
}
