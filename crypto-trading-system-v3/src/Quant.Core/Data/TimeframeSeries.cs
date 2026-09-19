using System;
using Quant.Core.Config;
using Quant.Core.Indicators;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Data;

/// <summary>
/// One symbol on one timeframe: the bar history plus every indicator computed from it.
///
/// The central invariant is that <see cref="OnBarClosed"/> is the ONLY way indicator state
/// advances, and it is called exactly once per bar, with a bar that has finished forming.
/// Nothing here ever sees a partial bar. That is what makes a backtest and a live run agree
/// and what makes look-ahead structurally impossible rather than merely avoided by care.
/// </summary>
public sealed class TimeframeSeries
{
    private readonly DataConfig _config;
    private readonly RollingWindow _atrHistory;
    private readonly RollingWindow _volumeHistory;
    private readonly RollingWindow _bbWidthHistory;
    private readonly RollingWindow _rangeHistory;

    public TimeframeSeries(Tf timeframe, DataConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        Timeframe = timeframe;

        Bars = new Ring<Candle>(config.BarHistory);

        Atr = new Atr(config.AtrPeriods);
        Adx = new Adx(config.AdxPeriods);
        Rsi = new Rsi(config.RsiPeriods);
        Bollinger = new BollingerBands(config.BollingerPeriods, config.BollingerDeviations);
        EmaFast = new Ema(config.EmaFastPeriods);
        EmaSlow = new Ema(config.EmaSlowPeriods);
        EmaTrend = new Ema(config.EmaTrendPeriods);
        Macd = new MacdHistogram();
        Slope = new LinearRegressionSlope(config.SlopePeriods);
        Donchian = new DonchianChannel(config.DonchianPeriods);
        Structure = new SwingStructure(config.SwingConfirmationBars, Math.Max(64, config.DonchianPeriods * 3));
        RealizedVol = new RealizedVolatility(config.RealizedVolPeriods, BarsPerYear(timeframe));

        _atrHistory = new RollingWindow(config.PercentileWindow);
        _volumeHistory = new RollingWindow(config.VolumeWindow);
        _bbWidthHistory = new RollingWindow(config.PercentileWindow);
        _rangeHistory = new RollingWindow(config.PercentileWindow);
    }

    public Tf Timeframe { get; }
    public Ring<Candle> Bars { get; }

    public Atr Atr { get; }
    public Adx Adx { get; }
    public Rsi Rsi { get; }
    public BollingerBands Bollinger { get; }
    public Ema EmaFast { get; }
    public Ema EmaSlow { get; }
    public Ema EmaTrend { get; }
    public MacdHistogram Macd { get; }
    public LinearRegressionSlope Slope { get; }
    public DonchianChannel Donchian { get; }
    public SwingStructure Structure { get; }
    public RealizedVolatility RealizedVol { get; }

    /// <summary>Closed bars processed since construction. Not capped by the ring size.</summary>
    public long BarsProcessed { get; private set; }

    /// <summary>Open time of the most recent closed bar; default when none has arrived.</summary>
    public DateTime LastBarOpenTimeUtc { get; private set; }

    /// <summary>
    /// Number of bar intervals skipped since the previous close. 0 means contiguous;
    /// anything above 0 is a data gap (spec section 6).
    /// </summary>
    public int LastGapBars { get; private set; }

    /// <summary>Total gaps observed. A feed that gaps constantly is a feed to distrust.</summary>
    public long GapCount { get; private set; }

    /// <summary>
    /// True once every indicator on THIS timeframe has warmed up and enough bars exist for
    /// its percentile ranks to mean something.
    ///
    /// Note what this deliberately does NOT include: the bar minimum required before the
    /// symbol may be traded. That is a property of the SIGNAL timeframe alone and lives in
    /// <see cref="SymbolDataSet.IsReady"/>. Applying it here would mean a 4-hour context
    /// series needed hundreds of 4-hour bars -- months of warm-up -- before a 5-minute
    /// decision could ever be made.
    /// </summary>
    public bool IsReady =>
        BarsProcessed >= _config.MinBarsPerTimeframe &&
        Atr.IsReady && Adx.IsReady && Bollinger.IsReady && EmaSlow.IsReady && Donchian.IsReady && Slope.IsReady;

    /// <summary>Most recent closed bar. Only valid once at least one bar has closed.</summary>
    public Candle Last => Bars.Count > 0 ? Bars[0] : default;

    /// <summary>ATR as a fraction of the last close.</summary>
    public double AtrFraction => Bars.Count == 0 ? 0 : Atr.AsFractionOf(Last.Close);

    /// <summary>
    /// Percentile rank of current volatility against its own recent history, 0..1.
    ///
    /// Ranked on ATR AS A FRACTION OF PRICE, never on ATR in price units. In an instrument
    /// that doubles, ATR in price units roughly doubles too, so a raw-ATR percentile would
    /// sit pinned at 1.0 for the whole advance and the system would permanently believe it
    /// was in maximum volatility. The fraction is the scale-free form and is what makes this
    /// rank mean the same thing in month one and month twelve.
    /// </summary>
    public double AtrPercentile => _atrHistory.PercentileRank(CurrentAtrFraction, minSample: 30);

    /// <summary>Percentile rank of the last bar's volume, 0..1.</summary>
    public double VolumePercentile => Bars.Count == 0 ? 0.5 : _volumeHistory.PercentileRank(Last.Volume, minSample: 30);

