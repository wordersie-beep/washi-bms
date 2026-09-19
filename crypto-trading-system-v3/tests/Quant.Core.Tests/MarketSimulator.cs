using System;
using System.Collections.Generic;
using Quant.Core.Config;
using Quant.Core.Data;
using Quant.Core.Features;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Tests;

/// <summary>
/// Deterministic synthetic market used across the pipeline tests.
///
/// It is not trying to be a realistic price model. It is trying to produce markets whose
/// TRUE regime is known by construction, so that the regime classifier can be tested
/// against ground truth instead of against its own output.
/// </summary>
public sealed class MarketSimulator
{
    private readonly Pcg32 _rng;
    private double _price;
    private DateTime _time;

    public MarketSimulator(double startPrice = 50000, ulong seed = 12345, DateTime? start = null)
    {
        _price = startPrice;
        _rng = new Pcg32(seed);
        _time = start ?? new DateTime(2025, 6, 2, 0, 0, 0, DateTimeKind.Utc);
    }

    public double Price => _price;
    public DateTime Time => _time;

    /// <summary>Generates bars with a given per-bar drift and volatility, both as fractions.</summary>
    public List<Candle> Generate(int count, double driftPerBar, double volPerBar, int minutesPerBar = 5, double volumeBase = 1000)
    {
        var bars = new List<Candle>(count);
        for (int i = 0; i < count; i++)
        {
            double open = _price;
            double shock = _rng.NextGaussian() * volPerBar;
            double close = open * Math.Exp(driftPerBar + shock);

            // Intrabar extension proportional to the realised move, plus a floor so bars are
            // never degenerate.
            double extension = Math.Abs(close - open) * (0.3 + (0.7 * _rng.NextDouble())) + (open * volPerBar * 0.25);
            double high = Math.Max(open, close) + extension;
            double low = Math.Min(open, close) - extension;

            double volume = volumeBase * (0.5 + _rng.NextDouble()) * (1 + (Math.Abs(shock) / Math.Max(volPerBar, 1e-9) * 0.3));

            bars.Add(new Candle(_time, open, high, low, close, volume));
            _price = close;
            _time = _time.AddMinutes(minutesPerBar);
        }
        return bars;
    }

    /// <summary>A mean-reverting market that oscillates about a fixed centre.</summary>
    public List<Candle> GenerateRange(int count, double centre, double halfWidth, int minutesPerBar = 5)
    {
        var bars = new List<Candle>(count);
        for (int i = 0; i < count; i++)
        {
            double open = _price;
            double pull = (centre - open) / halfWidth * 0.25;
            double close = open + (pull * halfWidth * 0.3) + (_rng.NextGaussian() * halfWidth * 0.18);
            double extension = halfWidth * 0.08 * (0.5 + _rng.NextDouble());

            bars.Add(new Candle(_time, open,
                Math.Max(open, close) + extension,
                Math.Min(open, close) - extension,
                close, 1000 * (0.6 + _rng.NextDouble())));

            _price = close;
            _time = _time.AddMinutes(minutesPerBar);
        }
        return bars;
    }

    public void Skip(TimeSpan by) => _time = _time.Add(by);
}

/// <summary>Wires a <see cref="SymbolDataSet"/> and feeds it a bar stream, including quotes.</summary>
public sealed class PipelineHarness
{
    public PipelineHarness(EngineConfig config = null, string symbol = "BTCUSD")
    {
        Config = config ?? new EngineConfig();
        Spec = new SymbolSpec(symbol, pipSize: 0.01, tickSize: 0.01, digits: 2,
            volumeInUnitsMin: 0.01, volumeInUnitsMax: 100, volumeInUnitsStep: 0.01,
            commissionPerMillionQuote: 35, pipValuePerUnit: 0.01,
            minStopLossDistancePrice: 0, isTradingEnabled: true);

        Data = new SymbolDataSet(symbol, Spec, Config.Data);
        foreach (Tf tf in Config.Data.Timeframes) _pending[tf] = new List<Candle>();
        Features = new FeatureEngine(Config.Data);
        QualityMonitor = new DataQualityMonitor(Config.Data);
    }

    private readonly Dictionary<Tf, List<Candle>> _pending = new Dictionary<Tf, List<Candle>>();

    public EngineConfig Config { get; }
    public SymbolSpec Spec { get; }
    public SymbolDataSet Data { get; }
    public FeatureEngine Features { get; }
    public DataQualityMonitor QualityMonitor { get; }
    public FeatureVector LastFeatures { get; private set; }
    public DateTime LastTimeUtc { get; private set; }

    /// <summary>
    /// Feeds bars to every configured timeframe. Slower timeframes are synthesised by
    /// aggregating the signal-timeframe bars, which is exactly what the platform does.
    /// </summary>
    /// <summary>
    /// Aggregation state is kept on the harness, not per call, so that feeding one bar at a
    /// time produces exactly the same series as feeding the whole stream at once.
    /// </summary>
    public void Feed(IEnumerable<Candle> signalBars, double spreadFraction = 0.0002)
    {
        int signalMinutes = (int)Config.Data.SignalTimeframe;

        foreach (Candle bar in signalBars)
        {
            LastTimeUtc = bar.OpenTimeUtc.AddMinutes(signalMinutes);

            double spread = bar.Close * spreadFraction;
            Data.OnQuote(new Quote(LastTimeUtc, bar.Close - (spread / 2), bar.Close + (spread / 2)));

            foreach (Tf tf in Config.Data.Timeframes)
            {
                int ratio = Math.Max(1, (int)tf / signalMinutes);
                if ((int)tf < signalMinutes) continue;

                _pending[tf].Add(bar);
                if (_pending[tf].Count >= ratio)
                {
                    Data.OnBarClosed(tf, Aggregate(_pending[tf]));
                    _pending[tf].Clear();
                }
            }

            if (Data.IsReady)
            {
                LastFeatures = Features.Build(LastTimeUtc, Data, GlobalMarketContext.Unavailable, 0, 0);
            }
        }
    }

    private static Candle Aggregate(List<Candle> bars)
    {
        double high = double.MinValue, low = double.MaxValue, volume = 0;
        for (int i = 0; i < bars.Count; i++)
        {
            if (bars[i].High > high) high = bars[i].High;
            if (bars[i].Low < low) low = bars[i].Low;
            volume += bars[i].Volume;
        }
        return new Candle(bars[0].OpenTimeUtc, bars[0].Open, high, low, bars[bars.Count - 1].Close, volume);
    }
}
