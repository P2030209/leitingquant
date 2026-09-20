using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Diagnostics;
using Microsoft.Win32;
using System.Text.Json;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Threading;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using WindowSpy.Ocr;
using System.Management;

namespace WindowSpy
{
    public partial class MainWindow : System.Windows.Window, IHostAdapter, IFlowHost
    {
        private IntPtr _boundAHwnd = IntPtr.Zero;
        private IntPtr _boundBHwnd = IntPtr.Zero;
        /// <summary>IHostAdapter 当前操作目标：'A' 或 'B'（流程节点 target 参数切换）</summary>
        private char _adapterTarget = 'A';
        private IntPtr ActiveHwnd => _adapterTarget == 'B' ? _boundBHwnd : _boundAHwnd;
        private readonly OnnxOcrHelper _ocr = new();
        private IntPtr _hwnd = IntPtr.Zero;
        private HwndSource? _hwndSource;
        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_ID = 1001;    // F12 紧急停止（固定）
        private const int HOTKEY_ID2 = 1002;   // F9 呼出/隐藏
        private const uint VK_F9 = 0x78;
        private const uint VK_F12 = 0x7B;
        private NativeMethods.LowLevelMouseProc _hookProc;
        private IntPtr _hookID = IntPtr.Zero;
        private readonly System.Collections.Generic.Queue<DateTime> _rightClickTimes = new();

        private readonly object _logLock = new object();
        private readonly System.Collections.Generic.Queue<(DateTime ts, string text)> _pendingLogs = new();
        private bool _logFlushScheduled = false;

        /// <summary>软件显示版本：单一来源 = csproj 的 &lt;Version&gt;（程序集版本），格式 v主.次.修订</summary>
        public static string AppVersion
        {
            get
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "v?" : $"v{v.Major}.{v.Minor}.{v.Build}";
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            TitleVersionText.Text = AppVersion;
            AboutVersionText.Text = $"版本 {AppVersion} · 三角洲行动 · 子弹行情自动化助手";
            this.Loaded += MainWindow_Loaded;
            _ocr.Logger = AppendLog;
            _ocr.UseGpu = GpuAccelCheck?.IsChecked == true;
            _hookProc = HookCallback;
            DeepSeekClient.LoadConfig(); // 读取 deepseek.json（ApiKey/Model）
            if (AiKeyBox != null && !string.IsNullOrEmpty(DeepSeekClient.ApiKey)) AiKeyBox.Password = DeepSeekClient.ApiKey;
            if (AiModelBox != null && !string.IsNullOrEmpty(DeepSeekClient.Model)) AiModelBox.Text = DeepSeekClient.Model;
            AppendLog($"授权: {LicenseManager.LicenseId}，有效期至 {LicenseManager.ExpiresAtText}，功能[{string.Join(",", LicenseManager.Features)}]");

            if (GpuAccelCheck != null)
            {
                GpuAccelCheck.Checked += (s, e) => { _ocr.UseGpu = true; AppendLog("设置：已启用 GPU 加速识别"); };
                GpuAccelCheck.Unchecked += (s, e) => { _ocr.UseGpu = false; AppendLog("设置：已强制切换为 CPU 模式"); };
            }
        }

