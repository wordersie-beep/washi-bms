using System;
using System.Collections.Generic;
using Quant.Core.Config;
using Quant.Core.Features;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Regime;

/// <summary>
/// The baseline regime classifier: transparent, deterministic scoring over normalised
/// features, with hysteresis on the output.
///
/// Why scoring rather than a decision tree of thresholds: a hard threshold at ADX 25 means
/// 24.9 and 25.1 are different worlds, which they are not. Scores degrade gracefully, and
/// the MARGIN between the top two scores is what the confidence is built from — a market
/// that is 51% trend and 49% range genuinely is ambiguous, and the system should size
/// accordingly rather than commit.
///
/// Hysteresis matters just as much: without it the label flips bar to bar in exactly the
/// conditions where flipping is most expensive.
/// </summary>
public sealed class StatisticalRegimeModel : IMarketRegimeModel
{
    /// <summary>
    /// Regimes that describe market STRUCTURE. Exactly one of these can be true at a time,
    /// so they compete with each other for the primary label.
    /// </summary>
    private static readonly MarketRegime[] StructuralRegimes =
    {
        MarketRegime.Panic,
        MarketRegime.LiquidityStress,
        MarketRegime.Euphoria,
        MarketRegime.Breakout,
        MarketRegime.TrendUp,
        MarketRegime.TrendDown,
        MarketRegime.Range,
        MarketRegime.Chop,
    };

    /// <summary>
    /// Regimes that describe the VOLATILITY STATE. These are a different axis: a market can
    /// be trending and volatile at the same time, so they must not compete head-to-head with
    /// the structural regimes for the primary label. They are scored as residuals -- see
    /// <see cref="ScoreAll"/> -- and additionally reported separately on the assessment so
    /// that sizing and exits always know the volatility state whatever the primary label is.
    /// </summary>
    private static readonly MarketRegime[] VolatilityRegimes =
    {
        MarketRegime.HighVolatility,
        MarketRegime.LowVolatility,
    };

    private readonly RegimeConfig _config;
    private readonly Dictionary<MarketRegime, double> _scores = new Dictionary<MarketRegime, double>();
    private double _volatilityState;

    private MarketRegime _confirmed = MarketRegime.Unknown;
    private MarketRegime _previousConfirmed = MarketRegime.Unknown;
    private MarketRegime _candidate = MarketRegime.Unknown;
    private int _candidateStreak;
    private int _barsInRegime;
    private int _barsSinceChange = int.MaxValue;

