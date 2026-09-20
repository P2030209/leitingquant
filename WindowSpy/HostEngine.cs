using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WindowSpy
{
    /// <summary>
    /// 引擎与主窗口之间的操作接口：截图 OCR / 相对坐标点击 / 文本输入都由 MainWindow 实现。
    /// 所有方法允许在后台线程被调用。
    /// </summary>
    public interface IHostAdapter
    {
        /// <summary>窗口相对区域 OCR；numbersOnly=true 时只保留数字</summary>
        string OcrRegion(Rectangle relRect, bool numbersOnly);
        /// <summary>全窗口 OCR，返回拼接文本（封禁检测/邮件识别用）</summary>
        string OcrScreen();
        /// <summary>点击窗口相对坐标，dwellMs 按下停留，jitter 随机偏移像素</summary>
        void ClickRel(int x, int y, int dwellMs, int jitter);
        /// <summary>仅移动鼠标到窗口相对坐标（不点击）</summary>
        void MoveRel(int x, int y);
        /// <summary>鼠标按键按下/弹起：button 0=左键 1=右键 2=中键（先移动到坐标）</summary>
        void MouseButtonRel(int button, bool down, int x, int y);
        /// <summary>在当前位置滚轮：delta 正=上滚（120 的倍数），负=下滚</summary>
        void Wheel(int delta);
        /// <summary>向当前焦点框粘贴文本（自动 Ctrl+A 覆盖）</summary>
        void PasteText(string text);
        /// <summary>按一个虚拟键（如 Esc）</summary>
        void TapKey(byte vk, int dwellMs = 40);
        /// <summary>按下/弹起一个虚拟键（组合键用）</summary>
        void KeyDown(byte vk);
        void KeyUp(byte vk);
        /// <summary>游戏窗口置顶并还原</summary>
        void BringToFront();
        /// <summary>切换后续所有操作/抓屏的目标窗口：'A'=窗口A（默认），'B'=窗口B</summary>
        void SetTarget(char target);
        /// <summary>指定目标窗口是否已绑定（'A' 或 'B'）</summary>
        bool HasTarget(char target);
        /// <summary>
        /// 在窗口截图中查找模板图片。searchRegion 为窗口相对区域（Empty=全窗口）。
        /// 命中(相似度≥threshold)时 relX/relY 为模板中心窗口相对坐标。
        /// </summary>
        bool FindImage(string templatePath, Rectangle searchRegion, double threshold,
            out int relX, out int relY, out double score);
    }

    /// <summary>托管引擎全部参数（由自动倒卖 Tab 收集，可序列化为 wzconfig.json）</summary>
    public class HostConfig
    {
        // —— 行情与策略 ——
        public string MarketUrl = "";
        public string Strategy = "SMART";
        public int HistoryLimit = 60;
        public int MaPeriod = 20;
        public int RsiPeriod = 14;
        public double BuyPremiumPct = 2;     // 现价低于 MA 该百分比才允许买
        public double TakeProfitPct = 10;    // 价格涨幅止盈%
        public double StopLossPct;           // 价格跌幅止损%（0=不止损）
        public double TrailingPct;           // 移动止盈：自最高点回撤%（0=关闭）
        public int MaxHoldRounds;            // 持仓最多多少轮强平（0=不限）
        public int IntervalSec = 60;         // 空仓轮询秒
        public int HoldIntervalSec = 20;     // 持仓时盯盘秒（极速卖出）
        public double MinExpProfitPct = 3;   // 最低税后预期利润率%
        public double RsiBuyMax = 70;        // RSI 高于该值不买（防追在顶部）
        public double PosBuyMax = 95;        // 区间位置高于该值不买
        public int CandidateCount = 6;       // 预选前 N 名做历史指标校验
        public bool UseAi;
        public string AiPrompt = "";
        public string AiModel = "deepseek-chat";

        // —— 钱包与仓位 ——
        public bool WalletEnabled;
        public Rectangle WalletRect;                 // 窗口相对
        public bool WalletNumbersOnly = true;
        public double BudgetPct = 50;                // 单笔动用识别余额的 %
        public double ReserveHaff;                   // 固定预留哈弗币
        public int MaxLots = 999;

        // —— 买入数量 +/− 自动调整 ——
        public bool QtyEnabled;
        public Point QtyPlus;
        public Point QtyMinus;
        public int QtyResetClicks = 20;              // 买入前先点 − 复位次数
        public int QtyClickDelayMs = 60;

        // —— 邮件哈弗币领取 ——
        public bool MailEnabled;
        public Point MailBtn;
        public Point ClaimBtn;
        public Point CloseBtn;                       // 无坐标则按 Esc
        public string MailKeywords = "领取,哈弗币,附件";
        public bool MailOcrGate = true;              // true=OCR 看到关键词才点领取

        // —— 重新选中子弹（搜索） ——
        public bool SelectEnabled;
        public Point SearchBox;
        public Point SearchResult;
        public int SearchSettleMs = 400;

        // —— 交易坐标 ——
        public Point TradeEntry;                     // 大厅「交易行」入口（可选，负坐标=不点击）
        public Point BuyPoint;
        public Point SellPoint;
        public int ClickDwellMs = 80;
        public int JitterPx = 3;

        // —— 风控 ——
        public bool BanCheck = true;
        public string BanKeywords = "";
        public int BanEveryRounds = 1;
        public int MaxRunMin = 90;
        public int RestMin = 15;
        public int StopCooldownRounds = 30;          // 止损子弹冷却轮数

        // —— 速度 ——
        public bool Turbo;                           // 极速模式：并行拉取/短等待/少偏移/持仓快轮询
    }

    /// <summary>持久化持仓状态（软件重启不丢仓）</summary>
    public class HostState
    {
        public bool Holding;
        public string HoldName = "";
        public double HoldPrice;
        public int HoldLots;
        public DateTime HoldTime;
        public int HoldRounds;
        public double PeakPrice;
        public double RealizedPnl;
        public int TradeCount;
        public Dictionary<string, int> Cooldown = new(); // 子弹名 → 剩余冷却轮数
    }

    /// <summary>
    /// 原生托管引擎：邮件领取 → 余额识别 → 持仓状态机（选品/买入/止盈止损/卖出）→ 风控节律。
    /// 修复旧版模板队列"买入价每轮被覆盖、止盈永不触发、无持仓概念"的问题。
    /// </summary>
    public class HostEngine
    {
        private readonly IHostAdapter _ui;
        private readonly HostConfig _cfg;
        private readonly Action<string> _log;
        private volatile bool _stop;
        private Task? _task;

        private HostState _state = new();
        private string StatePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "host_state.json");
        private int _round;
        private DateTime _runStart = DateTime.Now;

        public bool IsRunning => _task != null && !_task.IsCompleted;
        public HostState State => _state;

        public HostEngine(IHostAdapter ui, HostConfig cfg, Action<string> log)
        {
            _ui = ui;
            _cfg = cfg;
            _log = log;
        }

        public void Start()
        {
            if (IsRunning) return;
            LoadState();
            _stop = false;
            _runStart = DateTime.Now;
            _round = 0;
            _task = Task.Run(RunLoop);
        }

        public void Stop() => _stop = true;

        // ================= 主循环 =================
        private void RunLoop()
        {
            Publish(HostStatusKind.Running, _state.Holding ? $"持仓中：{_state.HoldName}" : "空仓待命");
            _log($"===== 托管引擎启动 · 策略[{MarketService.StrategyLabel(_cfg.Strategy)}]{(_cfg.Turbo ? " · 极速模式" : "")} · {(_state.Holding ? $"恢复持仓 {_state.HoldName} {_state.HoldLots}份@{_state.HoldPrice:0.##}" : "空仓")} =====");

            try
            {
                while (!_stop)
                {
                    _round++;
                    // 连跑上限 → 强制休息
                    if (_cfg.MaxRunMin > 0 && _cfg.RestMin > 0 &&
                        (DateTime.Now - _runStart).TotalMinutes >= _cfg.MaxRunMin)
                    {
                        Publish(HostStatusKind.Resting, $"连跑{_cfg.MaxRunMin}分钟，休息{_cfg.RestMin}分钟");
                        _log($"节律：已连跑{_cfg.MaxRunMin}分钟，强制休息{_cfg.RestMin}分钟");
                        Signal(SignalLevel.Wait, "进入休息节律", $"连跑上限{_cfg.MaxRunMin}分，休息{_cfg.RestMin}分");
                        SleepTicks(_cfg.RestMin * 60);
                        _runStart = DateTime.Now;
                        Publish(HostStatusKind.Running, "休息结束，恢复托管");
                        _log("节律：休息结束，恢复托管");
                    }

                    try { Round(); }
                    catch (Exception ex)
                    {
                        _log($"[第{_round}轮] 异常(已跳过本轮): {ex.Message}");
                        Signal(SignalLevel.Warn, $"第{_round}轮异常", ex.Message);
                    }

                    int wait = _state.Holding && _cfg.HoldIntervalSec > 0
                        ? Math.Min(_cfg.IntervalSec, _cfg.Turbo ? Math.Max(5, _cfg.HoldIntervalSec / 2) : _cfg.HoldIntervalSec)
                        : _cfg.IntervalSec;
                    SleepTicks(wait);
                }
            }
            finally
            {
                SaveState();
                Publish(HostStatusKind.Stopped, "已停止（持仓状态已保存，下次启动自动恢复）");
                _log("===== 托管已停止（持仓状态已保存）=====");
            }
        }

        private void Round()
        {
            // 冷却递减
            if (_state.Cooldown.Count > 0)
            {
                foreach (var k in _state.Cooldown.Keys.ToList())
                {
                    _state.Cooldown[k]--;
                    if (_state.Cooldown[k] <= 0) _state.Cooldown.Remove(k);
                }
            }

            // ① 封禁检测（极速模式可降频）
            if (_cfg.BanCheck && _cfg.BanEveryRounds > 0 && _round % _cfg.BanEveryRounds == 0)
            {
                if (BanScan()) return; // 命中已急停
            }

            // ② 邮件领取哈弗币
            if (_cfg.MailEnabled) TryClaimMail();

            // ②.5 确保进入交易行（配置了入口坐标时；已在交易行再点一次通常无害）
            if (_cfg.TradeEntry.X >= 0 && _cfg.TradeEntry.Y >= 0)
            {
                Click(_cfg.TradeEntry, "交易行入口");
                Settle();
            }

            // ③ 钱包余额
            double wallet = 0;
            if (_cfg.WalletEnabled)
            {
                wallet = ReadWallet();
                Signal(SignalLevel.Info, $"余额 {wallet:0,0}", $"第{_round}轮 OCR 识别哈弗币");
            }
            Publish(HostStatusKind.Running, "", wallet);

            // ④ 行情
            var quotes = MarketService.FetchCatalog(_cfg.MarketUrl);
            if (quotes.Count == 0) { _log($"[第{_round}轮] 行情0条，跳过"); return; }

            if (_state.Holding) RoundHolding(quotes, wallet);
            else RoundScout(quotes, wallet);

            SaveState();
            Publish(HostStatusKind.Running, "", wallet);
        }

        // ================= 持仓轮：盯卖出 =================
        private void RoundHolding(List<MarketQuote> quotes, double wallet)
        {
            var q = MarketService.Match(quotes, _state.HoldName);
            if (q == null)
            {
                _log($"[第{_round}轮·持仓] 行情中未找到「{_state.HoldName}」，继续持有等待");
                return;
            }
            _state.HoldRounds++;
            if (q.Price > _state.PeakPrice) _state.PeakPrice = q.Price;

            var hist = SafeHistory(q.Name);
            double ma = QuantMath.MA(hist, _cfg.MaPeriod);
            double rsi = QuantMath.RSI(hist, _cfg.RsiPeriod);
            double pos = QuantMath.Position(hist, _cfg.MaPeriod);
            double retPct = (q.Price - _state.HoldPrice) / _state.HoldPrice * 100.0;
            double netPnl = (q.Price * MarketService.SellFactor - _state.HoldPrice) * _state.HoldLots;

            EmitTick(q, hist, ma, rsi, pos, true);

            // 卖出判定
            string? reason = null;
            if (_cfg.TakeProfitPct > 0 && retPct >= _cfg.TakeProfitPct)
                reason = $"达到止盈 +{retPct:0.#}%≥{_cfg.TakeProfitPct:0.#}%";
            else if (_cfg.StopLossPct > 0 && retPct <= -Math.Abs(_cfg.StopLossPct))
                reason = $"触发止损 {retPct:0.#}%≤-{Math.Abs(_cfg.StopLossPct):0.#}%";
            else if (_cfg.TrailingPct > 0 && _state.PeakPrice > _state.HoldPrice)
            {
                double draw = (_state.PeakPrice - q.Price) / _state.PeakPrice * 100.0;
                if (draw >= _cfg.TrailingPct && retPct > 0)
                    reason = $"移动止盈 高点回撤{draw:0.#}%≥{_cfg.TrailingPct:0.#}%（仍盈利{retPct:0.#}%）";
            }
            else if (_cfg.MaxHoldRounds > 0 && _state.HoldRounds >= _cfg.MaxHoldRounds)
                reason = $"持仓超时 {_state.HoldRounds}轮";

            // AI 卖出意见（可选）
            if (reason == null && _cfg.UseAi)
            {
                var ai = AskAi(new Dictionary<string, string>
                {
                    ["PickName"] = q.Name, ["Price"] = q.Price.ToString("0.##"),
                    ["BuyPrice"] = _state.HoldPrice.ToString("0.##"),
                    ["ProfitPct"] = retPct.ToString("0.#"),
                    ["Ma20"] = ma.ToString("0.##"), ["Rsi14"] = rsi.ToString("0.#"),
                    ["Pos20"] = pos.ToString("0.#"), ["Change"] = q.ChangePct.ToString("0.#"),
                    ["ExitEst"] = q.Price.ToString("0.##"),
                }, sellMode: true);
                if (ai == "SELL") reason = $"AI 建议卖出（浮盈{retPct:0.#}% RSI{rsi:0}）";
            }

            _log($"[第{_round}轮·持仓] {q.Name} 成本{_state.HoldPrice:0.##} 现价{q.Price:0.##} 浮盈{retPct:+0.#;-0.#}% 税后浮盈{netPnl:+0,0;-0,0} RSI{rsi:0} 位{pos:0}% → {(reason ?? "继续持有")}");

            if (reason == null)
            {
                Signal(SignalLevel.Wait, $"持有 {q.Name}", $"{retPct:+0.#;-0.#}% RSI{rsi:0} 未触止盈止损");
                return;
            }

            // —— 执行卖出：重新选中 → 点卖出 ——
            if (_cfg.SelectEnabled) SelectBullet(q.Name);
            Click(_cfg.SellPoint, "卖出");
            Settle();

            double after = _cfg.WalletEnabled ? ReadWallet() : 0;
            _state.RealizedPnl += netPnl;
            _state.TradeCount++;
            bool stopped = reason.Contains("止损");
            TradeBus.PublishTrade(new TradeRecord
            {
                IsBuy = false, Bullet = q.Name, Price = q.Price, Lots = _state.HoldLots,
                Amount = q.Price * MarketService.SellFactor * _state.HoldLots,
                WalletAfter = after, RealizedPnl = netPnl, ReturnPct = retPct, Note = reason
            });
            Signal(stopped ? SignalLevel.Warn : SignalLevel.Sell, $"卖出 {q.Name}",
                $"{_state.HoldLots}份@{q.Price:0.##} {reason} 税后{netPnl:+0,0;-0,0}哈弗币");
            _log($"  ↳ 卖出成交：{q.Name} {_state.HoldLots}份@{q.Price:0.##}，{reason}，税后盈亏 {netPnl:+0,0;-0,0}，累计 {_state.RealizedPnl:+0,0;-0,0}");

            if (stopped) _state.Cooldown[q.Name] = Math.Max(1, _cfg.StopCooldownRounds);
            _state.Holding = false; _state.HoldName = ""; _state.HoldLots = 0;
            _state.HoldPrice = 0; _state.HoldRounds = 0; _state.PeakPrice = 0; _state.HoldTime = default;
        }

        // ================= 空仓轮：选品买入 =================
        private void RoundScout(List<MarketQuote> quotes, double wallet)
        {
            double budget = wallet > 0
                ? Math.Max(0, (wallet - _cfg.ReserveHaff) * _cfg.BudgetPct / 100.0)
                : 0;
            if (wallet > 0 && budget < 1)
            {
                _log($"[第{_round}轮·空仓] 余额{wallet:0,0} 预留{_cfg.ReserveHaff:0,0} 动用{_cfg.BudgetPct:0.#}% 后预算不足，跳过");
                Signal(SignalLevel.Wait, "预算不足", $"余额{wallet:0,0}，可动用预算<1");
                return;
            }

            var cands = MarketService.PickTop(quotes, _cfg.Strategy, new MarketService.PickOptions
            {
                Budget = budget,
                MinExpProfitPct = _cfg.MinExpProfitPct,
                MaxLots = _cfg.MaxLots,
                Blacklist = _state.Cooldown.Keys.ToHashSet()
            });

            if (cands.Count == 0)
            {
                _log($"[第{_round}轮·空仓] 余额{wallet:0,0}，{quotes.Count}条报价中无满足门槛(预期利润≥{_cfg.MinExpProfitPct:0.#}%)的子弹，等待");
                Signal(SignalLevel.Wait, "无合格标的", $"{quotes.Count}条均未达预期利润门槛");
                return;
            }

            // 前 N 名并行拉历史 → 指标校验
            var top = cands.Take(_cfg.CandidateCount).ToList();
            var enriched = new List<(MarketService.Candidate c, List<double> hist, double ma, double rsi, double pos)>();
            var histMap = new Dictionary<string, List<double>>();
            Parallel.ForEach(top, () => new Dictionary<string, List<double>>(), (cand, _, local) =>
            {
                try { local[cand.Quote.Name] = SafeHistory(cand.Quote.Name); } catch { local[cand.Quote.Name] = new List<double> { cand.Quote.Price }; }
                return local;
            }, local => { lock (histMap) foreach (var kv in local) histMap[kv.Key] = kv.Value; });

            MarketService.Candidate? chosen = null;
            string chosenWhy = "";
            foreach (var cand in top)
            {
                var hist = histMap.TryGetValue(cand.Quote.Name, out var h) && h.Count > 1
                    ? h : new List<double> { cand.Quote.Price, cand.Quote.Price };
                double ma = QuantMath.MA(hist, _cfg.MaPeriod);
                double rsi = QuantMath.RSI(hist, _cfg.RsiPeriod);
                double pos = QuantMath.Position(hist, _cfg.MaPeriod);

                if (cand.Quote.Price >= ma * (1 + _cfg.BuyPremiumPct / 100.0))
                { Tag(cand, hist, ma, rsi, pos, quotes.Count, wallet); continue; }
                if (rsi >= _cfg.RsiBuyMax) { Tag(cand, hist, ma, rsi, pos, quotes.Count, wallet); continue; }
                if (pos >= _cfg.PosBuyMax) { Tag(cand, hist, ma, rsi, pos, quotes.Count, wallet); continue; }

                // AI 确认
                if (_cfg.UseAi)
                {
                    var ai = AskAi(new Dictionary<string, string>
                    {
                        ["PickName"] = cand.Quote.Name, ["Price"] = cand.Quote.Price.ToString("0.##"),
                        ["Change"] = cand.Quote.ChangePct.ToString("0.#"),
                        ["Ma20"] = ma.ToString("0.##"), ["Rsi14"] = rsi.ToString("0.#"),
                        ["Pos20"] = pos.ToString("0.#"), ["ExitEst"] = cand.ExitEst.ToString("0.##"),
                        ["Min20"] = QuantMath.Min(hist, _cfg.MaPeriod).ToString("0.##"),
                        ["Max20"] = QuantMath.Max(hist, _cfg.MaPeriod).ToString("0.##"),
                    }, sellMode: false);
                    if (ai != "BUY")
                    {
                        _log($"  淘汰 {cand.Quote.Name}：AI 回复「{ai}」");
                        Tag(cand, hist, ma, rsi, pos, quotes.Count, wallet);
                        continue;
                    }
                }

                chosen = cand;
                enriched.Add((cand, hist, ma, rsi, pos));
                chosenWhy = $"现价<MA{cand.Quote.Price:0.##}<{ma:0.##} RSI{rsi:0} 位{pos:0}% 预期+{cand.ExpProfitPct:0.#}%";
                EmitTick(cand.Quote, hist, ma, rsi, pos, false, cand.ExitEst, cand.ExpProfitPct);
                break;
            }

            if (chosen == null)
            {
                var best = top[0];
                var bh = histMap.TryGetValue(best.Quote.Name, out var bhh) ? bhh : new List<double> { best.Quote.Price };
                EmitTick(best.Quote, bh, QuantMath.MA(bh, _cfg.MaPeriod), QuantMath.RSI(bh, _cfg.RsiPeriod),
                    QuantMath.Position(bh, _cfg.MaPeriod), false, best.ExitEst, best.ExpProfitPct);
                _log($"[第{_round}轮·空仓] 评估{quotes.Count}条/前{top.Count}名，最优 {best.Quote.Name} 预期+{best.ExpProfitPct:0.#}% 但未过买入线(溢价/RSI/位置)，等待");
                Signal(SignalLevel.Wait, "无买点", $"最优 {best.Quote.Name} 未达买入条件");
                return;
            }

            var pick = chosen!;
            int lots = wallet > 0 ? pick.Lots : _cfg.MaxLots;
            lots = Math.Max(1, Math.Min(lots, _cfg.MaxLots));

            _log($"[第{_round}轮·空仓] 评估{quotes.Count}条 → 选中 {pick.Quote.Name} 现价{pick.Quote.Price:0.##} {chosenWhy} 可买{lots}份 预期总利{pick.ExpectedTotal:+0,0}");

            // —— 执行买入：选中子弹 → 调份数 → 点买入 ——
            if (_cfg.SelectEnabled) SelectBullet(pick.Quote.Name);
            if (_cfg.QtyEnabled) AdjustLots(lots);
            Click(_cfg.BuyPoint, "买入");
            Settle();

            _state.Holding = true;
            _state.HoldName = pick.Quote.Name;
            _state.HoldPrice = pick.Quote.Price;
            _state.HoldLots = lots;
            _state.HoldTime = DateTime.Now;
            _state.HoldRounds = 0;
            _state.PeakPrice = pick.Quote.Price;
            _state.TradeCount++;

            double afterBuy = _cfg.WalletEnabled ? ReadWallet() : 0;
            TradeBus.PublishTrade(new TradeRecord
            {
                IsBuy = true, Bullet = pick.Quote.Name, Price = pick.Quote.Price, Lots = lots,
                Amount = pick.Quote.Price * lots, WalletAfter = afterBuy, Note = chosenWhy
            });
            Signal(SignalLevel.Buy, $"买入 {pick.Quote.Name}", $"{lots}份@{pick.Quote.Price:0.##} 支出{pick.Quote.Price * lots:0,0} {chosenWhy}");
            _log($"  ↳ 买入成交：{pick.Quote.Name} {lots}份@{pick.Quote.Price:0.##}，支出 {pick.Quote.Price * lots:0,0}，进入持仓盯盘（止盈{_cfg.TakeProfitPct:0.#}% 止损{_cfg.StopLossPct:0.#}%）");
        }

        private void Tag(MarketService.Candidate c, List<double> hist, double ma, double rsi, double pos, int total, double wallet)
        {
            _log($"  淘汰 {c.Quote.Name}：现价{c.Quote.Price:0.##} MA{ma:0.##} RSI{rsi:0} 位{pos:0}% 预期+{c.ExpProfitPct:0.#}%");
        }

        // ================= 子动作 =================
        private bool BanScan()
        {
            try
            {
                var text = _ui.OcrScreen();
                var kwText = string.IsNullOrWhiteSpace(_cfg.BanKeywords)
                    ? "封禁,封号,封停,冻结,异常,违规,惩罚,限制,警告"
                    : _cfg.BanKeywords;
                var kws = kwText.Split(new[] { ',', '，', ';', '；', '|' }, StringSplitOptions.RemoveEmptyEntries);
                var hit = kws.FirstOrDefault(k => text.Contains(k.Trim()));
                if (hit != null)
                {
                    _log($"!!! 封禁检测命中「{hit.Trim()}」→ 已急停，请立即人工确认");
                    Signal(SignalLevel.Ban, "封禁关键词急停", $"画面含「{hit.Trim()}」");
                    Publish(HostStatusKind.BanHit, $"命中关键词 {hit.Trim()}");
                    _stop = true;
                    return true;
                }
            }
            catch (Exception ex) { _log($"封禁检测失败: {ex.Message}"); }
            return false;
        }

        private void TryClaimMail()
        {
            try
            {
                _ui.BringToFront();
                Click(_cfg.MailBtn, "邮箱");
                Thread.Sleep(_cfg.Turbo ? 250 : 600);

                bool has;
                if (_cfg.MailOcrGate)
                {
                    var text = _ui.OcrScreen();
                    var kws = _cfg.MailKeywords.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries);
                    has = kws.Any(k => text.Contains(k.Trim()));
                }
                else has = true;

                if (!has)
                {
                    CloseMail();
                    return;
                }

                Click(_cfg.ClaimBtn, "一键领取");
                Settle();
                _log("邮件：检测到可领取哈弗币，已点「一键领取」，继续交易");
                Signal(SignalLevel.Claim, "领取邮件哈弗币", "检测到可领取附件并点击");
                Thread.Sleep(_cfg.Turbo ? 200 : 500);
                CloseMail();
            }
            catch (Exception ex)
            {
                _log($"邮件领取失败(不影响交易): {ex.Message}");
            }
        }

        private void CloseMail()
        {
            if (_cfg.CloseBtn.X > 0 || _cfg.CloseBtn.Y > 0) Click(_cfg.CloseBtn, "关闭邮箱");
            else _ui.TapKey(0x1B, 40);
        }

        private double ReadWallet()
        {
            try
            {
                // 取原文：余额可能是 4,002K / 1.2M / 3万 这类带单位缩写，纯数字过滤会丢单位导致小1000倍
                var txt = _ui.OcrRegion(_cfg.WalletRect, false) ?? "";
                double best = 0;
                // 数字(可带千分位/小数) + 可选 K/M/万 单位
                var matches = System.Text.RegularExpressions.Regex.Matches(
                    txt, @"(\d{1,3}(?:[,，]\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?)\s*([KkMm萬万]?)");
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    var num = m.Groups[1].Value.Replace(",", "").Replace("，", "");
                    if (!double.TryParse(num, out double v)) continue;
                    var u = m.Groups[2].Value;
                    if (u == "K" || u == "k") v *= 1_000;
                    else if (u == "M" || u == "m") v *= 1_000_000;
                    else if (u == "万" || u == "萬") v *= 10_000;
                    if (v > best) best = v;
                }
                if (best <= 0) _log($"钱包：OCR 未识别到余额（原文「{txt.Trim()}」，检查识别区域）");
                return best;
            }
            catch (Exception ex)
            {
                _log($"钱包识别失败: {ex.Message}");
                return 0;
            }
        }

        private void SelectBullet(string name)
        {
            Click(_cfg.SearchBox, "搜索框");
            Thread.Sleep(_cfg.Turbo ? 120 : 260);
            _ui.PasteText(name);
            Thread.Sleep(_cfg.Turbo ? 150 : _cfg.SearchSettleMs);
            Click(_cfg.SearchResult, "搜索结果");
            Thread.Sleep(_cfg.Turbo ? 200 : 500);
        }

        private void AdjustLots(int target)
        {
            // 先连点 − 复位到 1（或0），再点 + 到目标份数
            for (int i = 0; i < _cfg.QtyResetClicks; i++)
            {
                Click(_cfg.QtyMinus, null, clickDwell: 15, jitter: 0);
                Thread.Sleep(_cfg.QtyClickDelayMs / (_cfg.Turbo ? 2 : 1));
            }
            for (int i = 1; i < target; i++)
            {
                Click(_cfg.QtyPlus, null, clickDwell: 15, jitter: 0);
                Thread.Sleep(_cfg.QtyClickDelayMs / (_cfg.Turbo ? 2 : 1));
            }
            _log($"份数调整：−复位{_cfg.QtyResetClicks}次，+{Math.Max(0, target - 1)}次 → {target}份");
        }

        private List<double> SafeHistory(string name)
        {
            try
            {
                var h = MarketService.FetchHistory(_cfg.MarketUrl, name, _cfg.HistoryLimit);
                if (h.Count == 0) h = new List<double>();
                return h;
            }
            catch { return new List<double>(); }
        }

        private string AskAi(Dictionary<string, string> vars, bool sellMode)
        {
            try
            {
                var prompt = _cfg.AiPrompt;
                if (string.IsNullOrWhiteSpace(prompt))
                    prompt = sellMode
                        ? "三角洲行动子弹交易。持有{PickName}，成本{BuyPrice}，现价{Price}，浮盈{ProfitPct}%，RSI14={Rsi14}，区间位置{Pos20}%。只回答SELL或HOLD。"
                        : "三角洲行动子弹倒卖。子弹{PickName}，现价{Price}，MA20={Ma20}，RSI14={Rsi14}，今日{Change}%，位置{Pos20}%。只回答BUY或WAIT。";
                foreach (var kv in vars.OrderByDescending(k => k.Key.Length))
                    prompt = prompt.Replace("{" + kv.Key + "}", kv.Value);
                Signal(SignalLevel.Info, "AI 决策中", sellMode ? "询问卖出意见" : "询问买入意见");
                var raw = DeepSeekClient.Ask(prompt, string.IsNullOrWhiteSpace(_cfg.AiModel) ? null : _cfg.AiModel);
                var ans = (raw.Split('\n')[0] ?? "").Trim().ToUpperInvariant();
                _log($"  AI 回复：{ans}");
                if (ans.Contains("BUY")) return "BUY";
                if (ans.Contains("SELL")) return "SELL";
                if (ans.Contains("HOLD") || ans.Contains("WAIT")) return sellMode ? "HOLD" : "WAIT";
                return ans;
            }
            catch (Exception ex)
            {
                _log($"  AI 调用失败(按不确认处理): {ex.Message}");
                return sellMode ? "HOLD" : "WAIT";
            }
        }

        private void EmitTick(MarketQuote q, List<double> hist, double ma, double rsi, double pos,
            bool holding, double exitEst = 0, double expPct = 0)
        {
            var series = hist == null || hist.Count == 0
                ? new List<double> { q.Price }
                : new List<double>(hist);
            if (series.Count == 0 || Math.Abs(series[series.Count - 1] - q.Price) > 0.001)
                series.Add(q.Price);
            TradeBus.PublishTick(new MarketTick
            {
                Bullet = q.Name, Price = q.Price, ChangePct = q.ChangePct,
                Holding = holding, History = series,
                MA = ma, MAPeriod = _cfg.MaPeriod, RSI = rsi, Position = pos,
                ExitEst = exitEst, ExpProfitPct = expPct
            });
        }

        private void Click(Point p, string? tag, int? clickDwell = null, int? jitter = null)
        {
            // 窗口坐标合法性：0,0 也可能合法，未配置的点用负坐标表示
            if (p.X < 0 || p.Y < 0) throw new InvalidOperationException($"{tag ?? "点击"}坐标未配置");
            _ui.BringToFront();
            _ui.ClickRel(p.X, p.Y,
                clickDwell ?? (_cfg.Turbo ? Math.Min(30, _cfg.ClickDwellMs) : _cfg.ClickDwellMs),
                jitter ?? (_cfg.Turbo ? Math.Min(1, _cfg.JitterPx) : _cfg.JitterPx));
        }

        /// <summary>点击后界面稳定等待</summary>
        private void Settle() => Thread.Sleep(_cfg.Turbo ? 120 : 350);

        private void Signal(SignalLevel lv, string title, string detail)
            => TradeBus.PublishSignal(lv, _round, title, detail);

        private void Publish(HostStatusKind st, string detail, double wallet = -1)
        {
            TradeBus.PublishStatus(new HostSnapshot
            {
                Status = st, Round = _round,
                Wallet = wallet >= 0 ? wallet : 0,
                Holding = _state.Holding, HoldName = _state.HoldName,
                HoldPrice = _state.HoldPrice, HoldLots = _state.HoldLots,
                HoldTime = _state.HoldTime, RealizedPnl = _state.RealizedPnl,
                TradeCount = _state.TradeCount, Strategy = _cfg.Strategy, Detail = detail
            });
        }

        private void SleepTicks(int seconds)
        {
            int ms = Math.Max(1, seconds * 1000);
            int step = 250;
            while (ms > 0 && !_stop) { int s = Math.Min(step, ms); Thread.Sleep(s); ms -= s; }
        }

        // ================= 状态持久化 =================
        private void LoadState()
        {
            try
            {
                if (!File.Exists(StatePath)) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
                var r = doc.RootElement;
                _state = new HostState
                {
                    Holding = r.TryGetProperty("holding", out var h) && h.GetBoolean(),
                    HoldName = r.TryGetProperty("holdName", out var n) ? n.GetString() ?? "" : "",
                    HoldPrice = r.TryGetProperty("holdPrice", out var hp) ? hp.GetDouble() : 0,
                    HoldLots = r.TryGetProperty("holdLots", out var hl) ? hl.GetInt32() : 0,
                    HoldTime = r.TryGetProperty("holdTime", out var ht) && ht.TryGetDateTime(out var htd) ? htd : default,
                    HoldRounds = r.TryGetProperty("holdRounds", out var hr) ? hr.GetInt32() : 0,
                    PeakPrice = r.TryGetProperty("peakPrice", out var pp) ? pp.GetDouble() : 0,
                    RealizedPnl = r.TryGetProperty("realizedPnl", out var rp) ? rp.GetDouble() : 0,
                    TradeCount = r.TryGetProperty("tradeCount", out var tc) ? tc.GetInt32() : 0,
                };
                if (r.TryGetProperty("cooldown", out var cd) && cd.ValueKind == JsonValueKind.Object)
                    foreach (var p in cd.EnumerateObject())
                        if (p.Value.TryGetInt32(out var v)) _state.Cooldown[p.Name] = v;
            }
            catch { _state = new HostState(); }
        }

        private void SaveState()
        {
            try
            {
                var json = JsonSerializer.Serialize(new
                {
                    holding = _state.Holding,
                    holdName = _state.HoldName,
                    holdPrice = _state.HoldPrice,
                    holdLots = _state.HoldLots,
                    holdTime = _state.HoldTime,
                    holdRounds = _state.HoldRounds,
                    peakPrice = _state.PeakPrice,
                    realizedPnl = _state.RealizedPnl,
                    tradeCount = _state.TradeCount,
                    cooldown = _state.Cooldown,
                    updatedAt = DateTime.Now
                }, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(StatePath, json);
            }
            catch { }
        }
    }
}
