// End-to-end simulator for QuantAI_Scalper_24x7_V3: the real cBot class runs against mocked cTrader services
// (account, symbols, bars, indicators, positions, history, orders, timer, log) injected through reflection.
// The market is synthetic (random walk with tick bursts) - the point is the bot's mechanics, not its profit:
// budget and margin rules, timeframe choice, entries and exits, market breaks, restarts, broker rejects, logging.
// It relies on internal members of cTrader.Automate 1.0.21 and is pinned to that package version.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using cAlgo.API;
using cAlgo.API.Collections;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;
using cAlgo.Robots;

namespace Sim
{
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object[], object> Handler;
        protected override object Invoke(MethodInfo targetMethod, object[] args) { return Handler(targetMethod, args); }
    }

    public static class Mk
    {
        public static readonly object NotHandled = new object();
        public static readonly Dictionary<string, int> Unknown = new Dictionary<string, int>();

        public static object Create(Type iface, Func<MethodInfo, object[], object> h)
        {
            object o = DispatchProxy.Create(iface, typeof(Proxy));
            ((Proxy)o).Handler = (m, a) =>
            {
                object r = h(m, a);
                if (!ReferenceEquals(r, NotHandled))
                    return r;
                string key = iface.Name + "." + m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")";
                Unknown[key] = Unknown.TryGetValue(key, out int n) ? n + 1 : 1;
                if (m.ReturnType == typeof(void))
                    return null;
                return m.ReturnType.IsValueType ? Activator.CreateInstance(m.ReturnType) : null;
            };
            return o;
        }

        public static T Create<T>(Func<MethodInfo, object[], object> h) { return (T)Create(typeof(T), h); }

        public static IReadonlyList<T> List<T>(IList<T> items)
        {
            return Create<IReadonlyList<T>>((m, a) =>
            {
                switch (m.Name)
                {
                    case "get_Count": return items.Count;
                    case "get_Item": return items[(int)a[0]];
                    case "GetEnumerator": return items.GetEnumerator();
                }
                return NotHandled;
            });
        }
    }

    public sealed class ClosedArgs : PositionClosedEventArgs
    {
        public ClosedArgs(Position p, PositionCloseReason r) : base(p, r) { }
    }

    public sealed class Spec
    {
        public string Name;
        public bool Crypto;
        public double Price;
        public double Pip;
        public int Digits;
        public long Lot;
        public double VMin, VStep, VMax;
        public double Spread;
        public double Leverage;
        public string Base, Quote;
        public double TickRate;
        public double SigmaMinute;
        public double CommissionPerMillion;
        public bool BrokerMarginWrong;
        public bool BrokerMarginNaN;
        public double TrueLeverage;      // 0 = Leverage; the margin the broker really blocks
        public double MinStopPips;
    }

    public sealed class BarStore
    {
        public Spec S;
        public int TfSec;
        public string TfName;
        public readonly List<DateTime> T = new List<DateTime>();
        public readonly List<double> O = new List<double>(), H = new List<double>(), L = new List<double>(), C = new List<double>(), V = new List<double>();
        public readonly List<double[]> Hidden = new List<double[]>();   // older bars, oldest first: ticks,o,h,l,c,v
        public int Version;
        public Bars Proxy;
        public DataSeries Open, High, Low, Close, Vol;
        public TimeSeries Times;

        public int LoadMore(int n)
        {
            int k = Math.Min(n, Hidden.Count);
            if (k == 0)
                return 0;
            var chunk = Hidden.GetRange(Hidden.Count - k, k);
            Hidden.RemoveRange(Hidden.Count - k, k);
            T.InsertRange(0, chunk.Select(b => new DateTime((long)b[0], DateTimeKind.Utc)));
            O.InsertRange(0, chunk.Select(b => b[1]));
            H.InsertRange(0, chunk.Select(b => b[2]));
            L.InsertRange(0, chunk.Select(b => b[3]));
            C.InsertRange(0, chunk.Select(b => b[4]));
            V.InsertRange(0, chunk.Select(b => b[5]));
            Version++;
            return k;
        }

        public void OnTick(DateTime t, double price)
        {
            DateTime open = World.Floor(t, TfSec);
            if (T.Count == 0 || open > T[T.Count - 1])
            {
                T.Add(open); O.Add(price); H.Add(price); L.Add(price); C.Add(price); V.Add(1);
            }
            else
            {
                int i = T.Count - 1;
                if (price > H[i]) H[i] = price;
                if (price < L[i]) L[i] = price;
                C[i] = price;
                V[i] += 1;
            }
        }
    }

    public sealed class IndCalc
    {
        public BarStore S;
        public string Kind;
        public int N;
        private double[] _v = new double[0];
        private double[] _ag = new double[0];
        private double[] _al = new double[0];
        private int _closed;
        private int _version = -1;

        public double Get(int i)
        {
            int count = S.T.Count;
            if (i < 0 || i >= count)
            {
                World.Current.OutOfRange++;
                return double.NaN;
            }
            if (_version != S.Version)
            {
                _version = S.Version;
                _closed = 0;
            }
            if (_v.Length < count)
            {
                int size = Math.Max(count + 64, _v.Length * 2);
                Array.Resize(ref _v, size);
                Array.Resize(ref _ag, size);
                Array.Resize(ref _al, size);
            }
            while (_closed < count - 1)
            {
                Step(_closed, true);
                _closed++;
            }
            if (i < count - 1)
                return _v[i];
            return Step(i, false);
        }

        private double Tr(int i)
        {
            if (i == 0)
                return S.H[0] - S.L[0];
            return Math.Max(S.H[i] - S.L[i], Math.Max(Math.Abs(S.H[i] - S.C[i - 1]), Math.Abs(S.L[i] - S.C[i - 1])));
        }

        private double Step(int i, bool store)
        {
            double v;
            double ag = 0, al = 0;
            switch (Kind)
            {
                case "EMA":
                    v = i == 0 ? S.C[0] : _v[i - 1] + 2.0 / (N + 1) * (S.C[i] - _v[i - 1]);
                    break;
                case "ATR":
                    if (i < N - 1)
                        v = double.NaN;
                    else if (i == N - 1)
                    {
                        double sum = 0;
                        for (int k = 0; k < N; k++) sum += Tr(k);
                        v = sum / N;
                    }
                    else
                        v = (_v[i - 1] * (N - 1) + Tr(i)) / N;
                    break;
                default: // RSI
                    if (i < N)
                    {
                        v = double.NaN;
                    }
                    else
                    {
                        if (i == N)
                        {
                            for (int k = 1; k <= N; k++)
                            {
                                double d = S.C[k] - S.C[k - 1];
                                if (d > 0) ag += d; else al -= d;
                            }
                            ag /= N;
                            al /= N;
                        }
                        else
                        {
                            double d = S.C[i] - S.C[i - 1];
                            ag = (_ag[i - 1] * (N - 1) + Math.Max(d, 0)) / N;
                            al = (_al[i - 1] * (N - 1) + Math.Max(-d, 0)) / N;
                        }
                        v = al == 0 ? 100.0 : 100.0 - 100.0 / (1.0 + ag / al);
                    }
                    break;
            }
            if (store)
            {
                _v[i] = v;
                _ag[i] = ag;
                _al[i] = al;
            }
            return v;
        }
    }

    public sealed class SymState
    {
        public Spec S;
        public double Mid;
        public double SpreadNow;
        public Symbol Proxy;
        public readonly Dictionary<string, BarStore> Stores = new Dictionary<string, BarStore>();
        public readonly List<Action<SymbolTickEventArgs>> TickHandlers = new List<Action<SymbolTickEventArgs>>();
        public int BurstLeft;
        public int BurstDir;
        public bool WasOpen;
        public DateTime OpenedAt = DateTime.MinValue;
        public double Round(double p) { return Math.Round(p, S.Digits); }
        public double Bid { get { return Round(Mid - SpreadNow / 2); } }
        public double Ask { get { return Round(Mid + SpreadNow / 2); } }
    }

    public sealed class PosState
    {
        public int Id;
        public SymState Sym;
        public string Label;
        public TradeType Type;
        public double Vol;
        public double InitialVol;
        public double Entry;
        public DateTime EntryTime;
        public double? SL;
        public double? TP;
        public string Comment;
        public double MarginPerUnit;
        public double CommissionPerUnitSide;
        public bool Closed;
        public Position Proxy;
    }

    public sealed class HistRec
    {
        public int PositionId;
        public double Net, Gross, Vol;
        public string Label, Sym, Comment;
        public DateTime Time, EntryTime;
        public TradeType Type;
        public HistoricalTrade Proxy;
    }

    public sealed class World
    {
        public static World Current;
        public DateTime Now;
        public readonly Random Rng;
        public readonly Dictionary<string, SymState> Syms = new Dictionary<string, SymState>();
        public readonly List<string> Names = new List<string>();
        public readonly List<PosState> Open = new List<PosState>();
        public readonly List<HistRec> Hist = new List<HistRec>();
        public readonly List<Action<PositionClosedEventArgs>> ClosedHandlers = new List<Action<PositionClosedEventArgs>>();
        public readonly List<Action> TimerHandlers = new List<Action>();
        public readonly Dictionary<object, BarStore> SeriesOwner = new Dictionary<object, BarStore>(ReferenceEqualityComparer.Instance);
        public readonly List<string> Log = new List<string>();
        public readonly List<string> Violations = new List<string>();
        public double Balance;
        public double StartBalance;
        public int NextId = 1000;
        public int OutOfRange;
        public bool Stopped;
        public int ModifyRequests;
        public int MaxModifiesPerSecond;
        private int _modifiesThisSecond;
        private DateTime _modSecond;
        public int Orders;
        public int OrderErrors;
        public readonly Dictionary<string, int> Closes = new Dictionary<string, int>();
        public readonly Dictionary<string, string> Storage = new Dictionary<string, string>();
        public int StorageWrites;
        public QuantAI_Scalper_24x7_V3 Bot;
        public string ChartSymbol = "EURUSD";
        public int InitialVisibleBars = 250;
        public int TotalHistoryBars = 4200;
        public double LoggedMaxLen;
        public string LongestLine = "";

        private static readonly ConstructorInfo TrCtor = typeof(TradeResult).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(bool), typeof(ErrorCode?), typeof(Position), typeof(PendingOrder) }, null);
        private static readonly ConstructorInfo TickArgsCtor = typeof(SymbolTickEventArgs).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(string), typeof(double), typeof(double), typeof(Symbol) }, null);

        public World(int seed) { Rng = new Random(seed); Current = this; }

        public static DateTime Floor(DateTime t, int sec)
        {
            long ticks = sec * TimeSpan.TicksPerSecond;
            return new DateTime(t.Ticks - t.Ticks % ticks, DateTimeKind.Utc);
        }

        public double Gauss()
        {
            double u1 = 1.0 - Rng.NextDouble();
            double u2 = Rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        // ---------------------------------------------------------------- schedule
        public static bool IsOpen(Spec s, DateTime t)
        {
            TimeSpan tod = t.TimeOfDay;
            if (s.Crypto)
            {
                if (tod >= new TimeSpan(20, 59, 0) && tod < new TimeSpan(21, 5, 0)) return false;      // 6-minute daily maintenance
                if (t.DayOfWeek == DayOfWeek.Saturday && tod >= new TimeSpan(14, 0, 0) && tod < new TimeSpan(17, 0, 0)) return false; // long break
                return true;
            }
            DayOfWeek d = t.DayOfWeek;
            if (d == DayOfWeek.Saturday) return false;
            if (d == DayOfWeek.Friday && tod >= new TimeSpan(21, 0, 0)) return false;
            if (d == DayOfWeek.Sunday && tod < new TimeSpan(21, 5, 0)) return false;
            if (tod >= new TimeSpan(20, 59, 0) && tod < new TimeSpan(21, 1, 0)) return false;       // 2-minute rollover
            return true;
        }

        public static TimeSpan TillClose(Spec s, DateTime now)
        {
            if (!IsOpen(s, now)) return TimeSpan.Zero;
            DateTime m0 = Floor(now, 60);
            for (int k = 1; k <= 8 * 1440; k++)
            {
                DateTime t = m0.AddMinutes(k);
                if (!IsOpen(s, t)) return t - now;
            }
            return TimeSpan.FromDays(8);
        }

        public static TimeSpan TillOpen(Spec s, DateTime now)
        {
            if (IsOpen(s, now)) return TimeSpan.Zero;
            DateTime m0 = Floor(now, 60);
            for (int k = 1; k <= 8 * 1440; k++)
            {
                DateTime t = m0.AddMinutes(k);
                if (IsOpen(s, t)) return t - now;
            }
            return TimeSpan.FromDays(8);
        }

        // ---------------------------------------------------------------- money
        public double QuoteToEur(string ccy)
        {
            double eurusd = Syms.ContainsKey("EURUSD") ? Syms["EURUSD"].Mid : 1.17;
            switch (ccy)
            {
                case "EUR": return 1.0;
                case "USD": return 1.0 / eurusd;
                case "JPY": return 1.0 / (eurusd * (Syms.ContainsKey("USDJPY") ? Syms["USDJPY"].Mid : 148.0));
                case "CAD": return 1.0 / (eurusd * (Syms.ContainsKey("USDCAD") ? Syms["USDCAD"].Mid : 1.38));
                case "CHF": return 1.0 / (eurusd * (Syms.ContainsKey("USDCHF") ? Syms["USDCHF"].Mid : 0.80));
                case "GBP": return (Syms.ContainsKey("GBPUSD") ? Syms["GBPUSD"].Mid : 1.35) / eurusd;
                case "AUD": return (Syms.ContainsKey("AUDUSD") ? Syms["AUDUSD"].Mid : 0.66) / eurusd;
            }
            return 1.0;
        }

        public double Gross(PosState p)
        {
            SymState s = p.Sym;
            double px = p.Type == TradeType.Buy ? s.Bid : s.Ask;
            double diff = p.Type == TradeType.Buy ? px - p.Entry : p.Entry - px;
            return diff * p.Vol * QuoteToEur(s.S.Quote);
        }

        public double Net(PosState p) { return Gross(p) - 2 * p.CommissionPerUnitSide * p.Vol; }
        public double Equity { get { return Balance + Open.Sum(p => Net(p)); } }
        public double UsedMargin { get { return Open.Sum(p => p.MarginPerUnit * p.Vol); } }

        public double TrueMargin(SymState s, double units)
        {
            double px = s.Mid;
            return units * px * QuoteToEur(s.S.Quote) / (s.S.TrueLeverage > 0 ? s.S.TrueLeverage : s.S.Leverage);
        }

        // ---------------------------------------------------------------- proxies
        public TradeResult Ok(Position p) { return (TradeResult)TrCtor.Invoke(new object[] { true, null, p, null }); }
        public TradeResult Err(ErrorCode e) { OrderErrors++; return (TradeResult)TrCtor.Invoke(new object[] { false, (ErrorCode?)e, null, null }); }

        public void AddSymbol(Spec s)
        {
            var st = new SymState { S = s, Mid = s.Price, SpreadNow = s.Spread };
            Syms[s.Name] = st;
            Names.Add(s.Name);
            var tiers = new List<LeverageTier> { Mk.Create<LeverageTier>((m, a) => m.Name == "get_Volume" ? (object)1e12 : m.Name == "get_Leverage" ? (object)s.Leverage : Mk.NotHandled) };
            var quote = Mk.Create<Asset>((m, a) => m.Name == "get_Name" ? s.Quote : m.Name == "get_Digits" ? (object)2 : Mk.NotHandled);
            var bas = Mk.Create<Asset>((m, a) => m.Name == "get_Name" ? s.Base : m.Name == "get_Digits" ? (object)2 : Mk.NotHandled);
            var hours = Mk.Create<MarketHours>((m, a) =>
            {
                switch (m.Name)
                {
                    case "IsOpened": return a.Length == 0 ? IsOpen(s, Now) : IsOpen(s, (DateTime)a[0]);
                    case "TimeTillClose": return TillClose(s, Now);
                    case "TimeTillOpen": return TillOpen(s, Now);
                }
                return Mk.NotHandled;
            });
            st.Proxy = Mk.Create<Symbol>((m, a) =>
            {
                switch (m.Name)
                {
                    case "get_Name": return s.Name;
                    case "get_Bid": return st.Bid;
                    case "get_Ask": return st.Ask;
                    case "get_Spread": return st.Ask - st.Bid;
                    case "get_PipSize": return s.Pip;
                    case "get_TickSize": return Math.Pow(10, -s.Digits);
                    case "get_Digits": return s.Digits;
                    case "get_LotSize": return s.Lot;
                    case "get_VolumeInUnitsMin": return s.VMin;
                    case "get_VolumeInUnitsMax": return s.VMax;
                    case "get_VolumeInUnitsStep": return s.VStep;
                    case "get_PipValue": return s.Pip * QuoteToEur(s.Quote);
                    case "get_TickValue": return Math.Pow(10, -s.Digits) * QuoteToEur(s.Quote);
                    case "QuantityToVolumeInUnits": return (double)a[0] * s.Lot;
                    case "VolumeInUnitsToQuantity": return (double)a[0] / s.Lot;
                    case "GetEstimatedMargin":
                        {
                            if (s.BrokerMarginNaN) return double.NaN;
                            double lev = s.BrokerMarginWrong ? 30.0 : s.Leverage;
                            return (double)a[1] * st.Mid * QuoteToEur(s.Quote) / lev;
                        }
                    case "get_DynamicLeverage": return s.Crypto ? Mk.List(new List<LeverageTier>()) : Mk.List(tiers);
                    case "get_MarketHours": return hours;
                    case "get_TradingMode": return SymbolTradingMode.FullAccess;
                    case "get_IsTradingEnabled": return true;
                    case "get_Commission": return s.CommissionPerMillion;
                    case "get_CommissionType": return SymbolCommissionType.UsdPerMillionUsdVolume;
                    case "get_QuoteAsset": return quote;
                    case "get_BaseAsset": return bas;
                    case "get_MinStopLossDistance": return s.MinStopPips;
                    case "get_MinTakeProfitDistance": return 0.0;
                    case "get_MinDistanceType": return SymbolMinDistanceType.Pips;
                    case "add_Tick": st.TickHandlers.Add((Action<SymbolTickEventArgs>)a[0]); return null;
                    case "remove_Tick": st.TickHandlers.Remove((Action<SymbolTickEventArgs>)a[0]); return null;
                }
                return Mk.NotHandled;
            });
        }

        public BarStore GetStore(SymState st, TimeFrame tf)
        {
            string key = tf.ShortName;
            if (st.Stores.TryGetValue(key, out BarStore b))
                return b;
            int sec;
            switch (key)
            {
                case "m1": sec = 60; break;
                case "m5": sec = 300; break;
                case "m15": sec = 900; break;
                case "m30": sec = 1800; break;
                case "h1": sec = 3600; break;
                case "h4": sec = 14400; break;
                default: throw new InvalidOperationException("tf " + key);
            }
            b = new BarStore { S = st.S, TfSec = sec, TfName = key };
            // History backward from the current price; closed periods have no bars.
            var list = new List<double[]>();
            DateTime t = Floor(Now, sec);
            bool forming = IsOpen(st.S, Now);
            double close = st.Mid;
            double sigma = st.S.SigmaMinute * Math.Sqrt(sec / 60.0);
            int guard = 0;
            DateTime cursor = forming ? t : t.AddSeconds(-sec);
            while (list.Count < TotalHistoryBars && guard++ < TotalHistoryBars * 20)
            {
                if (!IsOpen(st.S, cursor) && !IsOpen(st.S, cursor.AddSeconds(sec - 1)))
                {
                    cursor = cursor.AddSeconds(-sec);
                    continue;
                }
                double open = close - sigma * Gauss();
                double hi = Math.Max(open, close) + Math.Abs(Gauss()) * sigma * 0.5;
                double lo = Math.Min(open, close) - Math.Abs(Gauss()) * sigma * 0.5;
                double vol = Math.Max(1, Math.Round(st.S.TickRate * sec * (0.5 + Rng.NextDouble()) * (Rng.NextDouble() < 0.05 ? 3 : 1)));
                if (list.Count == 0 && forming)
                {
                    open = hi = lo = close;
                    vol = 1;
                }
                list.Add(new double[] { cursor.Ticks, st.Round(open), st.Round(hi), st.Round(lo), st.Round(close), vol });
                close = open;
                cursor = cursor.AddSeconds(-sec);
            }
            list.Reverse();
            int visible = Math.Min(InitialVisibleBars, list.Count);
            b.Hidden.AddRange(list.Take(list.Count - visible));
            foreach (var bar in list.Skip(list.Count - visible))
            {
                b.T.Add(new DateTime((long)bar[0], DateTimeKind.Utc)); b.O.Add(bar[1]); b.H.Add(bar[2]); b.L.Add(bar[3]); b.C.Add(bar[4]); b.V.Add(bar[5]);
            }
            b.Open = Series(b, b.O); b.High = Series(b, b.H); b.Low = Series(b, b.L); b.Close = Series(b, b.C); b.Vol = Series(b, b.V);
            b.Times = Mk.Create<TimeSeries>((m, a) =>
            {
                switch (m.Name)
                {
                    case "get_Item":
                        {
                            int i = (int)a[0];
                            if (i < 0 || i >= b.T.Count) { OutOfRange++; return DateTime.MinValue; }
                            return b.T[i];
                        }
                    case "get_Count": return b.T.Count;
                    case "get_LastValue": return b.T[b.T.Count - 1];
                }
                return Mk.NotHandled;
            });
            b.Proxy = Mk.Create<Bars>((m, a) =>
            {
                switch (m.Name)
                {
                    case "get_Count": return b.T.Count;
                    case "get_OpenTimes": return b.Times;
                    case "get_OpenPrices": return b.Open;
                    case "get_HighPrices": return b.High;
                    case "get_LowPrices": return b.Low;
                    case "get_ClosePrices": return b.Close;
                    case "get_TickVolumes": return b.Vol;
                    case "LoadMoreHistory": return b.LoadMore(1000);
                    case "get_TimeFrame": return tf;
                    case "get_SymbolName": return st.S.Name;
                }
                if (m.Name.StartsWith("add_") || m.Name.StartsWith("remove_")) return null;
                return Mk.NotHandled;
            });
            SeriesOwner[b.Proxy] = b;
            SeriesOwner[b.Close] = b;
            st.Stores[key] = b;
            return b;
        }

        private DataSeries Series(BarStore b, List<double> data)
        {
            return Mk.Create<DataSeries>((m, a) =>
            {
                switch (m.Name)
                {
                    case "get_Item":
                        {
                            int i = (int)a[0];
                            if (i < 0 || i >= data.Count) { OutOfRange++; return double.NaN; }
                            return data[i];
                        }
                    case "get_Count": return data.Count;
                    case "get_LastValue": return data[data.Count - 1];
                    case "Last": return data[data.Count - 1 - (int)a[0]];
                }
                return Mk.NotHandled;
            });
        }

        private object Indicator(Type iface, IndCalc calc)
        {
            var result = Mk.Create<IndicatorDataSeries>((m, a) =>
            {
                switch (m.Name)
                {
                    case "get_Item": return calc.Get((int)a[0]);
                    case "get_Count": return calc.S.T.Count;
                    case "get_LastValue": return calc.Get(calc.S.T.Count - 1);
                }
                return Mk.NotHandled;
            });
            return Mk.Create(iface, (m, a) => m.Name == "get_Result" ? result : Mk.NotHandled);
        }

        public IIndicatorsAccessor Indicators()
        {
            return Mk.Create<IIndicatorsAccessor>((m, a) =>
            {
                switch (m.Name)
                {
                    case "ExponentialMovingAverage":
                        return Indicator(typeof(ExponentialMovingAverage), new IndCalc { S = SeriesOwner[a[0]], Kind = "EMA", N = (int)a[1] });
                    case "AverageTrueRange":
                        if (a.Length == 3 && a[0] is Bars)
                            return Indicator(typeof(AverageTrueRange), new IndCalc { S = SeriesOwner[a[0]], Kind = "ATR", N = (int)a[1] });
                        break;
                    case "RelativeStrengthIndex":
                        return Indicator(typeof(RelativeStrengthIndex), new IndCalc { S = SeriesOwner[a[0]], Kind = "RSI", N = (int)a[1] });
                }
                return Mk.NotHandled;
            });
        }

        public Position MakePositionProxy(PosState p)
        {
            return Mk.Create<Position>((m, a) =>
            {
                switch (m.Name)
                {
                    case "get_Id": return p.Id;
                    case "get_SymbolName": return p.Sym.S.Name;
                    case "get_Label": return p.Label;
                    case "get_TradeType": return p.Type;
                    case "get_VolumeInUnits": return p.Vol;
                    case "get_Quantity": return p.Vol / p.Sym.S.Lot;
                    case "get_EntryPrice": return p.Entry;
                    case "get_EntryTime": return p.EntryTime;
                    case "get_StopLoss": return p.SL;
                    case "get_TakeProfit": return p.TP;
                    case "get_NetProfit": return Net(p);
                    case "get_GrossProfit": return Gross(p);
                    case "get_Commissions": return -p.CommissionPerUnitSide * p.Vol;
                    case "get_Swap": return 0.0;
                    case "get_Margin": return p.MarginPerUnit * p.Vol;
                    case "get_Comment": return p.Comment;
                    case "get_Symbol": return p.Sym.Proxy;
                    case "get_CurrentPrice": return p.Type == TradeType.Buy ? p.Sym.Bid : p.Sym.Ask;
                    case "ModifyStopLossPrice": return ModifySl(p, (double?)a[0]);
                    case "Close": return ClosePos(p, p.Vol, PositionCloseReason.Closed);
                }
                return Mk.NotHandled;
            });
        }

        private TradeResult ModifySl(PosState p, double? price)
        {
            CountModify();
            if (p.Closed) return Err(ErrorCode.EntityNotFound);
            if (!IsOpen(p.Sym.S, Now)) return Err(ErrorCode.MarketClosed);
            if (price.HasValue)
            {
                bool ok = p.Type == TradeType.Buy ? price.Value < p.Sym.Bid : price.Value > p.Sym.Ask;
                if (!ok) return Err(ErrorCode.InvalidStopLossTakeProfit);
            }
            p.SL = price.HasValue ? p.Sym.Round(price.Value) : (double?)null;
            return Ok(p.Proxy);
        }

        private void CountModify()
        {
            ModifyRequests++;
            DateTime sec = Floor(Now, 1);
            if (sec != _modSecond) { _modSecond = sec; _modifiesThisSecond = 0; }
            _modifiesThisSecond++;
            MaxModifiesPerSecond = Math.Max(MaxModifiesPerSecond, _modifiesThisSecond);
        }

        public TradeResult Execute(TradeType type, string sym, double vol, string label, RelativeStopLossProtection sl, RelativeTakeProfitProtections tp, string comment)
        {
            Orders++;
            if (!Syms.TryGetValue(sym, out SymState st)) return Err(ErrorCode.UnknownSymbol);
            Spec s = st.S;
            if (!IsOpen(s, Now)) return Err(ErrorCode.MarketClosed);
            double steps = vol / s.VStep;
            if (vol < s.VMin - 1e-9 || vol > s.VMax + 1e-9 || Math.Abs(steps - Math.Round(steps)) > 1e-6) return Err(ErrorCode.BadVolume);
            double margin = TrueMargin(st, vol);
            if (margin > Equity - UsedMargin) return Err(ErrorCode.NoMoney);
            double entry = type == TradeType.Buy ? st.Ask : st.Bid;
            var p = new PosState
            {
                Id = NextId++, Sym = st, Label = label, Type = type, Vol = vol, InitialVol = vol, Entry = entry, EntryTime = Now, Comment = comment,
                MarginPerUnit = margin / vol,
                CommissionPerUnitSide = s.CommissionPerMillion > 0 ? s.CommissionPerMillion / 1e6 * (s.Base == "USD" ? 1.0 : st.Mid * (s.Quote == "USD" ? 1.0 : 1.0)) * QuoteToEur("USD") : 0.0
            };
            if (sl != null)
                p.SL = st.Round(type == TradeType.Buy ? entry - sl.Distance * s.Pip : entry + sl.Distance * s.Pip);
            if (tp != null && tp.LastTakeProfit != null)
                p.TP = st.Round(type == TradeType.Buy ? entry + tp.LastTakeProfit.Distance * s.Pip : entry - tp.LastTakeProfit.Distance * s.Pip);
            p.Proxy = MakePositionProxy(p);
            Open.Add(p);
            Balance -= 0; // commission is charged in the result
            return Ok(p.Proxy);
        }

        public TradeResult ClosePos(PosState p, double vol, PositionCloseReason reason)
        {
            if (p.Closed) return Err(ErrorCode.EntityNotFound);
            if (!IsOpen(p.Sym.S, Now)) return Err(ErrorCode.MarketClosed);
            Spec s = p.Sym.S;
            double steps = vol / s.VStep;
            if (vol <= 0 || Math.Abs(steps - Math.Round(steps)) > 1e-6) return Err(ErrorCode.BadVolume);
            if (vol < p.Vol - 1e-9 && (vol < s.VMin - 1e-9 || p.Vol - vol < s.VMin - 1e-9)) return Err(ErrorCode.BadVolume);
            if (vol > p.Vol + 1e-9) vol = p.Vol;
            double frac = vol / p.Vol;
            double gross = Gross(p) * frac;
            double net = gross - 2 * p.CommissionPerUnitSide * vol;
            Balance += net;
            var rec = new HistRec { PositionId = p.Id, Net = net, Gross = gross, Vol = vol, Label = p.Label, Sym = s.Name, Time = Now, Type = p.Type,
                                    EntryTime = p.EntryTime, Comment = p.Comment };
            rec.Proxy = Mk.Create<HistoricalTrade>((m, a) =>
            {
                switch (m.Name)
                {
                    case "get_PositionId": return rec.PositionId;
                    case "get_NetProfit": return rec.Net;
                    case "get_GrossProfit": return rec.Gross;
                    case "get_Label": return rec.Label;
                    case "get_SymbolName": return rec.Sym;
                    case "get_ClosingTime": return rec.Time;
                    case "get_EntryTime": return rec.EntryTime;
                    case "get_Comment": return rec.Comment;
                    case "get_VolumeInUnits": return rec.Vol;
                    case "get_TradeType": return rec.Type;
                }
                return Mk.NotHandled;
            });
            Hist.Add(rec);
            if (vol >= p.Vol - 1e-9)
            {
                // The platform reports the closing fill's figures on the closed position.
                p.Closed = true;
                Open.Remove(p);
                string key = reason.ToString();
                Closes[key] = Closes.TryGetValue(key, out int n) ? n + 1 : 1;
                foreach (var h in ClosedHandlers.ToList())
                    h(new ClosedArgs(p.Proxy, reason));
            }
            else
            {
                p.Vol -= vol;
            }
            return Ok(p.Proxy);
        }

        // ---------------------------------------------------------------- the bot
        public void Wire(QuantAI_Scalper_24x7_V3 bot)
        {
            Bot = bot;
            Type algo = typeof(Algo);
            Type robot = typeof(Robot);
            Assembly api = algo.Assembly;
            var server = Mk.Create<IServer>((m, a) => m.Name == "get_Time" || m.Name == "get_TimeInUtc" ? (object)Now : m.Name == "get_IsConnected" ? (object)true : Mk.NotHandled);
            var accountAsset = Mk.Create<Asset>((m, a) => m.Name == "get_Name" ? "EUR" : m.Name == "get_Digits" ? (object)2 : Mk.NotHandled);
            var account = Mk.Create<IAccount>((m, a) =>
            {
                switch (m.Name)
                {
                    case "get_Balance": return Balance;
                    case "get_Equity": return Equity;
                    case "get_Margin": return UsedMargin;
                    case "get_FreeMargin": return Equity - UsedMargin;
                    case "get_MarginLevel": return UsedMargin > 0 ? (double?)(Equity / UsedMargin * 100) : null;
                    case "get_PreciseLeverage": return 30.0;
                    case "get_Leverage": return 30;
                    case "get_Asset": return accountAsset;
                    case "get_BrokerName": return "Pepperstone Group Limited";
                    case "get_IsLive": return false;
                    case "get_AccountType": return AccountType.Hedged;
                    case "get_Number": return 1234567;
                }
                return Mk.NotHandled;
            });
            var positions = Mk.Create<Positions>((m, a) =>
            {
                switch (m.Name)
                {
                    case "get_Count": return Open.Count;
                    case "get_Item": return Open[(int)a[0]].Proxy;
                    case "FindAll":
                        {
                            string label = (string)a[0];
                            IEnumerable<PosState> q = Open.Where(p => p.Label == label);
                            if (a.Length >= 2)
                            {
                                string sym = a[1] is Symbol so ? so.Name : (string)a[1];
                                q = q.Where(p => p.Sym.S.Name == sym);
                            }
                            if (a.Length >= 3) q = q.Where(p => p.Type == (TradeType)a[2]);
                            return q.Select(p => p.Proxy).ToArray();
                        }
                    case "GetEnumerator": return Open.Select(p => p.Proxy).ToList().GetEnumerator();
                    case "add_Closed": ClosedHandlers.Add((Action<PositionClosedEventArgs>)a[0]); return null;
                    case "remove_Closed": ClosedHandlers.Remove((Action<PositionClosedEventArgs>)a[0]); return null;
                }
                if (m.Name.StartsWith("add_") || m.Name.StartsWith("remove_")) return null;
                return Mk.NotHandled;
            });
            var history = Mk.Create<History>((m, a) =>
            {
                switch (m.Name)
                {
                    case "get_Count": return Hist.Count;
                    case "get_Item": return Hist[(int)a[0]].Proxy;
                    case "FindAll":
                        {
                            string label = (string)a[0];
                            IEnumerable<HistRec> q = Hist.Where(h => h.Label == label);
                            if (a.Length >= 2)
                            {
                                string sym = a[1] is Symbol so ? so.Name : (string)a[1];
                                q = q.Where(h => h.Sym == sym);
                            }
                            return q.Select(h => h.Proxy).ToArray();
                        }
                }
                return Mk.NotHandled;
            });
            var symbols = Mk.Create<Symbols>((m, a) =>
            {
                switch (m.Name)
                {
                    case "Exists": return Syms.ContainsKey((string)a[0]);
                    case "GetSymbol": return Syms.TryGetValue((string)a[0], out SymState st) ? st.Proxy : null;
                    case "get_Count": return Names.Count;
                    case "get_Item": return Names[(int)a[0]];
                    case "GetEnumerator": return Names.GetEnumerator();
                }
                if (m.Name.StartsWith("add_") || m.Name.StartsWith("remove_")) return null;
                return Mk.NotHandled;
            });
            var marketData = Mk.Create<MarketData>((m, a) =>
            {
                if (m.Name == "GetBars")
                {
                    TimeFrame tf = (TimeFrame)a[0];
                    string sym = a.Length > 1 ? (string)a[1] : ChartSymbol;
                    return GetStore(Syms[sym], tf).Proxy;
                }
                return Mk.NotHandled;
            });
            Type tSymbolsProvider = api.GetType("cAlgo.API.Internals.ISymbolsProvider");
            Type tMarketDataProvider = api.GetType("cAlgo.API.Internals.IMarketDataProvider");
            Type tPositionsProvider = api.GetType("cAlgo.API.Internals.IPositionsProvider");
            Type tHistoryController = api.GetType("cAlgo.API.Internals.IHistoryController");
            Type tChartProvider = api.GetType("cAlgo.API.Internals.IChartProvider");
            Type tAppController = api.GetType("cAlgo.API.Internals.IApplicationController");
            Type tLog = api.GetType("cAlgo.API.Internals.ILog");
            Type tStop = api.GetType("cAlgo.API.Internals.IStopAlgoService");
            Type tNewTrade = api.GetType("cAlgo.API.Internals.NewTrade");
            object symbolsProvider = Mk.Create(tSymbolsProvider, (m, a) => m.Name == "GetSymbols" ? symbols : m.Name == "GetSymbol" ? (Syms.TryGetValue((string)a[0], out SymState st) ? st.Proxy : null) : Mk.NotHandled);
            object marketDataProvider = Mk.Create(tMarketDataProvider, (m, a) => m.Name == "GetMarketData" ? marketData : Mk.NotHandled);
            object positionsProvider = Mk.Create(tPositionsProvider, (m, a) => m.Name == "GetPositions" ? positions : Mk.NotHandled);
            object historyController = Mk.Create(tHistoryController, (m, a) => m.Name == "GetHistory" ? history : Mk.NotHandled);
            object chartProvider = Mk.Create(tChartProvider, (m, a) => throw new InvalidOperationException("no chart in the cloud"));
            object appController = Mk.Create(tAppController, (m, a) => m.Name == "GetApplication" ? null : Mk.NotHandled);
            object log = Mk.Create(tLog, (m, a) =>
            {
                string text = a.Length == 1 ? Convert.ToString(a[0], CultureInfo.InvariantCulture) : string.Join(" ", a.Select(x => x is object[] arr ? string.Join(" ", arr) : Convert.ToString(x, CultureInfo.InvariantCulture)));
                if (text.Length > LoggedMaxLen) { LoggedMaxLen = text.Length; LongestLine = text; }
                Log.Add(Now.ToString("ddd HH:mm:ss", CultureInfo.InvariantCulture) + " | " + text);
                return null;
            });
            object stop = Mk.Create(tStop, (m, a) => { if (m.Name == "Stop") { Stopped = true; return null; } return Mk.NotHandled; });
            object newTrade = Mk.Create(tNewTrade, (m, a) =>
            {
                switch (m.Name)
                {
                    case "ExecuteMarketOrder":
                        return Execute((TradeType)a[0], (string)a[1], (double)a[2], (string)a[3], (RelativeStopLossProtection)a[4], (RelativeTakeProfitProtections)a[5], (string)a[8]);
                    case "ClosePosition":
                        {
                            Position pos = (Position)a[0];
                            PosState p = Open.FirstOrDefault(x => ReferenceEquals(x.Proxy, pos));
                            if (p == null) return Err(ErrorCode.EntityNotFound);
                            return ClosePos(p, (double)a[1], PositionCloseReason.Closed);
                        }
                }
                return Mk.NotHandled;
            });
            var storage = Mk.Create<LocalStorage>((m, a) =>
            {
                string key = a.Length > 0 ? a[0] as string : null;
                if (key != null && key.Any(ch => !(char.IsLetterOrDigit(ch) || ch == ' ')))
                    throw new ArgumentException("LocalStorage key may contain only letters, digits and spaces: '" + key + "'");
                switch (m.Name)
                {
                    case "GetString": return Storage.TryGetValue(key, out string v) ? v : null;
                    case "SetString": Storage[key] = (string)a[1]; StorageWrites++; return null;
                    case "Remove": Storage.Remove(key); return null;
                    case "Flush": return null;
                    case "Reload": return null;
                }
                return Mk.NotHandled;
            });
            var timer = Mk.Create<cAlgo.API.Timer>((m, a) =>
            {
                switch (m.Name)
                {
                    case "Start": return null;
                    case "Stop": return null;
                    case "add_TimerTick": TimerHandlers.Add((Action)a[0]); return null;
                    case "remove_TimerTick": TimerHandlers.Remove((Action)a[0]); return null;
                }
                return Mk.NotHandled;
            });

            void Set(Type t, string name, object value)
            {
                PropertyInfo pi = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                pi.GetSetMethod(true).Invoke(bot, new[] { value });
            }
            Set(algo, "SymbolName", ChartSymbol);
            Set(algo, "TimeFrame", TimeFrame.Minute);
            Set(algo, "RunningMode", RunningMode.RealTime);
            Set(algo, "Server", server);
            Set(algo, "Indicators", Indicators());
            Set(algo, "PositionsProvider", positionsProvider);
            Set(algo, "HistoryController", historyController);
            Set(algo, "SymbolsProvider", symbolsProvider);
            Set(algo, "MarketDataProvider", marketDataProvider);
            Set(algo, "ChartProvider", chartProvider);
            Set(algo, "ApplicationController", appController);
            Set(algo, "LogService", log);
            Set(algo, "LocalStorage", storage);
            Set(robot, "Account", account);
            Set(robot, "NewTrade", newTrade);
            Set(robot, "StopCBotService", stop);
            algo.GetMethod("SetTimer", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(bot, new object[] { timer });

            foreach (PropertyInfo pi in bot.GetType().GetProperties())
            {
                var attr = pi.GetCustomAttribute<ParameterAttribute>();
                if (attr == null) continue;
                object v = attr.DefaultValue;
                if (v != null && v.GetType() != pi.PropertyType && !pi.PropertyType.IsEnum)
                    v = Convert.ChangeType(v, pi.PropertyType, CultureInfo.InvariantCulture);
                pi.SetValue(bot, v);
            }
        }

        public double BotDouble(string member)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            FieldInfo fi = Bot.GetType().GetField(member, flags);
            if (fi != null) return (double)fi.GetValue(Bot);
            return (double)Bot.GetType().GetMethod(member, flags, null, Type.EmptyTypes, null).Invoke(Bot, null);
        }

        public void Call(string method)
        {
            MethodInfo mi = Bot.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            mi.Invoke(Bot, null);
        }

        // ---------------------------------------------------------------- market simulation
        private void Tick(SymState st)
        {
            Spec s = st.S;
            double dt = 1.0 / Math.Max(0.05, s.TickRate);
            double step = s.SigmaMinute * Math.Sqrt(dt / 60.0) * Gauss();
            if (st.BurstLeft > 0)
                step += st.BurstDir * s.SigmaMinute * Math.Sqrt(dt / 60.0) * 0.6;
            st.Mid = Math.Max(st.Mid + step, s.Pip * 10);
            double spreadFactor = 1.0 + 0.3 * Math.Abs(Gauss());
            if ((Now - st.OpenedAt).TotalMinutes < 10) spreadFactor *= 3;
            TimeSpan tod = Now.TimeOfDay;
            if (!s.Crypto && (tod < TimeSpan.FromHours(6) || tod > TimeSpan.FromHours(20))) spreadFactor *= 2.5;
            st.SpreadNow = Math.Max(Math.Pow(10, -s.Digits), s.Spread * spreadFactor);
            double bid = st.Bid;
            foreach (BarStore b in st.Stores.Values)
                b.OnTick(Now, bid);
            // Server-side stops and targets.
            foreach (PosState p in Open.Where(x => x.Sym == st).ToList())
            {
                if (p.Type == TradeType.Buy)
                {
                    if (p.SL.HasValue && st.Bid <= p.SL.Value) ClosePos(p, p.Vol, PositionCloseReason.StopLoss);
                    else if (p.TP.HasValue && st.Bid >= p.TP.Value) ClosePos(p, p.Vol, PositionCloseReason.TakeProfit);
                }
                else
                {
                    if (p.SL.HasValue && st.Ask >= p.SL.Value) ClosePos(p, p.Vol, PositionCloseReason.StopLoss);
                    else if (p.TP.HasValue && st.Ask <= p.TP.Value) ClosePos(p, p.Vol, PositionCloseReason.TakeProfit);
                }
            }
            if (s.Name == ChartSymbol)
            {
                Call("OnTick");
            }
            else
            {
                var args = (SymbolTickEventArgs)TickArgsCtor.Invoke(new object[] { s.Name, st.Bid, st.Ask, st.Proxy });
                foreach (var h in st.TickHandlers.ToList())
                    h(args);
            }
        }

        private int Poisson(double lambda)
        {
            double l = Math.Exp(-lambda), p = 1.0;
            int k = 0;
            do { k++; p *= Rng.NextDouble(); } while (p > l);
            return k - 1;
        }

        public void Run(DateTime start, TimeSpan duration, Func<World, bool> onSecond = null)
        {
            Now = start;
            foreach (SymState st in Syms.Values)
                st.WasOpen = IsOpen(st.S, Now);
            Call("OnStart");
            DateTime end = start + duration;
            for (DateTime t = start; t < end && !Stopped; t = t.AddSeconds(1))
            {
                foreach (SymState st in Syms.Values)
                {
                    Now = t;
                    bool open = IsOpen(st.S, t);
                    if (open && !st.WasOpen) st.OpenedAt = t;
                    if (!open && st.WasOpen)
                        CheckFlatAtClose(st, t);
                    st.WasOpen = open;
                    if (!open) continue;
                    if (st.BurstLeft > 0) st.BurstLeft--;
                    else if (Rng.NextDouble() < 1.0 / 400) { st.BurstLeft = 20 + Rng.Next(40); st.BurstDir = Rng.Next(2) == 0 ? -1 : 1; }
                    double lambda = st.S.TickRate * (st.BurstLeft > 0 ? 4.0 : 1.0);
                    int k = Poisson(lambda);
                    for (int j = 0; j < k; j++)
                    {
                        Now = t.AddMilliseconds(1000.0 * (j + 1) / (k + 1));
                        Tick(st);
                    }
                }
                Now = t.AddSeconds(1);
                foreach (var h in TimerHandlers.ToList())
                    h();
                CheckInvariants();
                if (onSecond != null && !onSecond(this)) break;
            }
            Call("OnStop");
        }

        public void Restart(Action<QuantAI_Scalper_24x7_V3> configure)
        {
            Call("OnStop");
            ClosedHandlers.Clear();
            TimerHandlers.Clear();
            foreach (SymState st in Syms.Values) st.TickHandlers.Clear();
            var bot = new QuantAI_Scalper_24x7_V3();
            Wire(bot);
            configure?.Invoke(bot);
            Log.Add(Now.ToString("ddd HH:mm:ss", CultureInfo.InvariantCulture) + " | ---------------- RESTART ----------------");
            Call("OnStart");
        }

        // ---------------------------------------------------------------- checks
        public double RunResultStart;

        private void CheckFlatAtClose(SymState st, DateTime t)
        {
            // A long break starts: the bot must not hold positions into it.
            bool longBreak = !IsOpen(st.S, t.AddMinutes(15)) && !IsOpen(st.S, t.AddMinutes(10));
            if (!longBreak) return;
            foreach (PosState p in Open.Where(x => x.Sym == st && x.Label == "QuantAI_M1"))
                Violations.Add(Now.ToString("ddd HH:mm") + " position #" + p.Id + " on " + st.S.Name + " held into a long break");
        }

        private readonly Dictionary<int, DateTime> _noSlSince = new Dictionary<int, DateTime>();

        private void CheckInvariants()
        {
            var mine = Open.Where(p => p.Label == "QuantAI_M1").ToList();
            foreach (var g in mine.GroupBy(p => p.Sym.S.Name))
                if (g.Count() > 1) Violations.Add(Now.ToString("ddd HH:mm") + " " + g.Count() + " positions on " + g.Key);
            if (mine.Count > Bot.MaxOpenPositions) Violations.Add(Now.ToString("ddd HH:mm") + " too many positions " + mine.Count);
            foreach (var p in mine)
            {
                if (p.SL.HasValue) { _noSlSince.Remove(p.Id); continue; }
                if (!_noSlSince.ContainsKey(p.Id)) _noSlSince[p.Id] = Now;
                else if ((Now - _noSlSince[p.Id]).TotalSeconds > 3) Violations.Add(Now.ToString("ddd HH:mm") + " position #" + p.Id + " without stop");
            }
            if (Bot.BotBudget > 0)
            {
                // Same rule as the bot: only positions opened since the budget started belong to the budget.
                DateTime budgetStart = (DateTime)Bot.GetType().GetField("_budgetStart", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Bot);
                var budgetPositions = mine.Where(p => p.EntryTime >= budgetStart).ToList();
                double realized = Hist.Where(h => h.Label == "QuantAI_M1" && h.EntryTime >= budgetStart).Sum(h => h.Net);
                double floating = budgetPositions.Sum(p => Net(p));
                double budgetEquity = Math.Max(0, Math.Min(Equity, Bot.BotBudget + realized + floating));
                double used = budgetPositions.Sum(p => p.MarginPerUnit * p.Vol);
                if (used > budgetEquity * Bot.MaxTotalMarginPercent / 100.0 * 1.05 + 0.01)
                    Violations.Add(Now.ToString("ddd HH:mm") + " margin " + used.ToString("F2") + " > " + Bot.MaxTotalMarginPercent + "% of budget equity " + budgetEquity.ToString("F2"));
            }
        }
    }

    public sealed class ReportRow
    {
        public string Title;
        public string Check;
        public int Entries, Closed, Errors, Violations;
        public string Balance;
        public string Note = "";
        public bool Ok;
    }

    public static class Program
    {
        private static readonly List<ReportRow> Rows = new List<ReportRow>();
        private static List<Spec> Specs()
        {
            return new List<Spec>
            {
                new Spec { Name = "EURUSD", Price = 1.1700, Pip = 0.0001, Digits = 5, Lot = 100000, VMin = 1000, VStep = 1000, VMax = 1e8, Spread = 0.00001, Leverage = 30, Base = "EUR", Quote = "USD", TickRate = 1.0, SigmaMinute = 0.00008, CommissionPerMillion = 30 },
                new Spec { Name = "GBPUSD", Price = 1.3500, Pip = 0.0001, Digits = 5, Lot = 100000, VMin = 1000, VStep = 1000, VMax = 1e8, Spread = 0.00002, Leverage = 30, Base = "GBP", Quote = "USD", TickRate = 1.0, SigmaMinute = 0.00010, CommissionPerMillion = 30 },
                new Spec { Name = "USDJPY", Price = 148.00, Pip = 0.01, Digits = 3, Lot = 100000, VMin = 1000, VStep = 1000, VMax = 1e8, Spread = 0.003, Leverage = 30, Base = "USD", Quote = "JPY", TickRate = 1.0, SigmaMinute = 0.012, CommissionPerMillion = 30 },
                new Spec { Name = "AUDUSD", Price = 0.6600, Pip = 0.0001, Digits = 5, Lot = 100000, VMin = 1000, VStep = 1000, VMax = 1e8, Spread = 0.00002, Leverage = 20, Base = "AUD", Quote = "USD", TickRate = 0.8, SigmaMinute = 0.00006, CommissionPerMillion = 30 },
                new Spec { Name = "USDCAD", Price = 1.3800, Pip = 0.0001, Digits = 5, Lot = 100000, VMin = 1000, VStep = 1000, VMax = 1e8, Spread = 0.00003, Leverage = 30, Base = "USD", Quote = "CAD", TickRate = 0.8, SigmaMinute = 0.00007, CommissionPerMillion = 30 },
                new Spec { Name = "USDCHF", Price = 0.8000, Pip = 0.0001, Digits = 5, Lot = 100000, VMin = 1000, VStep = 1000, VMax = 1e8, Spread = 0.00003, Leverage = 30, Base = "USD", Quote = "CHF", TickRate = 0.8, SigmaMinute = 0.00006, CommissionPerMillion = 30 },
                new Spec { Name = "BTCUSD", Crypto = true, Price = 85000, Pip = 0.01, Digits = 2, Lot = 1, VMin = 0.01, VStep = 0.01, VMax = 100, Spread = 25, Leverage = 2, Base = "BTC", Quote = "USD", TickRate = 1.5, SigmaMinute = 51, BrokerMarginWrong = true },
                new Spec { Name = "ETHUSD", Crypto = true, Price = 3500, Pip = 0.01, Digits = 2, Lot = 1, VMin = 0.01, VStep = 0.01, VMax = 1000, Spread = 2.5, Leverage = 2, Base = "ETH", Quote = "USD", TickRate = 1.2, SigmaMinute = 2.8 },
                new Spec { Name = "SOLUSD", Crypto = true, Price = 140, Pip = 0.01, Digits = 2, Lot = 1, VMin = 0.1, VStep = 0.1, VMax = 10000, Spread = 0.12, Leverage = 2, Base = "SOL", Quote = "USD", TickRate = 0.8, SigmaMinute = 0.16 },
                new Spec { Name = "XRPUSD", Crypto = true, Price = 2.5, Pip = 0.0001, Digits = 4, Lot = 1, VMin = 10, VStep = 1, VMax = 1e6, Spread = 0.003, Leverage = 2, Base = "XRP", Quote = "USD", TickRate = 0.8, SigmaMinute = 0.0035, BrokerMarginNaN = true },
                new Spec { Name = "BCH/USD", Crypto = true, Price = 450, Pip = 0.01, Digits = 2, Lot = 1, VMin = 0.01, VStep = 0.01, VMax = 1000, Spread = 1.5, Leverage = 2, Base = "BCH", Quote = "USD", TickRate = 0.5, SigmaMinute = 0.36 },
            };
        }

        private static World Scenario(string title, int seed, DateTime start, TimeSpan duration, Action<QuantAI_Scalper_24x7_V3> configure, double balance, string logFile,
                                      Action<List<Spec>> tweak = null, Func<World, bool> onSecond = null, Action<World> beforeStart = null)
        {
            Console.WriteLine();
            Console.WriteLine("################ " + title);
            var w = new World(seed);
            List<Spec> specs = Specs();
            tweak?.Invoke(specs);
            foreach (Spec s in specs) w.AddSymbol(s);
            w.Names.Add("XAUUSD");          // offered but not listed
            w.Balance = w.StartBalance = balance;
            var bot = new QuantAI_Scalper_24x7_V3();
            w.Wire(bot);
            configure?.Invoke(bot);
            beforeStart?.Invoke(w);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            w.Run(start, duration, onSecond);
            sw.Stop();
            System.IO.File.WriteAllLines(logFile, w.Log);
            Report(w, sw.Elapsed);
            Rows.Add(new ReportRow
            {
                Title = title,
                Entries = w.Log.Count(l => l.Contains(" ENTRY ")),
                Closed = w.Log.Count(l => l.Contains(" CLOSED #")),
                Errors = w.Log.Count(l => l.Contains("ERROR in") || l.Contains("FATAL")),
                Violations = w.Violations.Count,
                Balance = w.StartBalance.ToString("F2", CultureInfo.InvariantCulture) + " → " + w.Balance.ToString("F2", CultureInfo.InvariantCulture)
            });
            return w;
        }

        private static void WriteReport(string path)
        {
            string H(string x) => System.Net.WebUtility.HtmlEncode(x);
            var sb = new StringBuilder();
            Rows.Sort((x, y) => string.CompareOrdinal(x.Title, y.Title));
            int ok = Rows.Count(r => r.Ok);
            sb.Append("<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            sb.Append("<title>Проверка QuantAI Scalper</title><style>");
            sb.Append(":root{--bg:#0f1115;--card:#171a21;--line:#262b35;--text:#e8eaf0;--muted:#9aa3b2;--ok:#3ecf8e;--bad:#ff6b6b}");
            sb.Append("*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.45 -apple-system,system-ui,Segoe UI,Roboto,sans-serif;padding:16px}");
            sb.Append("h1{font-size:20px;margin:0 0 4px}p.sub{color:var(--muted);margin:0 0 14px}");
            sb.Append(".sum{display:flex;gap:10px;flex-wrap:wrap;margin-bottom:14px}.pill{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:10px 12px}");
            sb.Append(".pill b{display:block;font-size:20px}.row{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:12px;margin-bottom:10px}");
            sb.Append(".t{display:flex;justify-content:space-between;gap:8px;font-weight:600}.ok{color:var(--ok)}.bad{color:var(--bad)}.c{color:var(--muted);margin-top:4px}");
            sb.Append(".m{display:flex;gap:14px;flex-wrap:wrap;margin-top:6px;font-size:13px;color:var(--muted)}.m span b{color:var(--text)}</style></head><body>");
            sb.Append("<h1>QuantAI_Scalper_24x7_V3 — проверка перед выдачей</h1>");
            sb.Append("<p class=\"sub\">Настоящий код бота на подменённой платформе cTrader, синтетический рынок. Проверяется механика, не прибыльность.</p>");
            sb.Append("<div class=\"sum\"><div class=\"pill\"><b class=\"" + (ok == Rows.Count ? "ok" : "bad") + "\">" + ok + " / " + Rows.Count + "</b>сценариев без ошибок</div>");
            sb.Append("<div class=\"pill\"><b>" + Rows.Sum(r => r.Entries) + "</b>сделок открыто</div>");
            sb.Append("<div class=\"pill\"><b>" + Rows.Sum(r => r.Violations) + "</b>нарушений правил риска</div>");
            sb.Append("<div class=\"pill\"><b>" + (Mk.Unknown.Count == 0 ? "0" : Mk.Unknown.Count.ToString()) + "</b>неизвестных вызовов API</div></div>");
            foreach (ReportRow r in Rows)
            {
                sb.Append("<div class=\"row\"><div class=\"t\"><span>" + H(r.Title.Split(':')[0]) + ". " + H(r.Check) + "</span><span class=\""
                          + (r.Ok ? "ok\">✓" : "bad\">✗") + "</span></div>");
                sb.Append("<div class=\"m\"><span>входов <b>" + r.Entries + "</b></span><span>закрыто <b>" + r.Closed + "</b></span><span>ошибок <b>" + r.Errors
                          + "</b></span><span>нарушений <b>" + r.Violations + "</b></span><span>счёт <b>" + H(r.Balance) + "</b></span></div>");
                if (r.Note.Length > 0)
                    sb.Append("<div class=\"c\">" + H(r.Note) + "</div>");
                sb.Append("</div>");
            }
            sb.Append("<p class=\"sub\">Правила, проверяемые каждую секунду: стоп у каждой позиции, не больше одной позиции на символ и 8 всего, "
                      + "залог бюджета ≤ 90 %, ни одной позиции в длинный перерыв рынка.</p></body></html>");
            System.IO.File.WriteAllText(path, sb.ToString());
        }

        private static void Report(World w, TimeSpan took)
        {
            int Count(string s) => w.Log.Count(l => l.Contains(s));
            Console.WriteLine("run " + took.TotalSeconds.ToString("F1") + " s | log lines " + w.Log.Count + ", longest " + w.LoggedMaxLen + " chars | stopped " + w.Stopped);
            Console.WriteLine("orders " + w.Orders + " (errors " + w.OrderErrors + ") | ENTRY " + Count(" ENTRY ") + " | CLOSED " + Count(" CLOSED #") + " | TIME EXIT " + Count("TIME EXIT #")
                              + " | BREAK EXIT " + Count("BREAK EXIT #") + " | TP1 " + Count(" TP1 #") + " | TRAIL " + Count(" TRAIL #") + " | MICRO-BE " + Count("MICRO-BE #")
                              + " | SKIP " + Count(" SKIP [") + " | STATUS " + Count("| STATUS ") + " | TIMEFRAME " + Count(" TIMEFRAME "));
            Console.WriteLine("platform closes: " + string.Join(", ", w.Closes.Select(kv => kv.Key + " " + kv.Value)) + " | modify requests " + w.ModifyRequests
                              + ", max/s " + w.MaxModifiesPerSecond + " | out-of-range reads " + w.OutOfRange);
            Console.WriteLine("balance " + w.StartBalance.ToString("F2") + " -> " + w.Balance.ToString("F2") + ", open positions " + w.Open.Count);
            var errors = w.Log.Where(l => l.Contains("ERROR") || l.Contains("FATAL") || l.Contains("Exception")).ToList();
            Console.WriteLine("error lines: " + errors.Count);
            foreach (var e in errors.Take(10)) Console.WriteLine("  ! " + e);
            Console.WriteLine("violations: " + w.Violations.Count);
            foreach (var v in w.Violations.Distinct().Take(15)) Console.WriteLine("  X " + v);
            var perHour = w.Log.GroupBy(l => l.Substring(0, 6)).Select(g => g.Count()).DefaultIfEmpty(0).Max();
            Console.WriteLine("max log lines in one hour: " + perHour);
        }

        public static int Main(string[] args)
        {
            string dir = args.Length > 0 ? args[0] : "sim-logs";
            Directory.CreateDirectory(dir);
            var mk = new Func<Action<QuantAI_Scalper_24x7_V3>, Action<QuantAI_Scalper_24x7_V3>>(f => f);

            // 1. Weekend, 50 EUR budget on the 49k demo: crypto only, TF choice, Saturday long break, daily 6-minute break.
            World a = Scenario("A: weekend Sat 06:00 +40h, budget 50 on a 49,092 EUR demo", 11, new DateTime(2026, 9, 26, 6, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(40),
                     b => { }, 49092.50, Path.Combine(dir, "logA.txt"));
            // 2. Forex week open: Sun 20:00 -> Mon 13:00, 50 EUR budget.
            World bw = Scenario("B: Sun 20:00 +17h, budget 50", 12, new DateTime(2026, 9, 27, 20, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(17),
                     b => { }, 49092.50, Path.Combine(dir, "logB.txt"));
            // 3. A real 20 EUR account (budget param 50 > equity 20).
            World c = Scenario("C: Mon 05:00 +10h, real 20 EUR account", 13, new DateTime(2026, 9, 28, 5, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(10),
                     b => { }, 20.0, Path.Combine(dir, "logC.txt"));
            // 4. Whole 49k account, 4 fixed lots, Friday into the weekend close.
            World d = Scenario("D: Fri 14:00 +9h, whole account, fixed 4 lots", 14, new DateTime(2026, 10, 2, 14, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(9),
                     b => { b.BotBudget = 0; b.FixedLots = 4; }, 49092.50, Path.Combine(dir, "logD.txt"));

            // 5. Restart while a position is open: the new instance must recover and manage it.
            bool restarted = false;
            World e = Scenario("E: restart with an open position", 15, new DateTime(2026, 9, 28, 7, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(6),
                     b => { }, 49092.50, Path.Combine(dir, "logE.txt"), null,
                     w => { if (!restarted && w.Open.Any(p => p.Label == "QuantAI_M1" && (w.Now - p.EntryTime).TotalSeconds > 20)) { restarted = true; w.Restart(null); } return true; });
            Console.WriteLine("restarted: " + restarted + " | RECOVERED lines " + e.Log.Count(l => l.Contains("RECOVERED #")));
            // 6. Daily loss limit and a broker minimum stop distance.
            World f = Scenario("F: daily loss 0.5% of 50 EUR", 16, new DateTime(2026, 9, 28, 6, 30, 0, DateTimeKind.Utc), TimeSpan.FromHours(20),
                     b => { b.DailyMaxLossPercent = 0.5; }, 49092.50, Path.Combine(dir, "logF.txt"));
            int entriesAfterHalt = 0;
            bool halted = false;
            foreach (string l in f.Log)
            {
                if (l.Contains("HALT:")) halted = true;
                else if (l.Contains("NEW DAY")) halted = false;
                else if (halted && l.Contains(" ENTRY ")) entriesAfterHalt++;
            }
            Console.WriteLine("HALT lines " + f.Log.Count(l => l.Contains("HALT:")) + " | entries while halted " + entriesAfterHalt);
            if (entriesAfterHalt > 0) f.Violations.Add("entries while halted");
            World h = Scenario("H: weekend, ETH broker min stop $10,000", 18, new DateTime(2026, 9, 26, 6, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(30),
                     b => { }, 49092.50, Path.Combine(dir, "logH.txt"),
                     specs => specs.First(x => x.Name == "ETHUSD").MinStopPips = 1000000);
            Console.WriteLine("MINSL skips " + h.Log.Count(l => l.Contains("closer than the broker minimum stop")) + " | ETH entries " + h.Log.Count(l => l.Contains("ETHUSD ENTRY")));
            // 7. The broker blocks 1:1 on ETH while every estimate says 1:2: one reject, then no hammering.
            World g = Scenario("G: 20 EUR account, ETH really needs 1:1", 17, new DateTime(2026, 9, 28, 6, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(12),
                     b => { }, 20.0, Path.Combine(dir, "logG.txt"),
                     specs => specs.First(x => x.Name == "ETHUSD").TrueLeverage = 1);
            Console.WriteLine("ETH order rejects " + g.Log.Count(l => l.Contains("ETHUSD ORDER FAILED")) + " | ETH entries " + g.Log.Count(l => l.Contains("ETHUSD ENTRY")));

            // 9. Restart keeps the budget and the day: equity and the day's start must be the same before and after.
            double equityBefore = 0, dayBefore = 0, equityAfter = 0, dayAfter = 0;
            bool restartedI = false;
            World i9 = Scenario("I: restart keeps the budget and the day's start", 19, new DateTime(2026, 9, 28, 6, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(8),
                     b => { }, 49092.50, Path.Combine(dir, "logI.txt"), null,
                     w =>
                     {
                         if (!restartedI && w.Now.Hour >= 11 && !w.Open.Any() && Math.Abs(w.BotDouble("_runResult")) > 1e-9)
                         {
                             restartedI = true;
                             equityBefore = w.BotDouble("BotEquity");
                             dayBefore = w.BotDouble("_dayStartEquity");
                             w.Restart(null);
                             equityAfter = w.BotDouble("BotEquity");
                             dayAfter = w.BotDouble("_dayStartEquity");
                         }
                         return true;
                     });
            Console.WriteLine("restarted " + restartedI + " | budget equity " + equityBefore.ToString("F4") + " -> " + equityAfter.ToString("F4")
                              + " | day start " + dayBefore.ToString("F4") + " -> " + dayAfter.ToString("F4") + " | storage writes " + i9.StorageWrites);
            if (!restartedI || Math.Abs(equityBefore - equityAfter) > 1e-6 || Math.Abs(dayBefore - dayAfter) > 1e-6)
                i9.Violations.Add("budget or day start changed across the restart");

            // 10. A BTC position of an earlier version on a 50 EUR budget: managed, but no new BTC trades.
            World j = Scenario("J: old BTC position on a 50 EUR budget is managed", 20, new DateTime(2026, 9, 26, 8, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(6),
                     b => { }, 49092.50, Path.Combine(dir, "logJ.txt"), null, null,
                     w =>
                     {
                         w.Now = new DateTime(2026, 9, 26, 7, 55, 0, DateTimeKind.Utc);
                         var sl = new RelativeStopLossProtection(40000.0);   // $400 in 0.01 pips
                         w.Execute(TradeType.Buy, "BTCUSD", 0.30, "QuantAI_M1", sl, null, "QAI|SWP|c=0.60|v=0.3|s=40000.0|t=50000.0");
                     });
            bool managed = j.Log.Any(l => l.Contains("BTCUSD: manage-only")) && j.Log.Any(l => l.Contains("BTCUSD RECOVERED"));
            bool newBtc = j.Log.Any(l => l.Contains("BTCUSD ENTRY"));
            bool closedByBot = j.Log.Any(l => l.Contains("BTCUSD CLOSED #"));
            Console.WriteLine("BTC manage-only " + managed + " | closed " + closedByBot + " | new BTC entries " + newBtc);
            if (!managed || newBtc || !closedByBot) j.Violations.Add("old BTC position not handled as manage-only");

            string[] checks =
            {
                "Выходные, бюджет 50 EUR: только крипта, выбор таймфрейма, суточный и субботний перерывы",
                "Открытие недели: форекс ждёт часов 06–20 UTC, потом 0.01 лота",
                "Настоящий счёт 20 EUR: форекс отключён по бюджету, торгует крипта",
                "Пятница, весь счёт, 4 лота: форекс закрывается до выходных",
                "Перезапуск с открытой позицией: новый экземпляр её подхватывает",
                "Дневной лимит убытка: после него ни одного входа",
                "Брокер требует залог 1:1 вместо 1:2: один отказ, без повторов",
                "Минимальный стоп брокера больше стопа бота: вход пропускается",
                "Перезапуск сохраняет бюджет и начало дня",
                "Старая позиция BTC на бюджете 50 EUR: ведётся вне бюджета, новых входов нет"
            };
            string[] notes =
            {
                "", "", "", "",
                "восстановлено позиций: " + e.Log.Count(l => l.Contains("RECOVERED #")),
                "срабатываний лимита: " + f.Log.Count(l => l.Contains("HALT:")) + ", входов после: " + entriesAfterHalt,
                "отказов брокера по ETH: " + g.Log.Count(l => l.Contains("ETHUSD ORDER FAILED")) + ", входов ETH после: " + g.Log.Count(l => l.Contains("ETHUSD ENTRY")),
                "пропусков по стопу брокера: " + h.Log.Count(l => l.Contains("closer than the broker minimum stop")),
                "бюджет " + equityBefore.ToString("F2", CultureInfo.InvariantCulture) + " → " + equityAfter.ToString("F2", CultureInfo.InvariantCulture)
                    + ", начало дня " + dayBefore.ToString("F2", CultureInfo.InvariantCulture) + " → " + dayAfter.ToString("F2", CultureInfo.InvariantCulture),
                "позиция закрыта ботом: " + (closedByBot ? "да" : "нет") + ", новых входов BTC: " + (newBtc ? "есть" : "нет")
            };
            World[] worlds = { a, bw, c, d, e, f, h, g, i9, j };
            // Scenario order in Rows follows the calls: A B C D E F H G I J.
            string[] order = { "A", "B", "C", "D", "E", "F", "H", "G", "I", "J" };
            string[] letters = { "A", "B", "C", "D", "E", "F", "G", "H", "I", "J" };
            for (int k = 0; k < Rows.Count && k < order.Length; k++)
            {
                int idx = Array.IndexOf(letters, order[k]);
                Rows[k].Check = checks[idx];
                Rows[k].Note = notes[idx];
                Rows[k].Ok = Rows[k].Errors == 0 && worlds[k].Violations.Count == 0;
                Rows[k].Violations = worlds[k].Violations.Count;
            }
            WriteReport(Path.Combine(dir, "report.html"));

            Console.WriteLine();
            Console.WriteLine("Unknown API members used by the bot (not mocked):");
            foreach (var kv in Mk.Unknown.OrderByDescending(k => k.Value)) Console.WriteLine("  " + kv.Key + " x" + kv.Value);
            bool ok = new[] { a, bw, c, d, e, f, g, h, i9, j }.All(w => w.Violations.Count == 0 && !w.Log.Any(l => l.Contains("ERROR in") || l.Contains("FATAL")));
            Console.WriteLine(ok ? "SIMULATION OK" : "SIMULATION FOUND PROBLEMS");
            return ok ? 0 : 1;
        }
    }
}
