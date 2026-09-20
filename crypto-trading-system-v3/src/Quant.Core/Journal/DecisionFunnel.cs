using System;
using System.Collections.Generic;
using System.Text;
using Quant.Core.Primitives;

namespace Quant.Core.Journal;

/// <summary>
/// Воронка решений: сколько кандидатов дошло до каждого фильтра и сколько его прошло.
///
/// Существует из-за дефекта, который иначе не виден ни одним тестом. Отношение прибыли к
/// риску считалось по первой цели (1.2R) и сравнивалось с минимумом 1.3 — фильтр отвергал
/// КАЖДОГО кандидата, в любом рынке, всегда. Каждый слой при этом работал правильно, все
/// тесты были зелёными, журнал был полон осмысленных отказов, и система не совершала ни
/// одной сделки.
///
/// Снаружи такое неотличимо от осторожности. Отличает его одно число: доля прошедших. У
/// осторожного фильтра она мала, у сломанного — ровно ноль, сколько бы кандидатов через
/// него ни прошло.
///
/// Воронка восстанавливается из счётчика причин отказа: фильтры применяются по порядку и
/// записывается ПЕРВЫЙ сработавший, поэтому до фильтра N дошли все, кого не отвергли
/// фильтры до него.
/// </summary>
public sealed class DecisionFunnel
{
    /// <summary>
    /// Фильтры в том порядке, в каком их применяет <see cref="Decision.TradeGate"/>.
    ///
    /// Порядок здесь дублирует порядок там, и это осознанная цена: альтернатива — заставить
    /// сам TradeGate вести воронку, то есть смешать принятие решения с отчётностью о нём.
    /// Расхождение порядка исказит промежуточные строки отчёта, но не итог: сумма отказов и
    /// доля прошедших до конца останутся верными.
    /// </summary>
    public static readonly NoTradeReason[] GateOrder =
    {
        NoTradeReason.DataQuality,
        NoTradeReason.MarketClosed,
        NoTradeReason.ReconciliationPending,
        NoTradeReason.RiskStateHalt,
        NoTradeReason.DailyLossLimit,
        NoTradeReason.WeeklyLossLimit,
        NoTradeReason.DrawdownLimit,
        NoTradeReason.ConsecutiveLossCooldown,
        NoTradeReason.MarginGuard,
        NoTradeReason.RiskOfRuin,
        NoTradeReason.ExecutionQuality,
        NoTradeReason.AnomalyDetected,
        NoTradeReason.RecoveryPeriod,
        NoTradeReason.NoSignal,
        NoTradeReason.StrategyDisabled,
        NoTradeReason.LowRegimeConfidence,
        NoTradeReason.RegimeMismatch,
        NoTradeReason.LowSignalConfidence,
        NoTradeReason.ExtremeEvent,
        NoTradeReason.SpreadTooWide,
        NoTradeReason.VolatilityTooHigh,
        NoTradeReason.VolatilityTooLow,
        NoTradeReason.Chasing,
        NoTradeReason.SignalExpired,
        NoTradeReason.DuplicateSignal,
        NoTradeReason.InvalidStopPlacement,
        NoTradeReason.PoorRiskReward,
        NoTradeReason.LowProbabilityConfidence,
        NoTradeReason.NegativeExpectedValue,
        NoTradeReason.InsufficientEdge,
        NoTradeReason.CorrelationLimit,
        NoTradeReason.SymbolExposureLimit,
        NoTradeReason.ClusterExposureLimit,
        NoTradeReason.DirectionalExposureLimit,
        NoTradeReason.PortfolioRiskLimit,
        NoTradeReason.MaxPositionsReached,
        NoTradeReason.PositionAlreadyOpen,
        NoTradeReason.SizeBelowMinimum,
        NoTradeReason.NotRankedHighEnough,
        NoTradeReason.ModeGuard,
        NoTradeReason.BrokerConstraint,
    };

