using System;
using System.Collections.Generic;
using Quant.Core.Primitives;

namespace Quant.Core.Data;

/// <summary>Outcome of the data-quality assessment for one symbol at one instant.</summary>
public readonly struct DataQualityReport
{
    public DataQualityReport(bool isAcceptable, double score, IReadOnlyList<string> issues,
        NoTradeReason reason = NoTradeReason.DataQuality)
    {
        IsAcceptable = isAcceptable;
        Score = score;
        Issues = issues ?? Array.Empty<string>();
        Reason = reason;
    }

    /// <summary>
    /// Причина отказа, если данные непригодны.
    ///
    /// Закрытый рынок — не дефект данных, и называть его так значит прятать самую частую
    /// причину бездействия за формулировкой, которая звучит как поломка. В сводке причин
    /// отказа разница между `MarketClosed` и `DataQuality` — это разница между «ждёт
    /// открытия» и «что-то сломалось».
    /// </summary>
    public NoTradeReason Reason { get; }

    /// <summary>False means: no new trade. Existing positions are still managed.</summary>
    public bool IsAcceptable { get; }

    /// <summary>Quality in 0..1, used to scale risk rather than merely to gate.</summary>
    public double Score { get; }

    /// <summary>Human-readable reasons, recorded in the journal so a veto is never mysterious.</summary>
    public IReadOnlyList<string> Issues { get; }

    public string IssueSummary => Issues.Count == 0 ? "clean" : string.Join("; ", Issues);

    public override string ToString() =>
        string.Format("{0} score={1:F2} [{2}]", IsAcceptable ? "OK" : "REJECT", Score, IssueSummary);
}
