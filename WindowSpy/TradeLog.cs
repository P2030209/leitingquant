using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WindowSpy
{
    /// <summary>单笔交易记录（开仓→平仓）</summary>
    public class PnlRecord
    {
        public DateTime OpenTime;
        public DateTime? CloseTime;
        public string Name = "";
        public double Qty;
        public double BuyPrice;
        public double? SellPrice;
        public double CurrentPrice;   // 最新市价（用于浮盈计算，不持久化也可）
        public bool Closed;

        public double? Pnl => SellPrice.HasValue
            ? Math.Round((SellPrice.Value - BuyPrice) * Qty, 2)
            : (double?)null;
        public double? PnlPct => BuyPrice > 0 && SellPrice.HasValue
            ? Math.Round((SellPrice.Value - BuyPrice) / BuyPrice * 100, 2)
            : (double?)null;
        public double FloatingPnl => Math.Round((CurrentPrice - BuyPrice) * Qty, 2);
        public double FloatingPnlPct => BuyPrice > 0 ? Math.Round((CurrentPrice - BuyPrice) / BuyPrice * 100, 2) : 0;
    }

    /// <summary>交易统计</summary>
    public class TradeStats
    {
        public int TodayCount;
        public double TodayPnl;
        public int TotalCount;
        public double TotalPnl;
        public double WinRate;
        public double MaxDrawdown;
        public List<PnlRecord> TodayRecords = new();
        // 持仓统计
        public int OpenCount;
        public double TotalCost;
        public double TotalValue;
        public double FloatingPnl;
        public double FloatingPnlPct;
        public List<PnlRecord> OpenPositions = new();
    }

    /// <summary>
    /// 全局交易日志：buy 节点开仓、sell/list_confirm 平仓，持久化到 %LOCALAPPDATA%\leiting\trades.json。
    /// 供收益仪表盘与 AI 复盘日报使用。
    /// </summary>
    public static class TradeLog
    {
        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "leiting");
        private static readonly string File = Path.Combine(Dir, "trades.json");
        private static readonly List<PnlRecord> _records = new();
        private static readonly object _lock = new();

        static TradeLog() { Load(); }

        private static void Load()
        {
            try
            {
                if (!System.IO.File.Exists(File)) return;
                var json = System.IO.File.ReadAllText(File);
                var list = JsonSerializer.Deserialize<List<PnlRecord>>(json, new JsonSerializerOptions { IncludeFields = true });
                if (list != null) { _records.Clear(); _records.AddRange(list); }
            }
            catch { /* 损坏则忽略，下次写入会覆盖 */ }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var json = JsonSerializer.Serialize(_records, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    IncludeFields = true
                });
                System.IO.File.WriteAllText(File, json);
            }
            catch { }
        }

        /// <summary>开仓：买入时记录</summary>
        public static void Open(string name, double qty, double buyPrice)
        {
            if (string.IsNullOrWhiteSpace(name) || qty <= 0 || buyPrice <= 0) return;
            lock (_lock)
            {
                _records.Add(new PnlRecord
                {
                    OpenTime = DateTime.Now,
                    Name = name.Trim(),
                    Qty = qty,
                    BuyPrice = buyPrice
                });
                Save();
            }
        }

        /// <summary>平仓：卖出时按品名匹配最早一笔未平仓记录</summary>
        public static void Close(string name, double sellPrice)
        {
            if (string.IsNullOrWhiteSpace(name) || sellPrice <= 0) return;
            lock (_lock)
            {
                var rec = _records.FirstOrDefault(r =>
                    !r.Closed && string.Equals(r.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (rec == null) return;
                rec.CloseTime = DateTime.Now;
                rec.SellPrice = sellPrice;
                rec.Closed = true;
                Save();
            }
        }

        /// <summary>今日统计（仪表盘用）</summary>
        public static TradeStats Stats()
        {
            lock (_lock)
            {
                var today = DateTime.Today;
                var todayClosed = _records.Where(r => r.Closed && r.CloseTime >= today).ToList();
                var allClosed = _records.Where(r => r.Closed).ToList();

                var stats = new TradeStats
                {
                    TodayCount = todayClosed.Count,
                    TodayPnl = todayClosed.Sum(r => r.Pnl ?? 0),
                    TotalCount = allClosed.Count,
                    TotalPnl = allClosed.Sum(r => r.Pnl ?? 0),
                    TodayRecords = todayClosed
                };

                // 胜率
                int wins = todayClosed.Count(r => (r.Pnl ?? 0) > 0);
                stats.WinRate = todayClosed.Count > 0
                    ? Math.Round((double)wins / todayClosed.Count * 100, 1) : 0;

                // 最大回撤：按平仓时间顺序的累计收益峰值回撤
                double peak = 0, cum = 0, maxDd = 0;
                foreach (var r in allClosed.OrderBy(r => r.CloseTime ?? r.OpenTime))
                {
                    cum += r.Pnl ?? 0;
                    if (cum > peak) peak = cum;
                    double dd = peak - cum;
                    if (dd > maxDd) maxDd = dd;
                }
                stats.MaxDrawdown = Math.Round(maxDd, 2);

                // 持仓统计
                var open = _records.Where(r => !r.Closed).ToList();
                stats.OpenCount = open.Count;
                stats.OpenPositions = open;
                stats.TotalCost = open.Sum(r => r.BuyPrice * r.Qty);
                stats.TotalValue = open.Sum(r => r.CurrentPrice * r.Qty);
                stats.FloatingPnl = open.Sum(r => r.FloatingPnl);
                stats.FloatingPnlPct = stats.TotalCost > 0
                    ? Math.Round(stats.FloatingPnl / stats.TotalCost * 100, 2) : 0;

                return stats;
            }
        }

        /// <summary>用最新行情更新所有未平仓记录的市价（仪表盘浮盈用）</summary>
        public static void UpdatePrices(IEnumerable<MarketQuote> quotes)
        {
            if (quotes == null) return;
            lock (_lock)
            {
                foreach (var r in _records.Where(r => !r.Closed))
                {
                    var q = quotes.FirstOrDefault(x =>
                        string.Equals(x.Name, r.Name, StringComparison.OrdinalIgnoreCase));
                    if (q != null) r.CurrentPrice = q.Price;
                }
            }
        }

        /// <summary>格式化今日交易清单（AI 复盘用）</summary>
        public static string FormatTodayReport()
        {
            var s = Stats();
            if (s.TodayCount == 0) return "今日暂无平仓交易记录。";

            var lines = new List<string>
            {
                $"今日交易 {s.TodayCount} 笔，总盈亏 {s.TodayPnl:+0.00;-0.00} 哈弗币，胜率 {s.WinRate}%。",
                "明细："
            };
            int i = 1;
            foreach (var r in s.TodayRecords.OrderBy(r => r.CloseTime))
            {
                lines.Add($"  {i++}. {r.Name} ×{r.Qty}：买入{r.BuyPrice:0} → 卖出{r.SellPrice:0}，" +
                          $"盈亏 {r.Pnl:+0.00;-0.00}（{r.PnlPct:+0.00;-0.00}%）");
            }
            return string.Join("\n", lines);
        }

        /// <summary>清空所有记录（仪表盘可提供按钮）</summary>
        public static void Clear()
        {
            lock (_lock) { _records.Clear(); Save(); }
        }
    }
}
