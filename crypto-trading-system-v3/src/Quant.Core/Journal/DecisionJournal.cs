using System;
using System.Collections.Generic;
using System.Text;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Journal;

/// <summary>Where journal output goes. Keeps the core free of any platform logging API.</summary>
public interface IJournalSink
{
    void Write(string message);
}

/// <summary>A sink that discards everything, for tests and for optimisation runs.</summary>
public sealed class NullJournalSink : IJournalSink
{
    public static readonly NullJournalSink Instance = new NullJournalSink();
    public void Write(string message) { }
}

/// <summary>
/// The decision journal (spec sections 121-123).
///
/// Keeps a bounded in-memory history of decisions and rejections, and writes to a sink at a
/// controlled rate. Rate control matters more than it sounds: a bot evaluating several
/// symbols every bar, refusing almost all of them, will produce an unreadable wall of output
/// if every refusal is printed. The compromise is that accepted trades and NEW rejection
/// reasons are always written, while a rejection reason repeating on the same symbol is
/// counted rather than reprinted.
/// </summary>
public sealed class DecisionJournal
{
    private readonly IJournalSink _sink;
    private readonly Ring<DecisionRecord> _accepted;
    private readonly Ring<DecisionRecord> _rejected;
    private readonly Dictionary<string, NoTradeReason> _lastReasonPerSymbol = new Dictionary<string, NoTradeReason>(StringComparer.Ordinal);
    private readonly Dictionary<NoTradeReason, int> _reasonCounts = new Dictionary<NoTradeReason, int>();

    public DecisionJournal(IJournalSink sink, int capacity = 500, bool verbose = false)
    {
        _sink = sink ?? NullJournalSink.Instance;
        _accepted = new Ring<DecisionRecord>(Math.Max(50, capacity / 5));
        _rejected = new Ring<DecisionRecord>(Math.Max(100, capacity));
        Verbose = verbose;
    }

    /// <summary>When true, every rejection is written rather than only the changes.</summary>
    public bool Verbose { get; set; }

    public IReadOnlyCollection<DecisionRecord> Accepted => _accepted;
    public IReadOnlyCollection<DecisionRecord> Rejected => _rejected;
    public IReadOnlyDictionary<NoTradeReason, int> ReasonCounts => _reasonCounts;

    public long TotalAccepted { get; private set; }
    public long TotalRejected { get; private set; }

    public void RecordAcceptance(DecisionRecord record)
    {
        if (record == null) return;

        _accepted.Add(record);
        TotalAccepted++;
        _lastReasonPerSymbol.Remove(record.SymbolName);

        // Accepted trades are always written in full. There are few of them and each one is
        // the thing a reader most wants to understand.
        _sink.Write(record.Render());
    }

    public void RecordRejection(DecisionRecord record)
    {
        if (record == null) return;

        _rejected.Add(record);
        TotalRejected++;

        _reasonCounts.TryGetValue(record.RejectionReason, out int count);
        _reasonCounts[record.RejectionReason] = count + 1;

        bool isNewReason =
            !_lastReasonPerSymbol.TryGetValue(record.SymbolName, out NoTradeReason previous) ||
            previous != record.RejectionReason;

        _lastReasonPerSymbol[record.SymbolName] = record.RejectionReason;

        if (Verbose || isNewReason)
        {
            _sink.Write(record.RenderCompact());
        }
    }

    public void Write(string message) => _sink.Write(message);

    /// <summary>
    /// Summary of why the system has been declining to trade.
    ///
    /// This is the most useful diagnostic the journal produces. A system that takes no
    /// trades for a week is either correctly waiting or quietly broken, and the only way to
    /// tell from the outside is to see which gate is doing the refusing.
    /// </summary>
    public string RejectionSummary(int topN = 8)
    {
        if (_reasonCounts.Count == 0) return "No rejections recorded.";

        var ordered = new List<KeyValuePair<NoTradeReason, int>>(_reasonCounts);
        ordered.Sort((a, b) => b.Value.CompareTo(a.Value));

        var sb = new StringBuilder();
        sb.AppendFormat("Decisions: {0} accepted, {1} declined. Leading reasons:", TotalAccepted, TotalRejected);

        int shown = Math.Min(topN, ordered.Count);
        for (int i = 0; i < shown; i++)
        {
            double share = TotalRejected == 0 ? 0 : (double)ordered[i].Value / TotalRejected;
            sb.AppendLine();
            sb.AppendFormat("  {0,-28} {1,6} ({2,5:P1})", ordered[i].Key, ordered[i].Value, share);
        }

        return sb.ToString();
    }

    public void Reset()
    {
        _accepted.Clear();
        _rejected.Clear();
        _lastReasonPerSymbol.Clear();
        _reasonCounts.Clear();
        TotalAccepted = 0;
        TotalRejected = 0;
    }
}
