using System;
using Quant.Core.Config;
using Quant.Core.Ev;
using Quant.Core.Exits;
using Quant.Core.Primitives;
using Quant.Core.Probability;
using Quant.Core.Stats;
using Xunit;
using Xunit.Abstractions;

namespace Quant.Core.Tests;

/// <summary>
/// Откуда система берёт доказательство преимущества, пока у неё нет сделок.
///
/// Без ответа на этот вопрос бот не совершил ни одной сделки — и не мог. Вероятность без
/// истории была константой: (безубыточность + априор) / 2 = 0.368 при любом сигнале, любом
/// рынке и любой уверенности. Ожидание после издержек — всегда −0.10R, требуемое — +0.23R.
///
/// Единственный тест, доказывавший, что система вообще торгует, задавал априорный винрейт
/// 80% — «способ гарантированно провести кандидата через экономические проверки». Тупик
/// обходили в тесте ровно там, где он был.
/// </summary>
public class EvidenceTests
{
    private readonly ITestOutputHelper _out;
    public EvidenceTests(ITestOutputHelper output) { _out = output; }

    private static (BayesianProbabilityModel Model, PerformanceStore Store, EngineConfig Config) Fresh()
    {
        var config = new EngineConfig();
        Presets.CryptoSmallTimeframe(config, Tf.M5);
        var store = new PerformanceStore(config.Adaptation);
        var model = new BayesianProbabilityModel(config.Probability, config.Ev, store, new CalibrationTracker(config.Probability));
        return (model, store, config);
    }

    private static ProbabilityEstimate Ask(BayesianProbabilityModel model, double confidence, double rr = 1.88) =>
        model.Estimate(new ProbabilityQuery
        {
            StrategyName = "TrendFollowing", SymbolName = "BTCUSD", Regime = MarketRegime.TrendUp,
            Direction = Side.Long, EnsembleConfidence = confidence, RewardToRisk = rr,
        });

    /// <summary>Виртуальная сделка: вход 100, стоп 99, цель 100 + rr.</summary>
    private static TradeRecord Virtual(ExitReason reason, double exitPrice, double rr = 1.88) => new TradeRecord
    {
        TradeId = Guid.NewGuid().ToString("N"), StrategyName = "TrendFollowing", SymbolName = "BTCUSD",
        Direction = Side.Long, Regime = MarketRegime.TrendUp,
        EntryPrice = 100, InitialStopPrice = 99, InitialTargetPrice = 100 + rr, ExitPrice = exitPrice,
        ExitReason = reason, IsVirtual = true, Mode = OperatingMode.Paper,
    };

    [Fact]
    public void WithoutEvidenceTheProbabilityIgnoresTheSignalEntirely()
    {
        // Документирует дефект: без доказательств вероятность не зависит ни от чего,
        // кроме настроек. Если это когда-нибудь станет неверным, значит, появился другой
        // источник информации — и этот тест нужно будет переосмыслить, а не удалить.
        var (model, _, _) = Fresh();

        double weak = Ask(model, 0.55).PWin;
        double strong = Ask(model, 0.95).PWin;

        _out.WriteLine($"без доказательств: P = {weak:F3} при слабом сигнале и {strong:F3} при сильном");
        Assert.Equal(weak, strong, 9);
    }

    [Fact]
    public void EachOutcomeIsCountedAsTheSameBetTheExpectationFormulaPrices()
    {
        // q = (R + L) / (RR + L): цель — целый выигрыш, стоп — целый проигрыш, выход по
        // времени — ровно столько, сколько он стоил. Считать выход с +0.3R «выигрышем»
        // значило бы платить за него в формуле как за +1.88R.
        var (target, _, _) = Fresh();
        for (int i = 0; i < 40; i++) target.ObserveEvidence(Virtual(ExitReason.TakeProfit1, 101.88));

        var (stop, _, _) = Fresh();
        for (int i = 0; i < 40; i++) stop.ObserveEvidence(Virtual(ExitReason.StopLoss, 99));

        var (flat, _, _) = Fresh();
        for (int i = 0; i < 40; i++) flat.ObserveEvidence(Virtual(ExitReason.TimeStop, 100.0));

        double pTarget = Ask(target, 0.7).PWin, pStop = Ask(stop, 0.7).PWin, pFlat = Ask(flat, 0.7).PWin;
        _out.WriteLine($"сорок целей: P={pTarget:F3}, сорок стопов: P={pStop:F3}, сорок выходов в ноль: P={pFlat:F3}");

        Assert.True(pTarget > pFlat && pFlat > pStop);

        // Выход в ноль эквивалентен ставке с нулевым валовым ожиданием: безубыточности.
        double breakEven = 1.05 / (1.88 + 1.05);
        Assert.InRange(pFlat, breakEven - 0.03, breakEven + 0.03);
    }

