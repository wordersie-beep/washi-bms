using System;
using System.Collections.Generic;
using System.Text;
using Quant.Core.Config;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Ev;

/// <summary>
/// Может ли инструмент на этом таймфрейме вообще окупить собственные издержки.
///
/// Существует потому, что на вопрос «почему бот не совершил ни одной сделки» есть ответ,
/// который не виден ни в одном журнале отказов: на некоторых сочетаниях инструмента и
/// таймфрейма сделка не может быть прибыльной АРИФМЕТИЧЕСКИ, при любой стратегии и любом
/// качестве сигнала. Система в этом случае ведёт себя правильно — она отказывает, — но
/// снаружи это неотличимо от поломки.
///
/// Считается ровно то же, что считает <see cref="CostModel"/>, только вперёд и в обе
/// стороны: не «сколько стоит эта сделка», а «какой должна быть волатильность, чтобы
/// сделка вообще имела смысл».
///
/// Итог — не мнение и не прогноз. Это деление одного измеренного числа на другое.
/// </summary>
public static class TradeabilityReport
{
    public enum Verdict
    {
        /// <summary>Издержки укладываются: экономика не мешает торговать.</summary>
        Tradeable,

        /// <summary>Спред велик относительно волатильности — фильтр спреда отвергнет всё.</summary>
        SpreadTooWide,

        /// <summary>Спред проходит, но издержки съедают слишком большую долю риска.</summary>
        CostsEatTheRisk,

        /// <summary>Издержки укладываются, но требуемое преимущество недостижимо на практике.</summary>
        EdgeRequirementUnrealistic,
    }

    public readonly struct Line
    {
        public Line(Tf timeframe, double atr, double spread, double spreadToAtr,
            double costR, double requiredEdgeR, Verdict verdict)
        {
            Timeframe = timeframe;
            Atr = atr;
            Spread = spread;
            SpreadToAtr = spreadToAtr;
            CostR = costR;
            RequiredEdgeR = requiredEdgeR;
            Outcome = verdict;
        }

        public Tf Timeframe { get; }
        public double Atr { get; }
        public double Spread { get; }

        /// <summary>Спред как доля ATR. Единственное число, которое решает всё остальное.</summary>
        public double SpreadToAtr { get; }

        /// <summary>Издержки круга как доля риска сделки.</summary>
        public double CostR { get; }

        /// <summary>Матожидание, ниже которого сделка не окупится.</summary>
        public double RequiredEdgeR { get; }

        public Verdict Outcome { get; }
        public bool IsTradeable => Outcome == Verdict.Tradeable;
    }

    /// <summary>
    /// Преимущество, выше которого требование перестаёт быть реалистичным.
    ///
    /// Число не выведено из теории: систематическое матожидание в треть риска на сделку —
    /// это уже очень много для любой публично известной стратегии на малом таймфрейме.
    /// Оно служит не порогом решения, а границей честности отчёта: выше неё формально
    /// «торгуемо» означает «нужно чудо».
    /// </summary>
    public const double UnrealisticEdgeR = 0.35;

    /// <summary>
    /// Считает экономику одного таймфрейма.
    /// </summary>
    /// <param name="atr">ATR этого таймфрейма в единицах цены.</param>
    /// <param name="spread">Спред для расчёта издержек, в единицах цены.</param>
    /// <param name="price">Текущая цена — для перевода комиссии из объёма в цену.</param>
    public static Line Evaluate(
        Tf timeframe, double atr, double spread, double price,
        SymbolSpec spec, EngineConfig config)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));

        if (atr <= 0 || spread < 0 || price <= 0)
        {
            return new Line(timeframe, atr, spread, double.MaxValue, double.MaxValue,
                double.MaxValue, Verdict.SpreadTooWide);
        }

        double spreadToAtr = spread / atr;

        // Стоп — это и есть определение 1R. Более широкий стоп делит те же издержки на
        // большее число, поэтому таймфрейм и множитель стопа входят в ответ вместе.
        double stop = atr * config.Exit.AtrStopMultiple;

        double slippage = spread * config.Execution.NormalSlippageSpreadFraction * 2.0;
        double commission = spec == null ? 0 : 2.0 * (price / 1_000_000.0) * spec.CommissionPerMillionQuote;
        double costR = MathUtil.SafeDiv(spread + slippage + commission, stop, double.MaxValue);

        double requiredEdge = config.Ev.BaseMinimumEdgeR + (config.Ev.CostEdgeMultiplier * costR);

        Verdict verdict =
            spreadToAtr > config.Risk.MaxSpreadToAtr ? Verdict.SpreadTooWide
            : costR > config.Exit.MaxCostShareOfRisk ? Verdict.CostsEatTheRisk
            : requiredEdge > UnrealisticEdgeR ? Verdict.EdgeRequirementUnrealistic
            : Verdict.Tradeable;

        return new Line(timeframe, atr, spread, spreadToAtr, costR, requiredEdge, verdict);
    }

    /// <summary>
    /// ATR, при котором таймфрейм становится торгуемым при данном спреде. Отвечает на
    /// вопрос «а какой таймфрейм брать» числом, а не советом.
    /// </summary>
    public static double AtrNeededFor(double spread, EngineConfig config)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));
        if (spread <= 0) return 0;

        // Из фильтра спреда: spread / atr <= MaxSpreadToAtr.
        double fromSpreadGate = spread / Math.Max(1e-12, config.Risk.MaxSpreadToAtr);

        // Из потолка издержек: (spread * (1 + 2f)) / (atr * m) <= MaxCostShareOfRisk.
        double multiplier = 1.0 + (2.0 * config.Execution.NormalSlippageSpreadFraction);
        double fromCostCap = spread * multiplier /
            Math.Max(1e-12, config.Exit.MaxCostShareOfRisk * config.Exit.AtrStopMultiple);

        return Math.Max(fromSpreadGate, fromCostCap);
    }

    public static string Render(string symbol, IReadOnlyList<Line> lines, double neededAtr)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=========== ЭКОНОМИКА ИНСТРУМЕНТА ===========");
        sb.AppendLine($"  {symbol}: может ли сделка окупить собственные издержки");
        sb.AppendLine("  ТФ      ATR        спред   спред/ATR  издержки  нужно EV   вывод");

        for (int i = 0; i < lines.Count; i++)
        {
            Line l = lines[i];
            string verdict = l.Outcome switch
            {
                Verdict.Tradeable => "торгуемо",
                Verdict.SpreadTooWide => "спред шире фильтра",
                Verdict.CostsEatTheRisk => "издержки съедают риск",
                Verdict.EdgeRequirementUnrealistic => "нужно нереальное преимущество",
                _ => "?",
            };

            sb.AppendFormat("  {0,-6} {1,9:F5} {2,9:F5} {3,9:P1} {4,9:P1} {5,9:F2}R   {6}",
                l.Timeframe, l.Atr, l.Spread, l.SpreadToAtr, l.CostR, l.RequiredEdgeR, verdict).AppendLine();
        }

        sb.AppendLine("  ---");
        sb.AppendFormat("  При этом спреде сделка окупается начиная с ATR {0:F5}.", neededAtr).AppendLine();
        sb.Append("=============================================");
        return sb.ToString();
    }
}
