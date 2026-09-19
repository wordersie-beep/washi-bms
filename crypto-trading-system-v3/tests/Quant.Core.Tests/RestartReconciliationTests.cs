using System;
using System.Collections.Generic;
using System.Linq;
using Quant.Core;
using Quant.Core.Config;
using Quant.Core.Ev;
using Quant.Core.Execution;
using Quant.Core.Journal;
using Quant.Core.Primitives;
using Quant.Core.Risk;
using Quant.Core.State;
using Xunit;

namespace Quant.Core.Tests;

/// <summary>
/// Рестарт — это момент, когда система знает о рынке меньше всего, а решений от неё ждут
/// столько же. Всё, что могло измениться, пока она была выключена, должно быть выяснено
/// ДО первого решения, а не по расписанию сверки.
/// </summary>
public class RestartReconciliationTests
{
    private static readonly SymbolSpec Spec =
        new SymbolSpec("BTCUSD", 0.01, 0.01, 2, 0.001, 1000, 0.001, 35, 0.01, 0, 2.0, true);

    // ── Уровень стоп-аута ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.5, 50.0)]      // доля
    [InlineData(0.2, 20.0)]      // доля
    [InlineData(50.0, 50.0)]     // уже проценты
    [InlineData(1.0, 100.0)]     // граница: доля
    [InlineData(0.0, 0.0)]       // не сообщён
    [InlineData(-1.0, 0.0)]      // бессмыслица
    public void TheStopOutLevelIsNormalisedToPercent(double raw, double expected)
    {
        // Платформы сообщают его по-разному. Ошибка в сто раз уводит порог опасности либо
        // в ноль — и защита молча выключается, — либо в тысячи процентов, и торговля
        // останавливается навсегда без единой сделки.
        Assert.Equal(expected, AccountSnapshot.NormaliseStopOutLevel(raw), 6);

        var account = new AccountSnapshot(10000, 10000, 0, 10000, 500, raw, false, "USD");
        Assert.Equal(expected, account.StopOutLevelPercent, 6);
    }

    [Fact]
    public void AnAbsurdStopOutLevelCannotHaltTradingForever()
    {
        // Брокер, сообщивший стоп-аут в непривычных единицах, не должен получать
        // возможность остановить торговлю навсегда: это отказ по чужой ошибке, а не по риску.
        var config = new EngineConfig();
        var risk = new RiskEngine(config.Risk, config.Sizing, new DrawdownTracker(),
            new ExecutionQualityTracker(config.Execution), new Stats.PerformanceStore(config.Adaptation),
            new RiskOfRuinEstimator(seed: 1));

        // 9000% стоп-аута — очевидная бессмыслица; уровень маржи здоровый.
        var account = new AccountSnapshot(10000, 10000, 500, 9500, marginLevelPercent: 2000,
            stopOutLevelPercent: 9000, isLive: false, currency: "USD");

        RiskAssessment assessment = risk.Evaluate(RiskFixtures.T0, account, 0, false);

        Assert.True(assessment.AllowsNewPositions,
            $"порог опасности ограничен сверху, поэтому здоровый счёт торгует: {assessment.ReasonSummary}");
    }

    // ── Повтор ордера при неизвестном исходе ─────────────────────────────────────

    [Fact]
    public void AnOrderWhoseOutcomeIsUnknownIsNeverRetried()
    {
        // Повтор после таймаута — это и есть способ получить вторую позицию того же
        // размера: первый ордер мог быть принят, просто ответ не дошёл.
        var config = new EngineConfig();
        var broker = new CountingBroker(outcomeUnknown: true);
        var execution = new ExecutionEngine(config.Execution, broker,
            new ExecutionQualityTracker(config.Execution), new IdempotencyGuard(), "test");

        execution.Open("sig-timeout", "BTCUSD", Side.Long, 1.0, Plan(), Spec, 50000, 1.0, 500, 2.0, null);

        Assert.Equal(1, broker.OpenAttempts);
    }

    [Fact]
    public void AKnownTransientFailureIsStillRetried()
    {
        // Отказ с ответом сервера — нет котировки, реквота — означает, что ордера точно
        // нет. Не повторять его значило бы терять сделки на ровном месте.
        var config = new EngineConfig();
        var broker = new CountingBroker(outcomeUnknown: false);
        var execution = new ExecutionEngine(config.Execution, broker,
            new ExecutionQualityTracker(config.Execution), new IdempotencyGuard(), "test");

        execution.Open("sig-requote", "BTCUSD", Side.Long, 1.0, Plan(), Spec, 50000, 1.0, 500, 2.0, null);

        Assert.Equal(config.Execution.MaxOrderRetries + 1, broker.OpenAttempts);
    }

    // ── Сверка после рестарта ────────────────────────────────────────────────────

