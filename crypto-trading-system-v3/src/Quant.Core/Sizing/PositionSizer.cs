using System;
using System.Collections.Generic;
using Quant.Core.Config;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Sizing;

/// <summary>The sizing decision, with every factor that produced it.</summary>
public sealed class SizingResult
{
    public static SizingResult Rejected(NoTradeReason reason, string detail) => new SizingResult
    {
        VolumeInUnits = 0,
        RejectionReason = reason,
        Detail = detail,
    };

    public double VolumeInUnits { get; init; }
    public double RiskPercent { get; init; }
    public double RiskAmount { get; init; }
    public double StopDistance { get; init; }

    /// <summary>Every multiplier applied, in order, so a size is always explainable.</summary>
    public IReadOnlyDictionary<string, double> Factors { get; init; }

    public NoTradeReason RejectionReason { get; init; }
    public string Detail { get; init; }

    public bool IsTradeable => VolumeInUnits > 0 && RejectionReason == NoTradeReason.None;

    public override string ToString() =>
        IsTradeable
            ? string.Format("{0:F4} units, risking {1:F3}% ({2:F2})", VolumeInUnits, RiskPercent, RiskAmount)
            : string.Format("no size: {0} ({1})", RejectionReason, Detail);
}

/// <summary>
/// Turns a decision into a number of units (spec section 32).
///
/// Sizing is multiplicative from a base risk percentage, and every factor is bounded at or
/// below 1.0 with one narrowly capped exception. That is a deliberate structural property:
/// it means no combination of inputs, and no parameter set a user or an optimiser can
/// produce, is capable of sizing ABOVE the base risk by more than the single explicitly
/// capped win-streak term. A system that can only scale down cannot martingale
/// (spec section 40), and enforcing that in the shape of the calculation is far more
/// reliable than enforcing it in review.
///
/// The final size is then clamped by the hard per-trade cap, checked against free margin,
/// and snapped DOWN onto the broker's volume grid. The one exception is the broker minimum:
/// a size below it may be raised TO it, and only when the minimum itself fits inside every
/// limit — risk per trade under the current posture, the hard cap and the remaining budget.
/// Rounding therefore still can never breach a risk limit; it can only overrule the
/// multipliers' wish to be smaller than the broker allows.
/// </summary>
public sealed class PositionSizer
{
    private readonly SizingConfig _config;
    private readonly RiskConfig _riskConfig;

