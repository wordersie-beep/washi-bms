using System;
using Quant.Core.Numerics;

namespace Quant.Core.Ev;

/// <summary>
/// The verdict on whether an idea is worth paying for (spec sections 17-19).
/// </summary>
public sealed class ExpectedValueResult
{
    public static ExpectedValueResult Reject(string reason) => new ExpectedValueResult
    {
        ExpectedValueR = double.NegativeInfinity,
        LowerBoundR = double.NegativeInfinity,
        DecisionEdgeR = double.NegativeInfinity,
        RequiredEdgeR = 0,
        Rationale = reason,
    };

    /// <summary>Point estimate of expected value per trade, in R, net of all costs.</summary>
    public double ExpectedValueR { get; init; }

    /// <summary>
    /// Нижняя доверительная граница ожидания. Сообщается, но решения не принимает — см.
    /// <see cref="Trust"/>, куда ушла её роль.
    /// </summary>
    public double LowerBoundR { get; init; }

    /// <summary>
    /// Величина, по которой принимается решение: байесовская точечная оценка ожидания после
    /// издержек. Она уже сжата к априорному «преимущества нет» ровно настолько, насколько
    /// мало доказательств, — поэтому вторая, отдельная надбавка за незнание была двойным
    /// счётом, и двойным счётом, из которого не было выхода.
    /// </summary>
    public double DecisionEdgeR { get; init; }

    /// <summary>Реальных сделок ещё меньше порога холодного старта.</summary>
    public bool IsColdStart { get; init; }

    /// <summary>
    /// Во что оценка обошлась бы, будь незнание ВЕТО: ширина оценки, тонкость выборки и
    /// отсутствие калибровки, в R. Теперь это не порог, а мера недоверия.
    /// </summary>
    public double EvidencePenaltyR { get; init; }

    /// <summary>
    /// Доля полного размера, которую оправдывают доказательства, (0, 1].
    ///
    /// Сюда переехала вся осторожность, которая раньше жила в пороге. Порог, снимаемый
    /// только сделками, запрещал сделки: система с настоящим преимуществом совершала
    /// двадцать сделок и замолкала навсегда — после холодного старта требование
    /// подскакивало на три четверти R, а снизить его могли только новые сделки. Размер
    /// же можно уменьшить, не запрещая: неуверенная система торгует мало, а не никогда.
    /// </summary>
    public double Trust { get; init; } = 1.0;

    /// <summary>Edge the trade had to clear, above zero, to be worth taking (spec section 18).</summary>
    public double RequiredEdgeR { get; init; }

    /// <summary>Expected win size in R, from the realistic target.</summary>
    public double ExpectedWinR { get; init; }

    /// <summary>Expected loss size in R, at or above 1.0 to allow for stop slippage.</summary>
    public double ExpectedLossR { get; init; }

    /// <summary>Round-trip cost in R.</summary>
    public double CostR { get; init; }

    /// <summary>Win probability used, after calibration.</summary>
    public double WinProbability { get; init; }

    /// <summary>Reward-to-risk at the target actually used, which may be below the one requested.</summary>
    public double EffectiveRewardToRisk { get; init; }

    /// <summary>True when the target was capped by the historical MFE distribution (spec section 44).</summary>
    public bool TargetCappedByHistory { get; init; }

    public string Rationale { get; init; }

    /// <summary>Единственные ворота: оценка ожидания после издержек обязана превысить требование.</summary>
    public bool IsAcceptable => MathUtil.IsFinite(DecisionEdgeR) && DecisionEdgeR >= RequiredEdgeR;

    /// <summary>How far past the required edge the trade sits. Feeds opportunity ranking.</summary>
    public double EdgeSurplusR => MathUtil.IsFinite(DecisionEdgeR) ? DecisionEdgeR - RequiredEdgeR : double.NegativeInfinity;

    public override string ToString() =>
        MathUtil.IsFinite(ExpectedValueR)
            ? string.Format("EV={0:F3}R (lower {1:F3}R, required {2:F3}R) p={3:P1} rr={4:F2} cost={5:F3}R trust={6:P0} -> {7}",
                ExpectedValueR, LowerBoundR, RequiredEdgeR, WinProbability, EffectiveRewardToRisk, CostR, Trust,
                IsAcceptable ? "ACCEPT" : "REJECT")
            : "REJECT: " + Rationale;
}
