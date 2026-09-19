using System;
using Quant.Core.Config;
using Quant.Core.Numerics;

namespace Quant.Core.Risk;

/// <summary>
/// Tracks how well orders actually fill against how well they were expected to
/// (spec sections 58-59).
///
/// This exists because execution decay is invisible to every other part of the system. A
/// strategy can keep producing exactly the same signals while the broker's fills quietly
/// get worse — a venue degrades, liquidity thins, the account is moved to a different
/// pool — and the only symptom is that a validated edge stops showing up in the P&amp;L.
/// Measuring realised slippage against the model's own prediction turns that from a mystery
/// into a number, and the number feeds straight back into position sizing.
/// </summary>
public sealed class ExecutionQualityTracker
{
    private readonly RollingWindow _slippageRatios;
    private readonly RollingWindow _absoluteSlippagePips;

    public ExecutionQualityTracker(ExecutionConfig config)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));
        _slippageRatios = new RollingWindow(config.ExecutionQualityWindow);
        _absoluteSlippagePips = new RollingWindow(config.ExecutionQualityWindow);
    }

    public int Observations => _slippageRatios.Count;
    public int RejectedOrders { get; private set; }
    public int SubmittedOrders { get; private set; }

    /// <summary>Mean realised slippage divided by predicted slippage. 1.0 means the model is accurate.</summary>
    public double SlippageMultiplier => _slippageRatios.Count < 5 ? 1.0 : MathUtil.Clamp(_slippageRatios.Mean, 0.2, 5.0);

    public double MedianSlippagePips => _absoluteSlippagePips.Median();
    public double WorstSlippagePips => _absoluteSlippagePips.Count == 0 ? 0 : _absoluteSlippagePips.Max();

    public double RejectionRate => MathUtil.SafeDiv(RejectedOrders, SubmittedOrders);

    /// <summary>
    /// Execution quality in 0..1, where 1 means fills match expectations and rejections are
    /// rare. Starts at a neutral 0.85 rather than 1.0: an unmeasured broker has not yet
    /// earned full marks.
    /// </summary>
    public double Quality
    {
        get
        {
            if (_slippageRatios.Count < 5) return 0.85;

            // Slippage at or below prediction scores full marks; at three times prediction,
            // nothing.
            double slippageScore = 1.0 - MathUtil.LinearScale(SlippageMultiplier, 1.0, 3.0);

            // Consistency matters separately from level: fills that are unpredictable make
            // every stop and target placement unreliable, even if they average out.
            double dispersion = _slippageRatios.StdDev;
            double consistencyScore = 1.0 - MathUtil.LinearScale(dispersion, 0.5, 2.5);

            double rejectionScore = 1.0 - MathUtil.LinearScale(RejectionRate, 0.02, 0.20);

            // The LEVEL of slippage dominates its consistency on purpose. Fills that are
            // reliably four times worse than modelled are predictable, but they are still
            // four times worse: predictability lets the cost model price them, it does not
            // make them cheap, and size should come down either way.
            return MathUtil.Clamp01((slippageScore * 0.60) + (consistencyScore * 0.15) + (rejectionScore * 0.25));
        }
    }

    /// <summary>
    /// Records a fill.
    /// </summary>
    /// <param name="requestedPrice">The price the decision was made at.</param>
    /// <param name="filledPrice">The price actually received.</param>
    /// <param name="isBuy">Direction, which determines the sign of adverse slippage.</param>
    /// <param name="predictedSlippage">What the cost model expected to lose, in price units.</param>
    /// <param name="pipSize">For reporting slippage in pips.</param>
    public void RecordFill(double requestedPrice, double filledPrice, bool isBuy, double predictedSlippage, double pipSize)
    {
        SubmittedOrders++;

        if (!MathUtil.IsFinite(requestedPrice) || !MathUtil.IsFinite(filledPrice) || requestedPrice <= 0 || filledPrice <= 0) return;

        // Adverse slippage is positive: buying higher, or selling lower, than requested.
        double adverse = isBuy ? filledPrice - requestedPrice : requestedPrice - filledPrice;

        if (pipSize > 0) _absoluteSlippagePips.Add(adverse / pipSize);

        // A prediction of zero cannot be scored, so those fills contribute to the absolute
        // record but not to the ratio.
        if (predictedSlippage > MathUtil.Epsilon)
        {
            _slippageRatios.Add(MathUtil.Clamp(adverse / predictedSlippage, -2, 10));
        }
    }

    public void RecordRejection()
    {
        SubmittedOrders++;
        RejectedOrders++;
    }

    public void Reset()
    {
        _slippageRatios.Clear();
        _absoluteSlippagePips.Clear();
        RejectedOrders = 0;
        SubmittedOrders = 0;
    }

    public override string ToString() =>
        string.Format("execution quality {0:P0} (slippage x{1:F2}, median {2:F1} pips, rejections {3:P1}, n={4})",
            Quality, SlippageMultiplier, MedianSlippagePips, RejectionRate, Observations);
}
