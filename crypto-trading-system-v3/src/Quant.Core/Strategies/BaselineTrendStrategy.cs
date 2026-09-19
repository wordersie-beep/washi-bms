using System;
using System.Collections.Generic;
using Quant.Core.Features;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Strategies;

/// <summary>
/// The BASELINE (spec sections 155-156): a deliberately trivial EMA-cross trend follower.
///
/// Its purpose is not to make money. Its purpose is to be the number the full system has to
/// beat. If the ensemble, the regime engine, the probability model and the adaptation layer
/// together cannot out-perform twenty lines of moving-average crossover on out-of-sample
/// data, then the complexity is not earning its keep and should be removed rather than
/// tuned. Keeping the baseline inside the same codebase — same data, same costs, same
/// execution assumptions — is what makes that comparison honest.
///
/// It is excluded from the live ensemble by default and run as a comparison arm.
/// </summary>
public sealed class BaselineTrendStrategy : StrategyBase
{
    public override string Name => "Baseline";
    public override StrategyKind Kind => StrategyKind.Baseline;

    /// <summary>Equal fit everywhere: the baseline gets no regime intelligence, by design.</summary>
    public override IReadOnlyDictionary<MarketRegime, double> RegimeFit => Fit(
        (MarketRegime.TrendUp, 1.0), (MarketRegime.TrendDown, 1.0), (MarketRegime.Range, 1.0),
        (MarketRegime.Breakout, 1.0), (MarketRegime.HighVolatility, 1.0), (MarketRegime.LowVolatility, 1.0),
        (MarketRegime.Chop, 1.0), (MarketRegime.Panic, 1.0), (MarketRegime.Euphoria, 1.0),
        (MarketRegime.LiquidityStress, 1.0), (MarketRegime.Transition, 1.0), (MarketRegime.Unknown, 1.0));

    public override IReadOnlyCollection<FeatureFamily> Families => Uses(FeatureFamily.TrendDirection);

    protected override StrategySignal EvaluateCore(StrategyContext ctx)
    {
        FeatureVector f = ctx.Features;
        double fast = ctx.Signal.EmaFast.Value;
        double slow = ctx.Signal.EmaSlow.Value;

        if (!ctx.Signal.EmaSlow.IsReady) return StrategySignal.Neutral(Name, "not ready");

        Side side = fast > slow ? Side.Long : fast < slow ? Side.Short : Side.None;
        if (side == Side.None) return StrategySignal.Neutral(Name, "no cross");

        double separation = MathUtil.SafeDiv(Math.Abs(fast - slow), f.Atr);
        double confidence = MathUtil.Clamp(0.5 + MathUtil.LinearScale(separation, 0.1, 1.0) * 0.3, 0, 0.8);

        return Signal(this, side, confidence, f.Price,
            $"baseline ema cross, separation {separation:F2} ATR",
            stopInAtr: ctx.Config.Exit.AtrStopMultiple);
    }
}