    [Fact]
    public void ARestoredPositionWithoutAStopGetsOneBack()
    {
        // Позиция без стопа — это позиция без ограничения убытка, а система при этом
        // считает, что защита есть. Ждать следующего бара нельзя.
        var config = new EngineConfig();
        var broker = new StopTrackingBroker();
        var store = new InMemoryStateStore();

        broker.Add(new BrokerPosition
        {
            PositionId = 77, Label = config.Execution.OrderLabelPrefix + "-test",
            SymbolName = "BTCUSD", Direction = Side.Long, VolumeInUnits = 1.0,
            EntryPrice = 50000, EntryTimeUtc = RiskFixtures.T0, StopLoss = null,
        });

        store.Save(StateWith(new PersistedPosition
        {
            TradeId = "t-77", SignalId = "s-77", BrokerPositionId = 77, SymbolName = "BTCUSD",
            Direction = (int)Side.Long, StrategyName = "Breakout", EntryTimeUtc = RiskFixtures.T0,
            EntryPrice = 50000, InitialVolumeInUnits = 1, CurrentVolumeInUnits = 1,
            InitialStopPrice = 49500, CurrentStopPrice = 49500, Target1Price = 51000,
            RiskPerUnit = 500, RiskAmount = 500,
        }));

        var engine = new TradingEngine(config, broker, store, NullJournalSink.Instance, "test");
        engine.AddSymbol("BTCUSD", Spec);
        engine.Restore(RiskFixtures.T0.AddHours(2));

        Assert.Equal(49500, broker.LastStopSet, 2);
    }

    [Fact]
    public void AMarkedSignalWithNoPositionIsReleasedOnRestart()
    {
        // Отметка ставится ДО отправки ордера и не снимается при неизвестном исходе —
        // иначе повтор открыл бы дубль. Но после рестарта неизвестность разрешима:
        // позиции нет, значит ордер не дошёл, и сигнал должен снова стать доступным.
        var config = new EngineConfig();
        var broker = new StopTrackingBroker();
        var store = new InMemoryStateStore();

        BotState state = StateWith(null);
        state.ExecutedSignalIds.Add("sig-lost-in-timeout");
        store.Save(state);

        var engine = new TradingEngine(config, broker, store, NullJournalSink.Instance, "test");
        engine.AddSymbol("BTCUSD", Spec);
        engine.Restore(RiskFixtures.T0.AddHours(2));

        Assert.False(engine.Idempotency.HasExecuted("sig-lost-in-timeout"));
    }

    // ── Вспомогательное ──────────────────────────────────────────────────────────

    private static BotState StateWith(PersistedPosition position)
    {
        var state = new BotState
        {
            InstanceId = "test", SavedAtUtc = RiskFixtures.T0, Mode = (int)OperatingMode.Paper,
            DayStartUtc = RiskFixtures.T0, DayStartEquity = 100000,
            WeekStartUtc = RiskFixtures.T0, WeekStartEquity = 100000, AllTimePeakEquity = 100000,
        };
        if (position != null) state.Positions.Add(position);
        return state;
    }

    private static Quant.Core.Exits.ExitPlan Plan() => new Quant.Core.Exits.ExitPlan
    {
        IsValid = true, StopPrice = 49500, StopDistance = 500, Target1Price = 50600,
    };

    /// <summary>Брокер, считающий попытки открытия.</summary>
    private sealed class CountingBroker : IBroker
    {
        private readonly bool _unknown;
        public CountingBroker(bool outcomeUnknown) { _unknown = outcomeUnknown; }

        public int OpenAttempts { get; private set; }

        public BrokerResult OpenPosition(string s, Side d, double v, double stop, double? tp, string l, string c, double m)
        {
            OpenAttempts++;
            return _unknown ? BrokerResult.Unknown("таймаут") : BrokerResult.Fail("нет котировки", isTransient: true);
        }

        public BrokerResult ClosePosition(long id, double v) => BrokerResult.Fail("нет", false);
        public BrokerResult ModifyStop(long id, double p) => BrokerResult.Fail("нет", false);
        public IReadOnlyList<BrokerPosition> GetOpenPositions(string prefix) => Array.Empty<BrokerPosition>();
        public AccountSnapshot GetAccount() => new AccountSnapshot(100000, 100000, 0, 100000, 5000, 50, false, "USD");
        public bool IsSimulated => true;
    }

    /// <summary>Брокер, запоминающий последний выставленный стоп.</summary>
    private sealed class StopTrackingBroker : IBroker
    {
        private readonly List<BrokerPosition> _positions = new List<BrokerPosition>();
        public double LastStopSet { get; private set; } = double.NaN;

        public void Add(BrokerPosition p) => _positions.Add(p);

        public BrokerResult OpenPosition(string s, Side d, double v, double stop, double? tp, string l, string c, double m) =>
            BrokerResult.Fail("не используется", false);

        public BrokerResult ClosePosition(long id, double v) => BrokerResult.Fail("не используется", false);

        public BrokerResult ModifyStop(long id, double price)
        {
            LastStopSet = price;
            for (int i = 0; i < _positions.Count; i++)
            {
                if (_positions[i].PositionId != id) continue;
                BrokerPosition old = _positions[i];
                _positions[i] = new BrokerPosition
                {
                    PositionId = old.PositionId, Label = old.Label, SymbolName = old.SymbolName,
                    Direction = old.Direction, VolumeInUnits = old.VolumeInUnits,
                    EntryPrice = old.EntryPrice, EntryTimeUtc = old.EntryTimeUtc, StopLoss = price,
                };
            }
            return BrokerResult.Ok(id, price, 0);
        }

        public IReadOnlyList<BrokerPosition> GetOpenPositions(string prefix) => _positions;
        public AccountSnapshot GetAccount() => new AccountSnapshot(100000, 100000, 0, 100000, 5000, 50, false, "USD");
        public bool IsSimulated => false;
    }
}
