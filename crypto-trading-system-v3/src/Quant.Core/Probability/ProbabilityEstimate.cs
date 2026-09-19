using System;
using Quant.Core.Numerics;

namespace Quant.Core.Probability;

/// <summary>
/// A win-probability estimate WITH its uncertainty (spec sections 15, 19).
///
/// The uncertainty is not decoration. A 65% estimate from twelve trades and a 65% estimate
/// from four hundred are different objects, and a system that cannot tell them apart will
/// size them identically and be destroyed by the first. Everything downstream consumes
/// <see cref="LowerBound"/>, not <see cref="PWin"/>.
/// </summary>
public readonly struct ProbabilityEstimate
{
    public static readonly ProbabilityEstimate Unavailable = new ProbabilityEstimate(0.5, 0.5, 0, 0, "no estimate available");

    public ProbabilityEstimate(double pWin, double standardError, double effectiveSample, double calibrationQuality, string basis)
    {
        PWin = MathUtil.Clamp01(pWin);
        StandardError = Math.Max(0, standardError);
        EffectiveSample = Math.Max(0, effectiveSample);
        CalibrationQuality = MathUtil.Clamp01(calibrationQuality);
        Basis = basis;
    }

    /// <summary>Posterior mean probability that the trade reaches its target before its stop.</summary>
    public double PWin { get; }

    public double PLoss => 1.0 - PWin;

    /// <summary>Posterior standard deviation of <see cref="PWin"/>.</summary>
    public double StandardError { get; }

    /// <summary>Pseudo-observations behind the estimate, prior included.</summary>
    public double EffectiveSample { get; }

    /// <summary>
    /// How well this model's stated probabilities have matched realised outcomes, 0..1
    /// (spec section 16). A model whose 70% calls win 45% of the time has its influence cut
    /// rather than its numbers trusted.
    /// </summary>
    public double CalibrationQuality { get; }

    /// <summary>Which slice of history the estimate came from, for the journal.</summary>
    public string Basis { get; }

    /// <summary>
    /// Confidence in the estimate itself, 0..1, from sample size and calibration together.
    /// Distinct from the probability: you can be very confident that something is unlikely.
    /// </summary>
    public double Confidence =>
        MathUtil.Clamp01(MathUtil.LinearScale(EffectiveSample, 10, 120) * (0.4 + (0.6 * CalibrationQuality)));

    /// <summary>Lower confidence bound on the win probability, <paramref name="z"/> sigma below the mean.</summary>
    public double LowerBound(double z) => MathUtil.Clamp01(PWin - (z * StandardError));

    public override string ToString() =>
        string.Format("p={0:P1} +/-{1:P1} n={2:F0} cal={3:P0} [{4}]", PWin, StandardError, EffectiveSample, CalibrationQuality, Basis);
}
