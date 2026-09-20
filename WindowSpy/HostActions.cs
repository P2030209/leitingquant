using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace WindowSpy
{
    /// <summary>
    /// 真实操作服务：托管引擎与「流程工坊」节点执行器共用的手/眼/交易动作。
    /// 所有坐标均为游戏窗口相对坐标，经 IHostAdapter 换算为屏幕坐标执行。
    /// </summary>
    public class HostActions
    {
        private readonly IHostAdapter _ui;
        private readonly Action<string> _log;

        public HostActions(IHostAdapter ui, Action<string> log) { _ui = ui; _log = log; }

        public void BringToFront() => _ui.BringToFront();

        /// <summary>切换操作目标窗口：'A'（默认）或 'B'。后续点击/抓屏/OCR/找图全部对该窗口生效。</summary>
        public void SetTarget(char target) => _ui.SetTarget(target);
        public bool HasTarget(char target) => _ui.HasTarget(target);

        public void Click(Point p, int dwellMs = 80, int jitterPx = 3)
        {
            if (p.X < 0 || p.Y < 0) throw new InvalidOperationException("点击坐标未配置");
            _ui.BringToFront();
            _ui.ClickRel(p.X, p.Y, Math.Max(10, dwellMs), jitterPx);
        }

        public void DoubleClick(Point p, int gapMs = 60)
        {
            Click(p, 60, 3);
            Thread.Sleep(Math.Max(20, gapMs));
            Click(p, 60, 3);
        }

        public void RepeatClick(Point p, int times, int intervalMs, int dwellMs)
        {
            for (int i = 0; i < times; i++)
            {
                Click(p, dwellMs, 0);
                if (i < times - 1) Thread.Sleep(Math.Max(5, intervalMs));
            }
        }

        public void Settle(int ms = 350) => Thread.Sleep(Math.Max(0, ms));

        public void Delay(double sec, double jitterSec)
        {
            if (sec <= 0) return;
            double add = 0;
            if (jitterSec > 0)
            {
                var rnd = new Random(Guid.NewGuid().GetHashCode());
                add = rnd.NextDouble() * 2 * jitterSec - jitterSec;
            }
            int ms = Math.Max(0, (int)((sec + add) * 1000));
            Thread.Sleep(ms);
        }

        public void KeyPress(string key, string? vkText)
        {
            byte vk = ResolveVk(key, vkText);
            _ui.TapKey(vk, 40);
        }

        public void KeyDown(string key, string? vkText) => _ui.KeyDown(ResolveVk(key, vkText));
        public void KeyUp(string key, string? vkText) => _ui.KeyUp(ResolveVk(key, vkText));

        /// <summary>组合键，如 Ctrl+A、Ctrl+Shift+V、Alt+Tab、Win+D</summary>
        public void KeyCombo(string combo)
        {
            var parts = (combo ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries);
            var vks = new List<byte>();
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length == 0) continue;
                vks.Add(ResolveVk(t, ""));
            }
            foreach (var v in vks) _ui.KeyDown(v);
            Thread.Sleep(60);
            for (int i = vks.Count - 1; i >= 0; i--) _ui.KeyUp(vks[i]);
        }

        public static byte ResolveVk(string key, string? vkText)
        {
            if (byte.TryParse(vkText?.Trim(), out byte v)) return v;
            var k = (key ?? "").Trim();
            if (k.Length == 1)
            {
                char c = char.ToUpperInvariant(k[0]);
                if (c >= 'A' && c <= 'Z') return (byte)c;
                if (c >= '0' && c <= '9') return (byte)c;
            }
            if (k.Length == 2 && k[0] == 'F' && int.TryParse(k.Substring(1), out int fn) && fn is >= 1 and <= 12)
                return (byte)(0x70 + fn - 1);
            return k switch
            {
                "Esc" => 0x1B,
                "回车" or "Enter" => 0x0D,
                "空格" or "Space" => 0x20,
                "Tab" => 0x09,
                "退格" or "Backspace" => 0x08,
                "End" or "结尾" => 0x23,
                "Ctrl" or "Control" => 0x11,
                "Shift" => 0x10,
                "Alt" or "Menu" => 0x12,
                "Win" => 0x5B,
                "F5" => 0x74,
                "F12" => 0x7B,
                "上" => 0x26,
                "下" => 0x28,
                "左" => 0x25,
                "右" => 0x27,
                _ => 0x1B
            };
        }

        public void MoveTo(Point p)
        {
            if (p.X < 0 || p.Y < 0) throw new InvalidOperationException("坐标未配置");
            _ui.BringToFront();
            _ui.MoveRel(p.X, p.Y);
        }

        public void MouseButton(Point p, int button, bool down)
        {
            if (p.X < 0 || p.Y < 0) throw new InvalidOperationException("坐标未配置");
            _ui.BringToFront();
            _ui.MouseButtonRel(button, down, p.X, p.Y);
        }

        public static int ButtonIndex(string name) => (name ?? "") switch
        {
            "右键" => 1,
            "中键" => 2,
            _ => 0
        };

        /// <summary>拖拽：起点按下→分步移动→终点弹起（拟人轨迹）</summary>
        public void Drag(Point from, Point to, int durationMs, int button)
        {
            if (from.X < 0 || to.X < 0) throw new InvalidOperationException("拖拽起终点未配置");
            _ui.BringToFront();
            _ui.MouseButtonRel(button, true, from.X, from.Y);
            Thread.Sleep(80);
            int steps = Math.Max(4, durationMs / 40);
            int stepMs = Math.Max(10, durationMs / steps);
            for (int i = 1; i <= steps; i++)
            {
                double t = (double)i / steps;
                // 加一点贝塞尔感的轻微偏移
                int x = (int)(from.X + (to.X - from.X) * t);
                int y = (int)(from.Y + (to.Y - from.Y) * t);
                _ui.MoveRel(x, y);
                Thread.Sleep(stepMs);
            }
            _ui.MouseButtonRel(button, false, to.X, to.Y);
        }

        /// <summary>在坐标处滚动滚轮 ticks 格（每格 120）</summary>
        public void Wheel(Point p, int ticks, int stepMs)
        {
            _ui.BringToFront();
            if (p.X >= 0) _ui.MoveRel(p.X, p.Y);
            int sign = Math.Sign(ticks);
            for (int i = 0; i < Math.Abs(ticks); i++)
            {
                _ui.Wheel(sign * 120);
                if (i < Math.Abs(ticks) - 1) Thread.Sleep(Math.Max(20, stepMs));
            }
        }

        /// <summary>模板匹配：命中返回 true 与窗口相对中心坐标</summary>
        public bool FindTemplate(string tplName, Rectangle region, double threshold,
            out int x, out int y, out double score)
        {
            var path = TemplateManager.PathOf(tplName);
            return _ui.FindImage(path, region, threshold, out x, out y, out score);
        }

        public string OcrRect(Rectangle r, bool numbersOnly) => _ui.OcrRegion(r, numbersOnly);
        public string OcrScreen() => _ui.OcrScreen();
        public void PasteText(string text) => _ui.PasteText(text);

        /// <summary>余额读取：支持 4,002K / 1.2M / 3万 等缩写</summary>
        public double ReadWallet(Rectangle r)
        {
            var txt = _ui.OcrRegion(r, false) ?? "";
            double best = ParseAmount(txt);
            if (best <= 0) _log($"读余额：未识别到数字（原文「{txt.Trim()}」）");
            return best;
        }

        /// <summary>从文本中解析最大金额（带 K/M/万 单位与千分位）</summary>
        public static double ParseAmount(string txt)
        {
            double best = 0;
            foreach (Match m in Regex.Matches(txt ?? "", @"(\d{1,3}(?:[,，]\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?)\s*([KkMm萬万]?)"))
            {
                var num = m.Groups[1].Value.Replace(",", "").Replace("，", "");
                if (!double.TryParse(num, out double v)) continue;
                var u = m.Groups[2].Value;
                if (u is "K" or "k") v *= 1_000;
                else if (u is "M" or "m") v *= 1_000_000;
                else if (u is "万" or "萬") v *= 10_000;
                if (v > best) best = v;
            }
            return best;
        }

        /// <summary>全屏关键词检测（封禁/邮件通用）</summary>
        public bool ScreenContainsAny(string words)
        {
            var text = _ui.OcrScreen();
            return SplitWords(words).Any(k => text.Contains(k.Trim()));
        }

        public static string[] SplitWords(string words)
            => (words ?? "").Split(new[] { ',', '，', ';', '；', '|' }, StringSplitOptions.RemoveEmptyEntries);

        /// <summary>领邮件：打开邮箱→看到关键词才点领取→关闭。返回是否点了领取</summary>
        public bool ClaimMail(Point mailBtn, Point claimBtn, Point closeBtn, string words, int settleMs = 350)
        {
            _ui.BringToFront();
            Click(mailBtn, 80, 3);
            Settle(settleMs + 250);
            bool has;
            try
            {
                var text = _ui.OcrScreen();
                has = SplitWords(words).Any(k => text.Contains(k.Trim()));
            }
            catch { has = true; }
            if (!has)
            {
                CloseMail(closeBtn);
                return false;
            }
            Click(claimBtn, 80, 3);
            Settle(settleMs + 150);
            CloseMail(closeBtn);
            return true;
        }

        public void CloseMail(Point closeBtn)
        {
            if (closeBtn.X >= 0 && closeBtn.Y >= 0) Click(closeBtn, 80, 3);
            else _ui.TapKey(0x1B, 40);
        }

        /// <summary>交易行内搜索并选中子弹（买前/卖前重新定位）</summary>
        public void SelectBullet(string name, Point box, Point result, int settleMs = 400)
        {
            Click(box, 80, 3);
            Settle(Math.Max(100, settleMs - 140));
            _ui.PasteText(name);
            Settle(settleMs);
            Click(result, 80, 3);
            Settle(settleMs + 100);
        }

        /// <summary>买入份数：先连点 − 复位，再点 + 到目标份数</summary>
        public void AdjustLots(Point minus, Point plus, int resetClicks, int target, int clickDelayMs = 45)
        {
            for (int i = 0; i < resetClicks; i++)
            {
                Click(minus, 15, 0);
                Thread.Sleep(Math.Max(10, clickDelayMs));
            }
            for (int i = 1; i < target; i++)
            {
                Click(plus, 15, 0);
                Thread.Sleep(Math.Max(10, clickDelayMs));
            }
            _log($"份数调整：−复位{resetClicks}次，+{Math.Max(0, target - 1)}次 → {target}份");
        }

        // —— 交易行出售上架 ——

        /// <summary>
        /// 上架定价。mode：
        /// 输入=点价格框后全选粘贴整数价；柱=点价格柱；删位法=输入参考价→End→退格N位→点价格柱。
        /// </summary>
        public void SetPrice(string mode, Point box, Point bar, double price, int delDigits, int settleMs = 350)
        {
            if (mode == "点价格柱")
            {
                Click(bar, 80, 3);
                Settle(settleMs);
                _log("定价：点击价格柱");
                return;
            }

            long px = (long)Math.Round(price);
            if (px <= 0) throw new InvalidOperationException("定价金额无效（<=0），检查价格变量");
            Click(box, 80, 3);
            Settle(180);
            // 全选原价格后粘贴新价
            _ui.KeyDown(0x11); Thread.Sleep(40); _ui.TapKey(0x41, 40);
            Thread.Sleep(40); _ui.KeyUp(0x11);
            Thread.Sleep(80);
            _ui.PasteText(px.ToString());
            Settle(200);

            if (mode == "删位法+价格柱")
            {
                int n = Math.Max(1, delDigits);
                _ui.TapKey(0x23, 40); // End 到末尾
                for (int i = 0; i < n; i++) { _ui.TapKey(0x08, 40); Thread.Sleep(60); }
                Settle(200);
                Click(bar, 80, 3);
                _log($"定价：删位法，参考价{px} 删{n}位后点价格柱");
            }
            else
            {
                _log($"定价：直接输入 {px}");
            }
            Settle(settleMs);
        }

        /// <summary>点上架按钮，可选再点二次确认</summary>
        public void ListConfirm(Point listBtn, Point confirmBtn, int settleMs = 600)
        {
            Click(listBtn, 80, 3);
            Settle(settleMs);
            if (confirmBtn.X >= 0 && confirmBtn.Y >= 0)
            {
                Click(confirmBtn, 80, 3);
                Settle(settleMs);
            }
        }

        /// <summary>军需处批量回收：推车图标→(可选)全选→回收→(可选)确认</summary>
        public void RecycleBatch(Point cart, Point selectAll, Point recycle, Point confirm, int settleMs = 500)
        {
            Click(cart, 80, 3);
            Settle(settleMs + 200);
            if (selectAll.X >= 0 && selectAll.Y >= 0) { Click(selectAll, 80, 3); Settle(300); }
            Click(recycle, 80, 3);
            Settle(settleMs);
            if (confirm.X >= 0 && confirm.Y >= 0) { Click(confirm, 80, 3); Settle(settleMs); }
            _log("军需处批量回收完成");
        }

        // —— 行情/选品（网络，允许后台线程调用）——
        public List<MarketQuote> FetchQuotes(string url) => MarketService.FetchCatalog(url);

        public MarketService.Candidate? Pick(List<MarketQuote> quotes, string strategy,
            double budget, double minExpProfitPct, int maxLots, HashSet<string>? blacklist = null)
        {
            var list = MarketService.PickTop(quotes, strategy, new MarketService.PickOptions
            {
                Budget = budget,
                MinExpProfitPct = minExpProfitPct,
                MaxLots = Math.Max(1, maxLots),
                Blacklist = blacklist
            });
            return list.Count > 0 ? list[0] : null;
        }

        // —— 参数文本解析 ——
        public static Point ParsePoint(string s)
        {
            var parts = (s ?? "").Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[0].Trim(), out int x) && int.TryParse(parts[1].Trim(), out int y))
                return new Point(x, y);
            return new Point(-1, -1);
        }

        public static Rectangle ParseRect(string s)
        {
            var parts = (s ?? "").Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4
                && int.TryParse(parts[0].Trim(), out int x) && int.TryParse(parts[1].Trim(), out int y)
                && int.TryParse(parts[2].Trim(), out int w) && int.TryParse(parts[3].Trim(), out int h))
                return new Rectangle(x, y, w, h);
            return Rectangle.Empty;
        }

        /// <summary>文本变量替换：{变量名} → 字符串值</summary>
        public static string Substitute(string template, Dictionary<string, object?> vars)
        {
            if (string.IsNullOrEmpty(template)) return "";
            return Regex.Replace(template, @"\{([^{}]+)\}", m =>
            {
                var name = m.Groups[1].Value.Trim();
                return vars.TryGetValue(name, out var v) && v != null ? v.ToString() ?? "" : "";
            });
        }

        /// <summary>数字规格：常量、{变量} 或表达式（如 (余额-1000)*0.5），失败返回 null</summary>
        public static double? ParseNumberSpec(string spec, Dictionary<string, object?> vars)
        {
            spec = (spec ?? "").Trim();
            if (spec.Length == 0) return null;
            // 优先走表达式（中文变量名直接参与运算）
            var (ok, val) = FlowExpr.Eval(spec, vars);
            if (ok && val != null)
            {
                if (val is double d) return d;
                if (val is bool b) return b ? 1 : 0;
                if (double.TryParse(val.ToString(), out double dv)) return dv;
            }
            // 退化：{变量} 文本替换后解析金额
            var txt = Substitute(spec, vars);
            if (double.TryParse(txt.Trim(), out double v)) return v;
            double amt = ParseAmount(txt);
            return amt > 0 ? amt : null;
        }
    }
}
