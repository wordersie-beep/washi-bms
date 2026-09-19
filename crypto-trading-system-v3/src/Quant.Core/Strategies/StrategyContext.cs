using System;
using Quant.Core.Config;
using Quant.Core.Data;
using Quant.Core.Features;
using Quant.Core.Primitives;
using Quant.Core.Regime;

namespace Quant.Core.Strategies;

/// <summary>
/// Read-only input to a strategy evaluation.
///
/// Strategies get a context, never the live engine. They cannot place orders, cannot read
/// account state, cannot see the portfolio and cannot mutate anything. A strategy's only
/// job is to have an opinion about direction; everything about whether that opinion is
/// affordable belongs to layers that can see the whole book.
/// </summary>
public sealed class StrategyContext
{
    public StrategyContext(
        DateTime nowUtc,
        SymbolDataSet data,
        FeatureVector features,
        RegimeAssessment regime,
        GlobalMarketContext global,
        EngineConfig config)
    {
        NowUtc = nowUtc;
        Data = data ?? throw new ArgumentNullException(nameof(data));
        Features = features ?? throw new ArgumentNullException(nameof(features));
        Regime = regime ?? RegimeAssessment.Unknown;
        Global = global ?? GlobalMarketContext.Unavailable;
        Config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public DateTime NowUtc { get; }
    public SymbolDataSet Data { get; }
    public FeatureVector Features { get; }
    public RegimeAssessment Regime { get; }
    public GlobalMarketContext Global { get; }
    public EngineConfig Config { get; }

    public TimeframeSeries Signal => Data.Signal;
    public TimeframeSeries Context => Data.Context;
    public string SymbolName => Data.SymbolName;

    /// <summary>Last closed signal bar.</summary>
    public Candle Bar => Data.Signal.Last;

    public double Price => Features.Price;
    public double Atr => Features.Atr;
}