        public List<string> GetNetworkAdapters()
        {
            var list = new List<string>();
            try
            {
                var query = new SelectQuery("Win32_NetworkAdapter", "PhysicalAdapter=True AND NetConnectionID IS NOT NULL");
                using var searcher = new ManagementObjectSearcher(query);
                foreach (ManagementObject mo in searcher.Get())
                {
                    string name = mo["NetConnectionID"]?.ToString() ?? mo["Name"]?.ToString() ?? "Unknown";
                    list.Add(name);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[网络] 加载网卡列表失败: {ex.Message}");
            }
            return list;
        }

        public IntPtr GetBoundHwnd(TargetType target)
        {
            return target == TargetType.A ? _boundAHwnd : _boundBHwnd;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var helper = new WindowInteropHelper(this);
            _hwnd = helper.Handle;
            _hwndSource = HwndSource.FromHwnd(_hwnd);
            _hwndSource?.AddHook(WndProc);
            RegisterStopHotkey();
            try { NativeMethods.RegisterHotKey(_hwnd, HOTKEY_ID2, 0, VK_F9); } catch { }
        }
        protected override void OnClosed(EventArgs e)
        {
            try { NativeMethods.UnregisterHotKey(_hwnd, HOTKEY_ID); } catch { }
            try { NativeMethods.UnregisterHotKey(_hwnd, HOTKEY_ID2); } catch { }
            try { UninstallHook(); } catch { }
            try { _flowRunner?.Stop(); } catch { }
            base.OnClosed(e);
        }

        /// <summary>注册固定 F12 全局急停热键（窗口隐藏时也生效）</summary>
        private void RegisterStopHotkey()
        {
            try { NativeMethods.UnregisterHotKey(_hwnd, HOTKEY_ID); } catch { }
            try { NativeMethods.RegisterHotKey(_hwnd, HOTKEY_ID, 0, VK_F12); } catch { }
        }

        /// <summary>安装低级鼠标钩子：5 秒内右键 10 次 = 防卡死急停（流程启动时安装）</summary>
        private void InstallHook()
        {
            if (_hookID != IntPtr.Zero) return;
            lock (_rightClickTimes) _rightClickTimes.Clear();
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule? curModule = curProcess.MainModule)
            {
                if (curModule != null)
                    _hookID = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _hookProc, NativeMethods.GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        private void UninstallHook()
        {
            if (_hookID != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hookID);
                _hookID = IntPtr.Zero;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (int)wParam == NativeMethods.WM_RBUTTONDOWN)
            {
                lock (_rightClickTimes)
                {
                    var now = DateTime.Now;
                    _rightClickTimes.Enqueue(now);
                    while (_rightClickTimes.Count > 0 && (now - _rightClickTimes.Peek()).TotalSeconds > 5)
                        _rightClickTimes.Dequeue();

                    if (_rightClickTimes.Count >= 10)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            AppendLog("触发防卡死保护(5秒内右键10次)，正在急停流程");
                            StopFlowProject();
                        });
                        _rightClickTimes.Clear();
                    }
                }
            }
            return NativeMethods.CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == HOTKEY_ID)
                {
                    AppendLog("F12 全局急停：停止当前流程工程");
                    StopFlowProject();
                    Dispatcher.BeginInvoke(new Action(() => { Show(); Activate(); }));
                    handled = true;
                }
                else if (id == HOTKEY_ID2)
                {
                    Dispatcher.BeginInvoke(new Action(ToggleWindowVisible));
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        private void ToggleWindowVisible()
        {
            if (Visibility == Visibility.Visible) Hide();
            else { Show(); Activate(); }
        }

        private void UpdateBoundTitles()
        {
            BoundATitle.Text = _boundAHwnd == IntPtr.Zero ? "" : NativeMethods.GetWindowTitle(_boundAHwnd);
            BoundBTitle.Text = _boundBHwnd == IntPtr.Zero ? "" : NativeMethods.GetWindowTitle(_boundBHwnd);
        }

        private void BindA_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var picker = new OverlayPickWindow();
            var ok = picker.ShowDialog();
            if (ok == true)
            {
                var p = picker.ClickPoint;
                var pt = new WindowSpy.NativeMethods.POINT { X = (int)p.X, Y = (int)p.Y };
                var hwnd = NativeMethods.WindowFromPoint(pt);
                const uint GA_ROOT = 2;
                hwnd = NativeMethods.GetAncestor(hwnd, GA_ROOT);
                _boundAHwnd = hwnd;
                UpdateBoundTitles();
                AppendLog("已绑定窗口A(拖拽)");
            }
        }

        private void BindB_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var picker = new OverlayPickWindow();
            var ok = picker.ShowDialog();
            if (ok == true)
            {
                var p = picker.ClickPoint;
                var pt = new WindowSpy.NativeMethods.POINT { X = (int)p.X, Y = (int)p.Y };
                var hwnd = NativeMethods.WindowFromPoint(pt);
                const uint GA_ROOT = 2;
                hwnd = NativeMethods.GetAncestor(hwnd, GA_ROOT);
                _boundBHwnd = hwnd;
                UpdateBoundTitles();
                AppendLog("已绑定窗口B(拖拽)");
            }
        }

        private int ParseInt(string? s, int def)
        {
            if (string.IsNullOrWhiteSpace(s)) return def;
            var digits = new string(s.Where(char.IsDigit).ToArray());
            if (string.IsNullOrEmpty(digits)) return def;
            if (int.TryParse(digits, out var v)) return v;
            return def;
        }

        private static string Trunc(string text, int max) =>
            string.IsNullOrEmpty(text) || text.Length <= max ? text : text.Substring(0, max) + "…";

        /// <summary>获取主题资源画刷（深色工业主题）</summary>
        private static System.Windows.Media.Brush ResBrush(string key)
            => (System.Windows.Media.Brush)System.Windows.Application.Current.Resources[key];

        private void TestMarketButton_Click(object sender, RoutedEventArgs e)
        {
            var url = MarketUrlBox?.Text?.Trim() ?? "";
            var root = MarketService.NormalizeRoot(url);
            AppendLog($"行情测试：正在请求 {root}/api/market/ammo-catalog ...");
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var quotes = MarketService.FetchCatalog(url);
                    Dispatcher.Invoke(() =>
                    {
                        var top3 = string.Join(" ｜ ", quotes.Take(3)
                            .Select(q => $"{q.Name} {q.Price:0.#}({q.ChangePct:+0.#;-0.#}%)"));
                        AppendLog($"行情 ✓ {quotes.Count}条报价 · {top3}");
                    });
                    // 预演第一条报价的历史序列拉取 + 推送到监控台画图
                    var first = quotes.FirstOrDefault();
                    if (first != null)
                    {
                        try
                        {
                            var hist = MarketService.FetchHistory(url, first.Name, 60);
                            if (hist.Count > 0)
                            {
                                int maN = 20;
                                TradeBus.PublishTick(new MarketTick
                                {
                                    Bullet = first.Name, Price = first.Price, ChangePct = first.ChangePct,
                                    History = hist, MAPeriod = maN,
                                    MA = QuantMath.MA(hist, maN), RSI = QuantMath.RSI(hist, 14),
                                    Position = QuantMath.Position(hist, maN)
                                });
                                Dispatcher.Invoke(() =>
                                    AppendLog($"历史 ✓ {first.Name} {hist.Count}点 最新{hist[hist.Count - 1]:0.#}（已推送到监控台走势图）"));
                            }
                            else Dispatcher.Invoke(() => AppendLog($"历史序列：{first.Name} 暂无历史点（将仅用现价逐步积累）"));
                        }
                        catch (Exception hex)
                        {
                            Dispatcher.Invoke(() => AppendLog($"历史序列测试失败(执行时现价不受影响): {hex.Message}"));
                        }
                    }
                }
                catch (Exception tex)
                {
                    Dispatcher.Invoke(() => AppendLog($"行情测试失败: {tex.Message}"));
                }
            });
        }

        // ===== DeepSeek AI 决策 =====
        private void SaveAiConfig_Click(object sender, RoutedEventArgs e)
        {
            DeepSeekClient.ApiKey = AiKeyBox?.Password?.Trim() ?? "";
            DeepSeekClient.Model = string.IsNullOrWhiteSpace(AiModelBox?.Text) ? "deepseek-chat" : AiModelBox.Text.Trim();
            DeepSeekClient.SaveConfig();
            AppendLog($"AI配置已保存: 模型 {DeepSeekClient.Model}，Key {(string.IsNullOrEmpty(DeepSeekClient.ApiKey) ? "(空)" : "(已设置)")}（存于 deepseek.json）");
        }

        private void TestAiButton_Click(object sender, RoutedEventArgs e)
        {
            DeepSeekClient.ApiKey = AiKeyBox?.Password?.Trim() ?? "";
            DeepSeekClient.Model = string.IsNullOrWhiteSpace(AiModelBox?.Text) ? "deepseek-chat" : AiModelBox.Text.Trim();
            if (string.IsNullOrEmpty(DeepSeekClient.ApiKey)) { AppendLog("AI测试失败: 请先填写 API Key"); return; }
            AppendLog($"AI测试: 正在请求 DeepSeek [{DeepSeekClient.Model}] ...");
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var reply = DeepSeekClient.Ask("你是一个量化交易助手。收到请只回复两个字：收到");
                    Dispatcher.Invoke(() => AppendLog($"AI测试成功: {reply}"));
                }
                catch (Exception aex)
                {
                    Dispatcher.Invoke(() => AppendLog($"AI测试失败: {aex.Message}（Key 是否有效？网络是否可访问 api.deepseek.com？）"));
                }
            });
        }

        // ===== 自动倒卖向导（傻瓜模式） =====
        private void WzPickBuy_Click(object sender, RoutedEventArgs e) => WizardPick(WzBuyX, WzBuyY, "买入");
        private void WzPickSell_Click(object sender, RoutedEventArgs e) => WizardPick(WzSellX, WzSellY, "卖出");
        private void WzPickTrade_Click(object sender, RoutedEventArgs e) => WizardPick(WzTradeX, WzTradeY, "交易行入口");

        // ================= 流程工坊 / 流程工程运行台 =================
        private FlowEditorWindow? _flowWin;

        private static string FlowProjectsDir => System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "flows");

        private void OpenFlow_Click(object sender, RoutedEventArgs e) => OpenFlowEditor(null, null);

        /// <summary>mode: null=记住的最近工程；"new"=空白新工程；"file"=打开指定工程</summary>
        private FlowEditorWindow? OpenFlowEditor(string? mode, string? fileName)
        {
            if (!LicenseManager.HasFeature("pro"))
            {
                AppendLog("⛔ 流程工坊属于 Pro 功能，当前授权不包含 pro，请联系管理员升级");
                MessageBox.Show("流程工坊属于 Pro 功能，当前授权不包含。\n请联系管理员升级 license。", "需要 Pro 授权",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            if (_boundAHwnd == IntPtr.Zero) { AppendLog("流程工坊: 请先绑定窗口A（游戏窗口），坐标拾取依赖绑定"); }
            if (_flowWin != null) { _flowWin.Activate(); return _flowWin; }
            var win = new FlowEditorWindow(this) { Owner = this };
            if (mode == "new") win.StartBlankProject();
            else if (mode == "file" && !string.IsNullOrEmpty(fileName))
                win.OpenProjectFile(System.IO.Path.Combine(FlowProjectsDir, fileName));
            win.Closed += (s, ev) =>
            {
                _flowWin = null;
                string? savedName = null;
                try { savedName = win.CurrentFilePath != null ? System.IO.Path.GetFileName(win.CurrentFilePath) : null; } catch { }
                RefreshFlowProjects(savedName);
                // 出错暂停期间改完工程保存：热替换图，继续时从出错节点重跑
                if (_flowRunner != null && _flowRunner.State == RunState.ErrorPaused && _flowProjectFile != null)
                {
                    try
                    {
                        var ng = FlowGraph.FromJson(File.ReadAllText(_flowProjectFile));
                        if (_flowRunner.ReloadGraph(ng))
                        {
                            _flowGraph = ng;
                            AppendLog("[流程] 工程已热更新，点「▶ 继续」将重跑出错节点");
                        }
                        else
                        {
                            MessageBox.Show("出错的节点已被删除，无法从该步继续。\n请点「■ 停止」后重新启动工程。",
                                "无法继续", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                    catch (Exception ex) { AppendLog($"[流程] 工程热更新失败: {ex.Message}"); }
                }
            };
            _flowWin = win;
            win.Show();
            AppendLog("流程工坊已打开：拖节点入画布、圆点拉线、属性面板点选坐标，保存后回主界面运行台启动");
            return win;
        }

        private FlowRunner? _flowRunner;
        private CancellationTokenSource? _flowCts;
        private string? _flowProjectFile;
        private FlowGraph? _flowGraph;
        private string? _flowCurNodeId;

        bool IFlowHost.IsFlowRunning =>
            _flowRunner != null && (_flowRunner.State == RunState.Running
                                    || _flowRunner.State == RunState.Paused
                                    || _flowRunner.State == RunState.ErrorPaused);

        bool IFlowHost.HasWindowA => _boundAHwnd != IntPtr.Zero;

        private void FlowProjRefresh_Click(object sender, RoutedEventArgs e) => RefreshFlowProjects();

        private void RefreshFlowProjects(string? selectFile = null)
        {
            if (FlowProjectCombo == null) return;
            string? keep = selectFile ?? FlowProjectCombo.SelectedItem as string;
            FlowProjectCombo.Items.Clear();
            try
            {
                if (Directory.Exists(FlowProjectsDir))
                {
                    foreach (var name in Directory.GetFiles(FlowProjectsDir, "*.json")
                                 .Select(System.IO.Path.GetFileName)
                                 .Where(n => n != "_last.json")
                                 .OrderBy(n => n))
                        FlowProjectCombo.Items.Add(name);
                }
            }
            catch { }
            if (keep != null && FlowProjectCombo.Items.Contains(keep)) FlowProjectCombo.SelectedItem = keep;
            else if (FlowProjectCombo.Items.Count > 0) FlowProjectCombo.SelectedIndex = 0;
        }

        private void FlowProjNew_Click(object sender, RoutedEventArgs e) => OpenFlowEditor("new", null);

        private void FlowProjEdit_Click(object sender, RoutedEventArgs e)
        {
            var file = FlowProjectCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(file))
            {
                AppendLog("[流程] 还没有工程：点「🧩 新建工程」，在流程工坊里编排并保存");
                OpenFlowEditor("new", null);
                return;
            }
            OpenFlowEditor("file", file);
        }

        private static readonly Dictionary<string, string> FlowLvTag = new()
        {
            {"info","流程"}, {"action","动作"}, {"buy","买入"}, {"sell","卖出"},
            {"claim","邮件"}, {"wait","等待"}, {"warn","警告"}, {"error","错误"}
        };

        private void FlowLogSink(string level, string msg)
            => AppendLog($"[{FlowLvTag.GetValueOrDefault(level, level)}] {msg}");

        private void FlowRun_Click(object sender, RoutedEventArgs e) => StartSelectedProject(silent: false);

        /// <summary>
        /// 启动运行台当前选中工程。silent=false 给手动按钮用（弹窗提示）；
        /// silent=true 给定时任务用（仅写日志、不弹窗）。返回 false=未启动。
        /// </summary>
        private bool StartSelectedProject(bool silent)
        {
            if (((IFlowHost)this).IsFlowRunning) return false;
            if (_boundAHwnd == IntPtr.Zero)
            {
                if (silent) AppendLog("[定时] 启动失败：尚未绑定窗口A");
                else MessageBox.Show("请先在首页把准星拖到游戏窗口上绑定窗口A，流程操作依赖绑定窗口。",
                    "未绑定窗口", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            var file = FlowProjectCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(file))
            {
                AppendLog("[流程] 没有可用工程：点「🧩 新建工程」编排后保存到 flows 目录，再点刷新");
                return false;
            }
            var path = System.IO.Path.Combine(FlowProjectsDir, file);
            FlowGraph g;
            try { g = FlowGraph.FromJson(File.ReadAllText(path)); }
            catch (Exception ex)
            {
                if (silent) AppendLog($"[定时] 工程读取失败：{ex.Message}");
                else MessageBox.Show("工程读取失败：" + ex.Message, "工程错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            if (!g.Nodes.Any(n => n.Type == "start"))
            {
                if (silent) AppendLog($"[定时] 启动失败：工程 {file} 里没有「开始」节点");
                else MessageBox.Show("工程里没有「开始」节点，无法启动。", "工程错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            _flowProjectFile = path;
            _flowGraph = g;
            _flowCurNodeId = null;
            _flowCts = new CancellationTokenSource();
            var r = new FlowRunner(this, FlowLogSink);
            r.HighlightChanged += OnFlowHighlight;
            r.VarsChanged += OnFlowVars;
            r.NodeError += OnFlowNodeError;
            r.StateChanged += OnFlowState;
            r.Finished += OnFlowFinished;
            _flowRunner = r;
            AppendLog($"[流程] ▶ 启动工程：{file}（{g.Nodes.Count}个节点）——窗口将隐藏，F9呼出，F12急停");
            AppendLog("[流程] 提示：游戏请用「无边框窗口化」显示模式；独占全屏下后台抓屏与遮罩可能失效");
            InstallHook();   // 5秒右键10次防卡死急停
            Task.Run(() =>
            {
                try { r.Run(g, _flowCts.Token); }
                catch (Exception ex) { AppendLog($"[流程] 运行异常: {ex.Message}"); }
            });
            Hide();
            return true;
        }

        private void FlowPause_Click(object sender, RoutedEventArgs e) => _flowRunner?.Pause();
        private void FlowResume_Click(object sender, RoutedEventArgs e) => _flowRunner?.Resume();
        private void FlowStop_Click(object sender, RoutedEventArgs e) => StopFlowProject();

        private void StopFlowProject()
        {
            var r = _flowRunner;
            if (r == null) return;
            if (r.State is RunState.Idle or RunState.Finished or RunState.Stopped) return;
            AppendLog("[流程] ■ 急停中（等待中的操作会立即中断）…");
            r.Stop();
        }

        private void OnFlowHighlight(string? nodeId)
        {
            _flowCurNodeId = nodeId;
            Dispatcher.BeginInvoke(new Action(UpdateFlowStatusLine));
        }

        private void OnFlowVars() => Dispatcher.BeginInvoke(new Action(UpdateFlowStatusLine));

        private void OnFlowNodeError(string nodeId, string title, string msg)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Show();
                Activate();
                AppendLog($"[流程] ⛔ 节点「{title}」出错已暂停：{msg}");
                FlowStateLamp.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x4D, 0x55));
                FlowStatusText.Text = $"⛔ 「{title}」出错已暂停：{msg} —— 点「📝 编辑工程」修正保存后，点「▶ 继续」从该步重跑";
            }));
        }

        private void OnFlowState(RunState s) => Dispatcher.BeginInvoke(new Action(() =>
        {
            SetFlowButtons(s);
            UpdateFlowStatusLine();
        }));

        private void OnFlowFinished(bool ok) => Dispatcher.BeginInvoke(new Action(() =>
        {
            Show();
            Activate();
            SetFlowButtons(RunState.Idle);
            FlowStateLamp.Fill = ok
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x58, 0xE0, 0x7D))
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5A, 0x64, 0x72));
            FlowStatusText.Text = ok
                ? "状态：✅ 流程正常跑完 · F9 可随时呼出/隐藏窗口"
                : "状态：■ 流程已停止 · 可重新选择工程启动";
            AppendLog(ok ? "[流程] ✅ 流程正常跑完" : "[流程] ■ 流程已停止");
            _flowRunner = null;
            try { _flowCts?.Dispose(); } catch { }
            _flowCts = null;
        }));

        private void SetFlowButtons(RunState s)
        {
            bool active = s is RunState.Running or RunState.Paused or RunState.ErrorPaused;
            FlowRunBtn.IsEnabled = !active;
            FlowPauseBtn.IsEnabled = s == RunState.Running;
            FlowResumeBtn.IsEnabled = s is RunState.Paused or RunState.ErrorPaused;
            FlowStopBtn.IsEnabled = active;
            FlowProjectCombo.IsEnabled = !active;
        }

        private void UpdateFlowStatusLine()
        {
            var r = _flowRunner;
            string stateName = r?.State switch
            {
                RunState.Running => "🟢 运行中",
                RunState.Paused => "🟠 已暂停",
                RunState.ErrorPaused => "🔴 出错暂停",
                RunState.Finished => "✅ 已完成",
                RunState.Stopped => "■ 已停止",
                _ => "⚪ 空闲"
            };
            string node = "";
            if (_flowCurNodeId != null && _flowGraph?.Node(_flowCurNodeId) is FlowNode n)
                node = $" ｜ 当前：{n.Alias}";
            string vars = "";
            if (r != null)
            {
                var keys = r.Vars.Keys
                    .Where(k => k.Contains("哈弗币") || k.Contains("余额") || k.Contains("利润"))
                    .Take(3).ToList();
                if (keys.Count > 0)
                    vars = " ｜ " + string.Join(" ｜ ", keys.Select(k => $"{k}={r.Vars[k]}"));
            }
            if (r != null && r.State is RunState.Running or RunState.Paused)
            {
                FlowStateLamp.Fill = r.State == RunState.Running
                    ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x58, 0xE0, 0x7D))
                    : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x8A, 0x2A));
            }
            FlowStatusText.Text = $"状态：{stateName}{node}{vars} ｜ F9 呼出/隐藏 · F12 急停";
        }

        IHostAdapter IFlowHost.Adapter => this;

        System.Drawing.Point? IFlowHost.PickCoord(string label)
        {
            if (_boundAHwnd == IntPtr.Zero)
            {
                AppendLog($"点选{label}失败: 请先绑定窗口A");
                return null;
            }
            var picker = new OverlayPickWindow();
            if (picker.ShowDialog() == true)
            {
                var wrect = NativeMethods.GetRect(_boundAHwnd);
                var p = new System.Drawing.Point((int)picker.ClickPoint.X - wrect.Left, (int)picker.ClickPoint.Y - wrect.Top);
                AppendLog($"流程工坊·{label}: {p.X},{p.Y}");
                return p;
            }
            return null;
        }

        System.Drawing.Rectangle? IFlowHost.PickRect()
        {
            if (_boundAHwnd == IntPtr.Zero)
            {
                AppendLog("框选失败: 请先绑定窗口A");
                return null;
            }
            var picker = new OverlaySelectWindow();
            if (picker.ShowDialog() == true)
            {
                var wr = NativeMethods.GetRect(_boundAHwnd);
                var s = picker.SelectedRect;
                int x = Math.Max(0, s.X - wr.Left), y = Math.Max(0, s.Y - wr.Top);
                var r = new System.Drawing.Rectangle(x, y,
                    Math.Min(s.Width, wr.Width - x), Math.Min(s.Height, wr.Height - y));
                AppendLog($"流程工坊·识别区域: {x},{y},{r.Width},{r.Height}");
                return r;
            }
            return null;
        }

        private void WizardPick(System.Windows.Controls.TextBox tbX, System.Windows.Controls.TextBox tbY, string label)
        {
            if (_boundAHwnd == IntPtr.Zero) { AppendLog($"点选{label}坐标失败: 请先在左侧拖拽绑定窗口A（游戏窗口）"); return; }
            var picker = new OverlayPickWindow();
            if (picker.ShowDialog() == true)
            {
                var wrect = NativeMethods.GetRect(_boundAHwnd);
                tbX.Text = ((int)picker.ClickPoint.X - wrect.Left).ToString();
                tbY.Text = ((int)picker.ClickPoint.Y - wrect.Top).ToString();
                AppendLog($"已记录{label}坐标（窗口A相对）: {tbX.Text},{tbY.Text}");
            }
        }

        private string WzStrategy()
        {
            if (WzStrategyBox?.SelectedItem is System.Windows.Controls.ComboBoxItem ci && ci.Content is string s && s.Length > 0)
                return s.Split(' ')[0];
            return "PROFIT";
        }

        private System.Drawing.Rectangle _wzWalletRect;
        private int _firstInferenceHintLogged;

        // —— 点选坐标 ——
        private void WzPickSearch_Click(object sender, RoutedEventArgs e) => WizardPick(WzSearchX, WzSearchY, "搜索框");
        private void WzPickResult_Click(object sender, RoutedEventArgs e) => WizardPick(WzResultX, WzResultY, "搜索结果第一项");
        private void WzPickPlus_Click(object sender, RoutedEventArgs e) => WizardPick(WzPlusX, WzPlusY, "数量+");
        private void WzPickMinus_Click(object sender, RoutedEventArgs e) => WizardPick(WzMinusX, WzMinusY, "数量−");
        private void WzPickMail_Click(object sender, RoutedEventArgs e) => WizardPick(WzMailX, WzMailY, "邮箱按钮");
        private void WzPickClaim_Click(object sender, RoutedEventArgs e) => WizardPick(WzClaimX, WzClaimY, "一键领取");
        private void WzPickClose_Click(object sender, RoutedEventArgs e) => WizardPick(WzCloseX, WzCloseY, "关闭邮箱");

        private void WzPickWallet_Click(object sender, RoutedEventArgs e)
        {
            if (_boundAHwnd == IntPtr.Zero) { AppendLog("余额识别: 请先绑定窗口A"); return; }
            var picker = new OverlaySelectWindow();
            if (picker.ShowDialog() == true)
            {
                var wr = NativeMethods.GetRect(_boundAHwnd);
                var s = picker.SelectedRect;
                int x = Math.Max(0, s.X - wr.Left), y = Math.Max(0, s.Y - wr.Top);
                _wzWalletRect = new System.Drawing.Rectangle(x, y,
                    Math.Min(s.Width, wr.Width - x), Math.Min(s.Height, wr.Height - y));
                WzWalletRectBox.Text = $"{x},{y},{_wzWalletRect.Width},{_wzWalletRect.Height}";
                AppendLog($"已记录余额识别区（窗口A相对）: {_wzWalletRect}");
            }
        }

        private void WzTestWallet_Click(object sender, RoutedEventArgs e)
        {
            if (_boundAHwnd == IntPtr.Zero) { AppendLog("余额识别: 请先绑定窗口A"); return; }
            if (_wzWalletRect.Width < 5) { AppendLog("余额识别: 请先框选区域"); return; }
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // 与引擎一致读原文：4,002K / 1.2M / 3万 等缩写单位不能丢
                    var text = ((IHostAdapter)this).OcrRegion(_wzWalletRect, false);
                    double parsed = 0;
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                        text ?? "", @"(\d{1,3}(?:[,，]\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?)\s*([KkMm萬万]?)"))
                    {
                        var num = m.Groups[1].Value.Replace(",", "").Replace("，", "");
                        if (!double.TryParse(num, out double v)) continue;
                        var u = m.Groups[2].Value;
                        if (u is "K" or "k") v *= 1_000;
                        else if (u is "M" or "m") v *= 1_000_000;
                        else if (u is "万" or "萬") v *= 10_000;
                        if (v > parsed) parsed = v;
                    }
                    Dispatcher.Invoke(() => AppendLog(parsed > 0
                        ? $"余额识别测试：原文「{text?.Trim()}」→ {parsed:0,0} 哈弗币"
                        : $"余额识别测试：原文「{text?.Trim()}」未解析出数字"));
                }
                catch (Exception ex) { Dispatcher.Invoke(() => AppendLog($"余额识别失败: {ex.Message}")); }
            });
        }

        // —— IHostAdapter：引擎的手和眼 ——
        string IHostAdapter.OcrRegion(System.Drawing.Rectangle relRect, bool numbersOnly)
        {
            if (ActiveHwnd == IntPtr.Zero) throw new InvalidOperationException($"未绑定窗口{_adapterTarget}，无法识别");
            using var bmp = NativeMethods.CaptureWindow(ActiveHwnd);
            using var mat = BitmapConverter.ToMat(bmp);
            int x = Math.Max(0, Math.Min(mat.Cols - 1, relRect.X));
            int y = Math.Max(0, Math.Min(mat.Rows - 1, relRect.Y));
            int w = Math.Max(1, Math.Min(mat.Cols - x, relRect.Width));
            int h = Math.Max(1, Math.Min(mat.Rows - y, relRect.Height));
            using var roi = new Mat(mat, new OpenCvSharp.Rect(x, y, w, h));
            if (Interlocked.Exchange(ref _firstInferenceHintLogged, 1) == 0)
                AppendLog("提示：首次推理识别会慢很多，这属于正常现象");
            var regions = _ocr.OcrAsync(roi).GetAwaiter().GetResult();
            var text = string.Join(" ", regions.Select(z => z.Text ?? ""));
            return numbersOnly
                ? string.Join("", text.Where(c => char.IsDigit(c) || c == '.' || c == ',' || c == '-' || c == '，' || c == ' '))
                : text;
        }

        string IHostAdapter.OcrScreen()
        {
            if (ActiveHwnd == IntPtr.Zero) throw new InvalidOperationException($"未绑定窗口{_adapterTarget}，无法识别");
            using var bmp = NativeMethods.CaptureWindow(ActiveHwnd);
            using var mat = BitmapConverter.ToMat(bmp);
            var regions = _ocr.OcrAsync(mat).GetAwaiter().GetResult();
            return string.Join(" ", regions.Select(z => z.Text ?? ""));
        }

        void IHostAdapter.ClickRel(int x, int y, int dwellMs, int jitter)
        {
            if (ActiveHwnd == IntPtr.Zero) throw new InvalidOperationException($"未绑定窗口{_adapterTarget}，无法点击");
            var r = NativeMethods.GetRect(ActiveHwnd);
            int ox = 0, oy = 0;
            if (jitter > 0)
            {
                ox = new Random(Guid.NewGuid().GetHashCode()).Next(-jitter, jitter + 1);
                oy = new Random(Guid.NewGuid().GetHashCode()).Next(-jitter, jitter + 1);
            }
            NativeMethods.ClickAtScreen(r.Left + x + ox, r.Top + y + oy, Math.Max(10, dwellMs));
        }

        void IHostAdapter.PasteText(string text)
        {
            Dispatcher.Invoke(() =>
            {
                try { System.Windows.Clipboard.SetText(text); } catch { }
            });
            // Ctrl+A 全选覆盖，再 Ctrl+V
            const byte VK_CONTROL = 0x11, VK_A = 0x41, VK_V = 0x56;
            NativeMethods.keybd_event(VK_CONTROL, 0, 0, 0);
            NativeMethods.keybd_event(VK_A, 0, 0, 0);
            System.Threading.Thread.Sleep(30);
            NativeMethods.keybd_event(VK_A, 0, NativeMethods.KEYEVENTF_KEYUP, 0);
            NativeMethods.keybd_event(VK_CONTROL, 0, NativeMethods.KEYEVENTF_KEYUP, 0);
            System.Threading.Thread.Sleep(60);
            NativeMethods.keybd_event(VK_CONTROL, 0, 0, 0);
            NativeMethods.keybd_event(VK_V, 0, 0, 0);
            System.Threading.Thread.Sleep(30);
            NativeMethods.keybd_event(VK_V, 0, NativeMethods.KEYEVENTF_KEYUP, 0);
            NativeMethods.keybd_event(VK_CONTROL, 0, NativeMethods.KEYEVENTF_KEYUP, 0);
        }

        void IHostAdapter.TapKey(byte vk, int dwellMs)
        {
            NativeMethods.keybd_event(vk, 0, 0, 0);
            if (dwellMs > 0) System.Threading.Thread.Sleep(dwellMs);
            NativeMethods.keybd_event(vk, 0, NativeMethods.KEYEVENTF_KEYUP, 0);
        }

        void IHostAdapter.MoveRel(int x, int y)
        {
            if (ActiveHwnd == IntPtr.Zero) throw new InvalidOperationException($"未绑定窗口{_adapterTarget}，无法移动鼠标");
            var r = NativeMethods.GetRect(ActiveHwnd);
            NativeMethods.MoveToScreen(r.Left + x, r.Top + y);
        }

        void IHostAdapter.MouseButtonRel(int button, bool down, int x, int y)
        {
            if (ActiveHwnd == IntPtr.Zero) throw new InvalidOperationException($"未绑定窗口{_adapterTarget}，无法操作鼠标");
            var r = NativeMethods.GetRect(ActiveHwnd);
            NativeMethods.MouseButtonScreen(button, down, r.Left + x, r.Top + y);
        }

        void IHostAdapter.Wheel(int delta) => NativeMethods.MouseWheelScreen(delta);

        void IHostAdapter.KeyDown(byte vk) => NativeMethods.keybd_event(vk, 0, 0, 0);
        void IHostAdapter.KeyUp(byte vk) => NativeMethods.keybd_event(vk, 0, NativeMethods.KEYEVENTF_KEYUP, 0);

        private readonly System.Collections.Generic.Dictionary<string, Mat> _tplCache = new();

        private Mat GetTemplate(string path)
        {
            if (_tplCache.TryGetValue(path, out var cached) && !cached.IsDisposed) return cached;
            var tpl = Cv2.ImRead(path, ImreadModes.Color);
            _tplCache[path] = tpl;
            return tpl;
        }

        bool IHostAdapter.FindImage(string templatePath, Rectangle searchRegion, double threshold,
            out int relX, out int relY, out double score)
        {
            relX = -1; relY = -1; score = 0;
            if (!System.IO.File.Exists(templatePath)) throw new System.IO.FileNotFoundException("模板图片不存在", templatePath);
            if (ActiveHwnd == IntPtr.Zero) throw new InvalidOperationException($"未绑定窗口{_adapterTarget}，无法找图");
            using var bmp = NativeMethods.CaptureWindow(ActiveHwnd);
            using var full = BitmapConverter.ToMat(bmp);
            int sx = 0, sy = 0;
            Mat search;
            if (searchRegion.Width > 4 && searchRegion.Height > 4)
            {
                int x = Math.Max(0, Math.Min(full.Cols - 1, searchRegion.X));
                int y = Math.Max(0, Math.Min(full.Rows - 1, searchRegion.Y));
                int w = Math.Min(full.Cols - x, searchRegion.Width);
                int h = Math.Min(full.Rows - y, searchRegion.Height);
                search = new Mat(full, new OpenCvSharp.Rect(x, y, w, h));
                sx = x; sy = y;
            }
            else search = full;

            var tpl = GetTemplate(templatePath);
            if (tpl.Empty() || tpl.Cols > search.Cols || tpl.Rows > search.Rows)
            { if (search != full) search.Dispose(); return false; }

            using var result = new Mat();
            Cv2.MatchTemplate(search, tpl, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double max, out _, out var maxLoc);
            score = max;
            if (search != full) search.Dispose();
            if (max < threshold) return false;
            relX = sx + maxLoc.X + tpl.Cols / 2;
            relY = sy + maxLoc.Y + tpl.Rows / 2;
            return true;
        }

        void IHostAdapter.BringToFront()
        {
            if (ActiveHwnd == IntPtr.Zero) return;
            Dispatcher.Invoke(() =>
            {
                try
                {
                    if (NativeMethods.IsIconic(ActiveHwnd)) NativeMethods.ShowWindow(ActiveHwnd, 9);
                    NativeMethods.SetForegroundWindow(ActiveHwnd);
                }
                catch { }
            });
        }

        void IHostAdapter.SetTarget(char target)
        {
            _adapterTarget = target == 'B' ? 'B' : 'A';
        }

        bool IHostAdapter.HasTarget(char target)
            => target == 'B' ? _boundBHwnd != IntPtr.Zero : _boundAHwnd != IntPtr.Zero;

        // —— 监控台 ——
        private void OpenMonitor_Click(object sender, RoutedEventArgs e)
        {
            var w = new MonitorWindow { Owner = this };
            w.Show();
            w.Activate();
        }

        // —— 托管配置持久化 wzconfig.json ——
        private class WzConfigDto
        {
            public string Strategy = "SMART";
            public string BuyPremium = "2", MaPeriod = "20", TakeProfit = "10", StopLoss = "0", IntervalSec = "60";
            public string MinProfit = "3", RsiMax = "70", Trailing = "0", MaxHoldRounds = "0", HoldIntervalSec = "20";
            public string BuyX = "", BuyY = "", SellX = "", SellY = "";
            public string TradeX = "", TradeY = "";
            public bool Ai; public bool Turbo;
            public bool SelectOn; public string SearchX = "", SearchY = "", ResultX = "", ResultY = "";
            public bool WalletOn; public int WX, WY, WW, WH;
            public string BudgetPct = "50", Reserve = "0", MaxLots = "999";
            public bool QtyOn; public string PlusX = "", PlusY = "", MinusX = "", MinusY = "", QtyReset = "20";
            public bool MailOn; public string MailX = "", MailY = "", ClaimX = "", ClaimY = "", CloseX = "", CloseY = "", MailKw = "领取,哈弗币,附件";
            public bool Ban = true; public string BanEvery = "1", BanKw = "", MaxRunMin = "90", RestMin = "15";
        }

        private static string WzConfigPath =>
            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wzconfig.json");

        private void WzSaveCfg_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var d = new WzConfigDto
                {
                    Strategy = WzStrategy(),
                    BuyPremium = WzBuyPremium.Text, MaPeriod = WzMaPeriod.Text, TakeProfit = WzTakeProfit.Text,
                    StopLoss = WzStopLoss.Text, IntervalSec = WzIntervalSec.Text,
                    MinProfit = WzMinProfit.Text, RsiMax = WzRsiMax.Text, Trailing = WzTrailing.Text,
                    MaxHoldRounds = WzMaxHoldRounds.Text, HoldIntervalSec = WzHoldIntervalSec.Text,
                    BuyX = WzBuyX.Text, BuyY = WzBuyY.Text, SellX = WzSellX.Text, SellY = WzSellY.Text,
                    TradeX = WzTradeX.Text, TradeY = WzTradeY.Text,
                    Ai = WzAiCheck.IsChecked == true, Turbo = WzTurbo.IsChecked == true,
                    SelectOn = WzSelectCheck.IsChecked == true,
                    SearchX = WzSearchX.Text, SearchY = WzSearchY.Text, ResultX = WzResultX.Text, ResultY = WzResultY.Text,
                    WalletOn = WzWalletCheck.IsChecked == true,
                    WX = _wzWalletRect.X, WY = _wzWalletRect.Y, WW = _wzWalletRect.Width, WH = _wzWalletRect.Height,
                    BudgetPct = WzBudgetPct.Text, Reserve = WzReserve.Text, MaxLots = WzMaxLots.Text,
                    QtyOn = WzQtyCheck.IsChecked == true,
                    PlusX = WzPlusX.Text, PlusY = WzPlusY.Text, MinusX = WzMinusX.Text, MinusY = WzMinusY.Text, QtyReset = WzQtyReset.Text,
                    MailOn = WzMailCheck.IsChecked == true,
                    MailX = WzMailX.Text, MailY = WzMailY.Text, ClaimX = WzClaimX.Text, ClaimY = WzClaimY.Text,
                    CloseX = WzCloseX.Text, CloseY = WzCloseY.Text, MailKw = WzMailKeywords.Text,
                    Ban = WzBanCheck.IsChecked == true, BanEvery = WzBanEvery.Text, BanKw = WzBanKeywords.Text,
                    MaxRunMin = WzMaxRunMin.Text, RestMin = WzRestMin.Text
                };
                System.IO.File.WriteAllText(WzConfigPath,
                    System.Text.Json.JsonSerializer.Serialize(d, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                AppendLog($"托管配置已保存：{WzConfigPath}");
            }
            catch (Exception ex) { AppendLog($"托管配置保存失败: {ex.Message}"); }
        }

        private void WzLoadCfg_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!System.IO.File.Exists(WzConfigPath)) { AppendLog("托管配置: 尚无 wzconfig.json"); return; }
                var d = System.Text.Json.JsonSerializer.Deserialize<WzConfigDto>(System.IO.File.ReadAllText(WzConfigPath));
                if (d == null) return;
                SelectComboPrefix(WzStrategyBox, d.Strategy);
                WzBuyPremium.Text = d.BuyPremium; WzMaPeriod.Text = d.MaPeriod; WzTakeProfit.Text = d.TakeProfit;
                WzStopLoss.Text = d.StopLoss; WzIntervalSec.Text = d.IntervalSec;
                WzMinProfit.Text = d.MinProfit; WzRsiMax.Text = d.RsiMax; WzTrailing.Text = d.Trailing;
                WzMaxHoldRounds.Text = d.MaxHoldRounds; WzHoldIntervalSec.Text = d.HoldIntervalSec;
                WzBuyX.Text = d.BuyX; WzBuyY.Text = d.BuyY; WzSellX.Text = d.SellX; WzSellY.Text = d.SellY;
                WzTradeX.Text = d.TradeX ?? ""; WzTradeY.Text = d.TradeY ?? "";
                WzAiCheck.IsChecked = d.Ai; WzTurbo.IsChecked = d.Turbo;
                WzSelectCheck.IsChecked = d.SelectOn;
                WzSearchX.Text = d.SearchX; WzSearchY.Text = d.SearchY; WzResultX.Text = d.ResultX; WzResultY.Text = d.ResultY;
                WzWalletCheck.IsChecked = d.WalletOn;
                _wzWalletRect = new System.Drawing.Rectangle(d.WX, d.WY, d.WW, d.WH);
                WzWalletRectBox.Text = d.WW > 0 ? $"{d.WX},{d.WY},{d.WW},{d.WH}" : "";
                WzBudgetPct.Text = d.BudgetPct; WzReserve.Text = d.Reserve; WzMaxLots.Text = d.MaxLots;
                WzQtyCheck.IsChecked = d.QtyOn;
                WzPlusX.Text = d.PlusX; WzPlusY.Text = d.PlusY; WzMinusX.Text = d.MinusX; WzMinusY.Text = d.MinusY; WzQtyReset.Text = d.QtyReset;
                WzMailCheck.IsChecked = d.MailOn;
                WzMailX.Text = d.MailX; WzMailY.Text = d.MailY; WzClaimX.Text = d.ClaimX; WzClaimY.Text = d.ClaimY;
                WzCloseX.Text = d.CloseX; WzCloseY.Text = d.CloseY; WzMailKeywords.Text = d.MailKw;
                WzBanCheck.IsChecked = d.Ban; WzBanEvery.Text = d.BanEvery; WzBanKeywords.Text = d.BanKw;
                WzMaxRunMin.Text = d.MaxRunMin; WzRestMin.Text = d.RestMin;
                AppendLog("托管配置已载入");
            }
            catch (Exception ex) { AppendLog($"托管配置载入失败: {ex.Message}"); }
        }

        private static void SelectComboPrefix(System.Windows.Controls.ComboBox box, string code)
        {
            foreach (var it in box.Items)
            {
                if (it is System.Windows.Controls.ComboBoxItem ci && ci.Content is string s && s.StartsWith(code + " ", StringComparison.OrdinalIgnoreCase))
                { box.SelectedItem = ci; return; }
            }
        }

        private void NumberOnly_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            e.Handled = new System.Text.RegularExpressions.Regex("[^0-9]+").IsMatch(e.Text);
        }

        // ================= 左侧导航：三页面切换 =================
        private void Nav_Changed(object sender, RoutedEventArgs e)
        {
            // InitializeComponent 阶段导航先于页面字段生成，空值守卫
            if (PageConsole == null || PageData == null || PageAbout == null) return;
            PageConsole.Visibility = NavConsole.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageData.Visibility = NavData.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>窗口聚焦时 F12 同样急停（全局 F12 走热键，隐藏时也生效）</summary>
        private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.F12)
            {
                StopFlowProject();
                e.Handled = true;
            }
        }

        // ================= 定时任务（对运行台选中工程生效，每日循环） =================
        private System.Windows.Threading.DispatcherTimer? _schedTimer;
        private DateTime? _schedStartAt;
        private DateTime? _schedStopAt;

        private void SchedStartToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_schedStartAt != null)
            {
                _schedStartAt = null;
                SchedStartBtn.Content = "启用启动";
                AppendLog("[定时] 已取消定时启动");
            }
            else
            {
                if (!TryReadSchedHM(SchedStartH, SchedStartM, allowEmpty: false, out var hm)) return;
                _schedStartAt = NextOccurrence(hm);
                SchedStartBtn.Content = "取消启动";
                AppendLog($"[定时] 已启用定时启动：每日 {hm.Hour:00}:{hm.Minute:00}（首次 {_schedStartAt:MM-dd HH:mm}）");
            }
            EnsureSchedTimer();
            UpdateSchedStatus();
        }

        private void SchedStopToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_schedStopAt != null)
            {
                _schedStopAt = null;
                SchedStopBtn.Content = "启用停止";
                AppendLog("[定时] 已取消定时停止");
            }
            else
            {
                if (!TryReadSchedHM(SchedStopH, SchedStopM, allowEmpty: true, out var hm)) return;
                _schedStopAt = NextOccurrence(hm);
                SchedStopBtn.Content = "取消停止";
                AppendLog($"[定时] 已启用定时停止：每日 {hm.Hour:00}:{hm.Minute:00}（首次 {_schedStopAt:MM-dd HH:mm}）");
            }
            EnsureSchedTimer();
            UpdateSchedStatus();
        }

        private static bool TryReadSchedHM(TextBox hBox, TextBox mBox, bool allowEmpty, out (int Hour, int Minute) hm)
        {
            hm = (0, 0);
            string hs = hBox.Text?.Trim() ?? "";
            string ms = mBox.Text?.Trim() ?? "";
            if (allowEmpty && hs.Length == 0 && ms.Length == 0) { hm = (0, 0); return true; }
            if (!int.TryParse(hs, out int h) || !int.TryParse(ms, out int m) || h < 0 || h > 23 || m < 0 || m > 59)
            {
                MessageBox.Show("时间格式不正确：小时 0-23、分钟 0-59。", "定时任务", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            hm = (h, m);
            return true;
        }

        private static DateTime NextOccurrence((int Hour, int Minute) hm)
        {
            var now = DateTime.Now;
            var target = new DateTime(now.Year, now.Month, now.Day, hm.Hour, hm.Minute, 0);
            if (target <= now) target = target.AddDays(1);
            return target;
        }

        private void EnsureSchedTimer()
        {
            if (_schedStartAt != null || _schedStopAt != null)
            {
                if (_schedTimer == null)
                {
                    _schedTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    _schedTimer.Tick += SchedTimer_Tick;
                }
                if (!_schedTimer.IsEnabled) _schedTimer.Start();
            }
            else
            {
                _schedTimer?.Stop();
            }
        }

        private void SchedTimer_Tick(object? sender, EventArgs e)
        {
            var now = DateTime.Now;
            if (_schedStartAt != null && now >= _schedStartAt)
            {
                _schedStartAt = _schedStartAt.Value.AddDays(1); // 每日循环
                AppendLog("[定时] 到达定时启动时间，正在启动选中工程…");
                StartSelectedProject(silent: true);
                UpdateSchedStatus();
            }
            if (_schedStopAt != null && now >= _schedStopAt)
            {
                _schedStopAt = _schedStopAt.Value.AddDays(1);
                AppendLog("[定时] 到达定时停止时间，正在停止当前工程…");
                StopFlowProject();
                UpdateSchedStatus();
            }
        }

        private void UpdateSchedStatus()
        {
            string s = _schedStartAt != null ? $"启 {_schedStartAt:HH:mm}" : "启 --:--";
            string p = _schedStopAt != null ? $"停 {_schedStopAt:HH:mm}" : "停 --:--";
            SchedStatusText.Text = $"定时：{s} ｜ {p}（每日循环）";
        }

        private class StepDto
        {
            public string Type { get; set; } = "";
            public string Target { get; set; } = "";
            public int DelayMs { get; set; }
            public int RandomDelay { get; set; }
            public int DwellMs { get; set; }
            public int RandomDwell { get; set; }
            public int RandomX { get; set; }
            public int RandomY { get; set; }
            public int Count { get; set; }
            public string Pattern { get; set; } = "";
            public string Key { get; set; } = "";
            public int RectX { get; set; }
            public int RectY { get; set; }
            public int RectW { get; set; }
            public int RectH { get; set; }
            public int PointX { get; set; }
            public int PointY { get; set; }
            public bool JumpOnTrue { get; set; }
            public bool OcrNumbersOnly { get; set; }
            public bool ReuseOcrOnRoiUnchanged { get; set; }
            public string NetworkAdapterName { get; set; } = "";
            public bool NetworkEnable { get; set; }
            public string QuantFunc { get; set; } = "MA";
            public int QuantParam { get; set; } = 20;
            public string ResultKey { get; set; } = "";
            public string MarketUrl { get; set; } = "";
            public string PriceVar { get; set; } = "";
            public string ChangeVar { get; set; } = "";
            public bool SeedHistory { get; set; } = true;
            public int HistoryLimit { get; set; } = 60;
            public bool AutoPick { get; set; }
            public string PickStrategy { get; set; } = "RISE";
            public string BulletVar { get; set; } = "";
            public string AiPrompt { get; set; } = "";
            public string AiModel { get; set; } = "deepseek-chat";
            public string BanKeywords { get; set; } = "";
        }

        private void CaptionMin_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void CaptionClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshFlowProjects();
        }




        private void AppendLog(string text)
        {
            lock (_logLock)
            {
                _pendingLogs.Enqueue((DateTime.Now, text));
                if (_logFlushScheduled) return;
                _logFlushScheduled = true;
            }

            Dispatcher.BeginInvoke(new Action(FlushLogs));
        }

        private void FlushLogs()
        {
            System.Collections.Generic.List<(DateTime ts, string text)> batch = new();
            lock (_logLock)
            {
                _logFlushScheduled = false;
                while (_pendingLogs.Count > 0 && batch.Count < 200)
                {
                    batch.Add(_pendingLogs.Dequeue());
                }
                if (_pendingLogs.Count > 0) _logFlushScheduled = true;
            }

            if (batch.Count > 0 && OutputBox != null)
            {
                var paragraph = OutputBox.Document.Blocks.FirstBlock as System.Windows.Documents.Paragraph;
                if (paragraph == null)
                {
                    paragraph = new System.Windows.Documents.Paragraph();
                    OutputBox.Document.Blocks.Add(paragraph);
                }

                foreach (var item in batch)
                {
                    var timestampRun = new System.Windows.Documents.Run(item.ts.ToString("HH:mm:ss") + " ") { Foreground = ResBrush("TextSecondaryBrush") };
                    paragraph.Inlines.Add(timestampRun);

                    string[] logParts = System.Text.RegularExpressions.Regex.Split(item.text, @"( 未识别到数据 大概原因：识别区域过小或执行速度太快没有识别到对应图片| 未识别到数据 大概原因：识别区域过小或执行速度太快没有识别到对应应图片| 未识别到数据可能原因：识别区域过小或执行速度太快没有识别到对应应图片| 未识别到数据| 未识别到)");
                    foreach (var part in logParts)
                    {
                        if (string.IsNullOrEmpty(part)) continue;
                        if (part.StartsWith(" 未识别到"))
                        {
                            paragraph.Inlines.Add(new System.Windows.Documents.Run(part) { Foreground = ResBrush("DangerBrush") });
                        }
                        else
                        {
                            paragraph.Inlines.Add(new System.Windows.Documents.Run(part) { Foreground = ResBrush("TextPrimaryBrush") });
                        }
                    }
                    paragraph.Inlines.Add(new System.Windows.Documents.LineBreak());
                }

                if (paragraph.Inlines.Count > 2000)
                {
                    OutputBox.Document.Blocks.Clear();
                    paragraph = new System.Windows.Documents.Paragraph();
                    paragraph.Inlines.Add(new System.Windows.Documents.Run($"[系统] 日志过长已自动清理 ({DateTime.Now:HH:mm:ss})\n") { Foreground = ResBrush("TextSecondaryBrush") });
                    OutputBox.Document.Blocks.Add(paragraph);
                }

                OutputBox.ScrollToEnd();
            }

            if (batch.Count > 0)
            {
                try
                {
                    var dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "jietu");
                    System.IO.Directory.CreateDirectory(dir);
                    var log = System.IO.Path.Combine(dir, "app.log");
                    var sbFile = new System.Text.StringBuilder();
                    foreach (var item in batch)
                    {
                        sbFile.Append(item.ts.ToString("yyyy-MM-dd HH:mm:ss")).Append(' ').Append(item.text).Append('\n');
                    }
                    System.IO.File.AppendAllText(log, sbFile.ToString());
                }
                catch { }
            }

            lock (_logLock)
            {
                if (_logFlushScheduled)
                {
                    Dispatcher.BeginInvoke(new Action(FlushLogs));
                }
            }
        }

        private void CopyLog_Click(object sender, RoutedEventArgs e)
        {
            if (OutputBox != null)
            {
                var textRange = new System.Windows.Documents.TextRange(OutputBox.Document.ContentStart, OutputBox.Document.ContentEnd);
                if (!string.IsNullOrWhiteSpace(textRange.Text))
                {
                    try
                    {
                        Clipboard.SetText(textRange.Text);
                        MessageBox.Show("日志已复制到剪贴板", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"复制失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }
    }
}
