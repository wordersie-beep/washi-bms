using System;
using System.Collections.Generic;
using Quant.Core.Config;
using Quant.Core.Primitives;

namespace Quant.Core.Data;

/// <summary>
/// Everything the system knows about one instrument: bars on every configured timeframe,
/// the tick stream statistics, spread statistics and the current contract terms.
/// </summary>
public sealed class SymbolDataSet
{
    private readonly Dictionary<Tf, TimeframeSeries> _series = new Dictionary<Tf, TimeframeSeries>();
    private readonly DataConfig _config;

    public SymbolDataSet(string symbolName, SymbolSpec spec, DataConfig config)
    {
        if (string.IsNullOrWhiteSpace(symbolName)) throw new ArgumentException("Symbol name is required.", nameof(symbolName));
        _config = config ?? throw new ArgumentNullException(nameof(config));

        SymbolName = symbolName;
        Spec = spec;
        Ticks = new TickStatistics(config);
        Spread = new SpreadModel(config);

        foreach (Tf tf in config.Timeframes)
        {
            if (!_series.ContainsKey(tf)) _series[tf] = new TimeframeSeries(tf, config);
        }
    }

    public string SymbolName { get; }

    /// <summary>Contract terms, refreshed from the broker rather than assumed.</summary>
    public SymbolSpec Spec { get; private set; }

    public TickStatistics Ticks { get; }
    public SpreadModel Spread { get; }
    public Quote LatestQuote { get; private set; }

    public IReadOnlyDictionary<Tf, TimeframeSeries> AllSeries => _series;

    /// <summary>The timeframe that drives decisions.</summary>
    public TimeframeSeries Signal => _series[_config.SignalTimeframe];

    /// <summary>The slower timeframe that supplies trend context.</summary>
    public TimeframeSeries Context => _series[_config.ContextTimeframe];

    public TimeframeSeries Series(Tf tf) => _series.TryGetValue(tf, out TimeframeSeries s) ? s : null;

    /// <summary>
    /// True once the symbol may be traded.
    ///
    /// Only the signal and context timeframes gate this. The other timeframes are
    /// enrichment: a slow one that has not warmed up yet must not block the symbol
    /// indefinitely, it must simply contribute nothing. The signal timeframe additionally
    /// has to clear the configured bar minimum, which is what guarantees the percentile
    /// ranks and the long trend EMA are backed by real history rather than by three bars
    /// and an assumption.
    /// </summary>
    public bool IsReady =>
        Signal.IsReady &&
        Signal.BarsProcessed >= _config.MinBarsBeforeTrading &&
        Context.IsReady;

    /// <summary>Bars still needed on the signal timeframe before trading may begin.</summary>
    public long SignalBarsRemaining => Math.Max(0, _config.MinBarsBeforeTrading - Signal.BarsProcessed);

    public void UpdateSpec(SymbolSpec spec)
    {
        if (spec != null) Spec = spec;
    }

    /// <summary>Records a quote and updates the spread and microstructure statistics.</summary>
    public bool OnQuote(in Quote quote)
    {
        double atr = Signal.Atr.IsReady ? Signal.Atr.Value : 0;
        bool accepted = Ticks.Observe(quote, atr);
        if (!accepted) return false;

        LatestQuote = quote;
        Spread.Observe(quote.Spread);
        return true;
    }

    /// <summary>Advances one timeframe by one closed bar.</summary>
    public bool OnBarClosed(Tf tf, in Candle bar) =>
        _series.TryGetValue(tf, out TimeframeSeries s) && s.OnBarClosed(bar);

    public void Reset()
    {
        foreach (KeyValuePair<Tf, TimeframeSeries> kv in _series) kv.Value.Reset();
        Ticks.Reset();
        Spread.Reset();
        LatestQuote = default;
    }
}