    public PositionSizer(SizingConfig config, RiskConfig riskConfig)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _riskConfig = riskConfig ?? throw new ArgumentNullException(nameof(riskConfig));
    }

    /// <summary>
    /// Computes position size.
    /// </summary>
    /// <param name="spec">Instrument terms, for volume normalisation and margin.</param>
    /// <param name="account">Account state.</param>
    /// <param name="entryPrice">Intended entry.</param>
    /// <param name="stopPrice">Intended stop.</param>
    /// <param name="direction">Trade direction.</param>
    /// <param name="riskStateMultiplier">From the risk engine, 0..1.</param>
    /// <param name="regimeMultiplier">From the regime assessment, 0..1.</param>
    /// <param name="ensembleConfidence">Blended signal confidence, 0..1.</param>
    /// <param name="edgeSurplusR">How far past the required edge the trade sits, in R.</param>
    /// <param name="strategyWeight">The leading strategy's ensemble weight, 0..1.</param>
    /// <param name="atrPercentile">Current volatility percentile, for volatility targeting.</param>
    /// <param name="correlationPenalty">0..1, where 1 means fully correlated with the existing book.</param>
    /// <param name="dataQualityScore">0..1 from the data quality monitor.</param>
    /// <param name="executionQuality">0..1 from the execution quality tracker.</param>
    /// <param name="remainingRiskBudgetPercent">Portfolio headroom still available, in percent of equity.</param>
    public SizingResult Compute(
        SymbolSpec spec,
        AccountSnapshot account,
        double entryPrice,
        double stopPrice,
        Side direction,
        double riskStateMultiplier,
        double regimeMultiplier,
        double ensembleConfidence,
        double edgeSurplusR,
        double strategyWeight,
        double atrPercentile,
        double correlationPenalty,
        double dataQualityScore,
        double executionQuality,
        double remainingRiskBudgetPercent,
        bool isColdStart = false,
        double trust = 1.0)
    {
        if (spec == null) return SizingResult.Rejected(NoTradeReason.BrokerConstraint, "symbol specification unavailable");
        if (!account.IsUsable) return SizingResult.Rejected(NoTradeReason.DataQuality, "account snapshot unusable");
        if (direction == Side.None) return SizingResult.Rejected(NoTradeReason.NoSignal, "no direction");

        double stopDistance = direction == Side.Long ? entryPrice - stopPrice : stopPrice - entryPrice;
        if (stopDistance <= 0)
        {
            return SizingResult.Rejected(NoTradeReason.InvalidStopPlacement, $"stop is on the wrong side of entry ({stopDistance:F6})");
        }

        var factors = new Dictionary<string, double>(StringComparer.Ordinal);

        // --- Multiplicative factors, every one of them at most 1.0 -----------------------
        double riskState = MathUtil.Clamp01(riskStateMultiplier);
        double regime = MathUtil.Clamp01(regimeMultiplier);

        // Confidence: at the minimum usable confidence the trade is sized small; at full
        // conviction it is sized at the base. ConfidenceWeight sets how much this matters.
        double confidence = Blend(MathUtil.Clamp01(ensembleConfidence), _config.ConfidenceWeight);

        // Edge: surplus above the required threshold, saturating at 0.5R so an outlier
        // estimate cannot buy an outsized position.
        double edge = Blend(MathUtil.LinearScale(edgeSurplusR, 0, 0.5), _config.EdgeWeight);

        double strategy = MathUtil.Clamp(strategyWeight <= 0 ? 1.0 : MathUtil.LinearScale(strategyWeight, 0.05, 0.35), 0.4, 1.0);
        double volatility = VolatilityTargetFactor(atrPercentile);
        double correlation = MathUtil.Clamp(1.0 - (0.5 * MathUtil.Clamp01(correlationPenalty)), 0.5, 1.0);
        double dataQuality = MathUtil.Clamp(dataQualityScore, 0.3, 1.0);

        // Доверие к оценке преимущества: вся осторожность «мы ещё мало знаем» теперь здесь,
        // а не в пороге решения. Пока реальных сделок меньше порога холодного старта,
        // доверие дополнительно ограничено сверху: теневые доказательства не знают реального
        // исполнения, и первые настоящие сделки обязаны быть малыми, сколько бы виртуальных
        // побед ни накопилось.
        double evidence = MathUtil.Clamp(trust, 0.0, 1.0);
        if (isColdStart) evidence = Math.Min(evidence, MathUtil.Clamp01(_config.ColdStartRiskMultiplier));
        double execution = MathUtil.Clamp(executionQuality, 0.3, 1.0);

        factors["riskState"] = riskState;
        factors["regime"] = regime;
        factors["confidence"] = confidence;
        factors["edge"] = edge;
        factors["strategy"] = strategy;
        factors["volatility"] = volatility;
        factors["correlation"] = correlation;
        factors["dataQuality"] = dataQuality;
        factors["execution"] = execution;
        factors["trust"] = evidence;

        double riskPercent = _config.RiskPerTradePercent
            * riskState * regime * confidence * edge * strategy * volatility * correlation * dataQuality * execution * evidence;

        // --- Hard caps -------------------------------------------------------------------
        // Applied last and unconditionally. Whatever the factors produced, this is the line
        // that cannot be crossed.
        riskPercent = Math.Min(riskPercent, _riskConfig.HardMaxRiskPerTradePercent);
        riskPercent = Math.Min(riskPercent, Math.Max(0, remainingRiskBudgetPercent));

        // Пол сравнивается с риском В ТЕХ ЖЕ ЕДИНИЦАХ, в каких его уменьшила политика.
        //
        // Множители делятся на два рода. Качество — режим, уверенность, преимущество,
        // вес, волатильность, корреляция, данные, исполнение — говорит о КАНДИДАТЕ, и пол
        // спрашивает ровно о нём: не сжался ли он по собственным достоинствам до
        // бессмысленности. Политика — постура риска и холодный старт — говорит о
        // СОСТОЯНИИ СИСТЕМЫ и уменьшает размер намеренно.
        //
        // Сравнение с неизменным полом смешивало одно с другим, и политика превращала
        // «меньше» в «никогда». На счёте в миллион евро холодный старт отвергал каждого
        // кандидата, даже сильного: 0.30% × качество × 0.35 не дотягивало до 0.05%. А
        // холодный старт заканчивается только после двадцати сделок — которых поэтому не
        // могло быть. Тот же замкнутый круг, что был разорван в оценке преимущества,
        // просто на слой ниже. И Defensive вёл себя как Halt: лестница из четырёх
        // ступеней на деле состояла из трёх.
        //
        // Масштабированный пол эквивалентен вопросу «прошёл бы кандидат пол без
        // политики» — и остаётся осмысленным, когда размер дополнительно зажат потолком
        // или остатком бюджета.
        double policy = riskState * evidence;
        double floor = _config.MinRiskPerTradePercent * policy;

        if (riskPercent <= 0 || riskPercent < floor)
        {
            return SizingResult.Rejected(NoTradeReason.SizeBelowMinimum,
                $"risk shrank to {riskPercent:F4}%, below the {floor:F4}% floor " +
                $"({_config.MinRiskPerTradePercent:F4}% × политика {policy:F2})");
        }

        // --- Units --------------------------------------------------------------------------
        // Риск считается в ВАЛЮТЕ СЧЁТА, а стоп — в цене инструмента. Делить одно на другое
        // напрямую можно только когда котируемая валюта совпадает с валютой счёта. Счёт в
        // EUR и ETHUSD: стоп в 50 долларов — это не 50 евро, и позиция получилась бы больше
        // задуманной на весь курс пары.
        double riskAmount = account.Equity * riskPercent / 100.0;
        double rawUnits = spec.UnitsForMoney(riskAmount, stopDistance);

        double units = spec.NormalizeVolumeDown(rawUnits);
        if (units <= 0)
        {
            // Меньше минимального объёма брокер не продаёт. Округлить ВВЕРХ до минимума
            // можно — но только если минимум укладывается во все ЛИМИТЫ: риск на сделку с
            // учётом постуры, жёсткий потолок и остаток бюджета. Превышается лишь пожелание
            // множителей быть ещё меньше, а не граница, которую задал человек.
            //
            // Без этого на небольшом счёте каждая первая сделка отвергалась: осторожный
            // начальный размер выходил меньше минимального объёма, холодный старт не мог
            // закончиться, потому что заканчивается он сделками. На BTCUSD при стопе 2.4 ATR
            // так жили все счета от 880 до 2514 долларов — риск на сделку позволял
            // минимальную позицию, а бот не торговал никогда.
            double ceiling = Math.Min(_config.RiskPerTradePercent * riskState, _riskConfig.HardMaxRiskPerTradePercent);
            ceiling = Math.Min(ceiling, Math.Max(0, remainingRiskBudgetPercent));

            double minimumRiskPercent = account.Equity > 0
                ? spec.MoneyFor(stopDistance, spec.VolumeInUnitsMin) / account.Equity * 100.0
                : double.MaxValue;

            if (spec.VolumeInUnitsMin > 0 && minimumRiskPercent <= ceiling)
            {
                units = spec.NormalizeVolumeDown(spec.VolumeInUnitsMin);
                factors["roundedUpToBrokerMinimum"] = MathUtil.SafeDiv(minimumRiskPercent, riskPercent, 1.0);
            }
            else
            {
                return SizingResult.Rejected(NoTradeReason.SizeBelowMinimum,
                    $"{rawUnits:F6} units is below the broker minimum of {spec.VolumeInUnitsMin:F6}; " +
                    $"the minimum would risk {minimumRiskPercent:F3}% against a {ceiling:F3}% limit");
            }
        }

        // --- Margin guard (spec sections 114-115) ----------------------------------------------
        double estimatedMargin = spec.EstimateMargin(units, entryPrice);
        double marginBudget = account.FreeMargin * _config.MaxMarginUtilization;

        // Нулевая или отрицательная свободная маржа — это отказ, а не пропуск проверки.
        // Условие «и бюджет положителен» отключало защиту ровно в том состоянии, ради
        // которого она существует: денег нет, значит сделки нет.
        if (marginBudget <= 0)
        {
            return SizingResult.Rejected(NoTradeReason.MarginGuard,
                $"свободная маржа {account.FreeMargin:F2} не позволяет открыть позицию");
        }

        if (estimatedMargin > marginBudget)
        {
            double scaled = units * (marginBudget / estimatedMargin);
            units = spec.NormalizeVolumeDown(scaled);
            if (units <= 0)
            {
                return SizingResult.Rejected(NoTradeReason.MarginGuard,
                    $"estimated margin {estimatedMargin:F2} exceeds the {marginBudget:F2} budget and cannot be scaled down");
            }
            factors["marginScaled"] = marginBudget / estimatedMargin;
        }

        // Recompute the REALISED risk from the size actually obtainable. Rounding down means
        // the true risk is at or below the requested figure, and the record must say so.
        double actualRiskAmount = spec.MoneyFor(stopDistance, units);
        double actualRiskPercent = 100.0 * actualRiskAmount / account.Equity;

        return new SizingResult
        {
            VolumeInUnits = units,
            RiskPercent = actualRiskPercent,
            RiskAmount = actualRiskAmount,
            StopDistance = stopDistance,
            Factors = factors,
            RejectionReason = NoTradeReason.None,
        };
    }

    /// <summary>
    /// Blends a 0..1 score toward 1.0 by a weight, so that a weight of 0 disables the factor
    /// entirely and a weight of 1 applies it in full. The result is always at most 1.0, which
    /// is what keeps the whole calculation incapable of scaling up.
    /// </summary>
    private static double Blend(double score, double weight)
    {
        double w = MathUtil.Clamp01(weight);
        return MathUtil.Clamp(1.0 - (w * (1.0 - MathUtil.Clamp01(score))), 0.05, 1.0);
    }

    /// <summary>
    /// Volatility targeting (spec section 64).
    ///
    /// Scales size DOWN as volatility rises above its neutral percentile, and never up when
    /// it falls below. Sizing up into quiet markets is how a system arranges to be at its
    /// largest exactly when volatility mean-reverts back up, which it does.
    /// </summary>
    private double VolatilityTargetFactor(double atrPercentile)
    {
        double excess = MathUtil.Clamp01(atrPercentile) - _config.VolatilityTargetPercentile;
        if (excess <= 0) return _config.MaxVolatilityMultiplier;

        double reduction = MathUtil.Clamp01(excess / Math.Max(1.0 - _config.VolatilityTargetPercentile, 1e-6));
        double factor = 1.0 - (reduction * MathUtil.Clamp01(_config.VolatilityTargetStrength));

        return MathUtil.Clamp(factor, _config.MinVolatilityMultiplier, _config.MaxVolatilityMultiplier);
    }
}
