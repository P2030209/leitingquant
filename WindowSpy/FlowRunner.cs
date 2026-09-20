using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MoonSharp.Interpreter;

namespace WindowSpy
{
    /// <summary>临时文件：using 结束即删除；执行 Python/Lua 脚本用</summary>
    internal sealed class TempFile : IDisposable
    {
        public string Path { get; }
        public TempFile(string name, string content)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name);
            File.WriteAllText(Path, content);
        }
        public void Dispose()
        {
            try { if (File.Exists(Path)) File.Delete(Path); } catch { }
        }
    }
    /// <summary>流程运行状态</summary>
    public enum RunState { Idle, Running, Paused, ErrorPaused, Finished, Stopped }

    /// <summary>
    /// 流程工坊执行器：按连线真实执行节点（手/眼/交易/AI），支持是/否分支、循环N次/条件循环、隐式回流、跳出。
    /// 在后台线程运行；节点高亮/日志/变量变化通过事件抛给 UI。
    /// 支持：暂停/继续、急停（等待轮询即时响应）、节点出错暂停→修正后从该步继续。
    /// </summary>
    public class FlowRunner
    {
        public event Action<string?>? HighlightChanged;
        /// <summary>level: info/action/buy/sell/wait/claim/warn/error</summary>
        public event Action<string, string>? Logged;
        public event Action? VarsChanged;
        public event Action<bool>? Finished;             // true=正常跑完 false=中止/出错
        /// <summary>某节点执行抛异常（nodeId, 节点标题, 错误信息）——界面据此闪红；此时流程已暂停等待修正</summary>
        public event Action<string, string, string>? NodeError;
        /// <summary>出错修正后重新执行前，清除节点红色</summary>
        public event Action<string>? NodeErrorCleared;
        /// <summary>运行状态变化（运行/暂停/出错暂停/结束）</summary>
        public event Action<RunState>? StateChanged;
        /// <summary>最近一次出错的节点Id（null=无）</summary>
        public string? LastErrorNode { get; private set; }
        /// <summary>最近一次出错信息</summary>
        public string? LastErrorMessage { get; private set; }

        public IReadOnlyDictionary<string, object?> Vars => _vars;
        private readonly Dictionary<string, object?> _vars = new();
        private readonly HostActions _act;

        private CancellationToken _ct;
        private CancellationTokenSource? _ownCts;
        private FlowNode? _current;
        private readonly ManualResetEventSlim _pauseGate = new(true);

        private class Frame
        {
            public string HeaderId = "";
            public int Remaining;
            public bool Infinite;
            public int Iteration;
        }

        /// <summary>一层调用图：主流程或一次子流程模块调用；帧栈按层隔离，模块内循环不会跳回主流程</summary>
        private class CallLevel
        {
            public FlowGraph G = new();
            public readonly List<Frame> Frames = new();
            public FlowNode? Caller;     // 上一层的 subflow 卡片节点（顶层为 null）
            public string ModFile = "";
            public string ModName = "";
        }
        private readonly List<CallLevel> _calls = new();
        // 当前执行所在层的图（模块内执行时指向子图，收尾弹层后自动回主图）
        private FlowGraph _g => _calls[^1].G;
        private List<Frame> _frames => _calls[^1].Frames;

        // Execute("subflow") 暂存的下一层信息，主循环见到 IntoSub 后压栈
        private FlowGraph? _pendingGraph;
        private string? _pendingEntryId;
        private string _pendingModFile = "";
        private string _pendingModName = "";

        private bool _luaInitialized;

        private enum Flow { Edge, Break, End, IntoSub }

        public bool IsRunning { get; private set; }
        public RunState State { get; private set; } = RunState.Idle;

        public FlowRunner(IHostAdapter adapter, Action<string, string> logSink)
        {
            _act = new HostActions(adapter, s => Log("info", s));
            // 宿主日志也汇入（带日志级别）
            Logged += (level, m) => logSink(level, m);
        }

        /// <summary>暂停：长等待/循环即时挂起，节点动作间隙生效</summary>
        public void Pause()
        {
            if (State != RunState.Running) return;
            State = RunState.Paused;
            _pauseGate.Reset();
            StateChanged?.Invoke(State);
            Log("warn", "⏸ 已暂停（查看收益/修正参数后点「继续」；急停则结束流程）");
        }

        /// <summary>继续：出错暂停时会从出错的那一步重新执行，循环栈和变量保留</summary>
        public void Resume()
        {
            if (State != RunState.Paused && State != RunState.ErrorPaused) return;
            State = RunState.Running;
            _pauseGate.Set();
            StateChanged?.Invoke(State);
            Log("info", "▶ 继续运行");
        }

        /// <summary>急停：取消等待并结束流程</summary>
        public void Stop()
        {
            try { _ownCts?.Cancel(); } catch { }
            _pauseGate.Set();
        }

        /// <summary>
        /// 出错暂停期间从文件改完工程后热替换图：按节点 Id 重新定位当前节点，
        /// 循环栈(_frames)按 HeaderId 工作、变量(_vars)保留。返回 false=当前节点已被删除。
        /// </summary>
        public bool ReloadGraph(FlowGraph ng)
        {
            var curId = _current?.Id;
            // 只替换顶层（主工程）；出错点若在模块内，节点在子层图里，沿调用栈找回
            _calls[0].G = ng;
            if (curId != null)
            {
                _current = ng.Node(curId)
                    ?? _calls.SelectMany(c => c.G.Nodes).FirstOrDefault(n => n.Id == curId);
                return _current != null;
            }
            return true;
        }

        /// <summary>节点边界检查点：急停抛异常，暂停则阻塞到继续</summary>
        private void CheckGate()
        {
            _ct.ThrowIfCancellationRequested();
            if (!_pauseGate.IsSet) _pauseGate.Wait(_ct);
            _ct.ThrowIfCancellationRequested();
        }

        /// <summary>可暂停、可急停的等待（100ms 粒度）</summary>
        private void Wait(int ms)
        {
            int waited = 0;
            while (waited < ms)
            {
                _ct.ThrowIfCancellationRequested();
                int chunk = Math.Min(100, ms - waited);
                if (_pauseGate.Wait(chunk)) waited += chunk;
                else _pauseGate.Wait(_ct);   // 暂停中：阻塞到继续/急停
            }
            _ct.ThrowIfCancellationRequested();
        }

        private void SetState(RunState s)
        {
            State = s;
            StateChanged?.Invoke(s);
        }

        public void Run(FlowGraph graph, CancellationToken ct)
        {
            _ownCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ct = _ownCts.Token;
            _pauseGate.Set();
            _calls.Clear();
            _calls.Add(new CallLevel { G = graph });
            _pendingGraph = null;
            _current = null;
            LastErrorNode = null;
            LastErrorMessage = null;
            IsRunning = true;
            SetState(RunState.Running);
            try
            {
                var start = _g.Nodes.FirstOrDefault(n => n.Type == "start");
                if (start == null) { Log("error", "流程缺少「开始」节点"); Finished?.Invoke(false); return; }

                _current = start;
                while (_current != null)
                {
                    CheckGate();
                    // 模块内执行时，主画布只显示调用卡片，高亮/闪红都落在 subflow 卡片上
                    HighlightChanged?.Invoke(_calls.Count > 1 ? _calls[^1].Caller!.Id : _current.Id);

                    string? port;
                    Flow flow;
                    try
                    {
                        (flow, port) = Execute(_current);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception nodeEx)
                    {
                        // 出错暂停：保留循环栈与变量，停在当前节点；修正保存后继续会重跑这一步
                        var title = FlowCatalog.Get(_current.Type).Title;
                        bool inMod = _calls.Count > 1;
                        string modChain = inMod
                            ? string.Join(" › ", _calls.Skip(1).Select(c => c.ModName)) : "";
                        string errNodeId = inMod ? _calls[^1].Caller!.Id : _current.Id;
                        LastErrorNode = errNodeId;
                        LastErrorMessage = nodeEx.Message;
                        Log("error", inMod
                            ? $"模块【{modChain}】内节点【{title}】执行失败：{nodeEx.Message}"
                            : $"节点【{title}】执行失败：{nodeEx.Message}");
                        Log("warn", inMod
                            ? "流程已暂停：双击该模块卡片打开模块编辑器修正并保存，再点「继续」将从模块开头重跑"
                            : "流程已暂停：点「编辑工程」修正该节点并保存，再点「继续」即从此步重跑");
                        NodeError?.Invoke(errNodeId, inMod ? $"模块【{modChain}】" : title, nodeEx.Message);
                        SetState(RunState.ErrorPaused);
                        _pauseGate.Reset();
                        _pauseGate.Wait(_ct);              // 阻塞到继续或急停
                        _ct.ThrowIfCancellationRequested();
                        var cleared = LastErrorNode;
                        LastErrorNode = null;
                        LastErrorMessage = null;
                        if (cleared != null) NodeErrorCleared?.Invoke(cleared);
                        SetState(RunState.Running);
                        if (inMod)
                        {
                            // 模块内出错：弹掉所有子层，回到 subflow 调用节点重新进入（整个模块重跑）
                            string callerId = _calls[^1].Caller!.Id;
                            while (_calls.Count > 1) _calls.RemoveAt(_calls.Count - 1);
                            _current = _g.Node(callerId);
                            if (_current == null)
                            {
                                Log("error", "模块调用节点在修改后的工程中已不存在，流程中止");
                                Finished?.Invoke(false);
                                return;
                            }
                        }
                        else if (_current == null)
                        {
                            Log("error", "当前节点在修改后的工程中已不存在，流程中止");
                            Finished?.Invoke(false);
                            return;
                        }
                        continue;
                    }

                    var prev = _current;
                    if (flow == Flow.Break)
                    {
                        var f = _frames[^1];
                        _frames.Remove(f);
                        var doneEdge = _g.OutEdge(f.HeaderId, "done");
                        _current = doneEdge != null ? _g.Node(doneEdge.To) : null;
                        continue;
                    }
                    if (flow == Flow.End)
                    {
                        // 模块层遇到「结束」节点 = 模块正常收尾，返回主流程；顶层才是整个流程结束
                        if (_calls.Count > 1) { _current = ExitSub(); continue; }
                        break;
                    }
                    if (flow == Flow.IntoSub)
                    {
                        // 进入子流程模块：压入新的调用层，从模块入口节点继续
                        var sg = _pendingGraph!;
                        var entry = sg.Node(_pendingEntryId!);
                        if (entry == null) throw new InvalidOperationException("模块入口节点已丢失");
                        _calls.Add(new CallLevel
                        {
                            G = sg, Caller = prev,
                            ModFile = _pendingModFile, ModName = _pendingModName
                        });
                        _pendingGraph = null;
                        _pendingEntryId = null;
                        _current = entry;
                        continue;
                    }

                    var edge = _g.OutEdge(prev.Id, port ?? "");
                    if (edge != null)
                    {
                        _current = _g.Node(edge.To);
                        continue;
                    }
                    // 未连线出口：循环体内隐式回到循环头
                    if (_frames.Count > 0 && !(IsLoopHeader(prev) && port is "body"))
                    {
                        _current = _g.Node(_frames[^1].HeaderId);
                        continue;
                    }
                    // 子流程层走到本路径末端 = 模块执行完成：弹层，沿调用卡片的默认出口回主流程
                    if (_calls.Count > 1) { _current = ExitSub(); continue; }
                    _current = null;
                }
                Log("info", "—— 流程执行结束 ——");
                SetState(RunState.Finished);
                Finished?.Invoke(true);
            }
            catch (OperationCanceledException)
            {
                Log("warn", "流程已急停");
                SetState(RunState.Stopped);
                Finished?.Invoke(false);
            }
            catch (Exception ex)
            {
                Log("error", $"流程异常中止: {ex.Message}");
                SetState(RunState.Stopped);
                Finished?.Invoke(false);
            }
            finally
            {
                IsRunning = false;
                HighlightChanged?.Invoke(null);
            }
        }

        private static bool IsLoopHeader(FlowNode n) => n.Type is "loop_n" or "loop_while";

        /// <summary>模块层收尾：弹出当前子流程层，返回调用卡片默认出口连到的节点（无连线则 null=主流程结束）</summary>
        private FlowNode? ExitSub()
        {
            var lvl = _calls[^1];
            _calls.RemoveAt(_calls.Count - 1);
            Log("info", $"🧩 模块【{lvl.ModName}】执行完成，回到主流程");
            var outEdge = _g.OutEdge(lvl.Caller!.Id, "");
            return outEdge != null ? _g.Node(outEdge.To) : null;
        }

        // 返回 (走向, 出口端口)
        private (Flow flow, string? port) Execute(FlowNode n)
        {
            string P(string k) => n.Get(k);
            Point Pt(string k) => HostActions.ParsePoint(P(k));
            Rectangle Rc(string k) => HostActions.ParseRect(P(k));
            int I(string k, int def = 0) => int.TryParse(P(k), out int v) ? v : def;
            double D(string k, double def = 0) => double.TryParse(P(k), out double v) ? v : def;
            void Set(string name, object? v) { _vars[name] = v; VarsChanged?.Invoke(); }
            double? Num(string spec) => HostActions.ParseNumberSpec(spec, _vars);

            // 屏蔽节点：跳过执行，沿默认出口继续
            if (n.Disabled)
            {
                Log("info", $"⚠ 节点「{FlowCatalog.Get(n.Type).Title}」已屏蔽，跳过执行");
                return (Flow.Edge, "");
            }

            // 目标窗口切换：运动/视觉/买卖节点的「目标窗口 A/B」参数（双开倒卖）
            // 点击/移动/滚轮/交易等动作内部会自行置顶；这里只需切换句柄，
            // 纯键盘节点不经过点击，需显式置顶，否则按键会发给当前前台窗口。
            var nodeDef = FlowCatalog.Get(n.Type);
            if (nodeDef.Params.Exists(pd => pd.Key == "wintarget"))
            {
                char tgt = string.Equals((P("wintarget") ?? "").Trim(), "B", StringComparison.OrdinalIgnoreCase) ? 'B' : 'A';
                if (!_act.HasTarget(tgt))
                    throw new InvalidOperationException($"该节点目标为窗口{tgt}，但主界面顶部尚未绑定窗口{tgt}");
                _act.SetTarget(tgt);
                if (n.Type is "keypress" or "key_down" or "key_up" or "key_combo")
                    _act.BringToFront();
            }

            // MoonSharp 0.9.8 沙箱：禁用 os/io/debug/require 用 SetModuleDisabled
            if (!_luaInitialized)
            {
                _luaInitialized = true;
            }

            switch (n.Type)
            {
                case "start": Log("info", "—— 流程开始 ——"); break;
                case "end": return (Flow.End, null);
                case "note": break;
                case "log_msg":
                {
                    var lvl = P("level") switch
                    {
                        "动作" => "action", "等待" => "wait", "警告" => "warn", "错误" => "error", _ => "info"
                    };
                    Log(lvl, HostActions.Substitute(P("text"), _vars));
                    break;
                }
                case "beep":
                {
                    int times = Math.Max(1, I("times", 2));
                    for (int i = 0; i < times; i++)
                    {
                        System.Media.SystemSounds.Asterisk.Play();
                        if (i < times - 1) Wait(500);
                    }
                    Log("info", $"声音提醒 ×{times}");
                    break;
                }
                case "stop_here":
                    Log("warn", $"主动停止：{P("text")}");
                    return (Flow.End, null);
                case "delay":
                {
                    double sec = D("sec", 1), jitter = D("jitter", 0);
                    double add = 0;
                    if (jitter > 0)
                    {
                        var rnd = new Random(Guid.NewGuid().GetHashCode());
                        add = rnd.NextDouble() * 2 * jitter - jitter;
                    }
                    Wait(Math.Max(0, (int)((sec + add) * 1000)));
                    Log("wait", $"等待 {sec} 秒");
                    break;
                }

                // ——— 运动控制 ———
                case "click":
                    _act.Click(Pt("pos"), I("dwell", 80), I("jitter", 3));
                    Log("action", $"点击 {P("pos")}"); break;
                case "dblclick":
                    _act.DoubleClick(Pt("pos"), I("dwell", 60));
                    Log("action", $"双击 {P("pos")}"); break;
                case "mouse_move":
                    _act.MoveTo(Pt("pos")); Log("action", $"移动到 {P("pos")}"); break;
                case "left_down":
                    _act.MouseButton(Pt("pos"), 0, true); Log("action", "左键按下"); break;
                case "left_up":
                    _act.MouseButton(Pt("pos"), 0, false); Log("action", "左键弹起"); break;
                case "right_click":
                    var rp = Pt("pos");
                    _act.MouseButton(rp, 1, true); Wait(Math.Max(20, I("dwell", 80)));
                    _act.MouseButton(rp, 1, false); Log("action", $"右键点击 {P("pos")}"); break;
                case "right_down":
                    _act.MouseButton(Pt("pos"), 1, true); Log("action", "右键按下"); break;
                case "right_up":
                    _act.MouseButton(Pt("pos"), 1, false); Log("action", "右键弹起"); break;
                case "middle_click":
                    _act.MouseButton(Pt("pos"), 2, true); Wait(80);
                    _act.MouseButton(Pt("pos"), 2, false); Log("action", "中键单击"); break;
                case "drag":
                    _act.Drag(Pt("from"), Pt("to"), I("duration", 400), HostActions.ButtonIndex(P("button")));
                    Log("action", $"拖拽 {P("from")} → {P("to")}"); break;
                case "wheel":
                    _act.Wheel(Pt("pos"), I("ticks", 3), I("stepms", 60));
                    Log("action", $"滚轮 {P("ticks")} 格"); break;
                case "repeatclick":
                    _act.RepeatClick(Pt("pos"), I("times", 1), I("interval", 40), I("dwell", 15));
                    Log("action", $"连点 {P("times")} 次"); break;
                case "keypress":
                    _act.KeyPress(P("key"), P("vk")); Log("action", $"按键 {P("key")}"); break;
                case "key_down":
                    _act.KeyDown(P("key"), P("vk")); Log("action", $"按下 {P("key")}"); break;
                case "key_up":
                    _act.KeyUp(P("key"), P("vk")); Log("action", $"弹起 {P("key")}"); break;
                case "key_combo":
                    _act.KeyCombo(P("combo")); Log("action", $"组合键 {P("combo")}"); break;
                case "input":
                    _act.BringToFront();
                    _act.Settle(150);
                    _act.PasteText(HostActions.Substitute(P("text"), _vars));
                    Log("action", $"输入文本: {Trunc(HostActions.Substitute(P("text"), _vars), 30)}"); break;
                case "bringfront":
                    _act.BringToFront(); Log("action", "窗口置顶"); break;

                // ——— 视觉识别 ———
                case "ocr_region":
                {
                    var txt = _act.OcrRect(Rc("rect"), P("num") == "true") ?? "";
                    Set(P("var"), txt);
                    Log("wait", $"区域识别 → {P("var")} = {Trunc(txt.Trim(), 40)}");
                    break;
                }
                case "ocr_screen":
                {
                    var txt = _act.OcrScreen() ?? "";
                    Set(P("var"), txt);
                    Log("wait", $"全屏识别 → {P("var")} ({txt.Length} 字)");
                    break;
                }
                case "read_wallet":
                {
                    var amt = _act.ReadWallet(Rc("rect"));
                    Set(P("var"), amt);
                    Log("claim", $"{P("var")} = {amt:0,0}");
                    break;
                }
                case "text_contains":
                {
                    var src = _vars.TryGetValue(P("var"), out var sv) ? sv?.ToString() ?? "" : "";
                    bool hit = HostActions.SplitWords(P("words")).Any(w => src.Contains(w.Trim()));
                    Log(hit ? "wait" : "info", $"文本判断「{Trunc(P("words"), 20)}」→ {(hit ? "找到" : "没找到")}");
                    return (Flow.Edge, hit ? "found" : "miss");
                }

                // ——— 模板视觉 ———
                case "find_template":
                case "click_template":
                case "wait_template":
                case "wait_gone":
                    return RunTemplateNode(n, Pt, Rc, D, I, P, Set);
                case "wait_text":
                {
                    int timeoutMs = I("timeout", 10) * 1000;
                    int interval = Math.Max(200, I("interval", 600));
                    var words = P("words");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < timeoutMs)
                    {
                        var text = _act.OcrScreen();
                        if (HostActions.SplitWords(words).Any(k => text.Contains(k.Trim())))
                        {
                            Log("wait", $"屏幕出现「{Trunc(words, 20)}」");
                            return (Flow.Edge, "found");
                        }
                        Wait(interval);
                    }
                    Log("warn", $"等待屏幕文字「{Trunc(words, 20)}」超时");
                    return (Flow.Edge, "timeout");
                }

                // ——— 买卖控制 ———
                case "enter_trade":
                    _act.Click(Pt("pos"), 80, 3); _act.Settle(500);
                    Log("action", "进入交易行"); break;
                case "claim_mail":
                {
                    bool got = _act.ClaimMail(Pt("mail"), Pt("claim"), Pt("close"), P("words"));
                    Log("claim", got ? "邮件：领取到哈弗币附件" : "邮件：无领取项，已关闭");
                    break;
                }
                case "fetch_quotes":
                {
                    Log("wait", "正在拉取行情…");
                    var quotes = _act.FetchQuotes(P("url"));
                    Set(P("var"), quotes);
                    Log("info", $"行情已拉取：{quotes.Count} 条 → {P("var")}");
                    break;
                }
                case "pick":
                {
                    if (_vars.TryGetValue(P("quotes"), out var qv) && qv is List<MarketQuote> ql)
                    {
                        double budget = Num(P("budget")) ?? 0;
                        var cand = _act.Pick(ql, P("strategy"), budget, D("minprofit", 3), I("maxlots", 999));
                        if (cand == null) { Log("warn", "选品：没有符合利润门槛的子弹"); return (Flow.Edge, "miss"); }
                        string px = P("prefix");
                        Set(px + "品名", cand.Quote.Name);
                        Set(px + "现价", cand.Quote.Price);
                        Set(px + "份数", (double)cand.Lots);
                        Set(px + "预期利润", cand.ExpProfitPct);
                        Set(px + "卖出价", cand.ExitEst);
                        Set(px + "总利润", cand.ExpectedTotal);
                        Log("buy", $"选品[{P("strategy")}] {cand.Quote.Name} 现价{cand.Quote.Price:0} " +
                                   $"预期{cand.ExpProfitPct:0.0}% 买{cand.Lots}份 总利润{cand.ExpectedTotal:0}");
                        return (Flow.Edge, "ok");
                    }
                    Log("error", "选品：行情列表变量不存在，先接「拉取全部行情」");
                    return (Flow.Edge, "miss");
                }
                case "select_bullet":
                {
                    var name = HostActions.Substitute(P("name"), _vars);
                    _act.SelectBullet(name, Pt("box"), Pt("result"));
                    Log("action", $"搜索选中：{name}");
                    break;
                }
                case "adjust_lots":
                {
                    int target = (int)(Num(P("target")) ?? 1);
                    target = Math.Max(1, target);
                    _act.AdjustLots(Pt("minus"), Pt("plus"), I("reset", 20), target);
                    Log("action", $"份数调整到 {target}");
                    break;
                }
                case "buy":
                    _act.Click(Pt("pos"), 80, 3); _act.Settle(I("settle", 350));
                    Log("buy", "执行买入"); break;
                case "sell":
                    _act.Click(Pt("pos"), 80, 3); _act.Settle(I("settle", 350));
                    Log("sell", "执行卖出"); break;
                case "hold_quote":
                {
                    var name = HostActions.Substitute(P("name"), _vars);
                    List<double> hist = new();
                    if (_vars.TryGetValue(P("quotes"), out var qv2) && qv2 is List<MarketQuote> ql2)
                    {
                        var mq = MarketService.Match(ql2, name);
                        if (mq != null) hist = mq.History ?? new();
                    }
                    if (hist.Count < 5)
                    {
                        try { hist = MarketService.FetchHistory(name, "5m", 60); } catch { }
                    }
                    double price = hist.Count > 0 ? hist[^1] : 0;
                    if (price <= 0 && _vars.TryGetValue(P("quotes"), out var qv3) && qv3 is List<MarketQuote> ql3)
                        price = MarketService.Match(ql3, name)?.Price ?? 0;
                    int maN = Math.Max(2, I("ma", 20));
                    double ma = QuantMath.MA(hist, maN);
                    double rsi = QuantMath.RSI(hist, 14);
                    double pos = QuantMath.Position(hist, maN);
                    double buyPrice = Num(P("buyprice")) ?? price;
                    double profitPct = buyPrice > 0 ? (price - buyPrice) / buyPrice * 100 : 0;
                    double exit = price;
                    if (_vars.TryGetValue(P("quotes"), out var qv4) && qv4 is List<MarketQuote> ql4)
                    {
                        var mq = MarketService.Match(ql4, name);
                        if (mq?.SevenHigh != null && mq.SevenLow != null)
                            exit = (mq.SevenHigh.Value + mq.SevenLow.Value) / 2 * 0.87;
                    }
                    string px2 = P("prefix");
                    Set(px2 + "现价", price); Set(px2 + "浮盈", profitPct); Set(px2 + "MA", ma);
                    Set(px2 + "RSI", rsi); Set(px2 + "位置", pos); Set(px2 + "卖出价", exit);
                    Log("wait", $"{name} 现价{price:0} 浮盈{profitPct:0.0}% RSI{rsi:0} 位置{pos:0.00}");
                    break;
                }

                // ——— 出售上架链路 ———
                case "open_warehouse":
                    _act.Click(Pt("pos"), 80, 3); _act.Settle(I("settle", 600));
                    Log("action", "打开仓库"); break;
                case "goto_ammo":
                    _act.Click(Pt("pos"), 80, 3); _act.Settle(I("settle", 400));
                    Log("action", "切到弹药分类"); break;
                case "trade_tab":
                    _act.Click(Pt("pos"), 80, 3); _act.Settle(I("settle", 500));
                    Log("action", $"交易行切到「{P("tab")}」页签"); break;
                case "sell_channel":
                {
                    // 从仓库点出售会弹窗：军需处（价低）/交易行；坐标留空则跳过（交易行出售页直接发起时无弹窗）
                    _act.Settle(I("wait", 700));
                    bool toTrade = P("channel") != "军需处";
                    var channelPt = toTrade ? Pt("tradePos") : Pt("merchantPos");
                    if (channelPt.X < 0 || channelPt.Y < 0)
                        Log("info", $"出售渠道弹窗：未配置「{P("channel")}」按钮坐标，跳过");
                    else
                    {
                        _act.Click(channelPt, 80, 3); _act.Settle(500);
                        Log("action", $"出售渠道选择 → {P("channel")}");
                    }
                    break;
                }
                case "sell_pick_item":
                {
                    var itemName = HostActions.Substitute(P("name"), _vars);
                    var box = Pt("box");
                    if (box.X >= 0 && box.Y >= 0) _act.SelectBullet(itemName, box, Pt("result"));
                    else { _act.Click(Pt("result"), 80, 3); _act.Settle(500); }
                    Log("action", $"选中出售物品：{itemName}");
                    break;
                }
                case "set_price":
                {
                    double sp = Num("price") ?? 0;
                    _act.SetPrice(P("mode"), Pt("box"), Pt("bar"), sp, I("deldigits", 1));
                    Log("sell", $"上架定价（{P("mode")}）");
                    break;
                }
                case "set_sell_qty":
                {
                    int target = Math.Max(1, (int)(Num("target") ?? 1));
                    _act.AdjustLots(Pt("minus"), Pt("plus"), I("reset", 20), target);
                    Log("action", $"上架数量调整到 {target}");
                    break;
                }
                case "list_confirm":
                    _act.ListConfirm(Pt("pos"), Pt("confirm"), I("settle", 600));
                    Log("sell", "已确认上架，等待成交"); break;
                case "recycle_batch":
                    _act.RecycleBatch(Pt("cart"), Pt("selectall"), Pt("recycle"), Pt("confirm"), I("settle", 500));
                    Log("sell", "军需处批量回收完成"); break;

                // ——— 逻辑 ———
                case "if":
                {
                    var (ok, val) = FlowExpr.Eval(P("expr"), _vars);
                    bool yes = ok && FlowExpr.IsTrue(val);
                    Log("wait", $"判断 {P("expr")} → {(yes ? "是" : "否")}");
                    return (Flow.Edge, yes ? "true" : "false");
                }
                case "loop_n":
                case "loop_while":
                    return LoopGate(n, I, P);
                case "subflow":
                {
                    // 子流程模块：从 nodemods 加载子图，压栈后从入口节点逐层执行
                    if (_calls.Count >= 20)
                        throw new InvalidOperationException("模块嵌套层数超过 20，疑似异常引用链，已中止");
                    var file = P("mod");
                    var name = string.IsNullOrWhiteSpace(P("modname")) ? file : P("modname");
                    if (string.IsNullOrWhiteSpace(file))
                        throw new InvalidOperationException("该模块卡片未绑定模块文件，请删除后从工具箱「🧩我的模块」重新拖入");
                    var path = SubflowLibrary.ResolvePath(file);
                    if (!File.Exists(path))
                        throw new InvalidOperationException($"模块文件已丢失：{file}（重新导入同名模块，或删除该模块卡片）");
                    var mod = SubflowLibrary.FromFile(path);
                    if (mod.Nodes.Count == 0)
                        throw new InvalidOperationException($"模块【{name}】是空的，没有任何节点");
                    if (_calls.Any(c => string.Equals(c.ModFile, path, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException($"模块【{name}】直接或间接调用了自己（循环引用），已阻止");
                    var entry = mod.Entry();
                    if (entry == null)
                        throw new InvalidOperationException($"模块【{name}】找不到入口节点");
                    _pendingGraph = mod.ToFlowGraph();
                    _pendingEntryId = entry.Id;
                    _pendingModFile = path;
                    _pendingModName = name;
                    Log("info", $"🧩 进入模块【{name}】（{mod.Nodes.Count} 个节点）");
                    return (Flow.IntoSub, null);
                }
                case "break":
                    if (_frames.Count == 0) { Log("warn", "跳出循环：不在循环内，忽略"); break; }
                    Log("info", "跳出循环");
                    return (Flow.Break, null);
                case "set_var":
                {
                    var raw = HostActions.Substitute(P("value"), _vars);
                    if (double.TryParse(raw, out double nv)) Set(P("name"), nv);
                    else Set(P("name"), raw);
                    Log("info", $"变量 {P("name")} = {raw}");
                    break;
                }
                case "calc":
                {
                    var (ok, val) = FlowExpr.Eval(P("expr"), _vars);
                    if (!ok) throw new InvalidOperationException($"表达式无法求值：{P("expr")}（检查变量名/拼写）");
                    Set(P("var"), val);
                    Log("info", $"{P("var")} = {val}");
                    break;
                }

                // ——— AI / 风控 ———
                case "ai_ask":
                {
                    var prompt = HostActions.Substitute(P("prompt"), _vars);
                    Log("wait", "AI 决策中…");
                    var reply = AskAi(prompt, P("model"));
                    Set(P("var"), reply);
                    Log("info", $"AI回复 → {Trunc(reply, 60)}");
                    break;
                }
                case "ai_branch":
                {
                    var prompt = HostActions.Substitute(P("question"), _vars);
                    Log("wait", "AI 是/否判断中…");
                    var reply = AskAi(prompt, P("model")) ?? "";
                    Set(P("var"), reply);
                    bool yes = reply.Contains("是") || reply.ToUpperInvariant().Contains("YES");
                    Log(yes ? "buy" : "wait", $"AI判断 → {(yes ? "是" : "否")}（{Trunc(reply, 30)}）");
                    return (Flow.Edge, yes ? "yes" : "no");
                }
                case "ai_execute":
                {
                    var prompt = HostActions.Substitute(P("prompt"), _vars);
                    Log("wait", "AI 三选一执行中…");
                    var reply = (AskAi(prompt, P("model")) ?? "").ToUpperInvariant();
                    Set(P("var"), reply);
                    string port = "c";
                    if (ContainsKw(reply, P("kwA"))) port = "a";
                    else if (ContainsKw(reply, P("kwB"))) port = "b";
                    else if (ContainsKw(reply, P("kwC"))) port = "c";
                    Log(port == "a" ? "buy" : port == "b" ? "sell" : "wait", $"AI执行 → 方案{port.ToUpperInvariant()}");
                    return (Flow.Edge, port);
                }
                case "ban_check":
                {
                    var text = _act.OcrScreen();
                    var words = string.IsNullOrWhiteSpace(P("words"))
                        ? "封禁,封号,封停,冻结,异常,违规,惩罚,限制,警告" : P("words");
                    bool hit = HostActions.SplitWords(words).Any(k => text.Contains(k.Trim()));
                    if (hit) { Log("error", "封禁检测：命中风险关键词！"); return (Flow.Edge, "hit"); }
                    return (Flow.Edge, "safe");
                }

                case "lua_script":
                {
                    var code = P("code");
                    if (string.IsNullOrWhiteSpace(code)) { Log("warn", "Lua 脚本代码为空"); break; }
                    Log("info", "执行 Lua 脚本");
                    try
                    {
                        var script = new Script();
                        // 暴露流程变量为全局 table
                        var varsTable = new Table(script);
                        foreach (var kv in _vars)
                            varsTable[kv.Key] = DynValue.FromObject(script, kv.Value?.ToString());
                        script.Globals["vars"] = varsTable;
                        // log 函数桥接
                        script.Globals["log"] = (Func<string, string, string>)((level, msg) => { Log(level, msg); return msg; });

                        script.DoString(code);
                        // 把 vars table 的变更同步回 _vars
                        var vt = (Table)((DynValue)script.Globals["vars"]).Table;
                        foreach (var keyDv in vt.Keys)
                        {
                            string key = keyDv.ToString();
                            var dv = vt.Get(keyDv);
                            if (dv == null || dv.Type == DataType.Nil) continue;
                            Set(key, (object)dv.ToString());
                        }
                        VarsChanged?.Invoke();
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log("error", $"Lua 脚本错误：{ex.Message}");
                        return (Flow.Edge, "error");
                    }
                }

                case "python_script":
                {
                    var code = P("code");
                    if (string.IsNullOrWhiteSpace(code)) { Log("warn", "Python 脚本代码为空"); break; }
                    var py = P("python") ?? "python";
                    var timeout = Math.Max(1, I("timeout", 30));
                    Log("info", $"执行 Python 脚本（{py}，超时 {timeout}s）");
                    try
                    {
                        using var tmp = new TempFile($"{Guid.NewGuid():N}.py", code);
                        var inputJson = JsonSerializer.Serialize(new { vars = _vars.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString()) });
                        var psi = new ProcessStartInfo(py, $"\"{tmp.Path}\"")
                        {
                            RedirectStandardInput = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            EnvironmentVariables = { ["__INPUT__"] = inputJson }
                        };
                        using var proc = Process.Start(psi);
                        if (proc == null) throw new InvalidOperationException($"无法启动 {py}");
                        proc.StandardInput.Write(inputJson);
                        proc.StandardInput.Close();
                        if (!proc.WaitForExit(timeout * 1000))
                        {
                            try { proc.Kill(true); } catch { }
                            Log("error", $"Python 脚本超时（>{timeout}s）");
                            return (Flow.Edge, "error");
                        }
                        var stderr = proc.StandardError.ReadToEnd().Trim();
                        if (!string.IsNullOrWhiteSpace(stderr)) Log("info", $"py：{stderr}");
                        if (proc.ExitCode != 0)
                        {
                            Log("error", $"Python 脚本返回 {proc.ExitCode}：{(stderr.Length > 300 ? stderr[..300] + "…" : stderr)}");
                            return (Flow.Edge, "error");
                        }
                        var stdout = proc.StandardOutput.ReadToEnd().Trim();
                        if (!string.IsNullOrWhiteSpace(stdout))
                        {
                            try
                            {
                                var doc = JsonDocument.Parse(stdout);
                                if (doc.RootElement.TryGetProperty("vars", out var varsEl))
                                {
                                    if (varsEl.ValueKind == JsonValueKind.Object)
                                        foreach (var p in varsEl.EnumerateObject())
                                            Set(p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText());
                                    VarsChanged?.Invoke();
                                }
                                else Log("info", $"py 输出：{(stdout.Length > 200 ? stdout[..200] + "…" : stdout)}");
                            }
                            catch { Log("info", $"py 输出非JSON：{(stdout.Length > 200 ? stdout[..200] + "…" : stdout)}"); }
                        }
                        break;
                    }
                    catch (Exception ex) when (ex is FileNotFoundException or Win32Exception)
                    {
                        Log("error", $"找不到 python：{py} —— 请在「python 路径」填完整路径或先安装 Python 并加入 PATH");
                        return (Flow.Edge, "error");
                    }
                    catch (Exception ex)
                    {
                        Log("error", $"Python 执行失败：{ex.Message}");
                        return (Flow.Edge, "error");
                    }
                }

                default:
                    Log("warn", $"未知节点类型: {n.Type}"); break;
            }
            return (Flow.Edge, "");
        }

        // right_click 修正：上面 case 里不能再调左键 Click，独立处理
        // （由 RunTemplateNode 之外，这里用专用分支）

        private (Flow, string?) RunTemplateNode(FlowNode n,
            Func<string, Point> Pt, Func<string, Rectangle> Rc,
            Func<string, double, double> D, Func<string, int, int> I,
            Func<string, string> P, Action<string, object?> Set)
        {
            string tpl = P("tpl");
            if (string.IsNullOrWhiteSpace(tpl)) { Log("error", "未选择模板（属性面板标定/选择）"); return (Flow.Edge, "miss"); }
            if (!TemplateManager.Exists(tpl)) { Log("error", $"模板不存在: {tpl}"); return (Flow.Edge, "miss"); }
            var region = Rc("region");
            double th = Math.Clamp(D("threshold", 0.85), 0.1, 1.0);

            bool Find(out int x, out int y, out double sc)
                => _act.FindTemplate(tpl, region, th, out x, out y, out sc);

            switch (n.Type)
            {
                case "find_template":
                {
                    bool ok = Find(out int x, out int y, out double sc);
                    string px = P("prefix");
                    if (ok) { Set(px + "X", (double)x); Set(px + "Y", (double)y); Set(px + "相似度", sc); }
                    Log(ok ? "wait" : "info", $"模板匹配「{tpl}」→ {(ok ? $"命中 {sc:P0} @{x},{y}" : "未命中")}");
                    return (Flow.Edge, ok ? "found" : "miss");
                }
                case "click_template":
                {
                    bool ok = Find(out int x, out int y, out double sc);
                    if (!ok) { Log("info", $"找图点击「{tpl}」未命中"); return (Flow.Edge, "miss"); }
                    x += I("offsetx", 0); y += I("offsety", 0);
                    int btn = HostActions.ButtonIndex(P("button"));
                    _act.MouseButton(new Point(x, y), btn, true);
                    Wait(Math.Max(20, I("dwell", 80)));
                    _act.MouseButton(new Point(x, y), btn, false);
                    Log("action", $"找图点击「{tpl}」{P("button")} @{x},{y} ({sc:P0})");
                    return (Flow.Edge, "found");
                }
                case "wait_template":
                {
                    int timeoutMs = I("timeout", 10) * 1000;
                    int interval = Math.Max(100, I("interval", 500));
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < timeoutMs)
                    {
                        if (Find(out int x, out int y, out double sc))
                        {
                            Set(P("prefix") + "X", (double)x); Set(P("prefix") + "Y", (double)y);
                            Log("wait", $"「{tpl}」出现了 @{x},{y} ({sc:P0})");
                            return (Flow.Edge, "found");
                        }
                        Wait(interval);
                    }
                    Log("warn", $"等待「{tpl}」超时");
                    return (Flow.Edge, "timeout");
                }
                case "wait_gone":
                {
                    int timeoutMs = I("timeout", 10) * 1000;
                    int interval = Math.Max(100, I("interval", 500));
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < timeoutMs)
                    {
                        if (!Find(out _, out _, out _)) { Log("wait", $"「{tpl}」已消失"); return (Flow.Edge, "gone"); }
                        Wait(interval);
                    }
                    Log("warn", $"等待「{tpl}」消失超时");
                    return (Flow.Edge, "timeout");
                }
            }
            return (Flow.Edge, "");
        }

        private (Flow, string?) LoopGate(FlowNode n, Func<string, int, int> I, Func<string, string> P)
        {
            var frame = _frames.FindLastIndex(f => f.HeaderId == n.Id);
            if (n.Type == "loop_n")
            {
                if (frame < 0)
                {
                    int count = Math.Max(0, I("count", 0));
                    _frames.Add(new Frame { HeaderId = n.Id, Remaining = count, Infinite = count == 0 });
                    frame = _frames.Count - 1;
                    Log("info", count == 0 ? "无限循环开始（仅急停可退出）" : $"循环开始，共 {count} 次");
                }
                var fr = _frames[frame];
                if (fr.Infinite)
                {
                    if (_g.OutEdge(n.Id, "body") == null) { Log("warn", "循环体未连线"); _frames.RemoveAt(frame); return (Flow.Edge, "done"); }
                    fr.Iteration++;
                    Log("info", $"执行循环体（第 {fr.Iteration} 轮，无限循环）");
                    return (Flow.Edge, "body");
                }
                if (fr.Remaining > 0)
                {
                    fr.Remaining--;
                    int left = fr.Remaining;
                    if (_g.OutEdge(n.Id, "body") == null) { Log("warn", "循环体未连线"); _frames.RemoveAt(frame); return (Flow.Edge, "done"); }
                    Log("info", $"执行循环体（剩余 {left} 次）");
                    return (Flow.Edge, "body");
                }
                _frames.RemoveAt(frame);
                Log("info", "循环完成");
                return (Flow.Edge, "done");
            }
            else // loop_while
            {
                var (ok, val) = FlowExpr.Eval(P("expr"), _vars);
                bool go = ok && FlowExpr.IsTrue(val);
                if (!go)
                {
                    if (frame >= 0) _frames.RemoveAt(frame);
                    Log("info", "条件循环结束");
                    return (Flow.Edge, "done");
                }
                if (frame < 0) _frames.Add(new Frame { HeaderId = n.Id });
                if (_g.OutEdge(n.Id, "body") == null) { Log("warn", "循环体未连线"); return (Flow.Edge, "done"); }
                Log("info", $"条件成立，执行循环体（{P("expr")}）");
                return (Flow.Edge, "body");
            }
        }

        private static bool ContainsKw(string replyUpper, string kw)
        {
            foreach (var k in HostActions.SplitWords(kw))
                if (replyUpper.Contains(k.Trim().ToUpperInvariant())) return true;
            return false;
        }

        private static string AskAi(string prompt, string model)
        {
            try
            {
                return string.IsNullOrWhiteSpace(model)
                    ? DeepSeekClient.Ask(prompt)
                    : DeepSeekClient.Ask(prompt, model.Trim());
            }
            catch (Exception ex) { throw new InvalidOperationException($"AI 请求失败: {ex.Message}"); }
        }

        private static string Trunc(string s, int n)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n) + "…");

        private void Log(string level, string msg) => Logged?.Invoke(level, msg);
    }
}