    [Fact]
    public void AFewLuckyShadowWinsDoNotOpenTheGate()
    {
        // Априорная сила защищает от везения: пять побед подряд — не доказательство.
        var (model, _, config) = Fresh();
        for (int i = 0; i < 5; i++) model.ObserveEvidence(Virtual(ExitReason.TakeProfit1, 101.88));

        ExpectedValueResult r = Evaluate(model, config);
        _out.WriteLine($"пять теневых побед: P={r.WinProbability:F3}, EV={r.ExpectedValueR:F3}R, нужно {r.RequiredEdgeR:F3}R");

        Assert.False(r.IsAcceptable);
    }

    [Fact]
    public void ASustainedShadowEdgeOpensTheGateWithASmallSize()
    {
        // Шестьдесят теневых сделок, из них 62% дошли до полной цели. Это преимущество, и
        // система обязана его увидеть — но доверять ему не полностью: размер малый.
        var (model, _, config) = Fresh();
        for (int i = 0; i < 60; i++)
        {
            bool win = (i * 62 / 100) != ((i + 1) * 62 / 100);
            model.ObserveEvidence(win ? Virtual(ExitReason.TakeProfit1, 101.88) : Virtual(ExitReason.StopLoss, 99));
        }

        ExpectedValueResult r = Evaluate(model, config);
        _out.WriteLine($"60 теневых сделок, 62% целей: P={r.WinProbability:F3}, EV={r.ExpectedValueR:F3}R, " +
                       $"нужно {r.RequiredEdgeR:F3}R, доверие {r.Trust:P0}");

        Assert.True(r.IsAcceptable, r.ToString());
        Assert.True(r.Trust < 0.6, $"доверие {r.Trust:P0} к одним лишь теневым сделкам слишком велико");
    }

    [Fact]
    public void EvidenceNeverTouchesRealStatistics()
    {
        // Теневые исходы идут только в модель вероятности. Не в риск-движок, не в счётчик
        // холодного старта, не в веса стратегий: иначе виртуальные победы снимали бы
        // защиту, заработанную реальными деньгами.
        var (model, store, _) = Fresh();
        for (int i = 0; i < 50; i++) model.ObserveEvidence(Virtual(ExitReason.TakeProfit1, 101.88));

        Assert.Equal(0, store.TotalTrades);
        double before = model.TotalEvidenceTrades;
        Assert.True(before > 40, $"накоплено лишь {before:F1} при затухании — меньше ожидаемого");

        // И реальную сделку за доказательство не выдать.
        var real = Virtual(ExitReason.TakeProfit1, 101.88);
        var notVirtual = new TradeRecord
        {
            StrategyName = real.StrategyName, EntryPrice = real.EntryPrice, InitialStopPrice = real.InitialStopPrice,
            InitialTargetPrice = real.InitialTargetPrice, ExitPrice = real.ExitPrice, IsVirtual = false,
        };
        model.ObserveEvidence(notVirtual);
        Assert.Equal(before, model.TotalEvidenceTrades, 9);
    }

    [Fact]
    public void OldEvidenceFadesSoAChangedMarketCanChangeTheVerdict()
    {
        // Двести стопов, затем двести целей. Без затухания оценка застряла бы посередине;
        // с затуханием последние сотни сделок весят больше, чем давние.
        var (model, _, _) = Fresh();
        for (int i = 0; i < 200; i++) model.ObserveEvidence(Virtual(ExitReason.StopLoss, 99));
        double afterLosses = Ask(model, 0.7).PWin;

        for (int i = 0; i < 200; i++) model.ObserveEvidence(Virtual(ExitReason.TakeProfit1, 101.88));
        double afterWins = Ask(model, 0.7).PWin;

        _out.WriteLine($"после 200 стопов P={afterLosses:F3}, после ещё 200 целей P={afterWins:F3}");
        Assert.True(afterWins > 0.5, "свежие доказательства не перевесили давние");
    }

