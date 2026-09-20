using System;
using System.Collections.Generic;
using System.Linq;
using Quant.Core;
using Quant.Core.Config;
using Quant.Core.Primitives;
using Quant.Core.Risk;
using Quant.Core.Stats;
using Xunit;
using Xunit.Abstractions;

namespace Quant.Core.Tests;

/// <summary>
/// Сквозной прогон всей системы на синтетическом рынке.
///
/// Отвечает на вопрос, на который не отвечает ни один модульный тест: делает ли она
/// вообще что-нибудь, и что именно. Каждый отдельный слой может быть правильным, а
/// система целиком — молчать месяцами, потому что какой-то порог отсекает всё.
///
/// Синтетический рынок НЕ доказывает прибыльности и не может её доказать: и цены, и
/// исполнение здесь придуманы. Он доказывает другое — что цикл замкнут, решения
/// принимаются, лимиты держатся и ничего не падает.
/// </summary>
public class EndToEndRunTests
{
    private readonly ITestOutputHelper _out;
    public EndToEndRunTests(ITestOutputHelper output) { _out = output; }

    [Fact]
    public void AFullMonthOfTradingRunsWithoutFallingOver()
    {
        var config = new EngineConfig();
        config.Mode = OperatingMode.Paper;

        var harness = new EngineHarness(config, "BTCUSD", startingEquity: 10000);

        // Прогрев: столько баров M5, сколько требуется до первого решения.
        var market = new MarketSimulator(startPrice: 50000, seed: 20260920);
        harness.FeedWarmUp(market.Generate(config.Data.MinBarsBeforeTrading + 60, 0.00004, 0.0016));

        _out.WriteLine("════════ ПОСЛЕ ПРОГРЕВА ════════");
        _out.WriteLine(harness.Engine.RenderHeartbeat(harness.LastTimeUtc));
        _out.WriteLine("");

        // Месяц рынка со сменой характера: тренд вверх, боковик, падение с ростом
        // волатильности, снова тренд. Один режим на весь прогон ничего не проверяет.
        var phases = new (string Name, List<Candle> Bars)[]
        {
            ("тренд вверх",        market.Generate(2000, 0.00012, 0.0018)),
            ("боковик",            market.GenerateRange(2000, market.Price, 1200)),
            ("падение, вола выше", market.Generate(1500, -0.00020, 0.0034)),
            ("тренд вверх снова",  market.Generate(2000, 0.00010, 0.0020)),
        };

        var alerts = new List<string>();
        foreach ((string name, List<Candle> bars) in phases)
        {
            DateTime before = harness.LastTimeUtc;
            long tradesBefore = harness.Engine.TradesClosed;

            harness.Feed(bars);

            foreach (string alert in harness.Engine.CollectAlerts(harness.LastTimeUtc))
            {
                alerts.Add($"[{name}] {alert}");
            }

            _out.WriteLine($"── {name}: {bars.Count} баров M5 ({bars.Count * 5 / 60.0 / 24.0:F1} сут), " +
                           $"сделок закрыто {harness.Engine.TradesClosed - tradesBefore}, " +
                           $"капитал {harness.Broker.GetAccount().Equity:F2}");
        }

        _out.WriteLine("");
        _out.WriteLine("════════ ПОЧЕМУ ТАКАЯ ПОСТУРА РИСКА ════════");
        RiskAssessment risk = harness.Engine.Risk.Evaluate(
            harness.LastTimeUtc, harness.Broker.GetAccount(), 0, false);
        _out.WriteLine($"состояние {risk.State}, множитель {risk.RiskMultiplier:F2}, блокировка {risk.BlockingReason}");
        foreach (string reason in risk.Reasons) _out.WriteLine("  причина: " + reason);

        _out.WriteLine("");
        _out.WriteLine("════════ СООБЩЕНИЯ ОБ ИЗМЕНЕНИЯХ ════════");
        if (alerts.Count == 0) _out.WriteLine("(состояние не менялось между фазами)");
        foreach (string a in alerts.Take(20)) _out.WriteLine(a);

        _out.WriteLine("");
        _out.WriteLine("════════ ИТОГОВЫЙ ДАШБОРД ════════");
        _out.WriteLine(harness.Engine.RenderDashboard(harness.LastTimeUtc));

        // --- Что именно утверждается ------------------------------------------------

        SegmentStats all = harness.Engine.Performance.Get("all");
        AccountSnapshot account = harness.Broker.GetAccount();

        // 1. Ни одного исключения за весь прогон.
        Assert.DoesNotContain(harness.Log, l => l.Contains("ОШИБКА") || l.Contains("Exception"));

        // 2. Кандидаты доходят до ЭКОНОМИЧЕСКИХ ворот.
        //
        //    Требовать сделок было бы неверно: синтетический рынок — случайное блуждание,
        //    преимущества в нём нет, и отказ по отрицательному ожиданию здесь правильное
        //    поведение, а не поломка.
        //
        //    Проверяется другое, и это ровно тот дефект, который был найден: ворота,
        //    не пропускающие НИЧЕГО И НИКОГДА. Отношение прибыли к риску считалось по
        //    первой цели (1.2R) и сравнивалось с минимумом 1.3 — кандидат отвергался в
        //    любом рынке при любых данных, и система не могла совершить ни одной сделки,
        //    хотя каждый её слой по отдельности работал правильно.
        IReadOnlyDictionary<NoTradeReason, int> reasons = harness.Engine.Journal.ReasonCounts;

        Assert.True(reasons.ContainsKey(NoTradeReason.NegativeExpectedValue) ||
                    reasons.ContainsKey(NoTradeReason.InsufficientEdge) ||
                    all.TotalTrades > 0,
            "ни один кандидат не дошёл до оценки ожидания — где-то раньше стоят непроходимые ворота: " +
            string.Join(", ", reasons.OrderByDescending(k => k.Value).Take(5).Select(k => $"{k.Key}={k.Value}")));

        // 3. Ни одни ворота не отвергают всё, что до них доходит, по структурной причине.
        //    Отношение прибыли к риску плана обязано быть достижимым.
        Assert.True(config.Exit.Target1R * config.Exit.Target1ClosePercent +
                    Math.Max(config.Exit.Target1R, config.Exit.Target2R) *
                    (1 - config.Exit.Target1ClosePercent) >= config.Ev.MinRewardToRisk,
            "план выхода не способен достичь минимально допустимого отношения прибыли к риску");

        // 4. Риск на сделку не превышал жёсткий предел НИ РАЗУ.
        IReadOnlyList<TradeRecord> trades = harness.Engine.Performance.Trades;
        double worstRisk = trades.Count == 0 ? 0 : trades.Max(t => t.RiskFractionOfEquity * 100.0);
        Assert.True(worstRisk <= config.Risk.HardMaxRiskPerTradePercent + 1e-9,
            $"риск на сделку {worstRisk:F3}% превысил предел {config.Risk.HardMaxRiskPerTradePercent:F3}%");

        // 5. Капитал не ушёл в минус и не обнулился.
        Assert.True(account.Equity > 0, "капитал обнулился");

        // 6. Состояние сохраняемо и восстановимо.
        harness.Engine.SaveStateForTest(harness.LastTimeUtc);
        Assert.NotNull(harness.Store.Load());

        _out.WriteLine("");
        _out.WriteLine("════════ ПРОВЕРКИ ════════");
        _out.WriteLine($"  исключений за прогон:      0");
        _out.WriteLine($"  сделок совершено:          {all.TotalTrades}");
        _out.WriteLine($"  худший риск на сделку:     {worstRisk:F3}% (предел {config.Risk.HardMaxRiskPerTradePercent:F2}%)");
        _out.WriteLine($"  капитал:                   {account.Equity:F2} (старт 10000.00)");
        _out.WriteLine($"  решений принято:           {harness.Engine.Journal.TotalAccepted + harness.Engine.Journal.TotalRejected}");
    }

