using System;
using System.Collections.Generic;
using System.Text;
using Quant.Core.Config;
using Quant.Core.Numerics;

namespace Quant.Core.Probability;

/// <summary>Realised outcomes for one predicted-probability band.</summary>
public sealed class CalibrationBin
{
    public double LowerEdge { get; init; }
    public double UpperEdge { get; init; }
    public int Count { get; private set; }
    public int Wins { get; private set; }
    public double PredictedSum { get; private set; }

    public double RealizedRate => MathUtil.SafeDiv(Wins, Count);
    public double MeanPredicted => MathUtil.SafeDiv(PredictedSum, Count);

    /// <summary>Signed gap between what was predicted and what happened.</summary>
    public double Error => MeanPredicted - RealizedRate;

    public void Add(double predicted, bool win)
    {
        Count++;
        PredictedSum += predicted;
        if (win) Wins++;
    }

    public void Reset() { Count = 0; Wins = 0; PredictedSum = 0; }

    public override string ToString() =>
        string.Format("[{0:P0}-{1:P0}] n={2} predicted={3:P1} realized={4:P1} err={5:+0.0%;-0.0%;0.0%}",
            LowerEdge, UpperEdge, Count, MeanPredicted, RealizedRate, Error);
}

/// <summary>
/// Checks whether stated probabilities mean anything (spec sections 16, 84, 125).
///
/// The test is the obvious one and it is rarely applied: among all the trades the model
/// called 70%, did about 70% win? A model that is consistently over-confident is not merely
/// inaccurate, it is actively dangerous, because every downstream layer — expected value,
/// edge threshold, position size — multiplies that number and compounds the error.
///
/// Two standard scores are kept:
///   * EXPECTED CALIBRATION ERROR: the sample-weighted mean absolute gap between predicted
///     and realised rates.
///   * BRIER SCORE: mean squared error of the probabilities, which unlike ECE also rewards
///     a model for being decisive rather than always saying "about half".
/// </summary>
public sealed class CalibrationTracker
{
    private readonly ProbabilityConfig _config;
    private readonly List<CalibrationBin> _bins = new List<CalibrationBin>();
    private double _brierSum;
    private int _observations;

