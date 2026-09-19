using System;

namespace Quant.Core.Numerics;

/// <summary>
/// Beta-Binomial posterior over a win rate (spec sections 15, 19, 22).
///
/// This is the system's answer to "10 trades is not proof of an edge": the prior pulls a
/// small sample toward the base rate, and the posterior standard deviation is carried
/// forward so that expected value can be judged on a lower confidence bound rather than on
/// a point estimate.
/// </summary>
public readonly struct BetaBinomial
{
    public BetaBinomial(double alpha, double beta)
    {
        Alpha = Math.Max(1e-6, alpha);
        Beta = Math.Max(1e-6, beta);
    }

    public double Alpha { get; }
    public double Beta { get; }

    /// <summary>Posterior mean win probability.</summary>
    public double Mean => Alpha / (Alpha + Beta);

    /// <summary>Posterior standard deviation of the win probability.</summary>
    public double StdDev
    {
        get
        {
            double n = Alpha + Beta;
            return Math.Sqrt((Alpha * Beta) / (n * n * (n + 1)));
        }
    }

    /// <summary>Total pseudo-observations backing the estimate (prior strength plus data).</summary>
    public double Strength => Alpha + Beta;

    /// <summary>
    /// Normal-approximation lower confidence bound on the win rate.
    /// <paramref name="z"/> of 1.0 is roughly one-sigma; 1.645 is a 95% one-sided bound.
    /// </summary>
    public double LowerBound(double z) => MathUtil.Clamp01(Mean - (z * StdDev));

    public double UpperBound(double z) => MathUtil.Clamp01(Mean + (z * StdDev));

    /// <summary>Builds a posterior from a prior mean, a prior strength and observed counts.</summary>
    public static BetaBinomial FromCounts(double priorMean, double priorStrength, double wins, double losses)
    {
        double m = MathUtil.Clamp(priorMean, 0.01, 0.99);
        double s = Math.Max(0.1, priorStrength);
        return new BetaBinomial((m * s) + Math.Max(0, wins), ((1 - m) * s) + Math.Max(0, losses));
    }

    public BetaBinomial WithObservation(bool win) =>
        win ? new BetaBinomial(Alpha + 1, Beta) : new BetaBinomial(Alpha, Beta + 1);

    public override string ToString() =>
        string.Format("Beta(a={0:F2}, b={1:F2}) mean={2:P1} sd={3:P1}", Alpha, Beta, Mean, StdDev);
}