    [Fact]
    public void TheSystemRefusesAlmostEverythingAndSaysWhy()
    {
        // Отдельная проверка утверждения из документации: на настройках по умолчанию
        // отклоняется подавляющее большинство сигналов. Если это не так, значит фильтры
        // не работают; если отклоняется всё, значит работают слишком хорошо.
        var config = new EngineConfig();
        config.Mode = OperatingMode.Paper;

        var harness = new EngineHarness(config, "BTCUSD", startingEquity: 10000);
        var market = new MarketSimulator(startPrice: 50000, seed: 771);

        harness.FeedWarmUp(market.Generate(config.Data.MinBarsBeforeTrading + 60, 0.00004, 0.0016));
        harness.Feed(market.Generate(4000, 0.00010, 0.0020));

        long accepted = harness.Engine.Journal.TotalAccepted;
        long rejected = harness.Engine.Journal.TotalRejected;
        long total = accepted + rejected;

        _out.WriteLine($"решений {total}: принято {accepted}, отклонено {rejected} ({(double)rejected / Math.Max(1, total):P2})");
        _out.WriteLine("");
        _out.WriteLine("причины отказа:");

        foreach (KeyValuePair<NoTradeReason, int> kv in harness.Engine.Journal.ReasonCounts
                     .OrderByDescending(k => k.Value).Take(12))
        {
            _out.WriteLine($"  {kv.Key,-28} {kv.Value,6}  {(double)kv.Value / Math.Max(1, rejected):P1}");
        }

        Assert.True(total > 0, "не принято ни одного решения — цикл не дошёл до оценки");
        Assert.True(rejected > accepted, "фильтры пропускают больше, чем отклоняют");
    }
}
