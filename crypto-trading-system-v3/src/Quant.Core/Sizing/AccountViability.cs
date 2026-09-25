using System;
using Quant.Core.Config;
using Quant.Core.Primitives;

namespace Quant.Core.Sizing;

/// <summary>
/// Хватает ли денег на счёте, чтобы самая маленькая позиция, которую допускает брокер,
/// уложилась в допустимый риск.
///
/// Брокер не продаёт позицию меньше минимального объёма, а сайзер никогда не округляет
/// ВВЕРХ — иначе округление нарушало бы лимит риска. Значит, на маленьком счёте каждый
/// кандидат будет отвергнут с причиной «размер ниже минимума», сколько бы сигналов ни
/// было. Снаружи это выглядит как «бот не торгует», хотя это чистая арифметика: на 50
/// евро минимальная позиция BTCUSD при стопе 2.4 ATR рискует почти пятой частью счёта.
///
/// Ничего не запрещает и ничего не меняет — считает и говорит.
/// </summary>
public static class AccountViability
{
    public enum Verdict
    {
        /// <summary>Минимальная позиция укладывается даже в осторожный начальный риск.</summary>
        Viable,

        /// <summary>
        /// Укладывается в полный риск на сделку, но не в осторожный начальный: первые сделки
        /// пойдут минимальным объёмом брокера — крупнее, чем хотелось бы, но в пределах лимита.
        /// </summary>
        ViableOnlyAtFullRisk,

        /// <summary>Минимальная позиция рискует больше, чем допустимо вообще.</summary>
        AccountTooSmall,
    }

    public readonly struct Result
    {
        public Result(string symbol, double minimumRiskMoney, double minimumRiskPercent,
            double equityForFullRisk, double equityForFirstTrades, Verdict verdict)
        {
            Symbol = symbol;
            MinimumRiskMoney = minimumRiskMoney;
            MinimumRiskPercent = minimumRiskPercent;
            EquityForFullRisk = equityForFullRisk;
            EquityForFirstTrades = equityForFirstTrades;
            Outcome = verdict;
        }

        public string Symbol { get; }

        /// <summary>Сколько денег счёта рискует минимальная позиция брокера при типичном стопе.</summary>
        public double MinimumRiskMoney { get; }

        /// <summary>То же в процентах от текущего капитала.</summary>
        public double MinimumRiskPercent { get; }

        /// <summary>Капитал, при котором минимальная позиция укладывается в полный риск на сделку.</summary>
        public double EquityForFullRisk { get; }

        /// <summary>Капитал, при котором она укладывается в риск первых, осторожных сделок.</summary>
        public double EquityForFirstTrades { get; }

        public Verdict Outcome { get; }
        public bool IsViable => Outcome == Verdict.Viable;
    }

    /// <param name="atr">ATR сигнального таймфрейма в единицах цены — стоп считается от него.</param>
    public static Result Evaluate(string symbol, SymbolSpec spec, double atr, double equity, EngineConfig config)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));
        if (spec == null || atr <= 0 || equity <= 0)
        {
            return new Result(symbol, double.MaxValue, double.MaxValue, double.MaxValue, double.MaxValue, Verdict.AccountTooSmall);
        }

        double stop = atr * config.Exit.AtrStopMultiple;
        double minimumRisk = spec.MoneyFor(stop, spec.VolumeInUnitsMin);

        double fullRisk = Math.Min(config.Sizing.RiskPerTradePercent, config.Risk.HardMaxRiskPerTradePercent) / 100.0;

        // Первые сделки идут с ограниченным доверием: реальных сделок ещё нет, и размер
        // урезан сверху множителем холодного старта.
        double firstTradesRisk = fullRisk * Math.Min(1.0, Math.Max(0.0, config.Sizing.ColdStartRiskMultiplier));

        double equityForFull = fullRisk > 0 ? minimumRisk / fullRisk : double.MaxValue;
        double equityForFirst = firstTradesRisk > 0 ? minimumRisk / firstTradesRisk : double.MaxValue;

        // Минимальная позиция, укладывающаяся в полный риск на сделку, будет открыта и в
        // начале: сайзер округляет до минимума брокера, если это не нарушает лимитов.
        // Первые сделки при этом крупнее осторожного размера — об этом и сообщается.
        Verdict verdict =
            equity >= equityForFirst ? Verdict.Viable
            : equity >= equityForFull ? Verdict.ViableOnlyAtFullRisk
            : Verdict.AccountTooSmall;

        return new Result(symbol, minimumRisk, minimumRisk / equity * 100.0, equityForFull, equityForFirst, verdict);
    }

    public static string Describe(Result r, string currency)
    {
        switch (r.Outcome)
        {
            case Verdict.Viable:
                return $"{r.Symbol}: счёта хватает. Минимальная позиция рискует {r.MinimumRiskMoney:F2} {currency} " +
                       $"({r.MinimumRiskPercent:F2}% капитала).";
            case Verdict.ViableOnlyAtFullRisk:
                return $"{r.Symbol}: торговать можно, но впритык. Минимальная позиция брокера рискует {r.MinimumRiskMoney:F2} {currency} " +
                       $"({r.MinimumRiskPercent:F2}% капитала) — первые сделки будут крупнее осторожного начального размера, " +
                       $"хотя и в пределах вашего риска на сделку. Спокойнее — от {r.EquityForFirstTrades:F0} {currency}.";
            default:
                return $"{r.Symbol}: СЧЁТ СЛИШКОМ МАЛ — НИ ОДНОЙ СДЕЛКИ НЕ БУДЕТ. Минимальная позиция брокера рискует " +
                       $"{r.MinimumRiskMoney:F2} {currency}, это {r.MinimumRiskPercent:F1}% капитала. " +
                       $"Нужно минимум {r.EquityForFullRisk:F0} {currency}, для первых сделок — {r.EquityForFirstTrades:F0} {currency}.";
        }
    }
}
