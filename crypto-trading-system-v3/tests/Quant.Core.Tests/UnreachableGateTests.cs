using System;
using Quant.Core.Config;
using Quant.Core.Exits;
using Quant.Core.Primitives;
using Quant.Core.Risk;
using Quant.Core.Stats;
using Xunit;

namespace Quant.Core.Tests;

/// <summary>
/// Ворота, не пропускающие ничего и никогда, — худший класс дефекта в этой системе.
///
/// Каждый слой при этом работает правильно, тесты зелёные, журнал полон осмысленных
/// отказов, и всё вместе не совершает ни одной сделки. Снаружи это неотличимо от
/// «система осторожна», и именно так оно и выглядело.
/// </summary>
public class UnreachableGateTests
{
    [Fact]
    public void TheRewardToRiskOfThePlanCountsTheWholePlanNotTheFirstTarget()
    {
        // Позиция закрывается по частям: доля на первой цели, доля на второй, остаток
        // ведётся трейлингом. Мерить план одной первой целью значит систематически
        // занижать его и не замечать вторую половину собственной конструкции.
        var plan = new ExitPlan
        {
            IsValid = true,
            StopPrice = 49500,
            StopDistance = 500,
            Target1R = 1.2,
            Target2R = 2.2,
            Target1ClosePercent = 0.40,
            Target2ClosePercent = 0.35,
        };

        // 0.40 на 1.2R, остальные 0.60 — на 2.2R.
        Assert.Equal((0.40 * 1.2) + (0.60 * 2.2), plan.RewardToRisk, 6);
        Assert.Equal(1.2, plan.RewardToRiskAtFirstTarget, 6);

        Assert.True(plan.RewardToRisk > plan.RewardToRiskAtFirstTarget);
    }

    [Fact]
    public void TheDefaultPlanCanActuallyPassTheDefaultMinimum()
    {
        // Именно это сочетание и было сломано: первая цель 1.2R против минимума 1.3.
        // Каждое значение по отдельности разумно, противоречие возникает только вместе —
        // поэтому и не было замечено.
        var config = new EngineConfig();

        double runner = 1.0 - config.Exit.Target1ClosePercent - config.Exit.Target2ClosePercent;
        double achievable = (config.Exit.Target1ClosePercent * config.Exit.Target1R) +
                            ((config.Exit.Target2ClosePercent + runner) * config.Exit.Target2R);

        Assert.True(achievable >= config.Ev.MinRewardToRisk,
            $"план даёт {achievable:F2}R при минимуме {config.Ev.MinRewardToRisk:F2}R — ни один кандидат не пройдёт");
    }

    [Fact]
    public void ConfigurationValidationCatchesAnUnreachableRewardGate()
    {
        // Проверка межсекционная, и именно поэтому её не было: обе величины по отдельности
        // проходят собственную валидацию.
        var config = new EngineConfig();
        config.Exit.Target1R = 0.5;
        config.Exit.Target2R = 0.8;
        config.Ev.MinRewardToRisk = 3.0;

        string problems = string.Join(" | ", config.Validate());
        Assert.Contains("ни один кандидат не пройдёт этот фильтр никогда", problems);
    }

    [Fact]
    public void ATransientAnomalyDoesNotPinTheRiskPostureForever()
    {
        // Аномалия рынка — не потеря денег. Требовать от неё денежного восстановления
        // значило бы запереть систему: разовый всплеск волатильности навсегда урезал бы
        // аппетит к риску, потому что условие выхода не имеет к причине входа отношения,
        // а в системе, которая торгует редко, капитал может не двигаться неделями.
        var config = new EngineConfig();
        var risk = new RiskEngine(config.Risk, config.Sizing, new DrawdownTracker(),
            new ExecutionQualityTracker(config.Execution), new PerformanceStore(config.Adaptation),
            new RiskOfRuinEstimator(seed: 5));

        var account = new AccountSnapshot(10000, 10000, 0, 10000, 2000, 50, false, "USD");

        // Сильная аномалия поднимает постуру.
        RiskAssessment stressed = risk.Evaluate(RiskFixtures.T0, account, anomalySeverity: 0.95, inRecoveryPeriod: false);
        Assert.True(stressed.State >= RiskState.Defensive, $"аномалия обязана поднять постуру, а не {stressed.State}");

        // Аномалия прошла, капитал не изменился — сделок не было.
        DateTime later = RiskFixtures.T0.AddMinutes(config.Risk.MinMinutesInRiskState + 60);
        RiskAssessment calm = risk.Evaluate(later, account, anomalySeverity: 0.0, inRecoveryPeriod: false);

        Assert.True(calm.State < stressed.State,
            $"постура осталась {calm.State}: выход требует роста капитала, которого без сделок не будет — " +
            string.Join("; ", calm.Reasons));
    }

    [Fact]
    public void ALossDrivenPostureStillRequiresEquityToRecover()
    {
        // Обратная сторона: если постуру подняли ПОТЕРИ, ослаблять её до возврата денег
        // нельзя — иначе защита снимается на самом дне.
        var config = new EngineConfig();
        var risk = new RiskEngine(config.Risk, config.Sizing, new DrawdownTracker(),
            new ExecutionQualityTracker(config.Execution), new PerformanceStore(config.Adaptation),
            new RiskOfRuinEstimator(seed: 6));

        // Границы периодов — полночь: именно так их хранит и восстанавливает система.
        risk.Restore(RiskState.Normal, 0, 0, null,
            RiskFixtures.T0.Date, dayStartEquity: 10000,
            RiskFixtures.T0.Date, weekStartEquity: 10000, RiskFixtures.T0);

        // Дневной убыток поднимает постуру.
        var losing = new AccountSnapshot(8600, 8600, 0, 8600, 2000, 50, false, "USD");
        RiskAssessment hit = risk.Evaluate(RiskFixtures.T0.AddMinutes(10), losing, 0, false);
        Assert.True(hit.State >= RiskState.Defensive,
            $"убыток 14% при лимите {config.Risk.DailyLossLimitPercent}% обязан поднять постуру, а не {hit.State}: " +
            string.Join("; ", hit.Reasons));

        // Время прошло, но деньги не вернулись — постура держится.
        //
        // Держится потому, что САМ УБЫТОК никуда не делся: причина продолжает действовать.
        // Это и есть нужное свойство — одного лишь истечения времени недостаточно.
        DateTime later = RiskFixtures.T0.AddMinutes(config.Risk.MinMinutesInRiskState + 120);
        RiskAssessment still = risk.Evaluate(later, losing, 0, false);

        Assert.Equal(hit.State, still.State);
        Assert.Contains(still.Reasons, r => r.Contains("daily loss"));

    }
}