    /// <summary>Percentile rank of the current Bollinger width, 0..1.</summary>
    public double BollingerWidthPercentile => _bbWidthHistory.PercentileRank(Bollinger.Value, minSample: 30);

    /// <summary>Percentile rank of the last bar's range as a fraction of price, 0..1.</summary>
    public double RangePercentile =>
        Bars.Count == 0 ? 0.5 : _rangeHistory.PercentileRank(MathUtil.SafeDiv(Last.Range, Last.Close), minSample: 30);

    /// <summary>Z-score of current volatility against its history, on the same scale-free basis.</summary>
    public double AtrZScore => _atrHistory.ZScore(CurrentAtrFraction);

    /// <summary>ATR as a fraction of the latest close. Zero until a bar has closed.</summary>
    private double CurrentAtrFraction => Bars.Count == 0 ? 0 : MathUtil.SafeDiv(Atr.Value, Last.Close);

    /// <summary>Z-score of the last bar's volume against its history.</summary>
    public double VolumeZScore => Bars.Count == 0 ? 0 : _volumeHistory.ZScore(Last.Volume);

    /// <summary>
    /// Volume acceleration: latest volume over the mean of the window, minus one.
    /// Positive means participation is rising.
    /// </summary>
    public double VolumeAcceleration =>
        Bars.Count == 0 ? 0 : MathUtil.Clamp(MathUtil.SafeDiv(Last.Volume, _volumeHistory.Mean, 1.0) - 1.0, -1, 10);

    /// <summary>History of ATR-as-fraction-of-price, the scale-free volatility series.</summary>
    public RollingWindow AtrFractionHistory => _atrHistory;
    public RollingWindow VolumeHistory => _volumeHistory;
    public RollingWindow ReturnHistory => RealizedVol.Returns;

    /// <summary>
    /// Advances every indicator by exactly one CLOSED bar.
    ///
    /// Returns false, and changes nothing, when the bar is malformed or out of order. A
    /// rejected bar is never partially applied: either the whole series moves forward or
    /// none of it does, because a half-updated indicator set is worse than a stale one.
    /// </summary>
    public bool OnBarClosed(in Candle bar)
    {
        if (!bar.IsWellFormed) return false;

        // Out-of-order or duplicate bars are dropped. Replaying a bar would double-count it
        // in every smoother and silently corrupt every percentile.
        if (BarsProcessed > 0 && bar.OpenTimeUtc <= LastBarOpenTimeUtc) return false;

        if (BarsProcessed > 0)
        {
            double intervalMinutes = (int)Timeframe;
            double elapsed = (bar.OpenTimeUtc - LastBarOpenTimeUtc).TotalMinutes;
            int expected = (int)Math.Round(elapsed / intervalMinutes);
            LastGapBars = Math.Max(0, expected - 1);
            if (LastGapBars > 0) GapCount++;
        }

        // Percentile histories are updated with the values that were current BEFORE this bar
        // is folded in, so that a percentile rank never compares a value against itself.
        if (Atr.IsReady && Bars.Count > 0) _atrHistory.Add(MathUtil.SafeDiv(Atr.Value, Bars[0].Close));
        if (Bollinger.IsReady) _bbWidthHistory.Add(Bollinger.Value);
        if (BarsProcessed > 0)
        {
            _volumeHistory.Add(Bars[0].Volume);
            _rangeHistory.Add(MathUtil.SafeDiv(Bars[0].Range, Bars[0].Close));
        }

        Bars.Add(bar);

        Atr.Update(bar);
        Adx.Update(bar);
        Rsi.Update(bar.Close);
        Bollinger.Update(bar.Close);
        EmaFast.Update(bar.Close);
        EmaSlow.Update(bar.Close);
        EmaTrend.Update(bar.Close);
        Macd.Update(bar.Close);
        Slope.Update(bar.Close);
        Donchian.Update(bar);
        Structure.Update(bar);
        RealizedVol.Update(bar.Close);

        LastBarOpenTimeUtc = bar.OpenTimeUtc;
        BarsProcessed++;
        return true;
    }

    /// <summary>Distance from price to an EMA, in ATR units. The comparable form.</summary>
    public double DistanceInAtr(double price, double level)
    {
        double atr = Atr.Value;
        return atr <= 0 ? 0 : MathUtil.Clamp((price - level) / atr, -50, 50);
    }

    /// <summary>EMA slope per bar, normalised by ATR so it is comparable across instruments.</summary>
    public double EmaSlopeInAtr(Ema ema)
    {
        double atr = Atr.Value;
        if (atr <= 0 || !ema.IsReady) return 0;
        return MathUtil.Clamp((ema.Value - ema.Previous) / atr, -10, 10);
    }

    private static double BarsPerYear(Tf tf) => 365.0 * 24.0 * 60.0 / (int)tf;

    public void Reset()
    {
        Bars.Clear();
        Atr.Reset(); Adx.Reset(); Rsi.Reset(); Bollinger.Reset();
        EmaFast.Reset(); EmaSlow.Reset(); EmaTrend.Reset(); Macd.Reset();
        Slope.Reset(); Donchian.Reset(); Structure.Reset(); RealizedVol.Reset();
        _atrHistory.Clear(); _volumeHistory.Clear(); _bbWidthHistory.Clear(); _rangeHistory.Clear();
        BarsProcessed = 0;
        LastBarOpenTimeUtc = default;
        LastGapBars = 0;
        GapCount = 0;
    }
}