    /// <summary>Один фильтр в воронке.</summary>
    public readonly struct Stage
    {
        public Stage(NoTradeReason reason, long reached, long rejected)
        {
            Reason = reason;
            Reached = reached;
            Rejected = rejected;
        }

        public NoTradeReason Reason { get; }
        public long Reached { get; }
        public long Rejected { get; }
        public long Passed => Reached - Rejected;

        /// <summary>Доля прошедших. Ровно ноль при ненулевом Reached — признак сломанного фильтра.</summary>
        public double PassRate => Reached <= 0 ? 1.0 : (double)Passed / Reached;

        /// <summary>
        /// Фильтр, через который не прошёл НИ ОДИН кандидат при достаточной выборке.
        ///
        /// Осторожный фильтр пропускает мало; сломанный — ноль, и никакое количество
        /// кандидатов этого не меняет.
        ///
        /// Сообщается только для СТРУКТУРНЫХ фильтров. У экономических ноль прошедших —
        /// законный исход: на рынке без преимущества система обязана отказывать всем, и
        /// объявлять это поломкой значило бы кричать «волки» там, где всё правильно.
        /// </summary>
        public bool LooksImpassable(long minimumSample) =>
            IsStructural(Reason) && Reached >= minimumSample && Passed == 0;
    }

    /// <summary>
    /// Экономические фильтры: их порог сравнивается с ПРЕИМУЩЕСТВОМ, а его на рынке может
    /// не быть вовсе. Ноль прошедших здесь — вывод о рынке, а не о коде.
    /// </summary>
    private static readonly HashSet<NoTradeReason> Economic = new HashSet<NoTradeReason>
    {
        NoTradeReason.NegativeExpectedValue,
        NoTradeReason.InsufficientEdge,
        NoTradeReason.LowProbabilityConfidence,
    };

    /// <summary>
    /// Структурный фильтр: его порог сравнивается с величиной, которую задаёт сама система
    /// — геометрией плана, уверенностью, спредом. Ноль прошедших здесь означает порог,
    /// недостижимый по построению.
    /// </summary>
    public static bool IsStructural(NoTradeReason reason) => !Economic.Contains(reason);

    /// <summary>
    /// Строит воронку по счётчику причин и числу принятых решений.
    /// </summary>
    public static IReadOnlyList<Stage> Build(
        IReadOnlyDictionary<NoTradeReason, int> reasonCounts, long accepted)
    {
        var stages = new List<Stage>(GateOrder.Length);
        if (reasonCounts == null) return stages;

        long total = accepted;
        foreach (KeyValuePair<NoTradeReason, int> kv in reasonCounts) total += kv.Value;

        long reached = total;
        for (int i = 0; i < GateOrder.Length; i++)
        {
            NoTradeReason reason = GateOrder[i];
            reasonCounts.TryGetValue(reason, out int rejected);

            // Фильтр, до которого никто не дошёл, в отчёт не попадает: строка с нулями
            // ничего не сообщает, а таких строк большинство.
            if (reached > 0 && (rejected > 0 || reached < total))
            {
                stages.Add(new Stage(reason, reached, rejected));
            }

            reached -= rejected;
        }

        return stages;
    }

    /// <summary>Фильтры, не пропустившие ни одного кандидата при достаточной выборке.</summary>
    public static IReadOnlyList<Stage> Impassable(IReadOnlyList<Stage> stages, long minimumSample = 50)
    {
        var result = new List<Stage>();
        for (int i = 0; i < stages.Count; i++)
        {
            if (stages[i].LooksImpassable(minimumSample)) result.Add(stages[i]);
        }
        return result;
    }

    public static string Render(IReadOnlyList<Stage> stages, long accepted)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=============== ВОРОНКА РЕШЕНИЙ ===============");
        sb.AppendLine("  фильтр                        дошло  отклонено  прошло");

        for (int i = 0; i < stages.Count; i++)
        {
            Stage s = stages[i];
            sb.AppendFormat("  {0,-26} {1,7} {2,10} {3,7}  {4,6:P1}{5}",
                s.Reason, s.Reached, s.Rejected, s.Passed, s.PassRate,
                s.LooksImpassable(50) ? "   <<< НЕ ПРОПУСКАЕТ НИЧЕГО"
                    : s.Reached >= 50 && s.Passed == 0 ? "   (преимущества нет — это о рынке)" : "").AppendLine();
        }

        sb.AppendFormat("  {0,-26} {1,7}", "ПРИНЯТО", accepted).AppendLine();
        sb.Append("===============================================");
        return sb.ToString();
    }
}
