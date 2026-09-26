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
            SpreadTests();
            CommissionTests();
            VolumeTests();
            SqueezeTests();
            SweepTests();
            FeatureTests();
            MarketTests();
            MarginTests();
            V3Tests();
            PipelineSmokeTest();
            Console.WriteLine("passed " + _pass + ", failed " + _fail);
            return _fail == 0 ? 0 : 1;
        }

        private static void MarketTests()
        {
            List<string> list = MarketMath.ParseSymbols("EURUSD, GBPUSD;usdjpy  EURUSD\tBTCUSD");
            Check(list.Count == 4 && list[0] == "EURUSD" && list[1] == "GBPUSD" && list[2] == "usdjpy" && list[3] == "BTCUSD",
                  "symbol list: separators, order kept, duplicate dropped (" + string.Join("|", list) + ")");
            Check(MarketMath.ParseSymbols("eurusd,EURUSD").Count == 1, "duplicates ignore case");
            Check(MarketMath.ParseSymbols("").Count == 0 && MarketMath.ParseSymbols(null).Count == 0 && MarketMath.ParseSymbols(" , ; ").Count == 0,
                  "empty lists");

            Check(MarketMath.SymbolKey("BTC/USD") == "BTCUSD", "BTC/USD -> BTCUSD");
            Check(MarketMath.SymbolKey("btcusd.") == "BTCUSD", "btcusd. -> BTCUSD");
            Check(MarketMath.SymbolKey("EUR-USD") == MarketMath.SymbolKey("EURUSD"), "EUR-USD matches EURUSD");
            Check(MarketMath.SymbolKey("") == "", "empty key");

            const double ratio = 0.5e-4;
            Check(MarketMath.IsFxLike(0.0001, 1.17, ratio), "EURUSD pip is Forex-like");
            Check(MarketMath.IsFxLike(0.01, 150.0, ratio), "USDJPY pip is Forex-like");
            Check(MarketMath.IsFxLike(0.0001, 0.66, ratio), "AUDUSD pip is Forex-like");
            Check(!MarketMath.IsFxLike(0.01, 110000.0, ratio), "BTCUSD 0.01 pip is not Forex-like");
            Check(!MarketMath.IsFxLike(1.0, 110000.0, ratio), "BTCUSD 1.0 pip is not Forex-like");
            Check(!MarketMath.IsFxLike(0.0001, 0.0, ratio), "no price -> not Forex-like");
            Near(MarketMath.BotPip(0.0001, 1.17, true, ratio), 0.0001, 1e-15, "EURUSD bot pip = broker pip");
            Near(MarketMath.BotPip(0.01, 110000.0, false, ratio), 5.5, 1e-9, "BTC bot pip = 0.5 bp of price ($5.5)");
            Near(MarketMath.BotPip(1.0, 110000.0, false, ratio), 5.5, 1e-9, "BTC bot pip independent of broker pip size");
            Near(MarketMath.BotPip(0.01, 4000.0, false, ratio), 0.2, 1e-12, "ETH bot pip = $0.20");
            Near(MarketMath.BotPip(10.0, 4000.0, false, ratio), 10.0, 1e-12, "bot pip never below the broker pip");
            Near(MarketMath.BotPip(0.01, 0.0, false, ratio), 0.01, 1e-15, "no price -> broker pip");
        }

        private static void MarginTests()
        {
            // EUR account, EURUSD at 1:30: margin = units / 30 EUR.
            Func<double, double> eurusd = v => v / 30.0;
            MarginFit f = MarketMath.FitMargin(400000, 1000, 1000, 49092, 49092, 0, 30, 90, true, eurusd, 20);
            Check(f.Rule == MarginRule.Fits && f.Units == 400000, "49k EUR: 4 lots EURUSD (13.3k margin) fits 30% of free margin");

            // Eight 4-lot signals in a row on a GBP-priced pair: the account never blocks more than 90% of equity.
            Func<double, double> gbp = v => v * 1.15 / 30.0;
            double equity = 49092, used = 0;
            int opened = 0;
            bool within = true;
            MarginRule last = MarginRule.Fits;
            for (int i = 0; i < 8; i++)
            {
                MarginFit g = MarketMath.FitMargin(400000, 1000, 1000, equity - used, equity, used, 30, 90, true, gbp, 20);
                last = g.Rule;
                if (g.Units <= 0)
                    continue;
                opened++;
                used += gbp(g.Units);
                within &= used <= equity * 0.90 + 1e-6 && g.Units <= 400000;
            }
            Check(within, "total margin stays <= 90% of equity (" + (used / equity * 100).ToString("F1") + "%)");
            Check(opened >= 5 && opened <= 8, "several positions still open under the cap (" + opened + ")");
            Check(last == MarginRule.TotalLimitReached || last == MarginRule.Insufficient || opened == 8, "later signals are refused, not over-sized");

            // 50 EUR micro account: the per-trade share (15 EUR) is below one 0.01 lot (33 EUR) -> min volume within the total limit.
            f = MarketMath.FitMargin(400000, 1000, 1000, 50, 50, 0, 30, 90, true, eurusd, 20);
            Check(f.Rule == MarginRule.MinimumWithinTotal && f.Units == 1000, "50 EUR: 4 lots requested -> 0.01 lot");
            f = MarketMath.FitMargin(400000, 1000, 1000, 50, 50, 0, 30, 90, false, eurusd, 20);
            Check(f.Rule == MarginRule.Insufficient && f.Units == 0, "min-lot override off -> no trade");
            f = MarketMath.FitMargin(400000, 1000, 1000, 50 - 1000 / 30.0, 50, 1000 / 30.0, 30, 90, true, eurusd, 20);
            Check(f.Rule == MarginRule.Insufficient && Math.Abs(f.MinNeed - 1000 / 30.0) < 1e-9, "50 EUR: a second 0.01 lot does not fit");
            f = MarketMath.FitMargin(400000, 1000, 1000, 100, 1000, 900, 30, 90, true, eurusd, 20);
            Check(f.Rule == MarginRule.TotalLimitReached && f.Units == 0, "90% of equity already blocked -> refused");

            // Crypto at 1:2: 4 BTC far exceed the share, the volume is cut to what 30% of free margin buys.
            Func<double, double> btc = v => v * 110000 * 0.86 / 2.0;
            f = MarketMath.FitMargin(4, 0.01, 0.01, 49092, 49092, 0, 30, 90, true, btc, 20);
            Check(f.Rule == MarginRule.ReducedToShare && f.Units > 0.3 && f.Units < 0.32 && btc(f.Units) <= 49092 * 0.30 + 1e-6,
                  "49k EUR: 4 BTC -> " + f.Units.ToString("F2") + " BTC within 30% of free margin");

            // Margin that is not proportional to volume: the loop steps down until it really fits.
            Func<double, double> fixedPart = v => 1000 + v / 30.0;
            f = MarketMath.FitMargin(400000, 1000, 1000, 10000 / 0.3, 1e9, 0, 30, 100, true, fixedPart, 20);
            Check(f.Rule == MarginRule.ReducedToShare && f.Units == 270000 && fixedPart(f.Units) <= 10000 + 1e-6,
                  "non-linear margin -> stepped down to " + f.Units);
        }

        private static void V3Tests()
        {
            // Alternative broker names inside one list item.
            List<string> items = MarketMath.ParseSymbols("BTCUSD, LINKUSD|LNKUSD ,ETHUSD");
            Check(items.Count == 3 && items[1] == "LINKUSD|LNKUSD", "'|' keeps alternatives in one item");
            List<string> alts = MarketMath.Alternatives("LINKUSD| LNKUSD |");
            Check(alts.Count == 2 && alts[0] == "LINKUSD" && alts[1] == "LNKUSD", "alternatives split and trimmed");
            Check(MarketMath.Alternatives("").Count == 0, "no alternatives in an empty item");

            // Timeframe lists.
            List<int> tf = MarketMath.ParseTimeFrameMinutes("m5, M15;h1 m5 Minute30 Hour4 d1 x7 m7 m0 Daily");
            Check(tf.Count == 7 && tf[0] == 5 && tf[1] == 15 && tf[2] == 60 && tf[3] == 30 && tf[4] == 240 && tf[5] == 1440 && tf[6] == 7,
                  "timeframe list parsed in order, duplicates and unknowns dropped (" + string.Join(",", tf) + ")");
            Check(MarketMath.ParseTimeFrameMinutes("m1").Count == 1 && MarketMath.ParseTimeFrameMinutes("Minute")[0] == 1, "m1 and Minute are one minute");
            Check(MarketMath.ParseTimeFrameMinutes("m11, h5, zz").Count == 0, "unsupported timeframes rejected");
            Check(MarketMath.ParseTimeFrameMinutes(null).Count == 0, "empty timeframe list");

            // Fastest timeframe whose spread / ATR fits.
            Check(MarketMath.FirstFitting(new[] { 0.40, 0.22, 0.15 }, 0.25) == 1, "BTC: m5 too costly, m15 fits");
            Check(MarketMath.FirstFitting(new[] { 0.10, 0.05 }, 0.12) == 0, "Razor EURUSD: m1 fits");
            Check(MarketMath.FirstFitting(new[] { 2.0, 1.2, double.PositiveInfinity }, 0.25) == -1, "ADA-like spread fits no timeframe");
            Check(MarketMath.FirstFitting(new[] { double.NaN, 0.2 }, 0.25) == 1, "unknown ATR never fits");

            // Tick baseline: 100 bars, but at most the last 2 hours and never fewer than 2 bars.
            Check(MarketMath.BaselineBars(100, 60, 7200, 2) == 100, "baseline m1: 100 bars as before");
            Check(MarketMath.BaselineBars(100, 300, 7200, 2) == 24, "baseline m5: 24 bars = 2 h");
            Check(MarketMath.BaselineBars(100, 900, 7200, 2) == 8, "baseline m15: 8 bars = 2 h");
            Check(MarketMath.BaselineBars(100, 1800, 7200, 2) == 4, "baseline m30: 4 bars = 2 h");
            Check(MarketMath.BaselineBars(100, 3600, 7200, 2) == 2, "baseline h1: 2 bars, not 100 (4 days)");
            Check(MarketMath.BaselineBars(100, 14400, 7200, 2) == 2, "baseline h4: never fewer than 2 bars");
            Check(MarketMath.BaselineBars(5, 60, 7200, 2) == 5, "baseline: a small setting is kept");
            Check(MarketMath.BaselineBars(100, 0, 7200, 2) == 100, "baseline: unknown timeframe keeps the setting");
            Check(MarketMath.BaselineBars(100, 1200, 7200, 2) == 6, "baseline m20: 6 bars = 2 h exactly");

            // Commission as a price distance: Pepperstone cTrader Razor, 3 USD per lot per side on EURUSD at 1.14 in EUR.
            double usdToEur = 1.0 / 1.14;
            double perUnitRoundTurn = 6.0 / 100000 * usdToEur;              // EUR per unit, both sides
            double pipValue = 0.0001 * usdToEur;                              // EUR per unit per pip
            double dist = MarketMath.CommissionDistance(perUnitRoundTurn, pipValue, 0.0001);
            Check(Math.Abs(dist - 0.00006) < 1e-12, "Razor 3 USD/lot/side = 0.6 pip round turn on EURUSD");
            Check(MarketMath.CommissionDistance(0, pipValue, 0.0001) == 0, "no commission (crypto) adds nothing");
            Check(MarketMath.CommissionDistance(perUnitRoundTurn, 0, 0.0001) == 0, "unknown pip value adds nothing");
            Check(MarketMath.CommissionDistance(perUnitRoundTurn, pipValue, 0) == 0, "unknown pip size adds nothing");
            double jpyPipValue = 0.01 / 148.0 * usdToEur;                     // USDJPY: EUR per unit per pip
            double jpyRoundTurn = 6.0 / 100000 * usdToEur;                    // 3 USD per lot per side, base USD
            Check(Math.Abs(MarketMath.CommissionDistance(jpyRoundTurn, jpyPipValue, 0.01) - 0.00888) < 1e-5, "Razor on USDJPY at 148 = 0.89 pip");

            // Mean true range, including the gap from the previous close.
            double[] h = { 10, 12, 11, 15 };
            double[] l = { 9, 10, 10, 13 };
            double[] c = { 9.5, 11, 10.5, 14 };
            // TR: bar1 max(2, |12-9.5|, |10-9.5|)=2.5; bar2 max(1, |11-11|, |10-11|)=1; bar3 max(2, |15-10.5|, |13-10.5|)=4.5
            Near(MarketMath.MeanTrueRange(h, l, c, 3, 3), (2.5 + 1 + 4.5) / 3, 1e-12, "true range uses previous close");
            Near(MarketMath.MeanTrueRange(h, l, c, 3, 10), (2.5 + 1 + 4.5) / 3, 1e-12, "true range with fewer bars than asked");
            Check(MarketMath.MeanTrueRange(h, l, c, 0, 5) == 0.0, "no true range without a previous bar");

            // Breaks: crypto maintenance is held, a Forex weekend is not.
            var close = new DateTime(2026, 9, 25, 20, 59, 0);
            Func<DateTime, bool> sixMinuteBreak = t => t < close || t >= close.AddMinutes(6);
            Func<DateTime, bool> weekend = t => t < close || t >= close.AddHours(48);
            Check(MarketMath.ReopensWithin(sixMinuteBreak, close, 15), "6-minute crypto break is short");
            Check(!MarketMath.ReopensWithin(weekend, close, 15), "weekend is a long break");
            Check(!MarketMath.ReopensWithin(sixMinuteBreak, close, 5), "a 6-minute break is long when only 5 are allowed");

            // Budget equity: a demo account behaves like the budget, a small account like itself.
            Near(MarketMath.BudgetEquity(49092, 50, 0), 50, 1e-9, "49k demo with a 50 budget = 50");
            Near(MarketMath.BudgetEquity(49092, 50, -7.5), 42.5, 1e-9, "the budget follows the bot's losses");
            Near(MarketMath.BudgetEquity(20, 50, 0), 20, 1e-9, "a 20 EUR account stays 20 under a 50 budget");
            Near(MarketMath.BudgetEquity(49092, 0, 123), 49092, 1e-9, "budget 0 = whole account");
            Check(MarketMath.BudgetEquity(10, 50, -80) == 0.0, "budget equity never negative");

            // Margin estimate: the largest usable figure wins.
            Near(MarketMath.MaxEstimate(double.NaN, 15.2, 0), 15.2, 1e-12, "NaN broker estimate ignored");
            Near(MarketMath.MaxEstimate(7.6, 15.2, 16.0), 16.0, 1e-12, "seen margin above the others wins");
            Check(MarketMath.MaxEstimate(0, -1, double.PositiveInfinity) == 0.0, "no usable estimate = 0");

            // 20 and 50 EUR budgets at EU retail leverage (1:30 Forex, 1:2 crypto).
            Func<double, double> eurusd = v => v / 30.0;                 // 0.01 lot = 1000 EUR -> 33.3 EUR
            Func<double, double> usdjpy = v => v * 0.855 / 30.0;         // 0.01 lot = 1000 USD -> 28.5 EUR
            Func<double, double> eth = v => v * 3500 * 0.855 / 2.0;      // 0.01 ETH -> 15.0 EUR
            Func<double, double> btc = v => v * 85000 * 0.855 / 2.0;     // 0.01 BTC -> 363 EUR
            MarginFit f = MarketMath.FitMargin(1000, 1000, 1000, 20, 20, 0, 30, 90, true, eurusd, 20);
            Check(f.Rule == MarginRule.Insufficient, "20 EUR: EURUSD 0.01 lot does not fit (33 EUR > 18 EUR)");
            f = MarketMath.FitMargin(1000, 1000, 1000, 20, 20, 0, 30, 90, true, usdjpy, 20);
            Check(f.Rule == MarginRule.Insufficient, "20 EUR: USDJPY 0.01 lot does not fit (28.5 EUR > 18 EUR)");
            f = MarketMath.FitMargin(0.01, 0.01, 0.01, 20, 20, 0, 30, 90, true, eth, 20);
            Check(f.Rule == MarginRule.MinimumWithinTotal && Math.Abs(f.Units - 0.01) < 1e-9, "20 EUR: ETH 0.01 trades (15 EUR <= 18 EUR)");
            f = MarketMath.FitMargin(0.01, 0.01, 0.01, 20, 20, 0, 30, 90, true, btc, 20);
            Check(f.Rule == MarginRule.Insufficient, "20 EUR: BTC 0.01 does not fit");
            f = MarketMath.FitMargin(1000, 1000, 1000, 50, 50, 0, 30, 90, true, eurusd, 20);
            Check(f.Rule == MarginRule.MinimumWithinTotal && f.Units == 1000, "50 EUR: EURUSD 0.01 lot trades");
            f = MarketMath.FitMargin(0.01, 0.01, 0.01, 50 - 1000 / 30.0, 50, 1000 / 30.0, 30, 90, true, eth, 20);
            Check(f.Rule == MarginRule.Insufficient, "50 EUR: ETH next to an open EURUSD 0.01 does not fit (15 > 11.7)");
            f = MarketMath.FitMargin(0.01, 0.01, 0.01, 50 - 15.0, 50, 15.0, 30, 90, true, eth, 20);
            Check(f.Rule == MarginRule.MinimumWithinTotal, "50 EUR: a second 0.01 ETH-sized crypto position fits (15 <= 30)");
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

            // A constant feature (every training sample saw a 0.0 spread) must be ignored, not divided by ~0.
            var flat = new GaussianNaiveBayes(4, 6000);
            for (int i = 0; i < 6000; i++)
            {
                bool w = r.NextDouble() < 0.27;
                flat.Add(new[] { Gauss(r) * 0.3 - 0.05, (w ? -0.03 : 0.01) + Gauss(r), (w ? -0.04 : 0.01) + Gauss(r) * 8, Math.Log(1e-3) }, w);
            }
            var cFlat = new double[4];
            double atTrain = flat.Predict(new[] { 0.1, 0.5, 3.0, Math.Log(1e-3) }, false, null);
            double offTrain = flat.Predict(new[] { 0.1, 0.5, 3.0, -2.08 }, false, cFlat);
            Near(offTrain, atTrain, 1e-12, "constant feature ignored when the live value differs");
            Check(offTrain > 0.2 && offTrain < 0.8, "no saturation to 0%/100% (" + offTrain + ")");
            Near(cFlat[3], 0.0, 0, "constant feature contributes nothing");

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

        private static void SpreadTests()
        {
            var t = new SpreadTracker(5);
            Check(double.IsNaN(t.Median()), "empty tracker has no median");
            t.Add(0.00002);
            Near(t.Median(), 0.00002, 1e-15, "single value");
            t.Add(0.00004);
            Near(t.Median(), 0.00003, 1e-15, "even count averages the middle pair");
            t.Add(0.00100);   // rollover spike
            Near(t.Median(), 0.00004, 1e-15, "a spike does not move the median much");
            t.Add(-1.0); t.Add(double.NaN);
            Check(t.Count == 3, "negative and NaN spreads ignored");
            for (int i = 0; i < 10; i++) t.Add(0.00001);
            Check(t.Count == 5, "window capped at capacity");
            Near(t.Median(), 0.00001, 1e-15, "old values leave the window");
        }

        private static void CommissionTests()
        {
            const double eurusd = 1.08, usdToEur = 1.0 / 1.08, pipValueEur = 0.0001 * usdToEur;
            double rt = CommissionMath.RoundTurnPerUnit(CommissionBasis.UsdPerMillionUsd, 30, eurusd, 100000, usdToEur, true, false, false);
            Near(rt, 2 * 30 * eurusd / 1e6 * usdToEur, 1e-15, "EURUSD, EUR account: 30 USD per million per side");
            Near(rt / pipValueEur, 0.648, 0.001, "Pepperstone-like Razor cost ~0.65 pip round turn");
            rt = CommissionMath.RoundTurnPerUnit(CommissionBasis.UsdPerLot, 3, eurusd, 100000, usdToEur, true, false, false);
            Near(rt / pipValueEur, 0.6, 1e-9, "3 USD per lot per side = 0.6 pip round turn on EURUSD");
            rt = CommissionMath.RoundTurnPerUnit(CommissionBasis.QuotePerLot, 3, eurusd, 100000, usdToEur, true, false, false);
            Near(rt / pipValueEur, 0.6, 1e-9, "quote currency per lot");
            rt = CommissionMath.RoundTurnPerUnit(CommissionBasis.PercentOfVolume, 0.002, eurusd, 100000, usdToEur, true, false, false);
            Near(rt, 2 * 0.002 / 100 * eurusd * usdToEur, 1e-15, "percentage of traded volume");
            const double usdjpy = 150.0, jpyToEur = 1.0 / 162.0;
            rt = CommissionMath.RoundTurnPerUnit(CommissionBasis.UsdPerMillionUsd, 30, usdjpy, 100000, jpyToEur, false, true, false);
            Near(rt, 2 * 30 / 1e6 * (usdjpy * jpyToEur), 1e-15, "USD base currency: notional is one USD per unit");
            Check(CommissionMath.RoundTurnPerUnit(CommissionBasis.UsdPerMillionUsd, 30, 0.85, 100000, 1.18, false, false, false) == 0,
                "no USD leg and no USD account: unknown, 0");
            rt = CommissionMath.RoundTurnPerUnit(CommissionBasis.UsdPerMillionUsd, 30, 0.85, 100000, 1.27, false, false, true);
            Near(rt, 2 * 30 / 1e6 * 0.85 * 1.27, 1e-15, "USD account: notional converted through the quote currency");
            Check(CommissionMath.RoundTurnPerUnit(CommissionBasis.UsdPerLot, 0, eurusd, 100000, usdToEur, true, false, false) == 0, "no commission (Standard account)");
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
            Near(lx[3], 0.1, 1e-12, "spread ratio is linear");
            Near(AiFeatures.Build(1.0, 0, 0.0002, 0, 0.0, 1)[3], 0.0, 0, "a 0.0 spread (raw account) stays 0, not a log floor");
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
            double pWith = nb.Predict(new[] { Math.Log(2.5), 0.8, 15.0, 0.08 }, false, null);
            double pAgainst = nb.Predict(new[] { Math.Log(2.5), -0.8, -15.0, 0.08 }, false, null);
            Console.WriteLine("  pipeline: momentum-aligned " + pWith.ToString("F3") + " vs counter-momentum " + pAgainst.ToString("F3"));
            Check(pWith > pAgainst, "model prefers trading with momentum on momentum data");
            Check(pWith >= 0 && pWith <= 1 && pAgainst >= 0 && pAgainst <= 1, "probabilities in range");
        }
    }
}
