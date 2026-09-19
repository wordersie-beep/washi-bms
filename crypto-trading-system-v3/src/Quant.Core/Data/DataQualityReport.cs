using System;
using System.Collections.Generic;

namespace Quant.Core.Data;

/// <summary>Outcome of the data-quality assessment for one symbol at one instant.</summary>
public readonly struct DataQualityReport
{
    public DataQualityReport(bool isAcceptable, double score, IReadOnlyList<string> issues)
    {
        IsAcceptable = isAcceptable;
        Score = score;
        Issues = issues ?? Array.Empty<string>();
    }

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