    public StatisticalRegimeModel(RegimeConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public string Name => "statistical-v3";
    public bool IsReady => _confirmed != MarketRegime.Unknown;

    public RegimeAssessment Classify(FeatureVector f)
    {
        if (f == null) return RegimeAssessment.Unknown;

        try
        {
            ScoreAll(f);
            return Confirm();
        }
        catch (Exception)
        {
            // A classifier failure must degrade to "no opinion", which downstream reads as
            // "do not trade" -- never to a confident guess.
            return RegimeAssessment.Unknown;
        }
    }

    private void ScoreAll(FeatureVector f)
    {
        _scores.Clear();

        double absTrend = Math.Abs(f.TrendStrength);
        double adxTrend = MathUtil.LinearScale(f.Adx, _config.RangeAdxThreshold, _config.TrendAdxThreshold + 10);
        double adxRange = 1.0 - MathUtil.LinearScale(f.Adx, _config.RangeAdxThreshold - 5, _config.TrendAdxThreshold);
        double fit = f.RegressionFit;
        double fitGate = MathUtil.LinearScale(fit, _config.TrendFitThreshold - 0.25, _config.TrendFitThreshold + 0.20);

        // --- Directional trend ----------------------------------------------------------
        // Requires strength AND fit AND multi-timeframe agreement. Any one of the three on
        // its own is routinely produced by noise.
        double trendCore = (adxTrend * 0.35) + (absTrend * 0.35) + (fitGate * 0.20) +
                           (MathUtil.Clamp01(Math.Abs(f.TimeframeAgreement)) * 0.10);

        if (f.TrendStrength > 0)
        {
            _scores[MarketRegime.TrendUp] = trendCore * DirectionBonus(f, Side.Long);
            _scores[MarketRegime.TrendDown] = 0;
        }
        else if (f.TrendStrength < 0)
        {
            _scores[MarketRegime.TrendDown] = trendCore * DirectionBonus(f, Side.Short);
            _scores[MarketRegime.TrendUp] = 0;
        }
        else
        {
            _scores[MarketRegime.TrendUp] = 0;
            _scores[MarketRegime.TrendDown] = 0;
        }

        // --- Range ----------------------------------------------------------------------
        // Non-trending, contained inside its bands, with structure that is going nowhere.
        double containment = 1.0 - MathUtil.Clamp01(Math.Abs(f.BollingerPercentB - 0.5) * 2.0);
        double structureFlat = 1.0 - MathUtil.Clamp01(Math.Abs(f.StructureScore));
        _scores[MarketRegime.Range] =
            (adxRange * 0.40) + (containment * 0.25) + (structureFlat * 0.20) + ((1.0 - absTrend) * 0.15);

        // --- Chop -----------------------------------------------------------------------
        // Distinct from Range: Range is orderly and tradeable from the edges; Chop is
        // directionless AND disorderly, with poor fit and small bodies. Conflating them is
        // how a mean-reversion book gets destroyed.
        double smallBodies = 1.0 - MathUtil.Clamp01(f.BodyFraction);
        double poorFit = 1.0 - fit;
        double timeframeConflict = MathUtil.Clamp01(-f.TimeframeAgreement);
        _scores[MarketRegime.Chop] =
            (adxRange * 0.30) + (poorFit * 0.30) + (smallBodies * 0.20) + (timeframeConflict * 0.20);

        // --- Breakout -------------------------------------------------------------------
        // Compression giving way, at an edge, with participation. Volume is required: a
        // breakout nobody is trading is a fake-out waiting to happen.
        double atEdge = Math.Max(
            MathUtil.LinearScale(f.DonchianPosition, 0.85, 1.0),
            MathUtil.LinearScale(1.0 - f.DonchianPosition, 0.85, 1.0));
        double wasCompressed = 1.0 - MathUtil.Clamp01(f.BollingerWidthPercentile);
        double expanding = MathUtil.LinearScale(f.VolatilityExpansion, 0.0, 0.25);
        double participation = MathUtil.LinearScale(f.VolumePercentile, 0.55, 0.90);
        _scores[MarketRegime.Breakout] =
            (atEdge * 0.35) + (wasCompressed * 0.20) + (expanding * 0.25) + (participation * 0.20);

        // --- Panic ----------------------------------------------------------------------
        // Extreme volatility with directional violence and abnormal participation. The
        // asymmetry is intentional: panic is overwhelmingly a downside phenomenon in crypto.
        double volExtreme = MathUtil.LinearScale(f.AtrPercentile, _config.PanicVolPercentile - 0.05, 1.0);
        double violence = MathUtil.LinearScale(Math.Abs(f.Return1InAtr), 1.5, 4.0);
        double volumeShock = MathUtil.LinearScale(f.VolumeZScore, 2.0, 5.0);
        double downside = f.Return5 < 0 ? 1.0 : 0.45;
        _scores[MarketRegime.Panic] = ((volExtreme * 0.45) + (violence * 0.30) + (volumeShock * 0.25)) * downside;

        // --- Euphoria -------------------------------------------------------------------
        // Vertical up-move, stretched from the mean, overbought, on heavy volume.
        double stretched = MathUtil.LinearScale(f.DistanceToEmaFastInAtr, 2.0, 5.0);
        double overbought = MathUtil.LinearScale(f.Rsi, 75, 90);
        double upThrust = f.Return5 > 0 ? MathUtil.LinearScale(f.Return5 / Math.Max(f.AtrFraction, 1e-9), 3, 10) : 0;
        _scores[MarketRegime.Euphoria] = (stretched * 0.35) + (overbought * 0.30) + (upThrust * 0.20) + (volumeShock * 0.15);

        // --- Liquidity stress -----------------------------------------------------------
        // Costs and feed behaviour, not price. This is the regime that most often makes an
        // otherwise-good signal unprofitable, and it is invisible to price-only models.
        double spreadStress = MathUtil.LinearScale(f.SpreadPercentile, _config.LiquidityStressSpreadPercentile - 0.10, 1.0);
        double spreadToAtrStress = MathUtil.LinearScale(f.SpreadToAtr, 0.10, 0.35);
        double feedStress = MathUtil.LinearScale(f.TickInterArrivalZScore, 2.0, 5.0);
        double thinVolume = 1.0 - MathUtil.Clamp01(f.VolumePercentile);
        _scores[MarketRegime.LiquidityStress] =
            (spreadStress * 0.35) + (spreadToAtrStress * 0.35) + (feedStress * 0.15) + (thinVolume * 0.15);

        foreach (MarketRegime key in new List<MarketRegime>(_scores.Keys))
        {
            _scores[key] = MathUtil.Clamp01(MathUtil.Finite(_scores[key]));
        }

        // --- Volatility states, scored as RESIDUALS -------------------------------------
        // "High volatility" is what you say about a market when you cannot say anything more
        // specific about it. If the structural read is strong -- a clean trend, a clear
        // breakout -- then the market's name is that structure, and the fact that it is also
        // volatile is carried separately in VolatilityState rather than overriding the label.
        //
        // Scoring these head-to-head with the structural regimes was a modelling error: a
        // strong uptrend in a volatile instrument would score 1.0 on both axes and the label
        // would be decided by whichever happened to be compared first.
        double structuralMax = 0;
        for (int i = 0; i < StructuralRegimes.Length; i++)
        {
            structuralMax = Math.Max(structuralMax, _scores[StructuralRegimes[i]]);
        }

        double rawHighVol = MathUtil.LinearScale(f.AtrPercentile, _config.HighVolPercentile - 0.15, _config.HighVolPercentile + 0.10);
        double rawLowVol = MathUtil.LinearScale(1.0 - f.AtrPercentile, 1.0 - _config.LowVolPercentile - 0.10, 1.0 - _config.LowVolPercentile + 0.15);

        double residual = 1.0 - structuralMax;
        _scores[MarketRegime.HighVolatility] = MathUtil.Clamp01(rawHighVol * residual);
        _scores[MarketRegime.LowVolatility] = MathUtil.Clamp01(rawLowVol * residual);

        // The volatility state itself, independent of the label: -1 very quiet, +1 extreme.
        _volatilityState = MathUtil.Clamp(rawHighVol - rawLowVol, -1, 1);
    }

    /// <summary>
    /// Rewards a directional trend read when structure and the higher timeframe agree with
    /// it. Returns a multiplier in 0.6..1.0 — agreement is a bonus, disagreement a discount,
    /// neither is a veto.
    /// </summary>
    private static double DirectionBonus(FeatureVector f, Side side)
    {
        double sign = side == Side.Long ? 1 : -1;
        double structureAgrees = MathUtil.Clamp01(0.5 + (sign * f.StructureScore * 0.5));
        double contextAgrees = MathUtil.Clamp01(0.5 + (sign * f.ContextTrendStrength * 0.5));
        return 0.6 + (0.4 * ((structureAgrees * 0.5) + (contextAgrees * 0.5)));
    }

    /// <summary>
    /// Applies hysteresis and computes confidence. A new regime must win for
    /// <see cref="RegimeConfig.ConfirmationBars"/> consecutive bars before it is adopted.
    /// </summary>
    private RegimeAssessment Confirm()
    {
        // Ranked over a FIXED, ordered list rather than over the dictionary, so that a tie
        // resolves the same way on every run and in every process. Dictionary enumeration
        // order is not a tie-break policy; the declared order of StructuralRegimes is, and
        // it puts the risk-off regimes first so a tie resolves toward caution.
        MarketRegime top = MarketRegime.Unknown;
        MarketRegime second = MarketRegime.Unknown;
        double topScore = -1, secondScore = -1;

        for (int i = 0; i < StructuralRegimes.Length + VolatilityRegimes.Length; i++)
        {
            MarketRegime regime = i < StructuralRegimes.Length
                ? StructuralRegimes[i]
                : VolatilityRegimes[i - StructuralRegimes.Length];

            if (!_scores.TryGetValue(regime, out double score)) continue;

            if (score > topScore)
            {
                second = top; secondScore = topScore;
                top = regime; topScore = score;
            }
            else if (score > secondScore)
            {
                second = regime; secondScore = score;
            }
        }

        if (topScore <= 0)
        {
            return RegimeAssessment.Unknown;
        }

        // --- Hysteresis -----------------------------------------------------------------
        if (top == _confirmed)
        {
            _candidate = top;
            _candidateStreak = _config.ConfirmationBars;
            _barsInRegime++;
        }
        else if (top == _candidate)
        {
            _candidateStreak++;
            if (_candidateStreak >= _config.ConfirmationBars)
            {
                _previousConfirmed = _confirmed;
                _confirmed = top;
                _barsInRegime = 1;
                _barsSinceChange = 0;
            }
            else
            {
                _barsInRegime++;
            }
        }
        else
        {
            _candidate = top;
            _candidateStreak = 1;
            _barsInRegime++;
        }

        if (_barsSinceChange < int.MaxValue) _barsSinceChange++;

        // The very first classification adopts immediately; there is nothing to flip from.
        if (_confirmed == MarketRegime.Unknown)
        {
            _confirmed = top;
            _barsInRegime = 1;
            _barsSinceChange = 0;
        }

        // --- Confidence -----------------------------------------------------------------
        // Three ingredients: how strong the winning score is, how far clear of the runner-up
        // it is, and how long it has persisted. A high score that barely beats its rival is
        // not confidence, it is a tie.
        double margin = MathUtil.Clamp01(topScore - Math.Max(0, secondScore));
        double strength = MathUtil.Clamp01(topScore);
        double persistence = MathUtil.LinearScale(_barsInRegime, 1, _config.ConfirmationBars * 3);

        double confidence = MathUtil.Clamp01((strength * 0.40) + (margin * 0.40) + (persistence * 0.20));

        // The reported regime may lag the instantaneous top score during hysteresis. When it
        // does, confidence must be cut: the model is knowingly reporting a stale label.
        if (top != _confirmed) confidence *= 0.55;

        bool transitioning = _barsSinceChange < _config.TransitionBars;
        double riskMultiplier = confidence;
        if (transitioning) riskMultiplier *= _config.TransitionRiskMultiplier;

        return new RegimeAssessment
        {
            Primary = _confirmed,
            Runner = second,
            Previous = _previousConfirmed,
            Confidence = confidence,
            Scores = new Dictionary<MarketRegime, double>(_scores),
            IsTransitioning = transitioning,
            BarsInRegime = _barsInRegime,
            RiskMultiplier = MathUtil.Clamp01(riskMultiplier),
            VolatilityState = _volatilityState,
        };
    }

    public void Reset()
    {
        _scores.Clear();
        _confirmed = MarketRegime.Unknown;
        _previousConfirmed = MarketRegime.Unknown;
        _candidate = MarketRegime.Unknown;
        _candidateStreak = 0;
        _barsInRegime = 0;
        _barsSinceChange = int.MaxValue;
        _volatilityState = 0;
    }
}