    [Fact]
    public void OnAMarketWithAnEdgeTradingContinuesPastTheColdStart()
    {
        // Прогон, на котором раньше был обрыв: двадцать сделок, затем ни одной за восемь
        // тысяч баров. Требование после холодного старта подскакивало на три четверти R, а
        // снизить его могли только сделки.
        var config = new EngineConfig { Mode = OperatingMode.Paper };
        Presets.CryptoSmallTimeframe(config, Tf.M5);

        var h = new EngineHarness(config, "BTCUSD", startingEquity: 100_000);
        var market = new MarketSimulator(startPrice: 50000, seed: 777);
        h.FeedWarmUp(market.Generate(config.Data.MinBarsBeforeTrading + 60, 0.00012, 0.0025));
        h.Feed(market.Generate(12000, 0.00012, 0.0025));

        _out.WriteLine($"сделок {h.Engine.TradesClosed}, доказательств {h.Engine.EvidenceTradesClosed:F0}, " +
                       $"капитал {h.Broker.GetAccount().Equity:F0}");

        Assert.True(h.Engine.TradesClosed > 2 * config.Ev.ColdStartTrades,
            $"после холодного старта торговля встала: {h.Engine.TradesClosed} сделок. " +
            h.Engine.Journal.RejectionSummary(topN: 8));
    }

    [Fact]
    public void TheVerdictIsAboutTheMarketNotAboutTheSettings()
    {
        // Раньше отказ был константой из настроек — одинаковой на любом рынке. Теперь тот
        // же код на рынке с преимуществом торгует, а на рынке без него — почти нет и почти
        // ничего не теряет.
        //
        // «Почти» — не допуск, а цена обучения. На случайном блуждании доказательства
        // иногда показывают везучую серию, система пробует малым объёмом, теряет копейки,
        // и доказательства её поправляют. Способность находить преимущество, которое
        // есть, неотделима от редких проб там, где его нет; важно, чтобы пробы были
        // редкими и дешёвыми.
        (long trendTrades, double trendEquity, double _) = Run(0.00012);
        (long walkTrades, double walkEquity, double walkEvidence) = Run(0.0);

        _out.WriteLine($"тренд: {trendTrades} сделок, капитал {trendEquity:F0}");
        _out.WriteLine($"блуждание: {walkTrades} сделок, капитал {walkEquity:F0}, доказательств {walkEvidence:F0}");

        Assert.True(walkEvidence > 50, "на блуждании доказательства не накапливаются");
        Assert.True(walkTrades * 4 <= trendTrades,
            $"система не различает рынки: {walkTrades} сделок без преимущества против {trendTrades} с ним");
        Assert.True(walkEquity >= 100_000 * 0.995,
            $"на рынке без преимущества потеряно {100_000 - walkEquity:F0} — пробы обязаны быть дешёвыми");
    }

    private static (long Trades, double Equity, double Evidence) Run(double drift)
    {
        var config = new EngineConfig { Mode = OperatingMode.Paper };
        Presets.CryptoSmallTimeframe(config, Tf.M5);

        var h = new EngineHarness(config, "BTCUSD", startingEquity: 100_000);
        var market = new MarketSimulator(startPrice: 50000, seed: 777);
        h.FeedWarmUp(market.Generate(config.Data.MinBarsBeforeTrading + 60, drift, 0.0025));
        h.Feed(market.Generate(10000, drift, 0.0025));

        return (h.Engine.TradesClosed, h.Broker.GetAccount().Equity, h.Engine.EvidenceTradesClosed);
    }

    private static ExpectedValueResult Evaluate(BayesianProbabilityModel model, EngineConfig config)
    {
        var ev = new ExpectedValueEngine(config.Ev, new PerformanceStore(config.Adaptation));
        var cost = new CostEstimate(spreadCost: 20, commissionCost: 0, slippageCost: 14, stopDistance: 264);
        return ev.Evaluate(Ask(model, 0.7), 1.88, cost, "TrendFollowing", MarketRegime.TrendUp, "BTCUSD",
            regimeConfidence: 0.7, volatilityStress: 0.1);
    }
}
