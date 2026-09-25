using System;
using System.Collections.Generic;
using cAlgo.Robots;

namespace Harness
{
    internal static class Program
    {
        private static int _pass;
        private static int _fail;

        private static void Check(bool ok, string name)
        {
            if (ok) { _pass++; }
            else { _fail++; Console.WriteLine("FAIL: " + name); }
        }

        private static void Near(double a, double b, double tol, string name)
        {
            bool ok = Math.Abs(a - b) <= tol;
            if (!ok) Console.WriteLine("  got " + a.ToString("R") + " expected " + b.ToString("R") + " (tol " + tol + ")");
            Check(ok, name);
        }

        private static int Main()
        {
            NaiveBayesTests();
            OutcomeTests();
            TickEngineTests();
            CalibratorTests();
            VolumeTests();
            SqueezeTests();
            SweepTests();
            FeatureTests();
            PipelineSmokeTest();
            Console.WriteLine("passed " + _pass + ", failed " + _fail);
            return _fail == 0 ? 0 : 1;
        }

        private static double Gauss(Random r)
        {
            double u1 = 1.0 - r.NextDouble();
            double u2 = r.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        private static void NaiveBayesTests()
        {
            var r = new Random(1);
            var nb = new GaussianNaiveBayes(4, 4000);
            Check(!nb.IsReady(10), "empty model not ready");
            Check(double.IsNaN(nb.Predict(new double[4], false, null)), "empty model predicts NaN");
            for (int i = 0; i < 4000; i++)
            {
                bool win = r.NextDouble() < 0.3;
                var x = new[] { (win ? 1.0 : -1.0) + Gauss(r), Gauss(r), Gauss(r) * 5, Gauss(r) };
                nb.Add(x, win);
            }
            Check(nb.Count == 4000, "count");
            Check(nb.IsReady(100), "ready");
            Near(nb.WinRate, 0.3, 0.03, "win rate ~0.3");
            double pHigh = nb.Predict(new[] { 1.5, 0, 0, 0 }, false, null);
            double pLow = nb.Predict(new[] { -1.5, 0, 0, 0 }, false, null);
            double pMid = nb.Predict(new[] { 0.0, 0, 0, 0 }, false, null);
            Check(pHigh > 0.8, "win-like vector -> high confidence (" + pHigh + ")");
            Check(pLow < 0.2, "loss-like vector -> low confidence (" + pLow + ")");
            Near(pMid, 0.5, 0.1, "neutral vector ~0.5 with balanced prior");
            double pMidEmp = nb.Predict(new[] { 0.0, 0, 0, 0 }, true, null);
            Check(pMidEmp < pMid - 0.1, "empirical prior pulls toward base rate (" + pMidEmp + ")");

            // Analytic check: equal-variance Gaussians with means +1/-1, sd 1 -> log-odds of feature 0 = 2x.
            var contrib = new double[4];
            double p = nb.Predict(new[] { 0.5, 0, 0, 0 }, false, contrib);
            Near(contrib[0], 1.0, 0.15, "feature 0 LLR ~ 2*x at x=0.5");
            double sum = 0; foreach (double c in contrib) sum += c;
            Near(Math.Log(p / (1 - p)), sum, 1e-9, "contributions sum to the log-odds");

            double pExtreme = nb.Predict(new[] { 1e9, -1e9, 1e9, double.MaxValue / 10 }, false, null);
            Check(!double.IsNaN(pExtreme) && pExtreme >= 0 && pExtreme <= 1, "extreme input stays a probability");
            Check(!nb.Add(new[] { double.NaN, 0, 0, 0 }, true), "NaN sample rejected");
            Check(!nb.Add(new[] { 1.0, 2.0 }, true), "wrong dimension rejected");

            // Rolling window: flip the regime and the model follows once the window rolls over.
            var roll = new GaussianNaiveBayes(4, 1000);
            for (int i = 0; i < 1000; i++) { bool w = i % 2 == 0; roll.Add(new[] { (w ? 1.0 : -1.0) + Gauss(r), Gauss(r), Gauss(r), Gauss(r) }, w); }
            Check(roll.Predict(new[] { 1.5, 0, 0, 0 }, false, null) > 0.8, "regime A learned");
            for (int i = 0; i < 1000; i++) { bool w = i % 2 == 0; roll.Add(new[] { (w ? -1.0 : 1.0) + Gauss(r), Gauss(r), Gauss(r), Gauss(r) }, w); }
            Check(roll.Count == 1000, "window capped at capacity");
            Check(roll.Predict(new[] { 1.5, 0, 0, 0 }, false, null) < 0.2, "regime B replaced regime A");

            // Incremental statistics equal a model trained from scratch on the same window.
            var inc = new GaussianNaiveBayes(4, 500);
            var samples = new List<double[]>();
            var labels = new List<bool>();
            for (int i = 0; i < 1733; i++)
            {
                bool w = r.NextDouble() < 0.4;
                var x = new[] { Gauss(r) + (w ? 0.5 : 0), Gauss(r) * 3 + 100, Gauss(r), Gauss(r) - 3 };
                samples.Add(x); labels.Add(w); inc.Add(x, w);
            }
            var fresh = new GaussianNaiveBayes(4, 500);
            for (int i = samples.Count - 500; i < samples.Count; i++) fresh.Add(samples[i], labels[i]);
            var probe = new[] { 0.3, 101.0, -0.2, -2.5 };
            Near(inc.Predict(probe, true, null), fresh.Predict(probe, true, null), 1e-9, "rolling == fresh on same window");
            Check(inc.Wins == fresh.Wins && inc.Losses == fresh.Losses, "rolling class counts");
        }

        private static void OutcomeTests()
        {
            const double spread = 0.00002, sl = 0.00016, tp = 0.00020, beTrig = 0.4 * 0.00016, beOff = 0.00001;
            // Long: entry ask 1.10002, SL 1.09986, TP 1.10022, BE trigger 1.100084, BE stop 1.10003.
            Check(!OutcomeSimulator.Tp1First(true, 1.10000, spread, sl, tp, beTrig, beOff,
                new[] { 1.10010, 1.10025 }, new[] { 1.09995, 1.10000 }, 2), "long: BE armed on bar 1, stopped at BE on bar 2");
            Check(OutcomeSimulator.Tp1First(true, 1.10000, spread, sl, tp, 0, beOff,
                new[] { 1.10010, 1.10025 }, new[] { 1.09995, 1.10000 }, 2), "long: same path without BE reaches TP1");
            Check(!OutcomeSimulator.Tp1First(true, 1.10000, spread, sl, tp, beTrig, beOff,
                new[] { 1.10005 }, new[] { 1.09980 }, 1), "long: stop first");
            Check(!OutcomeSimulator.Tp1First(true, 1.10000, spread, sl, tp, beTrig, beOff,
                new[] { 1.10030 }, new[] { 1.09980 }, 1), "long: stop and TP in one bar -> pessimistic loss");
            Check(OutcomeSimulator.Tp1First(true, 1.10000, spread, sl, tp, beTrig, beOff,
                new[] { 1.10022 }, new[] { 1.09999 }, 1), "long: TP1 touched exactly");
            Check(!OutcomeSimulator.Tp1First(true, 1.10000, spread, sl, tp, beTrig, beOff,
                new[] { 1.10005, 1.10006, 1.10004 }, new[] { 1.09995, 1.09996, 1.09994 }, 3), "long: time-out is not a win");
            Check(!OutcomeSimulator.Tp1First(true, 1.10000, spread, sl, tp, beTrig, beOff,
                new[] { 1.10030 }, new[] { 1.09999 }, 0), "long: zero horizon is not a win");

            // Short: entry bid 1.10000, SL ask 1.10016, TP ask 1.09980 -> bid low <= 1.09978.
            Check(OutcomeSimulator.Tp1First(false, 1.10000, spread, sl, tp, beTrig, beOff,
                new[] { 1.10005 }, new[] { 1.09978 }, 1), "short: TP1 on ask");
            Check(!OutcomeSimulator.Tp1First(false, 1.10000, spread, sl, tp, beTrig, beOff,
                new[] { 1.10005 }, new[] { 1.09979 }, 1), "short: bid 1 point short of TP1 on ask");
            Check(!OutcomeSimulator.Tp1First(false, 1.10000, spread, sl, tp, beTrig, beOff,
                new[] { 1.10014 }, new[] { 1.09990 }, 1), "short: stop hit on ask (bid high + spread)");
            Check(!OutcomeSimulator.Tp1First(false, 1.10000, spread, sl, tp, beTrig, beOff,
                new[] { 1.09995, 1.10000 }, new[] { 1.09990, 1.09970 }, 2), "short: BE armed then stopped at BE (1.09999)");
        }

        private static void TickEngineTests()
        {
            var t0 = new DateTime(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc);
            var e = new TickVelocityEngine(5.0);
            for (int i = 0; i < 100; i++) e.AddTick(t0.AddTicks(i * TimeSpan.TicksPerSecond / 10));
            DateTime now = t0.AddTicks(99 * TimeSpan.TicksPerSecond / 10);
            Check(e.CountSince(now, 3.0) == 30, "30 ticks in the last 3 s (" + e.CountSince(now, 3.0) + ")");
            Check(e.CountSince(now, 0.05) == 1, "only the last tick in 50 ms");
            Check(e.CountSince(now.AddSeconds(10), 3.0) == 0, "nothing in a later window");
            Check(e.Count <= 51 && e.Count >= 50, "retention keeps ~5 s (" + e.Count + ")");
            e.AddTick(t0);   // clock stepped back: stored as the latest time
            Check(e.CountSince(now, 0.05) == 2, "backward timestamp kept monotonic");

            var big = new TickVelocityEngine(1.0);
            for (int i = 0; i < 20000; i++) big.AddTick(t0.AddTicks(i * TimeSpan.TicksPerMillisecond));
            Check(big.Count <= 1001 && big.Count >= 1000, "compaction keeps the window (" + big.Count + ")");
            Check(big.CountSince(t0.AddTicks(19999 * TimeSpan.TicksPerMillisecond), 0.5) == 500, "count after compaction");
        }

        private static void CalibratorTests()
        {
            var c = new TickCalibrator(4);
            Near(c.Factor(3), 1.0, 0, "uncalibrated factor is 1");
            c.AddBar(90, 100); c.AddBar(90, 100);
            Near(c.Factor(3), 1.0, 0, "below min bars stays 1");
            c.AddBar(90, 100);
            Near(c.Factor(3), 0.9, 1e-12, "factor 0.9");
            c.AddBar(0, 100);
            Check(c.Bars == 3, "zero-observation bar ignored");
            for (int i = 0; i < 10; i++) c.AddBar(200, 100);
            Check(c.Bars == 4, "window evicts old bars");
            Near(c.Factor(3), 2.0, 1e-12, "factor follows recent bars");
        }

        private static void VolumeTests()
        {
            SplitPlan p = VolumeMath.PlanSplit(1000, 60, 1000, 1000);
            Check(!p.Splittable, "0.01 lot cannot be split");
            p = VolumeMath.PlanSplit(2000, 60, 1000, 1000);
            Check(p.Splittable && p.PartialVolume == 1000 && p.RemainingVolume == 1000, "0.02 lot -> 1000/1000");
            p = VolumeMath.PlanSplit(3000, 60, 1000, 1000);
            Check(p.Splittable && p.PartialVolume == 2000 && p.RemainingVolume == 1000, "0.03 lot -> 2000/1000");
            p = VolumeMath.PlanSplit(5000, 60, 1000, 1000);
            Check(p.Splittable && p.PartialVolume == 3000 && p.RemainingVolume == 2000, "0.05 lot -> 3000/2000");
            p = VolumeMath.PlanSplit(1000, 60, 100, 100);
            Check(p.Splittable && Math.Abs(p.PartialVolume - 600) < 1e-9 && Math.Abs(p.RemainingVolume - 400) < 1e-9, "fine step -> 600/400");
            p = VolumeMath.PlanSplit(4000, 100, 1000, 1000);
            Check(!p.Splittable, "100% is a full close, not a split");
            p = VolumeMath.PlanSplit(1500, 60, 1000, 100);
            Check(!p.Splittable, "1500 with min 1000 cannot leave two tradable parts");
            p = VolumeMath.PlanSplit(10000, 95, 1000, 1000);
            Check(p.Splittable && p.PartialVolume == 9000 && p.RemainingVolume == 1000, "95% keeps a min-size runner");
            Near(VolumeMath.FloorToStep(3600, 1000), 3000, 0, "floor 3600");
            Near(VolumeMath.FloorToStep(2999.9999999, 1000), 3000, 0, "floor absorbs float noise");
            Near(VolumeMath.CeilToStep(1000.0000001, 1000), 1000, 0, "ceil absorbs float noise");
            Near(VolumeMath.CeilToStep(1001, 1000), 2000, 0, "ceil 1001");
            Near(VolumeMath.RoundToStep(2500, 1000), 3000, 0, "midpoint rounds away from zero");
        }

        private static void SqueezeTests()
        {
            double[] h = { 1.10020, 1.10025, 1.10022, 1.10018, 1.10021, 1.10024 };
            double[] l = { 1.10000, 1.10004, 1.10001, 1.10002, 1.10003, 1.10005 };
            SqueezeCheck q = VolmanSetups.CheckSqueeze(h, l, 6, 1.10012, 0.00020, 1.5, 0.3);
            Check(q.Ok, "tight box around the EMA is a squeeze");
            Near(q.Height, 0.00025, 1e-12, "box height");
            Near(q.EmaGap, 0, 0, "EMA inside the box");
            q = VolmanSetups.CheckSqueeze(h, l, 6, 1.10012, 0.00015, 1.5, 0.3);
            Check(!q.Ok && !q.RangeOk, "box 2.5p > 1.5 x 1.5p ATR");
            q = VolmanSetups.CheckSqueeze(h, l, 6, 1.10040, 0.00020, 1.5, 0.3);
            Check(!q.Ok && q.RangeOk && !q.EmaOk, "EMA 1.5p above the box > 0.6p");
            Near(q.EmaGap, 0.00015, 1e-12, "EMA gap above");
            q = VolmanSetups.CheckSqueeze(h, l, 6, 1.09996, 0.00020, 1.5, 0.3);
            Check(q.Ok, "EMA 0.4p below the box within 0.6p");
            q = VolmanSetups.CheckSqueeze(h, l, 6, 1.10012, 0.0, 1.5, 0.3);
            Check(!q.Ok, "zero ATR never arms");
        }

        private static void SweepTests()
        {
            Check(SweepDetector.IsBullSweep(1.10000, 1.09995, 1.10010, 0.00002, true), "bull: wick through the low, close back above");
            Check(!SweepDetector.IsBullSweep(1.10000, 1.09995, 1.09997, 0.00002, true), "bull: close below the swept low is no sweep");
            Check(SweepDetector.IsBullSweep(1.10000, 1.09995, 1.09997, 0.00002, false), "bull: close requirement off");
            Check(!SweepDetector.IsBullSweep(1.10000, 1.09999, 1.10010, 0.00002, true), "bull: 0.1p pierce < 0.2p minimum");
            Check(SweepDetector.IsBullSweep(1.10000, 1.09998, 1.10010, 0.00002, true), "bull: pierce exactly at the minimum (float)");
            Check(!SweepDetector.IsBullSweep(1.10000, 1.10000, 1.10010, 0.0, true), "bull: equal low is not a pierce");
            Check(SweepDetector.IsBearSweep(1.10000, 1.10005, 1.09990, 0.00002, true), "bear: wick through the high, close back below");
            Check(!SweepDetector.IsBearSweep(1.10000, 1.10005, 1.10003, 0.00002, true), "bear: close above the swept high");
        }

        private static void FeatureTests()
        {
            double[] lx = AiFeatures.Build(2.0, 0.0001, 0.0002, 6.0, 0.1, 1);
            double[] sx = AiFeatures.Build(2.0, 0.0001, 0.0002, 6.0, 0.1, -1);
            Near(lx[0], Math.Log(2.0), 1e-12, "velocity is log-scaled");
            Near(lx[1], 0.5, 1e-12, "EMA distance in ATR");
            Near(sx[1], -0.5, 1e-12, "EMA distance mirrored for shorts");
            Near(sx[2], -6.0, 1e-12, "RSI slope mirrored for shorts");
            Check(lx[0] == sx[0] && lx[3] == sx[3], "non-directional features identical");
            double[] z = AiFeatures.Build(0.0, 0, 0.0002, 0.0, 0.1, 1);
            Check(!double.IsInfinity(z[0]) && !double.IsNaN(z[0]), "zero velocity stays finite");
        }

        // End-to-end on synthetic data: random walk with bursts of momentum. Momentum bars carry more ticks
        // and follow-through, so the classifier should learn to prefer high velocity + aligned RSI slope.
        private static void PipelineSmokeTest()
        {
            var r = new Random(7);
            int n = 6000;
            var close = new double[n]; var high = new double[n]; var low = new double[n]; var ticks = new double[n];
            double price = 1.10000, drift = 0;
            for (int i = 0; i < n; i++)
            {
                if (r.NextDouble() < 0.05) drift = (r.NextDouble() < 0.5 ? -1 : 1) * 0.00006;
                else if (r.NextDouble() < 0.10) drift = 0;
                double open = price;
                price += drift + Gauss(r) * 0.00010;
                close[i] = price;
                high[i] = Math.Max(open, price) + Math.Abs(Gauss(r)) * 0.00004;
                low[i] = Math.Min(open, price) - Math.Abs(Gauss(r)) * 0.00004;
                ticks[i] = 60 + Math.Abs(drift) * 1e6 * (1 + 0.3 * Gauss(r)) + Math.Abs(Gauss(r)) * 10;
            }
            // Simple indicators for the smoke test.
            var ema = new double[n]; var atr = new double[n]; var mom = new double[n];
            double k = 2.0 / 21;
            ema[0] = close[0]; atr[0] = high[0] - low[0];
            for (int i = 1; i < n; i++)
            {
                ema[i] = ema[i - 1] + k * (close[i] - ema[i - 1]);
                double tr = Math.Max(high[i] - low[i], Math.Max(Math.Abs(high[i] - close[i - 1]), Math.Abs(low[i] - close[i - 1])));
                atr[i] = atr[i - 1] + (tr - atr[i - 1]) / 14;
                mom[i] = i >= 3 ? (close[i] - close[i - 3]) / 0.00001 : 0;   // stands in for the RSI slope
            }
            var nb = new GaussianNaiveBayes(4, 2 * n);
            const int H = 5;
            var hb = new double[H]; var lb = new double[H];
            double spread = 0.00002;
            int wins = 0, total = 0;
            for (int i = 120; i < n - H; i++)
            {
                double baseline = 0; for (int j = i - 100; j < i; j++) baseline += ticks[j]; baseline /= 100;
                for (int j = 0; j < H; j++) { hb[j] = high[i + 1 + j]; lb[j] = low[i + 1 + j]; }
                double sl = 0.8 * atr[i], tp = 1.0 * atr[i];
                bool lw = OutcomeSimulator.Tp1First(true, close[i], spread, sl, tp, 0.4 * sl, 0.00001, hb, lb, H);
                bool sw = OutcomeSimulator.Tp1First(false, close[i], spread, sl, tp, 0.4 * sl, 0.00001, hb, lb, H);
                nb.Add(AiFeatures.Build(ticks[i] / baseline, close[i] - ema[i], atr[i], mom[i], spread / atr[i], 1), lw);
                nb.Add(AiFeatures.Build(ticks[i] / baseline, close[i] - ema[i], atr[i], mom[i], spread / atr[i], -1), sw);
                wins += (lw ? 1 : 0) + (sw ? 1 : 0); total += 2;
            }
            double baseRate = (double)wins / total;
            Console.WriteLine("  pipeline: samples " + nb.Count + ", base win-rate " + baseRate.ToString("F3")
                + ", class means TV " + nb.ClassMean(true, 0).ToString("F3") + "/" + nb.ClassMean(false, 0).ToString("F3")
                + " RSI " + nb.ClassMean(true, 2).ToString("F2") + "/" + nb.ClassMean(false, 2).ToString("F2"));
            Check(nb.IsReady(100), "pipeline model ready");
            Check(baseRate > 0.05 && baseRate < 0.95, "labels are not degenerate");
            double pWith = nb.Predict(new[] { Math.Log(2.5), 0.8, 15.0, Math.Log(0.08) }, false, null);
            double pAgainst = nb.Predict(new[] { Math.Log(2.5), -0.8, -15.0, Math.Log(0.08) }, false, null);
            Console.WriteLine("  pipeline: momentum-aligned " + pWith.ToString("F3") + " vs counter-momentum " + pAgainst.ToString("F3"));
            Check(pWith > pAgainst, "model prefers trading with momentum on momentum data");
            Check(pWith >= 0 && pWith <= 1 && pAgainst >= 0 && pAgainst <= 1, "probabilities in range");
        }
    }
}