    public CalibrationTracker(ProbabilityConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));

        double[] edges = config.CalibrationBucketEdges;
        for (int i = 0; i < edges.Length - 1; i++)
        {
            _bins.Add(new CalibrationBin { LowerEdge = edges[i], UpperEdge = edges[i + 1] });
        }
    }

    public IReadOnlyList<CalibrationBin> Bins => _bins;
    public int Observations => _observations;

    /// <summary>Mean squared error of the predicted probabilities. Lower is better; 0.25 is a coin flip.</summary>
    public double BrierScore => MathUtil.SafeDiv(_brierSum, _observations, 0.25);

    public void Observe(double predictedProbability, bool win)
    {
        double p = MathUtil.Clamp01(predictedProbability);

        _observations++;
        double outcome = win ? 1.0 : 0.0;
        _brierSum += (p - outcome) * (p - outcome);

        FindBin(p)?.Add(p, win);
    }

    /// <summary>
    /// Expected calibration error: the sample-weighted mean absolute gap between predicted
    /// and realised rates, over bins that have enough observations to be worth reading.
    /// Returns 0 when no bin qualifies — which <see cref="Quality"/> treats as "unknown",
    /// not as "perfect".
    /// </summary>
    public double ExpectedCalibrationError()
    {
        double weightedError = 0;
        int total = 0;

        for (int i = 0; i < _bins.Count; i++)
        {
            if (_bins[i].Count < _config.MinSamplePerCalibrationBin) continue;
            weightedError += Math.Abs(_bins[i].Error) * _bins[i].Count;
            total += _bins[i].Count;
        }

        return total == 0 ? 0 : weightedError / total;
    }

    /// <summary>
    /// Calibration quality in 0..1, used to scale the model's influence.
    ///
    /// Returns a deliberately mediocre 0.5 while evidence is thin. Returning 1.0 would let
    /// an untested model be trusted completely, which is exactly backwards: an unvalidated
    /// probability model is the one that most needs its influence limited.
    /// </summary>
    /// <summary>Есть ли хотя бы одна корзина с достаточной выборкой, чтобы калибровку можно было судить.</summary>
    public bool IsMeasured
    {
        get
        {
            for (int i = 0; i < _bins.Count; i++)
            {
                if (_bins[i].Count >= _config.MinSamplePerCalibrationBin) return true;
            }
            return false;
        }
    }

    public double Quality()
    {
        int usable = 0;
        for (int i = 0; i < _bins.Count; i++)
        {
            if (_bins[i].Count >= _config.MinSamplePerCalibrationBin) usable++;
        }
        if (usable == 0) return 0.5;

        double ece = ExpectedCalibrationError();
        double eceScore = 1.0 - MathUtil.Clamp01(ece / Math.Max(_config.MaxAcceptableCalibrationError, 1e-6));

        // Coverage: a model validated in one narrow band is only partly validated.
        double coverage = MathUtil.Clamp01((double)usable / Math.Max(1, _bins.Count / 2));

        return MathUtil.Clamp01((eceScore * 0.75) + (coverage * 0.25));
    }

    /// <summary>
    /// Maximum fraction by which a prediction may be shrunk toward the base rate on account
    /// of poor calibration.
    ///
    /// Bounded deliberately. An unbounded shrink lets a badly calibrated model lose ALL of
    /// its information, which creates a deadlock: the model needs to trade to accumulate the
    /// evidence that would prove it calibrated, but its estimates have been flattened to the
    /// base rate so nothing ever clears the edge threshold and it never trades. The harsh
    /// response to poor calibration belongs in the required-edge threshold, where it raises
    /// the bar, rather than here, where it would corrupt the estimate itself.
    /// </summary>
    private const double MaxBaseRateShrink = 0.40;

    /// <summary>
    /// Corrects a predicted probability using the realised outcomes (spec sections 16, 84).
    ///
    /// Two steps, and the first is the one that matters:
    ///
    ///   1. EMPIRICAL CORRECTION. If the bin this prediction falls into has enough history,
    ///      the prediction is moved toward what that bin actually delivered. A model that
    ///      says 60% and delivers 70% should have its 60% read as nearer 70%, which is what
    ///      recalibration means. Simply distrusting it would throw away a real, measurable,
    ///      correctable bias.
    ///
    ///   2. BOUNDED SHRINK. Separately, and only up to <see cref="MaxBaseRateShrink"/>, the
    ///      result is pulled toward the base rate in proportion to how poorly calibrated the
    ///      model has been overall. This is the humility term, not the correction term.
    /// </summary>
    public double Recalibrate(double predicted, double baseRate)
    {
        double p = MathUtil.Clamp01(predicted);

        // Step 1: empirical correction from the bin's own realised rate.
        double corrected = p;
        CalibrationBin bin = FindBin(p);
        if (bin != null && bin.Count >= _config.MinSamplePerCalibrationBin)
        {
            double evidence = MathUtil.LinearScale(bin.Count, _config.MinSamplePerCalibrationBin, _config.MinSamplePerCalibrationBin * 5);
            corrected = p + (evidence * (bin.RealizedRate - p));
        }

        // Step 2: bounded shrink toward the base rate — только если калибровка ИЗМЕРЕНА.
        //
        // Неизмеренная калибровка — это не плохая калибровка, а её отсутствие. Качество в
        // этом случае сообщается как 0.5, и сдвиг на 20% к безубыточности вычитал из
        // оценки то же незнание, которое байесовский априор уже вычел: 60 теневых сделок с
        // 62% целей давали 0.494, после сдвига — 0.468, ниже порога. Одно и то же
        // «мало данных» считалось дважды, и второй раз — так, что снять его можно только
        // реальными сделками. Неизмеренная калибровка по-прежнему режет РАЗМЕР — через
        // доверие к оценке, — но оценку больше не портит.
        if (!IsMeasured) return MathUtil.Clamp01(corrected);

        double shrink = MaxBaseRateShrink * (1.0 - Quality());
        return MathUtil.Clamp01((corrected * (1 - shrink)) + (baseRate * shrink));
    }

    /// <summary>Finds the bin a probability falls into, or null when none matches.</summary>
    private CalibrationBin FindBin(double p)
    {
        for (int i = 0; i < _bins.Count; i++)
        {
            bool isLast = i == _bins.Count - 1;
            if (p >= _bins[i].LowerEdge && (p < _bins[i].UpperEdge || (isLast && p <= _bins[i].UpperEdge)))
            {
                return _bins[i];
            }
        }
        return null;
    }

    /// <summary>Renders the reliability table for the journal.</summary>
    public string Report()
    {
        var sb = new StringBuilder();
        sb.AppendFormat("Calibration: n={0} ECE={1:P1} Brier={2:F4} quality={3:P0}",
            _observations, ExpectedCalibrationError(), BrierScore, Quality());

        for (int i = 0; i < _bins.Count; i++)
        {
            if (_bins[i].Count == 0) continue;
            sb.AppendLine();
            sb.Append("  ").Append(_bins[i]);
            if (_bins[i].Count < _config.MinSamplePerCalibrationBin) sb.Append(" (thin)");
        }

        return sb.ToString();
    }

    public void Reset()
    {
        for (int i = 0; i < _bins.Count; i++) _bins[i].Reset();
        _brierSum = 0;
        _observations = 0;
    }
}
