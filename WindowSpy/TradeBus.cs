using System;
using System.Collections.Generic;

namespace WindowSpy
{
    public enum HostStatusKind { Idle, Running, Resting, Stopped, BanHit, Error }
    public enum SignalLevel { Info, Buy, Sell, Wait, Claim, Ban, Warn, Error }

    /// <summary>一次行情快照（供 K 线/指标面板）</summary>
    public class MarketTick
    {
        public DateTime Time = DateTime.Now;
        public string Bullet = "";
        public double Price;
        public double ChangePct;
        public bool Holding;                 // 本次快照是否针对持仓子弹
        public List<double> History = new(); // 老→新 K 线数据
        public double MA;
        public int MAPeriod = 20;
        public double RSI;
        public double Position;
        public double ExitEst;
        public double ExpProfitPct;
    }

    /// <summary>一条策略信号/流水（监控台右侧信号栏）</summary>
    public class SignalLog
    {
        public DateTime Time = DateTime.Now;
        public SignalLevel Level = SignalLevel.Info;
        public int Round;
        public string Title = "";
        public string Detail = "";
    }

    /// <summary>一笔成交流水</summary>
    public class TradeRecord
    {
        public DateTime Time = DateTime.Now;
        public bool IsBuy;
        public string Bullet = "";
        public double Price;
        public int Lots;
        public double Amount;            // 成交金额（买=支出/卖=税后到账）
        public double WalletAfter;       // 成交后识别到的哈弗币余额（可为0=未识别）
        public double RealizedPnl;       // 卖出时的已实现盈亏（哈弗币）
        public double ReturnPct;         // 卖出时收益率%
        public string Note = "";
    }

    /// <summary>托管运行状态快照</summary>
    public class HostSnapshot
    {
        public HostStatusKind Status = HostStatusKind.Idle;
        public int Round;
        public double Wallet;
        public bool Holding;
        public string HoldName = "";
        public double HoldPrice;
        public double HoldLots;
        public DateTime HoldTime;
        public double LastPrice;
        public double RealizedPnl;
        public int TradeCount;
        public string Strategy = "SMART";
        public string Detail = "";
    }

    /// <summary>
    /// 托管引擎 → 行情监控台 的事件总线（进程内静态、线程安全）。
    /// 监控台可在任意时刻打开，环形缓冲会回放最近的事件。
    /// </summary>
    public static class TradeBus
    {
        private static readonly object Lock = new();
        private const int MaxSignals = 400, MaxTrades = 300, MaxTicks = 120, MaxEquity = 600;

        private static readonly List<SignalLog> _signals = new();
        private static readonly List<TradeRecord> _trades = new();
        private static readonly List<MarketTick> _ticks = new();
        private static readonly List<(DateTime t, double equity)> _equity = new();

        public static event Action<MarketTick>? Tick;
        public static event Action<SignalLog>? Signal;
        public static event Action<TradeRecord>? Trade;
        public static event Action<HostSnapshot>? Status;

        public static void PublishTick(MarketTick t)
        {
            lock (Lock) { Push(_ticks, t, MaxTicks); }
            try { Tick?.Invoke(t); } catch { }
        }

        public static void PublishSignal(SignalLog s)
        {
            lock (Lock) { Push(_signals, s, MaxSignals); }
            try { Signal?.Invoke(s); } catch { }
        }

        public static void PublishSignal(SignalLevel level, int round, string title, string detail = "")
            => PublishSignal(new SignalLog { Level = level, Round = round, Title = title, Detail = detail });

        public static void PublishTrade(TradeRecord r)
        {
            lock (Lock)
            {
                Push(_trades, r, MaxTrades);
                double equity = r.WalletAfter > 0 ? r.WalletAfter : 0;
                if (equity > 0) Push(_equity, (r.Time, equity), MaxEquity);
            }
            try { Trade?.Invoke(r); } catch { }
        }

        public static void PublishStatus(HostSnapshot s)
        {
            try { Status?.Invoke(s); } catch { }
        }

        public static List<SignalLog> RecentSignals() { lock (Lock) return new List<SignalLog>(_signals); }
        public static List<TradeRecord> RecentTrades() { lock (Lock) return new List<TradeRecord>(_trades); }
        public static List<MarketTick> RecentTicks() { lock (Lock) return new List<MarketTick>(_ticks); }
        public static List<(DateTime t, double equity)> EquityCurve() { lock (Lock) return new List<(DateTime, double)>(_equity); }

        public static void Reset()
        {
            lock (Lock) { _signals.Clear(); _trades.Clear(); _ticks.Clear(); _equity.Clear(); }
        }

        private static void Push<T>(List<T> list, T item, int max)
        {
            list.Add(item);
            if (list.Count > max) list.RemoveRange(0, list.Count - max);
        }
    }
}
