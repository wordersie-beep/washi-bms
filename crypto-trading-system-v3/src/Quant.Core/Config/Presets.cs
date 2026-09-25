using System;
using Quant.Core.Primitives;

namespace Quant.Core.Config;

/// <summary>
/// Согласованные наборы настроек.
///
/// Существуют потому, что тридцать два поля по отдельности разумны, а вместе легко
/// образуют сочетание, в котором не проходит ни один сигнал. Больше половины этой системы
/// написано на исправлении ровно таких сочетаний, и предлагать пользователю собрать их
/// самому — значит переложить на него работу, которую здесь уже делали неделю.
///
/// Живут в ядре, а не в роботе, ровно по одной причине: иначе их нельзя проверить
/// тестом. Набор, чью экономику никто не считал, — это не пресет, а совет.
/// </summary>
public static class Presets
{
    /// <summary>
    /// Крипта на малом таймфрейме.
    ///
    /// Стоп шире, чем принято, и это не про «дать сделке дышать». Издержки круга делятся
    /// на стоп: на дистанции 2.4 ATR тот же спред стоит на треть меньше доли риска, чем
    /// на 1.6 ATR. Это единственный рычаг, который удешевляет вход, ничего не обещая
    /// про прибыль.
    ///
    /// Порог спреда двигается вместе со стопом и по той же причине: он сравнивает спред
    /// с ATR, а платим мы долей от СТОПА. Оставить его на 0.15 при стопе 2.4 значило бы
    /// отвергать входы, которые стали дешевле.
    /// </summary>
    public static void CryptoSmallTimeframe(EngineConfig config, Tf signal)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));

        config.UseTimeframes(signal, Tf.H4);

        config.Exit.AtrStopMultiple = 2.4;
        config.Exit.Target1R = 1.1;
        config.Exit.Target2R = 2.4;
        config.Exit.BreakEvenTriggerR = 0.8;

        config.Risk.MaxSpreadToAtr = 0.20;

        config.Sizing.RiskPerTradePercent = 0.30;
        config.Risk.MaxTotalOpenRiskPercent = 1.5;
        config.Portfolio.MaxOpenPositions = 3;
    }
}
