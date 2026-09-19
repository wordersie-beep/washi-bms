using Quant.Core.Features;

namespace Quant.Core.Regime;

/// <summary>
/// Pluggable regime classifier (spec section 80).
///
/// The interface exists so that a learned classifier can later replace the statistical one
/// WITHOUT touching anything downstream, and — more importantly — so that the statistical
/// one remains available as a fallback when the learned one is unavailable, throws, or
/// drifts (spec sections 85-86). A model layer that cannot be switched off is a liability.
/// </summary>
public interface IMarketRegimeModel
{
    string Name { get; }

    /// <summary>True when the model has enough information to produce a usable read.</summary>
    bool IsReady { get; }

    /// <summary>
    /// Classifies the current market state. Must never throw and must never return null:
    /// on any internal failure it returns <see cref="RegimeAssessment.Unknown"/>, which the
    /// rest of the system treats as "do not trade".
    /// </summary>
    RegimeAssessment Classify(FeatureVector features);

    void Reset();
}
