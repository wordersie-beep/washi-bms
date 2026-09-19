using System;
using System.Collections.Generic;
using System.Text;
using Quant.Core.Adaptation;
using Quant.Core.Portfolio;
using Quant.Core.Primitives;
using Quant.Core.Probability;
using Quant.Core.Regime;
using Quant.Core.Risk;
using Quant.Core.Stats;

namespace Quant.Core.Journal;

/// <summary>
/// Текстовый дашборд состояния системы (раздел 113).
///
/// Только текст — никакого Chart, никакой графики. Это не эстетический выбор: cTrader Cloud
/// исполняет робота на Linux без GUI, и любая попытка рисовать там в лучшем случае ничего не
/// сделает, а в худшем уронит робота. Один и тот же вывод читается в логе облака, в
/// бэктесте и на локальном терминале.
///
/// Главное число здесь — «теплота портфеля»: сколько капитала реально находится под риском
/// по всем текущим стопам. Оно отвечает на единственный вопрос, который имеет значение в
/// плохой день — сколько я потеряю, если сработает всё сразу.
/// </summary>
public sealed class Dashboard
{
    public string Render(
        DateTime nowUtc,
        OperatingMode mode,
        AccountSnapshot account,
        RiskAssessment risk,
        PortfolioExposure exposure,
        IReadOnlyList<OpenPosition> positions,
        IReadOnlyDictionary<string, RegimeAssessment> regimes,
        IReadOnlyDictionary<string, StrategyState> strategies,
        PerformanceStore performance,
        CalibrationTracker calibration,
        ExecutionQualityTracker execution,
        DecisionJournal journal)
    {
        var sb = new StringBuilder();

        sb.AppendLine("================================================================");
        sb.AppendFormat("  QuantCryptoV3   {0:yyyy-MM-dd HH:mm:ss}Z   режим: {1}", nowUtc, mode).AppendLine();
        sb.AppendLine("================================================================");

        // --- Счёт и риск -----------------------------------------------------------------
        sb.AppendFormat("  Капитал        {0,12:F2} {1}   баланс {2:F2}", account.Equity, account.Currency, account.Balance).AppendLine();
        sb.AppendFormat("  Посτура риска  {0,12}   множитель {1:F2}", risk.State, risk.RiskMultiplier).AppendLine();
        sb.AppendFormat("  Просадка       24ч {0,5:F2}%  7д {1,5:F2}%  30д {2,5:F2}%  всего {3,5:F2}%",
            risk.Drawdown24hPercent, risk.Drawdown7dPercent, risk.Drawdown30dPercent, risk.DrawdownAllTimePercent).AppendLine();
        sb.AppendFormat("  Убыток         день {0,5:F2}%   неделя {1,5:F2}%   серия убытков {2}",
            risk.DailyLossPercent, risk.WeeklyLossPercent, risk.ConsecutiveLosses).AppendLine();
        sb.AppendFormat("  Риск разорения {0,12:P2}   качество исполнения {1:P0}",
            risk.RuinProbability, risk.ExecutionQuality).AppendLine();

        if (!risk.AllowsNewPositions)
        {
            sb.AppendFormat("  >>> НОВЫЕ ПОЗИЦИИ ЗАБЛОКИРОВАНЫ: {0} — {1}", risk.BlockingReason, risk.ReasonSummary).AppendLine();
        }

        // --- Портфель ---------------------------------------------------------------------
        sb.AppendLine("----------------------------------------------------------------");
        sb.AppendFormat("  ТЕПЛОТА ПОРТФЕЛЯ {0:F2}%   позиций {1}   направленный риск {2:F2}% (с учётом корреляции {3:F2}%)",
            exposure.TotalOpenRiskPercent, exposure.OpenPositionCount,
            exposure.NetDirectionalRiskPercent, exposure.CorrelationAdjustedDirectionalRiskPercent).AppendLine();

        if (positions != null && positions.Count > 0)
        {
            for (int i = 0; i < positions.Count; i++)
            {
                OpenPosition p = positions[i];
                if (p == null) continue;
                sb.AppendFormat("    {0,-10} {1,-5} {2,-18} вход {3,10:F2}  стоп {4,10:F2}  открыто {5,4:P0}  MFE {6,5:F2}R  MAE {7,5:F2}R",
                    p.SymbolName, p.Direction, p.StrategyName, p.EntryPrice, p.CurrentStopPrice,
                    p.RemainingFraction, p.MaxFavourableExcursionR, p.MaxAdverseExcursionR).AppendLine();
            }
        }

        // --- Режимы ------------------------------------------------------------------------
        if (regimes != null && regimes.Count > 0)
        {
            sb.AppendLine("----------------------------------------------------------------");
            foreach (KeyValuePair<string, RegimeAssessment> kv in regimes)
            {
                sb.AppendFormat("    {0,-10} {1}", kv.Key, kv.Value).AppendLine();
            }
        }

        // --- Стратегии ----------------------------------------------------------------------
        if (strategies != null && strategies.Count > 0)
        {
            sb.AppendLine("----------------------------------------------------------------");
            sb.AppendLine("  Стратегии:");
            foreach (KeyValuePair<string, StrategyState> kv in strategies)
            {
                SegmentStats stats = performance?.Get(PerformanceStore.StrategyKey(kv.Key));
                sb.AppendFormat("    {0,-20} {1,-11} вес {2:F3}   {3}",
                    kv.Key, kv.Value.Status, kv.Value.Weight,
                    stats != null && stats.TotalTrades > 0 ? stats.ToString() : "сделок нет").AppendLine();

                if (kv.Value.Status != StrategyStatus.Active)
                {
                    sb.AppendFormat("      причина: {0}", kv.Value.LastStatusReason).AppendLine();
                }
            }
        }

        // --- Результативность ----------------------------------------------------------------
        if (performance != null && performance.TotalTrades > 0)
        {
            SegmentStats all = performance.Overall;
            sb.AppendLine("----------------------------------------------------------------");
            sb.AppendFormat("  Сделок {0}   винрейт {1:P1}   матожидание {2:F3}R   недавнее {3:F3}R (эфф. выборка {4:F0})",
                all.TotalTrades, all.WinRate, all.ExpectancyR, all.RecentExpectancyR, all.RecentEffectiveSample).AppendLine();
            sb.AppendFormat("  PF {0:F2}   payoff {1:F2}   макс. просадка {2:F2}R   серии: побед {3}, убытков {4}",
                all.ProfitFactor, all.PayoffRatio, all.MaxDrawdownR, all.LongestWinStreak, all.LongestLossStreak).AppendLine();
            sb.AppendFormat("  Средний MAE {0:F2}R   средний MFE {1:F2}R   захват движения {2:P0}   удержание {3:F0} мин",
                all.AverageMaeR, all.AverageMfeR, all.AverageCaptureRatio, all.AverageDurationMinutes).AppendLine();

            if (all.ExpectancyDeteriorating)
            {
                sb.AppendFormat("  >>> CUSUM: обнаружено устойчивое падение матожидания (сила {0:F1})", all.DeteriorationSeverity).AppendLine();
            }
        }

        // --- Калибровка и исполнение ------------------------------------------------------------
        if (calibration != null && calibration.Observations > 0)
        {
            sb.AppendLine("----------------------------------------------------------------");
            sb.AppendFormat("  Калибровка: n={0}  ECE={1:P1}  Brier={2:F4}  качество {3:P0}",
                calibration.Observations, calibration.ExpectedCalibrationError(), calibration.BrierScore, calibration.Quality()).AppendLine();
        }

        if (execution != null && execution.Observations > 0)
        {
            sb.AppendFormat("  Исполнение: {0}", execution).AppendLine();
        }

        // --- Решения -----------------------------------------------------------------------------
        if (journal != null)
        {
            sb.AppendLine("----------------------------------------------------------------");
            sb.AppendLine(journal.RejectionSummary());
        }

        sb.Append("================================================================");
        return sb.ToString();
    }
}
