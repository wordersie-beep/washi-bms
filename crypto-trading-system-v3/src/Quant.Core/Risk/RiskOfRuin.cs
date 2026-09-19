using System;
using Quant.Core.Numerics;

namespace Quant.Core.Risk;

/// <summary>Risk-of-ruin estimate with the inputs it was derived from.</summary>
public readonly struct RuinEstimate
{
    public RuinEstimate(double probability, double winRate, double payoffRatio, double riskFraction, int horizonTrades, string method)
    {
        Probability = probability;
        WinRate = winRate;
        PayoffRatio = payoffRatio;
        RiskFraction = riskFraction;
        HorizonTrades = horizonTrades;
        Method = method;
    }

    /// <summary>Estimated probability of hitting the ruin threshold within the horizon.</summary>
    public double Probability { get; }

    public double WinRate { get; }
    public double PayoffRatio { get; }
    public double RiskFraction { get; }
    public int HorizonTrades { get; }
    public string Method { get; }

    public override string ToString() =>
        string.Format("ruin={0:P2} over {1} trades (wr={2:P1} payoff={3:F2} risk={4:P2}) [{5}]",
            Probability, HorizonTrades, WinRate, PayoffRatio, RiskFraction, Method);
}

/// <summary>
/// Estimates the probability of a catastrophic drawdown (spec sections 34, 98).
///
/// Uses Monte Carlo over resampled trade outcomes rather than the textbook closed form.
/// The closed form assumes fixed bet sizes and independent identically distributed outcomes;
/// this system compounds — risk is a fraction of current equity — and losses cluster. Both
/// of those matter enormously for the tail, and both are trivial to simulate and awkward to
/// solve analytically.
///
/// Loss clustering is modelled explicitly (spec section 96) because the assumption that
/// losses arrive independently is the single most optimistic assumption in conventional
/// risk-of-ruin arithmetic, and the one that markets most reliably violate.
/// </summary>
public sealed class RiskOfRuinEstimator
{
    private readonly int _paths;
    private readonly int _horizonTrades;
    private readonly ulong _seed;

    public RiskOfRuinEstimator(int paths = 2000, int horizonTrades = 200, ulong seed = 20260919UL)
    {
        _paths = Math.Max(200, paths);
        _horizonTrades = Math.Max(20, horizonTrades);
        _seed = seed;
    }

    /// <summary>
    /// Estimates ruin probability.
    /// </summary>
    /// <param name="winRate">Probability of a winning trade.</param>
    /// <param name="payoffRatio">Average win divided by average loss, in R.</param>
    /// <param name="riskFraction">Fraction of equity risked per trade.</param>
    /// <param name="ruinThresholdFraction">Fractional loss of starting equity that counts as ruin.</param>
    /// <param name="lossClusteringFactor">
    /// Probability that a loss is followed by another loss, over and above the base rate.
    /// 0 is independence; 0.2 means losses come in runs, which is what they do.
    /// </param>
    public RuinEstimate Estimate(
        double winRate,
        double payoffRatio,
        double riskFraction,
        double ruinThresholdFraction,
        double lossClusteringFactor = 0.15)
    {
        double p = MathUtil.Clamp(winRate, 0.01, 0.99);
        double payoff = MathUtil.Clamp(payoffRatio, 0.05, 20.0);
        double risk = MathUtil.Clamp(riskFraction, 0.0001, 0.5);
        double ruinLevel = 1.0 - MathUtil.Clamp(ruinThresholdFraction, 0.05, 0.95);
        double clustering = MathUtil.Clamp01(lossClusteringFactor);

        var rng = new Pcg32(_seed);
        int ruined = 0;

        for (int path = 0; path < _paths; path++)
        {
            double equity = 1.0;
            bool previousWasLoss = false;

            for (int trade = 0; trade < _horizonTrades; trade++)
            {
                // After a loss, the chance of another loss rises. This is what turns a
                // comfortable independent-trials figure into an honest one.
                double effectiveWinRate = previousWasLoss ? p * (1.0 - clustering) : p;

                bool win = rng.NextDouble() < effectiveWinRate;
                previousWasLoss = !win;

                // Risk compounds on current equity, so a drawdown shrinks subsequent bets.
                // That cuts both ways: it slows ruin, but it also slows recovery.
                double stake = equity * risk;
                equity += win ? stake * payoff : -stake;

                if (equity <= ruinLevel) { ruined++; break; }
                if (equity <= 0) { ruined++; break; }
            }
        }

        return new RuinEstimate(
            (double)ruined / _paths, p, payoff, risk, _horizonTrades, $"monte-carlo/{_paths}");
    }

    /// <summary>
    /// The classical closed-form approximation, kept as a cross-check on the simulation.
    ///
    /// Assumes fixed stakes and independent trials, so it is optimistic relative to the
    /// simulation. A large divergence between the two is itself informative: it means the
    /// compounding and clustering assumptions are doing a lot of work and the result should
    /// be treated with corresponding suspicion.
    /// </summary>
    public static double ClassicalApproximation(double winRate, double payoffRatio, double riskFraction, double ruinThresholdFraction)
    {
        double p = MathUtil.Clamp(winRate, 0.01, 0.99);
        double payoff = MathUtil.Clamp(payoffRatio, 0.05, 20.0);
        double risk = MathUtil.Clamp(riskFraction, 0.0001, 0.5);

        // Edge per unit staked. A non-positive edge means ruin is effectively certain over a
        // long enough horizon.
        double edge = (p * payoff) - (1 - p);
        if (edge <= 0) return 1.0;

        // Units of risk available before ruin.
        double units = MathUtil.Clamp(ruinThresholdFraction, 0.01, 0.99) / risk;

        // Standard gambler's-ruin form with an asymmetric payoff.
        double a = edge / (payoff * payoff * p + (1 - p));
        return MathUtil.Clamp01(Math.Exp(-2.0 * a * units));
    }
}
