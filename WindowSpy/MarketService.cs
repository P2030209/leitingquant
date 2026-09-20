using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace WindowSpy
{
    /// <summary>一条子弹行情报价</summary>
    public class MarketQuote
    {
        public string Name { get; set; } = "";
        public double Price { get; set; }           // 现价 latest_price
        public double ChangePct { get; set; }       // 相对昨日均价涨跌%
        public double? TodayHigh { get; set; }      // 今日最高
        public double? TodayLow { get; set; }       // 今日最低
        public double? YesterdayAvg { get; set; }   // 昨日均价 (昨日高低中点)
        public double? SevenHigh { get; set; }      // 七日最高
        public double? SevenLow { get; set; }       // 七日最低
        public List<double> History { get; set; } = new();
    }

    /// <summary>
    /// 行情数据源服务：直接使用 moligod 官方明文接口（无需解密、无需自建接口）：
    /// 1) GET {root}/api/market/ammo-catalog → gzip JSON，全量子弹现价/今日/昨日/七日高低
    /// 2) GET {root}/api/market/history?name=X&amp;interval=5m|1h&amp;limit=N&amp;lookup=name → 明文 JSON，时间序列 {time,avg,min,max,last}
    /// 注：/api/market/index 为 AES-GCM 加密（密钥在响应头 x-market-index-key），此处不使用。
    /// </summary>
    public static class MarketService
    {
        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        { Timeout = TimeSpan.FromSeconds(10) };

        /// <summary>规整站点根地址（空默认 moligod；允许用户粘贴完整接口地址，自动截到域名根）</summary>
        public static string NormalizeRoot(string? url)
        {
            var u = (url ?? "").Trim();
            if (u.Length == 0) return "https://moligod.com";
            if (!u.StartsWith("http", StringComparison.OrdinalIgnoreCase)) u = "https://" + u;
            var idx = u.IndexOf("/api/", StringComparison.Ordinal);
            if (idx > 0) u = u.Substring(0, idx);
            return u.TrimEnd('/');
        }

        /// <summary>拉取全量子弹报价（ammo-catalog；响应体为 gzip 但无 Content-Encoding 头，需手动解压）</summary>
        public static List<MarketQuote> FetchCatalog(string? rootUrl)
        {
            var root = NormalizeRoot(rootUrl);
            var json = GetJsonAutoGunzip(root + "/api/market/ammo-catalog");
            return ParseCatalog(json);
        }

        /// <summary>GET 并返回文本；响应体以 gzip 魔数(1F 8B)开头时手动解压（catalog 接口 Content-Type 为 octet-stream，自动解压不生效）</summary>
        private static string GetJsonAutoGunzip(string url)
        {
            var bytes = Http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
            {
                using var ms = new System.IO.MemoryStream(bytes);
                using var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress);
                using var outMs = new System.IO.MemoryStream();
                gz.CopyTo(outMs);
                return System.Text.Encoding.UTF8.GetString(outMs.ToArray());
            }
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        /// <summary>解析 ammo-catalog JSON</summary>
        public static List<MarketQuote> ParseCatalog(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement items;
            if (root.ValueKind == JsonValueKind.Array) items = root;
            else if (root.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array) items = arr;
            else throw new ArgumentException("ammo-catalog 数据中未找到 items 数组");

            var result = new List<MarketQuote>();
            foreach (var it in items.EnumerateArray())
            {
                var name = GetString(it, "name", "display_name", "item_name");
                var price = GetDouble(it, "latest_price", "price", "current_price");
                if (string.IsNullOrEmpty(name) || price == null) continue;

                var q = new MarketQuote
                {
                    Name = name,
                    Price = price.Value,
                    TodayHigh = GetDouble(it, "today_high_price"),
                    TodayLow = GetDouble(it, "today_low_price"),
                    SevenHigh = GetDouble(it, "seven_day_high_price"),
                    SevenLow = GetDouble(it, "seven_day_low_price"),
                };

                var yh = GetDouble(it, "yesterday_high_price");
                var yl = GetDouble(it, "yesterday_low_price");
                if (yh != null && yl != null)
                {
                    q.YesterdayAvg = (yh.Value + yl.Value) / 2.0;
                    // 涨跌% = 现价相对昨日均价
                    if (q.YesterdayAvg.Value > 0.0001)
                        q.ChangePct = (q.Price - q.YesterdayAvg.Value) / q.YesterdayAvg.Value * 100.0;
                }
                result.Add(q);
            }
            return result;
        }

        /// <summary>
        /// 拉取单种子弹历史价格序列（老→新，取每点 last）。
        /// 默认 5 分钟粒度；5m 无数据自动降级 1h。
        /// </summary>
        public static List<double> FetchHistory(string? rootUrl, string bulletName, int limit)
        {
            var root = NormalizeRoot(rootUrl);
            if (limit <= 0) limit = 60;
            var vals = FetchHistoryOnce(root, bulletName, "5m", limit);
            if (vals.Count == 0)
                vals = FetchHistoryOnce(root, bulletName, "1h", Math.Max(6, limit / 12));
            return vals;
        }

        private static List<double> FetchHistoryOnce(string root, string bulletName, string interval, int limit)
        {
            var url = $"{root}/api/market/history?name={Uri.EscapeDataString(bulletName)}&interval={interval}&limit={limit}&lookup=name";
            var json = GetJsonAutoGunzip(url);
            using var doc = JsonDocument.Parse(json);
            var rootEl = doc.RootElement;
            var list = new List<double>();
            if (rootEl.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in data.EnumerateArray())
                {
                    var v = GetDouble(p, "last", "avg", "close", "price");
                    if (v != null) list.Add(v.Value);
                }
            }
            return list; // 接口本身老→新
        }

        /// <summary>按名称模糊匹配（包含，忽略大小写）；名称为空返回第一条</summary>
        public static MarketQuote? Match(List<MarketQuote> quotes, string bulletName)
        {
            if (quotes.Count == 0) return null;
            if (string.IsNullOrWhiteSpace(bulletName)) return quotes[0];
            return quotes.FirstOrDefault(q => q.Name.Contains(bulletName, StringComparison.OrdinalIgnoreCase))
                ?? quotes.FirstOrDefault(q => bulletName.Contains(q.Name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>交易行卖出到手系数（由 moligod 行情页"今日最高(税后)"括号值反推，与网站口径一致）</summary>
        public const double SellFactor = 0.87;

        /// <summary>
        /// 选品候选：除评分外，携带税后预期利润率、预计卖出价、可买份数与预期总利润（哈弗币口径）。
        /// 托管引擎按 ExpectedTotal（预期总利润额）排序，实现"利益最大化"而非只看利润率。
        /// </summary>
        public class Candidate
        {
            public MarketQuote Quote = new();
            public double Score;
            public double ExitEst;          // 保守预计卖出价（七日中值）
            public double ExpProfitPct;     // 税后往返预期利润率 %
            public int Lots;                // 当前预算可买份数
            public double ExpectedTotal;    // 满仓预算下预期总利润（哈弗币）
            public List<string> Reasons = new();
        }

        /// <summary>选品过滤/预算参数</summary>
        public class PickOptions
        {
            public double Budget = 0;            // 单笔预算（哈弗币，0=不按预算过滤/不排序总额）
            public double MinExpProfitPct = 3;   // 最低税后预期利润率%
            public int MaxLots = 999;            // 份数上限
            public HashSet<string>? Blacklist;   // 冷却/止损过的子弹名
        }

        /// <summary>
        /// 预算感知选品：返回按策略排序的候选列表（托管引擎取前 N 个再做历史指标校验）。
        /// 预计卖出价统一用「七日高低中值」(比今日高点更现实)；
        /// SMART 综合策略 = 税后预期利润率 + 低于中值程度 + 区间振幅 的加权分，
        /// 有预算时按预期总利润（份数×单份净利润）排序，即总利润额最大化。
        /// </summary>
        public static List<Candidate> PickTop(List<MarketQuote> quotes, string? strategy, PickOptions? opts = null)
        {
            var st = NormalizeStrategy(strategy);
            var o = opts ?? new PickOptions();
            var list = new List<Candidate>();

            foreach (var q in quotes)
            {
                if (q.Price <= 0.0001) continue;
                if (o.Blacklist != null && o.Blacklist.Contains(q.Name)) continue;
                if (q.SevenHigh == null || q.SevenLow == null) continue;
                if (o.Budget > 0 && q.Price > o.Budget) continue; // 一份都买不起

                double mid7 = (q.SevenHigh.Value + q.SevenLow.Value) / 2.0;
                double exitEst = mid7;
                double netPerLot = exitEst * SellFactor - q.Price;
                double expPct = netPerLot / q.Price * 100.0;

                int lots = o.Budget > 0
                    ? Math.Min(o.MaxLots, (int)Math.Floor(o.Budget / q.Price))
                    : 1;
                if (o.Budget > 0 && lots < 1) continue;

                double dipPct = (mid7 - q.Price) / q.Price * 100.0;
                double rangePct = q.SevenLow.Value > 0.0001
                    ? (q.SevenHigh.Value - q.SevenLow.Value) / q.SevenLow.Value * 100.0
                    : 0;

                double score;
                var reasons = new List<string>();
                switch (st)
                {
                    case "PROFIT":
                        // 与网站"今日高点税后纯利润"口径保持一致
                        exitEst = q.TodayHigh ?? mid7;
                        netPerLot = exitEst * SellFactor - q.Price;
                        expPct = netPerLot / q.Price * 100.0;
                        score = expPct;
                        reasons.Add($"今日高{exitEst:0}税后空间{expPct:0.#}%");
                        break;
                    case "REBOUND":
                        score = dipPct - q.ChangePct;
                        reasons.Add($"超跌 今日{q.ChangePct:0.#}% 低于中值{dipPct:0.#}%");
                        break;
                    case "DIP":
                        score = dipPct;
                        reasons.Add($"低于七日中值 {dipPct:0.#}%");
                        break;
                    case "RANGE":
                        score = rangePct;
                        reasons.Add($"七日振幅 {rangePct:0.#}%");
                        break;
                    case "RISE":
                        score = q.ChangePct;
                        reasons.Add($"今日涨幅 {q.ChangePct:0.#}%");
                        break;
                    default: // SMART 智能综合
                        // 利润率为主(1.0)，折价深度为辅(0.35)，振幅给小权重(0.08，防窄幅假便宜)
                        score = expPct * 1.0 + Math.Max(0, dipPct) * 0.35 + rangePct * 0.08;
                        reasons.Add($"预期{expPct:0.#}% 折价{Math.Max(0, dipPct):0.#}% 振幅{rangePct:0.#}%");
                        break;
                }

                // 预期亏损 / 达不到门槛的直接剔除（RISE/RANGE 原策略不设利润门槛，保留原口径）
                bool needProfitGate = st == "SMART" || st == "PROFIT" || st == "DIP" || st == "REBOUND";
                if (needProfitGate && expPct < o.MinExpProfitPct) continue;

                double expectedTotal = lots * netPerLot;
                if (st == "SMART" && o.Budget > 0)
                    score = expectedTotal; // 利益最大化：按总利润额排序

                list.Add(new Candidate
                {
                    Quote = q, Score = score, ExitEst = exitEst, ExpProfitPct = expPct,
                    Lots = lots, ExpectedTotal = expectedTotal, Reasons = reasons
                });
            }

            list.Sort((a, b) => b.Score.CompareTo(a.Score));
            return list;
        }

        public static string NormalizeStrategy(string? strategy)
        {
            var st = (strategy ?? "SMART").Trim().ToUpperInvariant();
            return st switch
            {
                "PROFIT" or "RISE" or "DIP" or "REBOUND" or "RANGE" or "SMART" => st,
                _ => "SMART"
            };
        }

        /// <summary>
        /// 自动选品：按策略打分（分数越大越值得买），返回得分最高者。对齐 moligod 行情页口径：
        /// PROFIT=税后利润%(今日高点税后-现价，即网站"纯利润"逻辑) / RISE=追涨(现价相对昨日均价涨幅) /
        /// DIP=抄底(现价低于七日均价%，回归空间) / RANGE=空间(七日振幅%) / REBOUND=超跌反弹(七日回归空间+今日跌幅)
        /// ammo-catalog 全量带高低价，所有子弹均可评分。
        /// </summary>
        public static (MarketQuote Quote, double Score)? PickBest(List<MarketQuote> quotes, string? strategy)
        {
            var st = (strategy ?? "RISE").Trim().ToUpperInvariant();
            double? Score(MarketQuote q) => st switch
            {
                // 税后利润%：现价买入、今日高点卖出的税后空间（网站默认排序逻辑）
                "PROFIT" => q.TodayHigh.HasValue && q.Price > 0.0001
                    ? (q.TodayHigh.Value * SellFactor - q.Price) / q.Price * 100.0
                    : (double?)null,
                // 超跌反弹：七日回归空间 + 今日跌幅（跌得狠且低于七日均价越多分越高）
                "REBOUND" => q.SevenHigh.HasValue && q.SevenLow.HasValue && q.Price > 0.0001
                    ? ((q.SevenHigh.Value + q.SevenLow.Value) / 2.0 - q.Price) / q.Price * 100.0 - q.ChangePct
                    : (double?)null,
                "DIP" => q.SevenHigh.HasValue && q.SevenLow.HasValue && q.Price > 0.0001
                    ? ((q.SevenHigh.Value + q.SevenLow.Value) / 2.0 - q.Price) / q.Price * 100.0
                    : (double?)null,
                "RANGE" => q.SevenHigh.HasValue && q.SevenLow.HasValue && q.SevenLow.Value > 0.0001
                    ? (q.SevenHigh.Value - q.SevenLow.Value) / q.SevenLow.Value * 100.0
                    : (double?)null,
                _ => q.ChangePct // RISE
            };

            MarketQuote? best = null;
            double bestScore = double.NegativeInfinity;
            foreach (var q in quotes)
            {
                var s = Score(q);
                if (s == null) continue;
                if (s.Value > bestScore) { bestScore = s.Value; best = q; }
            }
            return best == null ? null : (best, bestScore);
        }

        public static string StrategyLabel(string? strategy) => NormalizeStrategy(strategy) switch
        {
            "PROFIT" => "税后利润",
            "REBOUND" => "超跌反弹",
            "DIP" => "抄底",
            "RANGE" => "空间",
            "SMART" => "智能综合",
            _ => "追涨"
        };

        private static string? GetString(JsonElement el, params string[] keys)
        {
            foreach (var k in keys)
            {
                if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
            }
            return null;
        }

        private static double? GetDouble(JsonElement el, params string[] keys)
        {
            foreach (var k in keys)
            {
                if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(k, out var v)) continue;
                if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
                if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s)) return s;
            }
            return null;
        }
    }
}
