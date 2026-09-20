using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Path = System.Windows.Shapes.Path;

namespace WindowSpy
{
    /// <summary>流程编辑器所需的宿主能力（由 MainWindow 实现）</summary>
    public interface IFlowHost
    {
        IHostAdapter Adapter { get; }
        System.Drawing.Point? PickCoord(string label);
        System.Drawing.Rectangle? PickRect();
        /// <summary>主窗口运行台是否正在跑工程（互斥，防止两处同时操控鼠标）</summary>
        bool IsFlowRunning { get; }
        /// <summary>是否已绑定窗口A（游戏窗口）；点选/框选坐标前必须先绑定</summary>
        bool HasWindowA { get; }
    }

    public partial class FlowEditorWindow : Window
    {
        private readonly IFlowHost _host;
        private FlowGraph _g = new();
        private string? _filePath;

        // 模块编辑模式（双击模块卡片打开；保存写回 nodemods/*.subflow.json）
        private bool _moduleMode;
        private string? _moduleFile;

        private const double NodeW = FlowLayout.NodeW;
        private static readonly SolidColorBrush EdgeBrush = new(Color.FromRgb(0x4A, 0x56, 0x6B));
        private static readonly SolidColorBrush BackEdgeBrush = new(Color.FromRgb(0x3F, 0xD6, 0xC9));
        private static readonly SolidColorBrush EdgeSelBrush = new(Color.FromRgb(0xFF, 0x8A, 0x2A));
        private static readonly SolidColorBrush TempBrush = new(Color.FromRgb(0xFF, 0x8A, 0x2A));
        private static readonly SolidColorBrush ArrowBrush = new(Color.FromRgb(0x6E, 0x7E, 0x96));
        private static readonly SolidColorBrush BackArrowBrush = new(Color.FromRgb(0x3F, 0xD6, 0xC9));

        private FlowNode? _selectedNode;
        private FlowEdge? _selectedEdge;
        private string? _linkFromNode;
        private string? _linkFromPort;
        private Path? _tempPath;

        private FlowNode? _dragNode;
        private System.Windows.Point _dragOff;

        // 多选（Ctrl/Shift+点选、Ctrl+空白框选）——用于打包模块/批量拖动/批量删除
        private readonly List<FlowNode> _selNodes = new();
        private bool _boxSelecting;
        private System.Windows.Point _boxStart;
        private Rectangle? _boxRect;
        // 多选整组拖动
        private System.Windows.Point _groupDragStart;
        private Dictionary<string, System.Windows.Point>? _groupOrig;

        // 画布平移/缩放
        private bool _panReady;          // 空白左键按下，尚未判定为拖拽
        private bool _panning;
        private System.Windows.Point _panStart;
        private double _panHOffset, _panVOffset;
        private double _zoom = 1.0;
        private readonly ScaleTransform _canvasScale = new(1, 1);
        private const double ZoomMin = 0.5, ZoomMax = 1.8;

        private readonly Dictionary<string, Border> _cards = new();
        private HashSet<string> _backEdges = new();
        private CancellationTokenSource? _cts;
        private FlowRunner? _runner;
        private string? _lastHighlight;
        private Window? _propsDialog;   // 右键「编辑属性」弹窗（点选坐标时要一起隐藏）

        // 出错节点闪红
        private string? _errorNodeId;
        private bool _errorFlashOn;
        private DispatcherTimer? _errorTimer;
        private static readonly SolidColorBrush ErrCardBrushA = new(Color.FromRgb(0x40, 0x13, 0x18));
        private static readonly SolidColorBrush ErrCardBrushB = new(Color.FromRgb(0x24, 0x10, 0x14));

        public FlowEditorWindow(IFlowHost host) : this(host, null) { }

        /// <summary>moduleFile 非空 = 模块编辑模式：打开 nodemods 下的 .subflow.json，保存写回该文件</summary>
        public FlowEditorWindow(IFlowHost host, string? moduleFile)
        {
            _host = host;
            InitializeComponent();
            Canvas.LayoutTransform = _canvasScale;
            BuildCanvasMenu();
            BuildToolbox();
            NewGraph(quiet: true);
            if (moduleFile != null) EnterModuleMode(moduleFile);
            else LoadLastOrDefault();
            PreviewKeyDown += OnPreviewKeyDown;
        }

        private static double NodeH(FlowNodeDef d)
            => FlowLayout.HeadH + (d.Outputs.Count > 1 ? d.Outputs.Count * FlowLayout.PortRowH : FlowLayout.BodyH);

        private void RefreshBackEdges() => _backEdges = FlowLayout.ClassifyBackEdges(_g);

        // ================= 工具箱 =================
        private void BuildToolbox()
        {
            string[] cats = { FlowCatalog.CatFlow, FlowCatalog.CatMotion, FlowCatalog.CatVision,
                              FlowCatalog.CatTrade, FlowCatalog.CatLogic, FlowCatalog.CatAi,
                              FlowCatalog.CatScript };
            foreach (var cat in cats)
            {
                var exp = new Expander
                {
                    Header = CatTitle(cat),
                    IsExpanded = true,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xCF, 0xD6, 0xE0)),
                    Margin = new Thickness(2, 2, 2, 4),
                    Background = new SolidColorBrush(Color.FromRgb(0x12, 0x15, 0x1B)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x23, 0x29, 0x36))
                };
                var panel = new StackPanel();
                foreach (var def in FlowCatalog.Defs.Where(d => d.Category == cat))
                {
                    var item = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1A, 0x22)),
                        BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(def.Color)),
                        BorderThickness = new Thickness(2, 0, 0, 0),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(8, 5, 6, 5),
                        Margin = new Thickness(2, 2, 2, 3),
                        Cursor = Cursors.Hand,
                        Tag = def.Type,
                        Child = new TextBlock
                        {
                            Text = def.Title,
                            Foreground = Brushes.WhiteSmoke,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        }
                    };
                    item.MouseLeftButtonDown += (s, e) =>
                    {
                        DragDrop.DoDragDrop(item, "FLOWNODE:" + def.Type, DragDropEffects.Copy);
                        e.Handled = true;
                    };
                    panel.Children.Add(item);
                }
                exp.Content = panel;
                ToolboxPanel.Children.Add(exp);
            }
            BuildModuleToolbox();
        }

        /// <summary>工具箱底部「🧩 我的模块」：扫描 nodemods/*.subflow.json，拖到画布即生成模块卡片</summary>
        private void BuildModuleToolbox()
        {
            // 旧分区先移除（打包/导入后重建）
            for (int i = ToolboxPanel.Children.Count - 1; i >= 0; i--)
                if (ToolboxPanel.Children[i] is FrameworkElement fe && fe.Tag as string == "MODSECTION")
                    ToolboxPanel.Children.RemoveAt(i);

            var refs = SubflowLibrary.List();
            var exp = new Expander
            {
                Tag = "MODSECTION",
                IsExpanded = refs.Count > 0,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCF, 0xD6, 0xE0)),
                Margin = new Thickness(2, 2, 2, 4),
                Background = new SolidColorBrush(Color.FromRgb(0x12, 0x15, 0x1B)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x23, 0x29, 0x36))
            };
            // 标题行：文字 + 导入/目录/刷新 小按钮
            var head = new DockPanel();
            var btnOpen = MakeMiniToolButton("📂 目录");
            btnOpen.Click += (s, e) =>
            {
                try
                {
                    Directory.CreateDirectory(SubflowLibrary.Dir);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", SubflowLibrary.Dir) { UseShellExecute = true });
                }
                catch (Exception ex) { AppendLog("error", $"打开模块目录失败：{ex.Message}"); }
                e.Handled = true;
            };
            var btnImp = MakeMiniToolButton("📥 导入");
            btnImp.Click += (s, e) => { ImportModule_Click(s, e); e.Handled = true; };
            DockPanel.SetDock(btnOpen, Dock.Right);
            DockPanel.SetDock(btnImp, Dock.Right);
            head.Children.Add(btnOpen);
            head.Children.Add(btnImp);
            head.Children.Add(new TextBlock
            {
                Text = $"🧩 我的模块（{refs.Count}）",
                Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0x9C, 0xFF)),
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "在画布上 Ctrl 拖框选中多个节点 → 右键「打包为我的模块」；模块文件在 nodemods 目录，可发给别人导入"
            });
            exp.Header = head;

            var panel = new StackPanel();
            if (refs.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "还没有模块。\n框选节点后右键即可打包；\n或点右上角「导入」.subflow.json",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x76, 0x86)),
                    FontSize = 11, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4, 4, 4, 8)
                });
            }
            foreach (var r in refs)
            {
                var item = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1A, 0x22)),
                    BorderBrush = new SolidColorBrush(r.Valid
                        ? Color.FromRgb(0xB9, 0x8C, 0xFF)
                        : Color.FromRgb(0x6B, 0x2E, 0x33)),
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(8, 5, 6, 5),
                    Margin = new Thickness(2, 2, 2, 3),
                    Cursor = r.Valid ? Cursors.Hand : Cursors.No,
                    Opacity = r.Valid ? 1.0 : 0.55,
                    Tag = r.File,
                    Child = new TextBlock
                    {
                        Text = r.Valid ? $"📦 {r.Name}（{r.NodeCount}节点）" : $"⚠ {r.Name}",
                        Foreground = Brushes.WhiteSmoke,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        ToolTip = r.Valid ? (string.IsNullOrWhiteSpace(r.Description) ? r.File : r.Description) : "模块损坏：" + r.Error
                    }
                };
                if (r.Valid)
                {
                    item.MouseLeftButtonDown += (s, e) =>
                    {
                        DragDrop.DoDragDrop(item, "FLOWMOD:" + r.File, DragDropEffects.Copy);
                        e.Handled = true;
                    };
                    var cm = new ContextMenu();
                    var miOpenEdit = new MenuItem { Header = "🧩 打开编辑" };
                    miOpenEdit.Click += (s, e) =>
                        new FlowEditorWindow(_host, r.File) { Owner = this }.Show();
                    var miDelMod = new MenuItem { Header = "🗑 删除模块文件" };
                    miDelMod.Click += (s, e) =>
                    {
                        if (MessageBox.Show($"删除模块【{r.Name}】？\n文件 {r.File} 会被永久删除，" +
                                            "已放进流程里的模块卡片将变为「模块丢失」。",
                                "删除模块", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                            == MessageBoxResult.OK)
                        {
                            SubflowLibrary.Delete(r.File);
                            AppendLog("warn", $"模块已删除：{r.File}");
                            BuildModuleToolbox();
                        }
                    };
                    cm.Items.Add(miOpenEdit);
                    cm.Items.Add(miDelMod);
                    item.ContextMenu = cm;
                }
                panel.Children.Add(item);
            }
            exp.Content = panel;
            ToolboxPanel.Children.Add(exp);
        }

        private Button MakeMiniToolButton(string text) => new()
        {
            Content = text,
            FontSize = 10,
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(3, 0, 0, 0),
            MinHeight = 20,
            Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1F, 0x29)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xBF, 0xC9, 0xD6)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x36, 0x45)),
            BorderThickness = new Thickness(1)
        };

        private static string CatTitle(string c) => c switch
        {
            FlowCatalog.CatFlow => "① 流程",
            FlowCatalog.CatMotion => "② 运动控制（手）",
            FlowCatalog.CatVision => "③ 视觉识别（眼）",
            FlowCatalog.CatTrade => "④ 买卖控制（交易）",
            FlowCatalog.CatLogic => "⑤ 逻辑判断（脑）",
            FlowCatalog.CatAi => "⑥ AI 与风控",
            FlowCatalog.CatScript => "⑦ 脚本扩展（Lua/Python）",
            _ => c
        };

        // ================= 画布渲染 =================
        private void RenderAll()
        {
            var keepSel = _selNodes.Select(n => n.Id).ToHashSet();
            RefreshBackEdges();
            Canvas.Children.Clear();
            _cards.Clear();
            RenderEdges();
            foreach (var n in _g.Nodes) AddNodeVisual(n);
            FitCanvasSize();
            // 重建后按 id 恢复多选（节点对象已换）
            _selNodes.Clear();
            foreach (var id in keepSel)
                if (_g.Node(id) is { } nn) _selNodes.Add(nn);
        }

        /// <summary>按节点范围自适应画布尺寸（纵向布局：高度随行数增长），不小于一屏</summary>
        private void FitCanvasSize()
        {
            double maxX = 900, maxY = 700;
            foreach (var n in _g.Nodes)
            {
                maxX = Math.Max(maxX, n.X + FlowLayout.NodeW + 60);
                maxY = Math.Max(maxY, n.Y + FlowLayout.NodeH(n) + 120);
            }
            Canvas.Width = maxX;
            Canvas.Height = maxY;
        }

        private void RenderEdges()
        {
            for (int i = Canvas.Children.Count - 1; i >= 0; i--)
            {
                var el = Canvas.Children[i];
                if (el is Path p && "EDGE".Equals(p.Tag)) Canvas.Children.RemoveAt(i);
                else if (el is Polygon pg && "EDGEARROW".Equals(pg.Tag)) Canvas.Children.RemoveAt(i);
                else if (el is Path hp && "EDGEHIT".Equals(((string?)hp.Tag)?.Split(':')[0])) Canvas.Children.RemoveAt(i);
            }

            // 汇合统计（仅正向边）：多入节点走下母线收口
            var predCnt = new Dictionary<string, int>();
            foreach (var ed in _g.Edges)
            {
                if (_backEdges.Contains(ed.Id)) continue;
                predCnt[ed.To] = predCnt.GetValueOrDefault(ed.To) + 1;
            }

            int backIndex = 0;
            foreach (var e in _g.Edges)
            {
                bool back = _backEdges.Contains(e.Id);
                System.Windows.Point a, b;
                PathGeometry geo;
                if (back)
                {
                    // 回流边：从源节点左侧出发，经左侧纵向通道回到目标节点左侧（青虚线）
                    a = LeftAnchor(e.From);
                    b = LeftAnchor(e.To);
                    double lane = Math.Max(24, a.X - 40 - (backIndex++ % 4) * 26);
                    var fig = new PathFigure { StartPoint = a, IsClosed = false };
                    fig.Segments.Add(new LineSegment(new System.Windows.Point(lane, a.Y), true));
                    fig.Segments.Add(new LineSegment(new System.Windows.Point(lane, b.Y), true));
                    fig.Segments.Add(new LineSegment(b, true));
                    geo = new PathGeometry(new[] { fig });
                }
                else
                {
                    var pa = PortAnchor(e.From, e.Port, false);
                    var pb = PortAnchor(e.To, "", true);
                    if (pa == null || pb == null) continue;
                    a = pa.Value; b = pb.Value;
                    bool fan = _g.Edges.Where(y => y.From == e.From && !_backEdges.Contains(y.Id))
                                      .Select(y => y.To).Distinct().Count() >= 2;
                    bool join = predCnt.GetValueOrDefault(e.To) >= 2;
                    geo = Ortho(a, b, fan, join);
                }

                bool sel = ReferenceEquals(_selectedEdge, e);
                var brush = sel ? EdgeSelBrush : back ? BackEdgeBrush : EdgeBrush;

                var hit = new Path
                {
                    Data = geo, Stroke = Brushes.Transparent, StrokeThickness = 16,
                    Tag = "EDGEHIT:" + e.Id, Cursor = Cursors.Hand
                };
                hit.MouseLeftButtonDown += (s, ev) => { SelectEdge(e); ev.Handled = true; };
                hit.ContextMenu = BuildEdgeMenu(e);
                hit.ContextMenuOpening += (s, ev) => SelectEdge(e);
                Canvas.Children.Add(hit);

                var path = new Path
                {
                    Data = geo,
                    Stroke = brush,
                    StrokeThickness = sel ? 3 : 1.8,
                    Tag = "EDGE",
                    StrokeDashArray = back ? new DoubleCollection { 5, 4 } : null
                };
                Canvas.Children.Add(path);

                // 箭头
                var arrow = MakeArrow(geo, sel ? EdgeSelBrush : back ? BackArrowBrush : ArrowBrush, back);
                arrow.Tag = "EDGEARROW";
                Canvas.Children.Add(arrow);
            }
        }

        /// <summary>沿路径终点切线方向画三角箭头</summary>
        private static Polygon MakeArrow(PathGeometry geo, Brush brush, bool enterFromBottom)
        {
            var fig = geo.Figures[0];
            System.Windows.Point tip, prev;
            if (fig.Segments.Count > 0 && fig.Segments[^1] is BezierSegment bs) { tip = bs.Point3; prev = bs.Point2; }
            else if (fig.Segments.Count > 0 && fig.Segments[^1] is LineSegment ls)
            {
                tip = ls.Point;
                // 折线：切线取倒数第二个拐点
                if (fig.Segments.Count >= 2 && fig.Segments[^2] is LineSegment p2) prev = p2.Point;
                else prev = fig.StartPoint;
            }
            else { tip = fig.StartPoint; prev = fig.StartPoint; }
            double ang = Math.Atan2(tip.Y - prev.Y, tip.X - prev.X);
            double size = 8;
            // 箭头整体后退，避免被节点卡片盖住
            double gap = 8;
            tip = new System.Windows.Point(tip.X - gap * Math.Cos(ang), tip.Y - gap * Math.Sin(ang));
            System.Windows.Point P(double da, double r) => new(
                tip.X + r * Math.Cos(ang + da), tip.Y + r * Math.Sin(ang + da));
            var poly = new Polygon
            {
                Points = new PointCollection { tip, P(Math.PI - 0.42, size), P(Math.PI + 0.42, size) },
                Fill = brush
            };
            return poly;
        }

        private System.Windows.Point LeftAnchor(string nodeId)
        {
            var n = _g.Node(nodeId);
            if (n == null) return new System.Windows.Point();
            double h = NodeH(FlowCatalog.Get(n.Type));
            return new System.Windows.Point(n.X, n.Y + h / 2);
        }

        /// <summary>
        /// 正交折线（工业视觉风格）：同轴=直线；分叉出口走节点下方横向母线；
        /// 汇合入口走目标上方横向母线；其余跨列连接在中点横移。
        /// </summary>
        private static PathGeometry Ortho(System.Windows.Point a, System.Windows.Point b, bool fan, bool join)
        {
            var fig = new PathFigure { StartPoint = a, IsClosed = false };
            if (Math.Abs(a.X - b.X) < 1.5)
            {
                fig.Segments.Add(new LineSegment(b, true));
                return new PathGeometry(new[] { fig });
            }
            double hi = a.Y + 24, lo = b.Y - 24;
            double yH;
            if (fan && hi <= lo) yH = hi;
            else if (join && lo >= a.Y + 6) yH = lo;
            else yH = Math.Clamp((a.Y + b.Y) / 2, a.Y + 6, Math.Max(a.Y + 6, b.Y - 6));
            fig.Segments.Add(new LineSegment(new System.Windows.Point(a.X, yH), true));
            fig.Segments.Add(new LineSegment(new System.Windows.Point(b.X, yH), true));
            fig.Segments.Add(new LineSegment(b, true));
            return new PathGeometry(new[] { fig });
        }

        /// <summary>拖拽连线时的临时折线（先直下再折向鼠标）</summary>
        private static PathGeometry TempLine(System.Windows.Point a, System.Windows.Point b)
        {
            double yH = Math.Min(a.Y + 28, Math.Max(a.Y + 8, b.Y - 4));
            var fig = new PathFigure { StartPoint = a, IsClosed = false };
            if (Math.Abs(a.X - b.X) < 1.5) fig.Segments.Add(new LineSegment(b, true));
            else
            {
                fig.Segments.Add(new LineSegment(new System.Windows.Point(a.X, yH), true));
                fig.Segments.Add(new LineSegment(new System.Windows.Point(b.X, yH), true));
                fig.Segments.Add(new LineSegment(b, true));
            }
            return new PathGeometry(new[] { fig });
        }

        /// <summary>端口锚点：入口=卡片顶部居中；出口=卡片底部（多出口底部均布）</summary>
        private System.Windows.Point? PortAnchor(string nodeId, string port, bool input)
        {
            var n = _g.Node(nodeId);
            if (n == null) return null;
            var def = FlowCatalog.Get(n.Type);
            double h = NodeH(def);
            if (input) return new System.Windows.Point(n.X + NodeW / 2, n.Y);
            if (def.Outputs.Count <= 1) return new System.Windows.Point(n.X + NodeW / 2, n.Y + h);
            int idx = def.Outputs.FindIndex(o => o.Port == port);
            if (idx < 0) idx = 0;
            double x = n.X + NodeW * (idx + 1) / (def.Outputs.Count + 1);
            return new System.Windows.Point(x, n.Y + h);
        }

        private void AddNodeVisual(FlowNode n)
        {
            var def = FlowCatalog.Get(n.Type);
            double h = NodeH(def);
            var color = (Color)ColorConverter.ConvertFromString(def.Color);

            var card = new Border
            {
                Width = NodeW,
                Height = h,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x23, 0x29, 0x36)),
                Background = new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x20)),
                Tag = "NODE:" + n.Id,
                Cursor = Cursors.SizeAll
            };

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(FlowLayout.HeadH) });
            root.RowDefinitions.Add(new RowDefinition());

            // 标题条
            var head = new Border
            {
                Background = new SolidColorBrush(color),
                CornerRadius = new CornerRadius(4, 4, 0, 0),
                Child = new TextBlock
                {
                    Text = string.IsNullOrEmpty(n.Alias) ? def.Title : n.Alias,
                    Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 12,
                    Margin = new Thickness(8, 3, 6, 3), TextTrimming = TextTrimming.CharacterEllipsis
                }
            };
            Grid.SetRow(head, 0);
            root.Children.Add(head);

            // 出口标签或摘要（多出口时按底部端口从左到右顺序编号，便于对应圆点）
            var body = new StackPanel { Margin = new Thickness(8, 2, 8, 4) };
            if (def.Outputs.Count > 1)
            {
                for (int i = 0; i < def.Outputs.Count; i++)
                    body.Children.Add(new TextBlock
                    {
                        Text = $"{i + 1}.{def.Outputs[i].Label}",
                        Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0xCE, 0xDA)),
                        FontSize = 11,
                        Height = FlowLayout.PortRowH,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Margin = new Thickness(0),
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
            }
            else
            {
                body.Children.Add(new TextBlock
                {
                    Text = FlowCatalog.Summary(n),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x96, 0xA5)),
                    FontSize = 11,
                    Margin = new Thickness(0, 2, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }
            Grid.SetRow(body, 1);
            root.Children.Add(body);
            card.Child = root;

            card.MouseLeftButtonDown += Card_MouseLeftButtonDown;
            card.MouseMove += Card_MouseMove;
            card.MouseLeftButtonUp += Card_MouseLeftButtonUp;
            card.ContextMenu = BuildNodeMenu(n);
            card.ContextMenuOpening += (s, ev) => SelectNode(n);

            Canvas.SetLeft(card, n.X);
            Canvas.SetTop(card, n.Y);
            Canvas.Children.Add(card);
            _cards[n.Id] = card;

            // 入口圆点（顶部居中）
            if (!def.NoInput)
                AddPort(n.Id, "", true, n.X + NodeW / 2, n.Y);

            // 出口圆点（底部居中；多出口底部均布）
            if (!def.NoOutput)
            {
                if (def.Outputs.Count <= 1)
                    AddPort(n.Id, def.Outputs[0].Port, false, n.X + NodeW / 2, n.Y + h);
                else
                    for (int i = 0; i < def.Outputs.Count; i++)
                        AddPort(n.Id, def.Outputs[i].Port, false,
                            n.X + NodeW * (i + 1) / (def.Outputs.Count + 1), n.Y + h);
            }

            ApplyCardBrush(n);
        }

        private void AddPort(string nodeId, string port, bool input, double x, double y)
        {
            var dot = new Ellipse
            {
                Width = 11, Height = 11,
                Fill = new SolidColorBrush(input ? Color.FromRgb(0x4D, 0xA3, 0xFF) : Color.FromRgb(0xFF, 0x8A, 0x2A)),
                Stroke = Brushes.Black, StrokeThickness = 1,
                Tag = $"PORT|{nodeId}|{port}|{(input ? "1" : "0")}",
                Cursor = Cursors.Cross
            };
            Canvas.SetLeft(dot, x - 5.5);
            Canvas.SetTop(dot, y - 5.5);
            dot.MouseLeftButtonDown += Port_MouseLeftButtonDown;
            Canvas.Children.Add(dot);
        }

        private void ApplyCardBrush(FlowNode n)
        {
            if (!_cards.TryGetValue(n.Id, out var card)) return;
            // 屏蔽节点：整体灰化（opacity 降低，其他边框/背景逻辑正常叠加上）
            card.Opacity = n.Disabled ? 0.35 : 1.0;
            // 出错节点：红色边框 + 淡红底（闪烁中明暗交替，停止后保持亮红）
            if (_errorNodeId == n.Id)
            {
                card.BorderBrush = new SolidColorBrush(
                    _errorFlashOn ? Color.FromRgb(0xFF, 0x4D, 0x55) : Color.FromRgb(0x8A, 0x22, 0x2A));
                card.Background = _errorFlashOn ? ErrCardBrushA : ErrCardBrushB;
                return;
            }
            Color c;
            if (_lastHighlight == n.Id) c = Color.FromRgb(0x58, 0xE0, 0x7D);
            else if (ReferenceEquals(_selectedNode, n)) c = Color.FromRgb(0xFF, 0x8A, 0x2A);
            else if (_selNodes.Contains(n)) c = Color.FromRgb(0xB9, 0x8C, 0xFF);
            else c = Color.FromRgb(0x23, 0x29, 0x36);
            card.BorderBrush = new SolidColorBrush(c);
            card.Background = new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x20));
        }

        private void RebuildCardVisual(FlowNode n)
        {
            RefreshCardText(n);
            ApplyCardBrush(n);
        }

        private void ClearNodeError()
        {
            _errorTimer?.Stop();
            var old = _errorNodeId;
            _errorNodeId = null;
            _errorFlashOn = false;
            if (old != null && _g.Node(old) != null) ApplyCardBrush(_g.Node(old)!);
        }

        private void OnNodeError(string nodeId, string title, string message)
        {
            Dispatcher.Invoke(() =>
            {
                _errorNodeId = nodeId;
                _errorFlashOn = true;
                if (_g.Node(nodeId) != null) ApplyCardBrush(_g.Node(nodeId)!);
                _errorTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
                _errorTimer.Tick -= ErrorTimer_Tick;
                _errorTimer.Tick += ErrorTimer_Tick;
                _errorTimer.Start();
                AppendLog("error", $"⚠ 出错节点：【{title}】——{message}（节点已闪红，修正后重新运行）");
            });
        }

        private void ErrorTimer_Tick(object? sender, EventArgs e)
        {
            if (_errorNodeId == null) { _errorTimer?.Stop(); return; }
            _errorFlashOn = !_errorFlashOn;
            if (_g.Node(_errorNodeId) != null) ApplyCardBrush(_g.Node(_errorNodeId)!);
        }

        // ================= 交互：选中/拖动/连线 =================
        private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border b && b.Tag is string tag && tag.StartsWith("NODE:"))
            {
                var n = _g.Node(tag.Substring(5));
                if (n == null) return;

                // 双击模块卡片 → 打开模块编辑器
                if (e.ClickCount >= 2 && n.Type == "subflow")
                {
                    OpenModuleFor(n);
                    e.Handled = true;
                    return;
                }

                bool additive = Keyboard.Modifiers is ModifierKeys.Control or ModifierKeys.Shift
                                or (ModifierKeys.Control | ModifierKeys.Shift);
                if (additive)
                {
                    if (_selNodes.Contains(n)) _selNodes.Remove(n);
                    else _selNodes.Add(n);
                    SelectNode(_selNodes.Count > 0 ? _selNodes[^1] : null);
                }
                else if (!_selNodes.Contains(n))
                {
                    _selNodes.Clear();
                    _selNodes.Add(n);
                    SelectNode(n);
                }
                else
                {
                    // 点在已有多选组内：保持组，仅切换主选
                    SelectNode(n);
                }

                var pos = e.GetPosition(Canvas);
                _dragNode = n;
                _dragOff = new System.Windows.Point(pos.X - n.X, pos.Y - n.Y);
                // 多选组整组拖动
                if (_selNodes.Count > 1 && _selNodes.Contains(n))
                {
                    _groupDragStart = pos;
                    _groupOrig = _selNodes.ToDictionary(x => x.Id, x => new System.Windows.Point(x.X, x.Y));
                }
                b.CaptureMouse();
                e.Handled = true;
            }
        }

        private void Card_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragNode == null || e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(Canvas);

            if (_groupOrig != null)
            {
                double dx = pos.X - _groupDragStart.X;
                double dy = pos.Y - _groupDragStart.Y;
                foreach (var kn in _selNodes)
                {
                    if (!_groupOrig.TryGetValue(kn.Id, out var o)) continue;
                    kn.X = Math.Max(0, o.X + dx);
                    kn.Y = Math.Max(0, o.Y + dy);
                    if (_cards.TryGetValue(kn.Id, out var kc))
                    {
                        Canvas.SetLeft(kc, kn.X);
                        Canvas.SetTop(kc, kn.Y);
                    }
                    MovePortsOf(kn);
                }
                RenderEdges();
                return;
            }

            _dragNode.X = Math.Max(0, pos.X - _dragOff.X);
            _dragNode.Y = Math.Max(0, pos.Y - _dragOff.Y);
            if (_cards.TryGetValue(_dragNode.Id, out var card))
            {
                Canvas.SetLeft(card, _dragNode.X);
                Canvas.SetTop(card, _dragNode.Y);
            }
            MovePortsOf(_dragNode);
            RenderEdges();
        }

        private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border b) b.ReleaseMouseCapture();
            _dragNode = null;
            _groupOrig = null;
            e.Handled = true;   // 阻止冒泡到画布，否则会被当成"空白单击"立刻取消选中
        }

        private void MovePortsOf(FlowNode n)
        {
            var def = FlowCatalog.Get(n.Type);
            double h = NodeH(def);
            foreach (var child in Canvas.Children)
            {
                if (child is Ellipse el && el.Tag is string t && t.StartsWith("PORT|"))
                {
                    var parts = t.Split('|');
                    if (parts[1] != n.Id) continue;
                    bool input = parts[3] == "1";
                    string port = parts[2];
                    double x, y;
                    if (input) { x = n.X + NodeW / 2; y = n.Y; }
                    else if (def.Outputs.Count <= 1) { x = n.X + NodeW / 2; y = n.Y + h; }
                    else
                    {
                        int idx = Math.Max(0, def.Outputs.FindIndex(o => o.Port == port));
                        x = n.X + NodeW * (idx + 1) / (def.Outputs.Count + 1);
                        y = n.Y + h;
                    }
                    Canvas.SetLeft(el, x - 5.5);
                    Canvas.SetTop(el, y - 5.5);
                }
            }
        }

        private void Port_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Ellipse el && el.Tag is string t && t.StartsWith("PORT|"))
            {
                var parts = t.Split('|');
                if (parts.Length >= 4 && parts[3] == "1") { e.Handled = true; return; } // 入口不发起连线
                _linkFromNode = parts[1];
                _linkFromPort = parts[2];
                _selectedEdge = null;
                try { if (!Canvas.IsMouseCaptured) Canvas.CaptureMouse(); }
                catch (InvalidOperationException) { /* 鼠标状态异常时放弃本次连线 */ CancelLink(); return; }
                e.Handled = true;
            }
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            // Ctrl 框选中：更新橡皮筋矩形
            if (_boxSelecting && e.LeftButton == MouseButtonState.Pressed)
            {
                var p = e.GetPosition(Canvas);
                double x = Math.Min(p.X, _boxStart.X), y = Math.Min(p.Y, _boxStart.Y);
                double w = Math.Abs(p.X - _boxStart.X), h = Math.Abs(p.Y - _boxStart.Y);
                if (_boxRect != null)
                {
                    Canvas.SetLeft(_boxRect, x);
                    Canvas.SetTop(_boxRect, y);
                    _boxRect.Width = Math.Max(1, w);
                    _boxRect.Height = Math.Max(1, h);
                }
                return;
            }
            // 平移中
            if (_panning) { DoCanvasPan(e); return; }
            // 空白左键按住：移动超过阈值即进入平移（工业视觉软件惯例）
            if (_panReady && e.LeftButton == MouseButtonState.Pressed && !_panning)
            {
                var p = e.GetPosition(CanvasScroll);
                if (Math.Abs(p.X - _panStart.X) > 5 || Math.Abs(p.Y - _panStart.Y) > 5)
                    BeginCanvasPan(e);
            }
            if (_panning) { DoCanvasPan(e); return; }

            if (_linkFromNode == null) return;
            var a = PortAnchor(_linkFromNode, _linkFromPort ?? "", false);
            var b = e.GetPosition(Canvas);
            if (a == null) return;
            if (_tempPath == null)
            {
                _tempPath = new Path { Stroke = TempBrush, StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 4, 3 } };
                Canvas.Children.Add(_tempPath);
            }
            _tempPath.Data = TempLine(a.Value, b);
        }

        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 仅画布空白处才准备平移（防止边/其他元素事件冒泡误入）
            if (!ReferenceEquals(e.OriginalSource, Canvas)) return;
            // Ctrl+空白拖动 = 橡皮筋框选（与平移区分）
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                _boxSelecting = true;
                _boxStart = e.GetPosition(Canvas);
                _boxRect = new Rectangle
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(0xB9, 0x8C, 0xFF)),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 3, 2 },
                    Fill = new SolidColorBrush(Color.FromArgb(0x24, 0xB9, 0x8C, 0xFF)),
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(_boxRect, _boxStart.X);
                Canvas.SetTop(_boxRect, _boxStart.Y);
                Canvas.Children.Add(_boxRect);
                return;
            }
            _panReady = true;
            _panning = false;
            _panStart = e.GetPosition(CanvasScroll);
            _panHOffset = CanvasScroll.HorizontalOffset;
            _panVOffset = CanvasScroll.VerticalOffset;
        }

        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // 框选松手：命中矩形内的节点全部入多选
            if (_boxSelecting)
            {
                var end = e.GetPosition(Canvas);
                double x0 = Math.Min(_boxStart.X, end.X), x1 = Math.Max(_boxStart.X, end.X);
                double y0 = Math.Min(_boxStart.Y, end.Y), y1 = Math.Max(_boxStart.Y, end.Y);
                if (_boxRect != null) Canvas.Children.Remove(_boxRect);
                _boxRect = null;
                _boxSelecting = false;
                if (x1 - x0 > 4 || y1 - y0 > 4)
                {
                    _selNodes.Clear();
                    foreach (var n in _g.Nodes)
                    {
                        double nh = FlowLayout.NodeH(n);
                        if (n.X + NodeW >= x0 && n.X <= x1 && n.Y + nh >= y0 && n.Y <= y1)
                            _selNodes.Add(n);
                    }
                    SelectNode(_selNodes.Count > 0 ? _selNodes[^1] : null);
                }
                return;
            }

            bool wasPanning = _panning;
            EndCanvasPan();
            _panReady = false;

            if (_linkFromNode != null)
            {
                var hit = VisualTreeHelper.HitTest(Canvas, e.GetPosition(Canvas));
                string? targetId = null;
                var d = hit?.VisualHit;
                while (d != null)
                {
                    if (d is FrameworkElement fe && fe.Tag is string tag)
                    {
                        if (tag.StartsWith("NODE:")) { targetId = tag.Substring(5); break; }
                        // 也允许直接松手在目标卡片顶部的蓝色入口圆点上
                        if (tag.StartsWith("PORT|"))
                        {
                            var pp = tag.Split('|');
                            if (pp.Length >= 4 && pp[3] == "1") { targetId = pp[1]; break; }
                        }
                    }
                    d = VisualTreeHelper.GetParent(d);
                }
                if (targetId != null && targetId != _linkFromNode)
                {
                    var targetDef = FlowCatalog.Get(_g.Node(targetId)!.Type);
                    if (targetDef.NoInput)
                        AppendLog("warn", "「开始」节点不能有输入连线");
                    else Connect(_linkFromNode, _linkFromPort ?? "", targetId);
                }
                CancelLink();
                return;
            }
            // 只在真正的画布空白处松手才取消选择（卡片/连线的松手会冒泡到这里）
            if (!wasPanning && ReferenceEquals(e.OriginalSource, Canvas))
            {
                _selectedNode = null; _selectedEdge = null;
                _selNodes.Clear();
                foreach (var n in _g.Nodes) ApplyCardBrush(n);
                BuildPropPanel();
            }
        }

        private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // 中键 = 平移（WPF 无独立中键事件）
            if (e.ChangedButton == MouseButton.Middle && e.ButtonState == MouseButtonState.Pressed)
            {
                BeginCanvasPan(e);
                e.Handled = true;
            }
        }

        private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle && _panning)
            {
                EndCanvasPan();
                e.Handled = true;
            }
        }

        private void BeginCanvasPan(MouseEventArgs e)
        {
            _panning = true;
            _panStart = e.GetPosition(CanvasScroll);
            _panHOffset = CanvasScroll.HorizontalOffset;
            _panVOffset = CanvasScroll.VerticalOffset;
            Canvas.CaptureMouse();
            Canvas.Cursor = Cursors.ScrollAll;
        }

        private void DoCanvasPan(MouseEventArgs e)
        {
            var p = e.GetPosition(CanvasScroll);
            CanvasScroll.ScrollToHorizontalOffset(Math.Max(0, _panHOffset + (_panStart.X - p.X)));
            CanvasScroll.ScrollToVerticalOffset(Math.Max(0, _panVOffset + (_panStart.Y - p.Y)));
        }

        private void EndCanvasPan()
        {
            if (!_panning) return;
            _panning = false;
            Canvas.ReleaseMouseCapture();
            Canvas.Cursor = Cursors.Arrow;
        }

        private void CanvasScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Ctrl=缩放，Shift=横向，默认=纵向；显式处理保证画布任何位置都能滚
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                double newZoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.12 : 0.89), ZoomMin, ZoomMax);
                if (Math.Abs(newZoom - _zoom) < 0.001) { e.Handled = true; return; }
                var content = e.GetPosition(Canvas);   // 变换后内容坐标
                var view = e.GetPosition(CanvasScroll);
                ApplyZoom(newZoom, content, view);
            }
            else if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                CanvasScroll.ScrollToHorizontalOffset(
                    Math.Max(0, CanvasScroll.HorizontalOffset - e.Delta));
            }
            else
            {
                CanvasScroll.ScrollToVerticalOffset(
                    Math.Max(0, CanvasScroll.VerticalOffset - e.Delta));
            }
            e.Handled = true;
        }

        private void ApplyZoom(double newZoom, System.Windows.Point content, System.Windows.Point view)
        {
            _zoom = newZoom;
            _canvasScale.ScaleX = newZoom;
            _canvasScale.ScaleY = newZoom;
            CanvasScroll.ScrollToHorizontalOffset(Math.Max(0, content.X * newZoom - view.X));
            CanvasScroll.ScrollToVerticalOffset(Math.Max(0, content.Y * newZoom - view.Y));
            ZoomLabel.Text = $"{newZoom * 100:0}%";
        }

        private void ZoomReset_Click(object sender, MouseButtonEventArgs e)
            => ApplyZoom(1.0, new System.Windows.Point(0, 0), new System.Windows.Point(0, 0));

        private void CanvasScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) { /* 预留 */ }

        private void CancelLink()
        {
            _linkFromNode = null; _linkFromPort = null;
            if (_tempPath != null) { Canvas.Children.Remove(_tempPath); _tempPath = null; }
            if (Canvas.IsMouseCaptured) Canvas.ReleaseMouseCapture();
        }

        private void Connect(string from, string port, string to)
        {
            // 同一出口只保留一条线（重新布线）
            _g.Edges.RemoveAll(e => e.From == from && e.Port == port);
            // 防止重复
            if (!_g.Edges.Any(e => e.From == from && e.Port == port && e.To == to))
                _g.Edges.Add(new FlowEdge { Id = _g.NewId("e"), From = from, Port = port, To = to });
            RenderAll();
        }

        // ================= 拖入新节点 =================
        private void Canvas_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.StringFormat)) e.Effects = DragDropEffects.Copy;
        }

        private void Canvas_Drop(object sender, DragEventArgs e)
        {
            var data = e.Data.GetData(DataFormats.StringFormat) as string;
            var pos = e.GetPosition(Canvas);
            double nx = Math.Max(10, pos.X - NodeW / 2), ny = Math.Max(10, pos.Y - 30);
            if (data != null && data.StartsWith("FLOWNODE:"))
            {
                var type = data.Substring(9);
                var n = _g.AddNode(type, nx, ny);
                RenderAll();
                _selNodes.Clear(); _selNodes.Add(n);
                SelectNode(n);
            }
            else if (data != null && data.StartsWith("FLOWMOD:"))
            {
                var file = data.Substring(8);
                var mod = SubflowLibrary.Load(file);
                if (mod == null || mod.Entry() == null)
                {
                    AppendLog("error", $"模块【{file}】已损坏或为空，无法放入");
                    return;
                }
                var n = _g.AddNode("subflow", nx, ny);
                n.Params["mod"] = file;
                n.Params["modname"] = mod.Name;
                RenderAll();
                _selNodes.Clear(); _selNodes.Add(n);
                SelectNode(n);
                AppendLog("info", $"已放入模块卡片【{mod.Name}】（{mod.Nodes.Count} 个节点），双击卡片可打开编辑");
            }
        }

        // ================= 选中/删除 =================
        private void SelectNode(FlowNode? n)
        {
            _selectedNode = n;
            _selectedEdge = null;
            foreach (var id in _cards.Keys.ToList())
                ApplyCardBrush(_g.Node(id)!);
            RenderEdges();
            BuildPropPanel();
        }

        private void SelectEdge(FlowEdge e)
        {
            _selectedEdge = e;
            _selectedNode = null;
            foreach (var id in _cards.Keys.ToList()) ApplyCardBrush(_g.Node(id)!);
            RenderEdges();
            BuildPropPanel();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // 在输入框内按 Delete/Backspace 只删字符，不删节点
            if (e.OriginalSource is TextBox or ComboBox or PasswordBox) return;
            if (e.Key == Key.Delete)
            {
                if (_selNodes.Count > 1)
                {
                    int removed = 0;
                    foreach (var n in _selNodes.ToList())
                    {
                        if (n.Type == "start") continue;
                        if (_errorNodeId == n.Id) ClearNodeError();
                        _g.RemoveNode(n.Id);
                        removed++;
                    }
                    _selNodes.Clear();
                    _selectedNode = null;
                    RenderAll(); BuildPropPanel();
                    if (removed > 0) AppendLog("info", $"已删除选中的 {removed} 个节点");
                    e.Handled = true;
                }
                else if (_selectedNode != null) { DeleteNode(_selectedNode); e.Handled = true; }
                else if (_selectedEdge != null)
                {
                    _g.Edges.Remove(_selectedEdge);
                    _selectedEdge = null;
                    RenderAll(); BuildPropPanel();
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Escape) CancelLink();
        }

        private void DeleteNode(FlowNode n)
        {
            if (n.Type == "start") { AppendLog("warn", "开始节点不能删除"); return; }
            if (_errorNodeId == n.Id) ClearNodeError();
            _g.RemoveNode(n.Id);
            _selectedNode = null;
            _selNodes.Remove(n);
            RenderAll(); BuildPropPanel();
        }

        // ================= 属性面板 =================
        private Button MkBtn(string content) => new()
        {
            Content = content,
            Style = (Style)FindResource("FBtn")
        };

        /// <summary>独立弹窗（无 Window 资源）用的深色按钮，避免深色背景上黑字看不见</summary>
        internal static Button DarkBtn(string content) => new()
        {
            Content = content,
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1F, 0x29)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xE3, 0xEC)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x36, 0x45)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(3),
            MinHeight = 28,
            Cursor = Cursors.Hand
        };

        private void BuildPropPanel()
        {
            PropPanel.Children.Clear();
            if (_selectedNode != null) BuildNodeProps(_selectedNode);
            else if (_selectedEdge != null) BuildEdgeProps(_selectedEdge);
            else
            {
                PropPanel.Children.Add(new TextBlock
                {
                    Text = "点选节点查看属性；\n从右侧圆点拖到目标节点连线；\n拖空白处移动节点；Delete 删除。",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x76, 0x86)),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2)
                });
                PropPanel.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 0), Opacity = 0.3 });
            }
        }

        private void BuildNodeProps(FlowNode n)
        {
            PropTitle.Text = n.Type == "subflow" ? "属性 · 🧩 我的模块" : $"属性 · {FlowCatalog.Get(n.Type).Title}";
            if (n.Type == "subflow") BuildSubflowProps(PropPanel, n, null);
            else BuildNodeEditor(PropPanel, n, null);
        }

        /// <summary>节点属性编辑器（右侧面板与右键属性弹窗共用）。onDelete 非空时表示处于弹窗中。</summary>
        private void BuildNodeEditor(Panel host, FlowNode n, Action? onDelete)
        {
            var def = FlowCatalog.Get(n.Type);

            var alias = LabelledBox("节点备注名(可空)", n.Alias, "");
            alias.TextBox.TextChanged += (s, e) => { n.Alias = alias.TextBox.Text; RefreshCardText(n); };
            host.Children.Add(alias.Wrap);

            foreach (var pd in def.Params)
            {
                var value = n.Get(pd.Key, pd.Default);
                switch (pd.Type)
                {
                    case FlowParamType.Bool:
                    {
                        var cb = new CheckBox
                        {
                            Content = pd.Label, Foreground = Brushes.WhiteSmoke,
                            IsChecked = value == "true", Margin = new Thickness(2, 8, 2, 2),
                            ToolTip = pd.Tip
                        };
                        cb.Checked += (s, e) => { n.Params[pd.Key] = "true"; RefreshCardText(n); };
                        cb.Unchecked += (s, e) => { n.Params[pd.Key] = "false"; RefreshCardText(n); };
                        host.Children.Add(cb);
                        break;
                    }
                    case FlowParamType.Combo:
                    {
                        var items = pd.Key == "tpl" ? TemplateManager.List() : pd.Options.ToList();
                        var cb = new ComboBox
                        {
                            Margin = new Thickness(2, 6, 2, 2),
                            IsEditable = pd.Key == "tpl",
                            ToolTip = pd.Tip
                        };
                        foreach (var o in items) cb.Items.Add(o);
                        if (items.Contains(value)) cb.SelectedItem = value;
                        else cb.Text = value;
                        cb.SelectionChanged += (s, e) => { n.Params[pd.Key] = cb.SelectedItem?.ToString() ?? cb.Text; RefreshCardText(n); };
                        cb.LostFocus += (s, e) => { n.Params[pd.Key] = cb.Text; RefreshCardText(n); };
                        host.Children.Add(MakeLabel(pd.Label, pd.Tip));
                        var row = new DockPanel { LastChildFill = true };
                        var calBtn = MkBtn("标定…");
                        calBtn.Width = 56; calBtn.Margin = new Thickness(4, 6, 0, 2);
                        DockPanel.SetDock(calBtn, Dock.Right);
                        calBtn.Click += (s, e) => CalibrateTemplate(cb);
                        row.Children.Add(calBtn);
                        row.Children.Add(cb);
                        host.Children.Add(row);
                        break;
                    }
                    case FlowParamType.Coord:
                    {
                        host.Children.Add(MakeLabel(pd.Label, pd.Tip));
                        var tb = new TextBox { Text = value, Tag = pd.Key };
                        var btn = MkBtn("点选坐标");
                        btn.Margin = new Thickness(2, 3, 2, 2);
                        btn.Click += (s, e) =>
                        {
                            var pt = DoPickCoord(def.Title + "·" + pd.Label);
                            if (pt.HasValue)
                            {
                                tb.Text = $"{pt.Value.X},{pt.Value.Y}";
                                n.Params[pd.Key] = tb.Text;
                                RefreshCardText(n);
                            }
                        };
                        var dp = new DockPanel();
                        var clear = MkBtn("×");
                        clear.Width = 30; clear.Margin = new Thickness(4, 3, 0, 2); clear.ToolTip = "清空";
                        DockPanel.SetDock(clear, Dock.Right);
                        clear.Click += (s, e) => { tb.Text = ""; n.Params[pd.Key] = ""; RefreshCardText(n); };
                        dp.Children.Add(clear); dp.Children.Add(btn);
                        host.Children.Add(tb);
                        host.Children.Add(dp);
                        WireText(tb, n, pd.Key);
                        break;
                    }
                    case FlowParamType.Rect:
                    {
                        host.Children.Add(MakeLabel(pd.Label, pd.Tip));
                        var tb = new TextBox { Text = value };
                        var dp = new DockPanel();
                        var sel = MkBtn("框选区域");
                        sel.Margin = new Thickness(2, 3, 4, 2);
                        DockPanel.SetDock(sel, Dock.Left);
                        sel.Click += (s, e) =>
                        {
                            var r = DoPickRect();
                            if (r.HasValue)
                            {
                                tb.Text = $"{r.Value.X},{r.Value.Y},{r.Value.Width},{r.Value.Height}";
                                n.Params[pd.Key] = tb.Text; RefreshCardText(n);
                            }
                        };
                        var clr = MkBtn("清空");
                        clr.Width = 70; clr.Margin = new Thickness(0, 3, 0, 2);
                        clr.Click += (s, e) => { tb.Text = ""; n.Params[pd.Key] = ""; RefreshCardText(n); };
                        dp.Children.Add(sel); dp.Children.Add(clr);
                        host.Children.Add(tb);
                        host.Children.Add(dp);
                        WireText(tb, n, pd.Key);
                        break;
                    }
                    default:
                    {
                        var lb = LabelledBox(pd.Label, value, pd.Tip);
                        host.Children.Add(lb.Wrap);
                        WireText(lb.TextBox, n, pd.Key);
                        break;
                    }
                    case FlowParamType.Code:
                    {
                        host.Children.Add(MakeLabel(pd.Label, pd.Tip));
                        var tb = new TextBox
                        {
                            Text = value,
                            AcceptsReturn = true,
                            AcceptsTab = true,
                            TextWrapping = TextWrapping.NoWrap,
                            MaxHeight = pd.Lines > 0 ? pd.Lines * 16 + 8 : 140,
                            TextAlignment = TextAlignment.Left,
                            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                            FontSize = 12,
                            Foreground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE7, 0xEC)),
                            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x10)),
                            BorderBrush = new SolidColorBrush(Color.FromRgb(0x23, 0x29, 0x36)),
                            BorderThickness = new Thickness(1),
                            Padding = new Thickness(6, 4, 6, 4),
                            Tag = pd.Key
                        };
                        tb.TextChanged += (s, e) => { n.Params[pd.Key] = tb.Text; RefreshCardText(n); };
                        host.Children.Add(tb);
                        break;
                    }
                }
            }

            // 屏蔽/启用
            var dis = MkBtn(n.Disabled ? "✓ 已屏蔽 · 点此启用" : "⚑ 屏蔽此节点");
            dis.Margin = new Thickness(2, 8, 2, 2);
            dis.Background = new SolidColorBrush(n.Disabled
                ? Color.FromRgb(0x2A, 0x4D, 0x3A)
                : Color.FromRgb(0x3D, 0x3A, 0x2E));
            dis.BorderBrush = new SolidColorBrush(n.Disabled
                ? Color.FromRgb(0x2B, 0x9C, 0x5E)
                : Color.FromRgb(0x8C, 0x7A, 0x3E));
            dis.Foreground = new SolidColorBrush(n.Disabled
                ? Color.FromRgb(0x7D, 0xDD, 0xA2)
                : Color.FromRgb(0xE8, 0xC9, 0x77));
            dis.Click += (s, e) =>
            {
                n.Disabled = !n.Disabled;
                RebuildCardVisual(n);
                BuildPropPanel();
                AppendLog("info", n.Disabled ? $"⛔ 节点「{FlowCatalog.Get(n.Type).Title}」已屏蔽"
                                             : $"✔ 节点「{FlowCatalog.Get(n.Type).Title}」已启用");
            };
            host.Children.Add(dis);

            var del = MkBtn(n.Type == "start" ? "开始节点不可删除" : "删除该节点");
            del.Margin = new Thickness(2, 14, 2, 2);
            del.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x80));
            del.BorderBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x2E, 0x33));
            if (n.Type == "start") del.IsEnabled = false;
            del.Click += (s, e) =>
            {
                DeleteNode(n);
                onDelete?.Invoke();
            };
            host.Children.Add(del);
        }

        // ================= 右键菜单 =================
        private void BuildCanvasMenu()
        {
            var m = new ContextMenu();
            var miPack = new MenuItem
            {
                Header = "🧩 打包选中节点为我的模块…",
                ToolTip = "先在画布上 Ctrl+点选 或 Ctrl+空白拖框 选中多个节点"
            };
            miPack.Click += (s, e) => PackageSelectedAsModule();
            var miImport = new MenuItem { Header = "📥 导入模块文件（.subflow.json）…" };
            miImport.Click += ImportModule_Click;
            var miArrange = new MenuItem { Header = "🧹 整理布局（自动排版）" };
            miArrange.Click += Arrange_Click;
            var miClear = new MenuItem { Header = "🗑 清空画布" };
            miClear.Click += Clear_Click;
            m.Items.Add(miPack);
            m.Items.Add(miImport);
            m.Items.Add(new Separator());
            m.Items.Add(miArrange);
            m.Items.Add(new Separator());
            m.Items.Add(miClear);
            m.Opened += (s, e) => miPack.IsEnabled = _selNodes.Count >= 1;
            Canvas.ContextMenu = m;
        }

        private ContextMenu BuildNodeMenu(FlowNode n)
        {
            var m = new ContextMenu();
            var miEdit = new MenuItem { Header = "✎ 编辑属性…" };
            miEdit.Click += (s, e) => OpenNodeProps(n);
            var miToggleDis = new MenuItem { Header = n.Disabled ? "✔ 启用节点" : "⛔ 屏蔽节点" };
            miToggleDis.Click += (s, e) =>
            {
                n.Disabled = !n.Disabled;
                RebuildCardVisual(n);
                BuildPropPanel();
            };
            var miDup = new MenuItem { Header = "⧉ 复制节点" };
            miDup.Click += (s, e) => DuplicateNode(n);
            var miPack = new MenuItem { Header = "🧩 打包选中节点为我的模块…" };
            miPack.Click += (s, e) => PackageSelectedAsModule();
            miPack.IsEnabled = false;
            var miOpenMod = new MenuItem { Header = "🧩 打开模块编辑…" };
            miOpenMod.Click += (s, e) => OpenModuleFor(n);
            miOpenMod.Visibility = n.Type == "subflow" ? Visibility.Visible : Visibility.Collapsed;
            var miDel = new MenuItem { Header = "🗑 删除节点（Delete）" };
            if (n.Type == "start") miDel.IsEnabled = false;
            miDel.Click += (s, e) => DeleteNode(n);
            m.Items.Add(miEdit);
            m.Items.Add(miToggleDis);
            m.Items.Add(miDup);
            m.Items.Add(miOpenMod);
            m.Items.Add(new Separator());
            m.Items.Add(miPack);
            m.Items.Add(new Separator());
            m.Items.Add(miDel);
            m.Opened += (s, e) =>
            {
                miPack.Header = _selNodes.Count > 1
                    ? $"🧩 打包选中的 {_selNodes.Count} 个节点为模块…"
                    : "🧩 打包选中节点为我的模块…（先 Ctrl 多选）";
                miPack.IsEnabled = _selNodes.Count > 1;
            };
            return m;
        }

        private ContextMenu BuildEdgeMenu(FlowEdge edge)
        {
            var m = new ContextMenu();
            var mi = new MenuItem { Header = "🗽 删除该连线（Delete）" };
            mi.Click += (s, e) =>
            {
                _g.Edges.Remove(edge);
                if (ReferenceEquals(_selectedEdge, edge)) _selectedEdge = null;
                RenderAll(); BuildPropPanel();
            };
            m.Items.Add(mi);
            return m;
        }

        /// <summary>节点属性弹窗（不依赖右侧面板，画布上右键即可改属性）</summary>
        private void OpenNodeProps(FlowNode n)
        {
            SelectNode(n);
            var win = new Window
            {
                Title = n.Type == "subflow" ? "属性 · 🧩 我的模块" : $"属性 · {FlowCatalog.Get(n.Type).Title}",
                Width = 380, Height = 520,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x10)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xEB, 0xF2))
            };
            var sp = new StackPanel();
            var sv = new ScrollViewer
            {
                Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(12)
            };
            var dock = new DockPanel();
            var closeBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var btnClose = DarkBtn("关闭");
            btnClose.Width = 84; btnClose.Margin = new Thickness(0, 0, 12, 8);
            btnClose.Click += (s, e) => win.Close();
            closeBar.Children.Add(btnClose);
            DockPanel.SetDock(closeBar, Dock.Bottom);
            dock.Children.Add(closeBar);
            dock.Children.Add(sv);
            win.Content = dock;
            if (n.Type == "subflow") BuildSubflowProps(sp, n, () => win.Close());
            else BuildNodeEditor(sp, n, () => win.Close());
            _propsDialog = win;
            try { win.ShowDialog(); }
            finally { _propsDialog = null; }
            // 弹窗关闭后右侧面板同步
            BuildPropPanel();
        }

        /// <summary>点选坐标前先检查绑定、隐藏本窗口（含属性弹窗）让游戏露出来</summary>
        private System.Drawing.Point? DoPickCoord(string label)
        {
            if (!_host.HasWindowA)
            {
                MessageBox.Show("请先回主界面「首页」把准星图标拖到游戏窗口上，完成窗口A绑定后再来点选。\n" +
                                "所有节点坐标都是相对游戏窗口的，绑定一次即可（游戏重启/改分辨率需重新绑定）。",
                    "未绑定游戏窗口", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            bool hasDlg = _propsDialog != null;
            _propsDialog?.Hide();
            Hide();
            Thread.Sleep(220);   // 等窗口动画/画面重绘，避免遮罩截到本窗口
            try { return _host.PickCoord(label); }
            catch (Exception ex)
            {
                AppendLog("error", $"点选坐标失败：{ex.Message}");
                MessageBox.Show("点选坐标失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
            finally
            {
                Show();
                if (hasDlg) _propsDialog?.Show();
                Activate();
                _propsDialog?.Activate();
            }
        }

        /// <summary>框选区域前先检查绑定、隐藏本窗口（含属性弹窗）让游戏露出来</summary>
        private System.Drawing.Rectangle? DoPickRect()
        {
            if (!_host.HasWindowA)
            {
                MessageBox.Show("请先回主界面「首页」把准星图标拖到游戏窗口上，完成窗口A绑定后再来框选。\n" +
                                "识别区域是相对游戏窗口的坐标，绑定一次即可（游戏重启/改分辨率需重新绑定）。",
                    "未绑定游戏窗口", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            bool hasDlg = _propsDialog != null;
            _propsDialog?.Hide();
            Hide();
            Thread.Sleep(220);
            try { return _host.PickRect(); }
            catch (Exception ex)
            {
                AppendLog("error", $"框选区域失败：{ex.Message}");
                MessageBox.Show("框选区域失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
            finally
            {
                Show();
                if (hasDlg) _propsDialog?.Show();
                Activate();
                _propsDialog?.Activate();
            }
        }

        private void DuplicateNode(FlowNode n)
        {
            var copy = _g.AddNode(n.Type, n.X + 28, n.Y + 28);
            copy.Alias = string.IsNullOrEmpty(n.Alias) ? "" : n.Alias + "_副本";
            foreach (var kv in n.Params) copy.Params[kv.Key] = kv.Value;
            RenderAll();
            SelectNode(copy);
            AppendLog("ok", $"已复制节点：{FlowCatalog.Get(n.Type).Title}（副本未带连线，需自行连接）");
        }

        private void BuildEdgeProps(FlowEdge e)
        {
            PropTitle.Text = "属性 · 连线";
            var fn = _g.Node(e.From); var tn = _g.Node(e.To);
            PropPanel.Children.Add(new TextBlock
            {
                Text = $"从：{FlowCatalog.Get(fn!.Type).Title}\n出口：{PortLabel(fn, e.Port)}\n到：{FlowCatalog.Get(tn!.Type).Title}",
                Foreground = Brushes.WhiteSmoke, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 8, 2, 8)
            });
            var del = MkBtn("删除该连线");
            del.Margin = new Thickness(2, 6, 2, 2);
            del.Click += (s, ev) =>
            {
                _g.Edges.Remove(e); _selectedEdge = null; RenderAll(); BuildPropPanel();
            };
            PropPanel.Children.Add(del);
        }

        private static string PortLabel(FlowNode n, string port)
        {
            var def = FlowCatalog.Get(n.Type);
            var p = def.Outputs.Find(o => o.Port == port);
            return p?.Label ?? "→";
        }

        private static TextBlock MakeLabel(string text, string tip)
            => new()
            {
                Text = text, Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA6, 0xB5)),
                Margin = new Thickness(2, 9, 2, 0), TextWrapping = TextWrapping.Wrap, ToolTip = tip
            };

        private record Labeled(StackPanel Wrap, TextBox TextBox);

        private static Labeled LabelledBox(string label, string value, string tip)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            sp.Children.Add(MakeLabel(label, tip));
            var tb = new TextBox
            {
                Text = value, Margin = new Thickness(2, 3, 2, 0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                AcceptsReturn = label.Contains("提问") || label.Contains("问题") || label.Contains("说明") || label.Contains("条件")
            };
            sp.Children.Add(tb);
            return new Labeled(sp, tb);
        }

        private void WireText(TextBox tb, FlowNode n, string key)
        {
            tb.TextChanged += (s, e) =>
            {
                n.Params[key] = tb.Text;
                RefreshCardText(n);
            };
        }

        private void RefreshCardText(FlowNode n)
        {
            RenderEdges();
            // 就地刷新卡片摘要，避免重绘属性面板导致输入框失焦
            if (!_cards.TryGetValue(n.Id, out var card)) return;
            var def = FlowCatalog.Get(n.Type);
            if (card.Child is not Grid grid) return;
            if (grid.Children.Count > 0 && grid.Children[0] is Border head && head.Child is TextBlock title)
                title.Text = string.IsNullOrEmpty(n.Alias) ? def.Title : n.Alias;
            if (def.Outputs.Count > 1) return;
            if (grid.Children.Count > 1 && grid.Children[1] is StackPanel body
                && body.Children.Count > 0 && body.Children[0] is TextBlock tb)
            {
                tb.Text = FlowCatalog.Summary(n);
            }
        }

        // ================= 模板标定 =================
        private void CalibrateTemplate(ComboBox cb)
        {
            var name = PromptDialog.Show(this, "标定图像模板", "模板名称（如 领取按钮 / 封禁弹窗）：", cb.Text ?? "");
            if (string.IsNullOrWhiteSpace(name)) return;
            Hide();
            Thread.Sleep(250);
            try
            {
                var sel = new OverlaySelectWindow();
                if (sel.ShowDialog() == true)
                {
                    var path = TemplateManager.Calibrate(name.Trim(), sel.SelectedRect);
                    AppendLog("claim", $"模板已标定：{name.Trim()} → {path}");
                    cb.Items.Clear();
                    foreach (var t in TemplateManager.List()) cb.Items.Add(t);
                    cb.Text = name.Trim();
                }
            }
            finally { Show(); Activate(); }
        }

        private void TemplateLib_Click(object sender, RoutedEventArgs e)
        {
            var win = new Window
            {
                Title = "图像模板库（标定后在模板节点里选用）",
                Width = 420, Height = 520,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x11, 0x16))
            };
            var dock = new DockPanel { Margin = new Thickness(10) };
            var bar = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(bar, Dock.Top);
            var lb = new ListBox { Height = 380, Margin = new Thickness(0, 8, 0, 8),
                Background = new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x20)), Foreground = Brushes.WhiteSmoke };
            var cal = DarkBtn("标定新模板");
            var del = DarkBtn("删除选中");
            bar.Children.Add(cal); bar.Children.Add(del);
            var close = DarkBtn("关闭");
            close.HorizontalAlignment = HorizontalAlignment.Right;
            DockPanel.SetDock(close, Dock.Bottom);
            close.Click += (s, ev) => win.Close();
            void Reload() { lb.Items.Clear(); foreach (var t in TemplateManager.List()) lb.Items.Add(t); }
            cal.Click += (s, ev) =>
            {
                var name = PromptDialog.Show(win, "标定图像模板", "模板名称：", "新模板");
                if (string.IsNullOrWhiteSpace(name)) return;
                win.Hide(); Hide(); Thread.Sleep(250);
                try
                {
                    var o = new OverlaySelectWindow();
                    if (o.ShowDialog() == true)
                    {
                        TemplateManager.Calibrate(name.Trim(), o.SelectedRect);
                        AppendLog("claim", $"模板已标定：{name.Trim()}");
                    }
                }
                finally { Show(); Activate(); win.Show(); Reload(); }
            };
            del.Click += (s, ev) =>
            {
                if (lb.SelectedItem is string t)
                {
                    if (MessageBox.Show($"删除模板 {t}？", "确认", MessageBoxButton.OKCancel) == MessageBoxResult.OK)
                    { TemplateManager.Delete(t); Reload(); }
                }
            };
            dock.Children.Add(bar); dock.Children.Add(close); dock.Children.Add(lb);
            win.Content = dock;
            Reload();
            win.ShowDialog();
            // 属性面板里的模板下拉同步刷新
            if (_selectedNode != null) BuildNodeProps(_selectedNode);
        }

        // ================= 我的模块（子流程） =================

        /// <summary>把当前画布上选中的多个节点打包成 nodemods 下的新模块（原节点保留，模块是副本）</summary>
        private void PackageSelectedAsModule()
        {
            if (_moduleMode)
            {
                AppendLog("warn", "模块编辑窗口内不能再打包模块（请回主流程画布操作）");
                return;
            }
            var picked = _selNodes.ToList();
            // start/end 是主流程专有节点：模块入口由外部连线决定、走完自然返回，不收入模块
            var excluded = picked.Where(n => n.Type is "start" or "end").ToList();
            picked = picked.Where(n => n.Type is not "start" and not "end").ToList();
            if (picked.Count == 0)
            {
                MessageBox.Show("请先在画布上选中要打包的节点（Ctrl+点选 或 Ctrl+空白拖框）。\n" +
                                "「开始/结束」节点属于主流程，不会打进模块。",
                    "打包模块", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (excluded.Count > 0)
                AppendLog("info", $"已自动跳过 {excluded.Count} 个开始/结束节点（模块不需要它们）");

            var ids = picked.Select(n => n.Id).ToHashSet();
            var internalEdges = _g.Edges.Where(e => ids.Contains(e.From) && ids.Contains(e.To)).ToList();
            // 来自选中集外部（含被剔除的开始节点）的入边 → 模块入口候选
            var externalIn = _g.Edges
                .Where(e => ids.Contains(e.To) && !ids.Contains(e.From))
                .Select(e => e.To).ToHashSet();
            var entryId = SubflowLibrary.InferEntryId(picked, internalEdges, externalIn);

            var name = PromptDialog.Show(this, "打包为我的模块",
                "模块名称（会显示在工具箱里）：", picked[0].Alias.Length > 0 ? picked[0].Alias : "我的模块" + picked.Count + "连");
            if (string.IsNullOrWhiteSpace(name)) return;

            // 坐标归一化到左上角留边距，模块在自己的编辑器里从 (40,30) 附近开始
            double minX = picked.Min(n => n.X), minY = picked.Min(n => n.Y);
            var nodes = picked.Select(n => new FlowNode
            {
                Id = n.Id, Type = n.Type, Alias = n.Alias, Disabled = n.Disabled,
                X = n.X - minX + 40, Y = n.Y - minY + 30,
                Params = new Dictionary<string, string>(n.Params)
            }).ToList();
            var edges = internalEdges.Select(e => new FlowEdge { Id = e.Id, From = e.From, Port = e.Port, To = e.To }).ToList();

            try
            {
                var mod = new SubflowMod
                {
                    Name = name.Trim(),
                    Description = "",
                    EntryId = entryId ?? "",
                    Nodes = nodes,
                    Edges = edges
                };
                var file = SubflowLibrary.SaveNew(mod);
                AppendLog("claim", $"🧩 模块【{name.Trim()}】已打包（{nodes.Count} 个节点、{edges.Count} 条内部连线），" +
                                   $"已出现在左侧工具箱「🧩我的模块」，可拖到任何流程复用。文件：{file}");
                BuildModuleToolbox();
            }
            catch (Exception ex)
            {
                AppendLog("error", $"打包模块失败：{ex.Message}");
                MessageBox.Show("打包失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>导入别人发来的 .subflow.json 到本机 nodemods 目录</summary>
        private void ImportModule_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "子流程模块|*.subflow.json|JSON 文件|*.json",
                InitialDirectory = Directory.Exists(SubflowLibrary.Dir) ? SubflowLibrary.Dir : AppDomain.CurrentDomain.BaseDirectory,
                Title = "选择要导入的模块文件"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var file = SubflowLibrary.ImportFile(dlg.FileName);
                var mod = SubflowLibrary.Load(file);
                AppendLog("claim", $"📥 模块已导入：{mod?.Name ?? file}（文件：{file}）");
                BuildModuleToolbox();
                MessageBox.Show($"模块【{mod?.Name ?? file}】已导入工具箱。\n拖到画布即可使用，双击卡片能打开继续编辑。",
                    "导入成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppendLog("error", $"导入模块失败：{ex.Message}");
                MessageBox.Show("导入失败，文件可能不是有效的模块：" + ex.Message, "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>双击 subflow 卡片：在新的工坊窗口里打开该模块，保存直接写回模块文件</summary>
        private void OpenModuleFor(FlowNode n)
        {
            var file = n.Get("mod");
            if (string.IsNullOrWhiteSpace(file))
            {
                MessageBox.Show("该模块卡片没有绑定模块文件，请删除后从工具箱重新拖入。", "模块缺失",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var path = SubflowLibrary.ResolvePath(file);
            if (!File.Exists(path))
            {
                AppendLog("error", $"模块文件不存在：{file}");
                MessageBox.Show($"找不到模块文件：\n{path}\n\n可点「📥导入」重新导入同名模块，或删除该卡片。",
                    "模块文件丢失", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var w = new FlowEditorWindow(_host, file) { Owner = this };
            w.Show();
        }

        private string _moduleName = "";
        private string _moduleDesc = "";

        /// <summary>进入模块编辑模式：载入 nodemods/*.subflow.json，保存写回原文件而非 flows 工程</summary>
        private void EnterModuleMode(string file)
        {
            var path = SubflowLibrary.ResolvePath(file);
            var mod = SubflowLibrary.FromFile(path);
            _moduleMode = true;
            _moduleFile = path;
            _moduleName = mod.Name;
            _moduleDesc = mod.Description ?? "";
            _g = mod.ToFlowGraph();
            _filePath = null;
            _selectedNode = null;
            _selNodes.Clear();
            Title = $"🧩 模块编辑 · {mod.Name}";
            PropTitle.Text = "属性 · 模块节点";
            RunBtn.Visibility = Visibility.Collapsed;
            StopBtn.Visibility = Visibility.Collapsed;
            RenderAll(); BuildPropPanel();
            AppendLog("info", $"已打开模块【{mod.Name}】（{mod.Nodes.Count} 个节点）：从入口节点开始执行，" +
                              "走到未连线出口或「结束」即返回主流程；变量与主流程共享。点「保存」写回模块文件。");
        }

        /// <summary>模块模式下的保存：收集画布回 SubflowMod 并覆盖原模块文件，入口节点尽量沿用原设置</summary>
        private bool SaveModule()
        {
            try
            {
                // 原入口还在就沿用，否则按内部入度重新推断
                var old = SubflowLibrary.FromFile(_moduleFile!);
                string? entryId = _g.Node(old.EntryId) != null ? old.EntryId
                          : SubflowLibrary.InferEntryId(_g.Nodes, _g.Edges, null);
                var mod = new SubflowMod
                {
                    Name = _moduleName,
                    Description = _moduleDesc,
                    EntryId = entryId ?? "",
                    Nodes = _g.Nodes,
                    Edges = _g.Edges
                };
                SubflowLibrary.SaveAs(mod, System.IO.Path.GetFileName(_moduleFile!));
                AppendLog("claim", $"🧩 模块【{_moduleName}】已保存，所有引用它的流程下次运行自动使用新版本");
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("模块保存失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>subflow 模块卡片的专属属性面板（不暴露 mod/modname 裸文本框）</summary>
        private void BuildSubflowProps(Panel host, FlowNode n, Action? onDelete)
        {
            var file = n.Get("mod");
            var name = n.Get("modname");
            var mod = SubflowLibrary.Load(file);

            var alias = LabelledBox("卡片备注名(可空)", n.Alias, "");
            alias.TextBox.TextChanged += (s, e) => { n.Alias = alias.TextBox.Text; RefreshCardText(n); };
            host.Children.Add(alias.Wrap);

            host.Children.Add(MakeLabel("绑定模块", "模块文件保存在软件目录 nodemods 下，可发给别人导入"));
            host.Children.Add(new TextBlock
            {
                Text = $"🧩 {name}\n📄 {file}\n节点数：{(mod != null ? mod.Nodes.Count.ToString() : "（模块已丢失）")}",
                Foreground = new SolidColorBrush(mod != null
                    ? Color.FromRgb(0xC9, 0x9C, 0xFF)
                    : Color.FromRgb(0xFF, 0x7A, 0x80)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 2, 2, 6)
            });
            if (mod != null && !string.IsNullOrWhiteSpace(mod.Description))
                host.Children.Add(new TextBlock
                {
                    Text = mod.Description,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x96, 0xA5)),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 2, 8)
                });

            var open = MkBtn("🧩 打开编辑（也可双击卡片）");
            open.Click += (s, e) => OpenModuleFor(n);
            host.Children.Add(open);

            var swap = MkBtn("🔄 换一个模块…");
            swap.Click += (s, e) =>
            {
                var dlg = new OpenFileDialog
                {
                    Filter = "子流程模块|*.subflow.json",
                    InitialDirectory = SubflowLibrary.Dir,
                    Title = "选择替换的模块文件"
                };
                if (dlg.ShowDialog() != true) return;
                try
                {
                    var m2 = SubflowMod.FromJson(File.ReadAllText(dlg.FileName));
                    if (m2.Nodes.Count == 0 || m2.Entry() == null)
                    {
                        MessageBox.Show("该模块为空或入口无效"); return;
                    }
                    n.Params["mod"] = System.IO.Path.GetFileName(dlg.FileName);
                    n.Params["modname"] = m2.Name;
                    AppendLog("info", $"模块卡片已替换为【{m2.Name}】");
                    RenderAll(); BuildPropPanel();
                }
                catch (Exception ex) { MessageBox.Show("替换失败：" + ex.Message); }
            };
            host.Children.Add(swap);

            var dir = MkBtn("📂 打开模块目录");
            dir.Click += (s, e) =>
            {
                try
                {
                    Directory.CreateDirectory(SubflowLibrary.Dir);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", SubflowLibrary.Dir) { UseShellExecute = true });
                }
                catch (Exception ex) { AppendLog("error", ex.Message); }
            };
            host.Children.Add(dir);

            var del = MkBtn("删除该模块卡片");
            del.Margin = new Thickness(2, 14, 2, 2);
            del.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x80));
            del.BorderBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x2E, 0x33));
            del.Click += (s, e) =>
            {
                DeleteNode(n);
                onDelete?.Invoke();
            };
            host.Children.Add(del);
        }

        // ================= 文件 =================
        private static string FlowDir => System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "flows");
        private static string LastPath => System.IO.Path.Combine(FlowDir, "_last.json");

        private void NewGraph(bool quiet)
        {
            _g = new FlowGraph { Name = "我的流程" };
            var s = _g.AddNode("start", 60, 60);
            _filePath = null;
            _selectedNode = null; _selectedEdge = null;
            RenderAll(); BuildPropPanel();
            if (!quiet) AppendLog("info", "已新建空白流程");
        }

        private void LoadLastOrDefault()
        {
            try
            {
                if (File.Exists(LastPath))
                {
                    _g = FlowGraph.FromJson(File.ReadAllText(LastPath));
                    _filePath = LastPath;
                    RenderAll();
                    AppendLog("info", "已载入上次编辑的流程");
                }
            }
            catch (Exception ex) { AppendLog("error", $"载入上次流程失败: {ex.Message}"); }
        }

        private void New_Click(object sender, RoutedEventArgs e)
        {
            if (_moduleMode) { MessageBox.Show("这是模块编辑窗口，请关闭后回主流程新建工程。"); return; }
            NewGraph(false);
        }

        /// <summary>主窗口「新建工程」入口：空白流程（保存后即成为 flows 下的新工程）</summary>
        public void StartBlankProject() => NewGraph(false);

        /// <summary>主窗口「编辑工程」入口：打开指定工程文件</summary>
        public bool OpenProjectFile(string path)
        {
            try
            {
                _g = FlowGraph.FromJson(File.ReadAllText(path));
                _filePath = path;
                _selectedNode = null; _selectedEdge = null;
                RenderAll(); BuildPropPanel();
                CanvasScroll.ScrollToTop();
                CanvasScroll.ScrollToHorizontalOffset(0);
                AppendLog("info", $"已打开工程：{System.IO.Path.GetFileName(path)}（{_g.Nodes.Count}个节点，{_g.Edges.Count}条连线）");
                return true;
            }
            catch (Exception ex)
            {
                AppendLog("error", $"打开工程失败: {ex.Message}");
                MessageBox.Show("打开失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>当前工程文件路径（主窗口在出错暂停改完后据此热重载）</summary>
        public string? CurrentFilePath => _filePath;

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            if (_moduleMode) { MessageBox.Show("模块编辑窗口不能打开工程文件，请关闭本窗口。"); return; }
            var dlg = new OpenFileDialog { Filter = "流程方案|*.json", InitialDirectory = FlowDir };
            if (dlg.ShowDialog() == true) OpenProjectFile(dlg.FileName);
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_moduleMode) { SaveModule(); return; }
            if (_filePath == null) { SaveAs_Click(sender, e); return; }
            SaveTo(_filePath);
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (_moduleMode)
            {
                MessageBox.Show("模块编辑窗口中「保存」直接写回模块文件，不另存工程。", "模块模式");
                return;
            }
            Directory.CreateDirectory(FlowDir);
            var dlg = new SaveFileDialog { Filter = "流程方案|*.json", InitialDirectory = FlowDir,
                FileName = string.IsNullOrEmpty(_g.Name) ? "流程.json" : _g.Name + ".json" };
            if (dlg.ShowDialog() == true) SaveTo(dlg.FileName);
        }

        private void SaveTo(string path)
        {
            try
            {
                File.WriteAllText(path, _g.ToJson());
                _filePath = path;
                try { File.WriteAllText(LastPath, _g.ToJson()); } catch { }
                AppendLog("info", $"已保存：{System.IO.Path.GetFileName(path)}");
            }
            catch (Exception ex) { MessageBox.Show("保存失败: " + ex.Message); }
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            if (_moduleMode)
            {
                if (MessageBox.Show("清空这个模块里的所有节点和连线？（保存后模块文件也会变空）",
                        "确认", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
                _g = new FlowGraph { Name = _moduleName };
                _selectedNode = null; _selectedEdge = null; _selNodes.Clear();
                RenderAll(); BuildPropPanel();
                return;
            }
            if (MessageBox.Show("清空画布上的所有节点和连线？", "确认", MessageBoxButton.OKCancel) == MessageBoxResult.OK)
                NewGraph(false);
        }

        private void Arrange_Click(object sender, RoutedEventArgs e)
        {
            FlowLayout.Arrange(_g);
            RenderAll();
            if (_selectedNode != null) SelectNode(_selectedNode);
            AppendLog("info", "已按执行顺序自动分层整理（青色虚线=循环/跳转回流线，从节点底部绕行）");
        }

        // ================= 运行/停止 =================
        private void Run_Click(object sender, RoutedEventArgs e)
        {
            if (_host.IsFlowRunning)
            {
                MessageBox.Show("主窗口「流程工程运行台」正在运行，请先在主界面停止后再在工坊内调试运行。",
                    "运行互斥", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (_runner is { IsRunning: true }) return;
            if (!_g.Nodes.Any(n => n.Type == "start"))
            { MessageBox.Show("流程里没有「开始」节点"); return; }
            try { File.WriteAllText(LastPath, _g.ToJson()); } catch { }

            ClearNodeError();
            _cts = new CancellationTokenSource();
            _runner = new FlowRunner(_host.Adapter, AppendLog);
            _runner.HighlightChanged += OnHighlight;
            _runner.VarsChanged += OnVars;
            _runner.NodeError += OnNodeError;
            _runner.Finished += ok => Dispatcher.Invoke(() =>
            {
                RunBtn.IsEnabled = true; StopBtn.IsEnabled = false;
                _lastHighlight = null;
                if (_runner.LastErrorNode == null)
                {
                    // 正常结束/手动停止：停闪烁、恢复卡片
                    _errorTimer?.Stop();
                    _errorFlashOn = false;
                }
                // 出错：红色节点继续慢闪，直到用户修正后重新运行
                foreach (var id in _cards.Keys.ToList()) ApplyCardBrush(_g.Node(id)!);
                if (!ok && _runner.LastErrorNode != null)
                    AppendLog("error", $"流程停在红色闪烁节点，请检查该节点坐标/参数后重新运行");
            });
            var graph = _g;
            var token = _cts.Token;
            RunBtn.IsEnabled = false; StopBtn.IsEnabled = true;
            AppendLog("info", "▶ 流程开始运行（真实操作，F12 可全局急停）");
            Task.Run(() => _runner.Run(graph, token));
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            AppendLog("warn", "正在停止…");
        }

        private void OnHighlight(string? nodeId)
        {
            Dispatcher.Invoke(() =>
            {
                if (_lastHighlight != null && _g.Node(_lastHighlight) != null) ApplyCardBrush(_g.Node(_lastHighlight)!);
                _lastHighlight = nodeId;
                if (nodeId != null && _g.Node(nodeId) != null) ApplyCardBrush(_g.Node(nodeId)!);
            });
        }

        private void OnVars()
        {
            Dispatcher.Invoke(() =>
            {
                VarBox.Items.Clear();
                if (_runner == null) return;
                foreach (var kv in _runner.Vars)
                {
                    var v = kv.Value;
                    string text = v switch
                    {
                        double d when d == Math.Floor(d) => $"{kv.Key} = {d:0}",
                        double dd => $"{kv.Key} = {dd:0.##}",
                        System.Collections.IList list => $"{kv.Key} = [{list.Count} 项数据]",
                        null => $"{kv.Key} = (空)",
                        _ => $"{kv.Key} = {kv.Value}"
                    };
                    VarBox.Items.Add(text.Length > 60 ? text.Substring(0, 60) + "…" : text);
                }
            });
        }

        // ================= 日志 =================
        private void AppendLog(string level, string msg)
        {
            Dispatcher.Invoke(() =>
            {
                var color = level switch
                {
                    "buy" => Color.FromRgb(0x58, 0xE0, 0x7D),
                    "sell" => Color.FromRgb(0xFF, 0xB0, 0x4D),
                    "wait" => Color.FromRgb(0x7F, 0xC6, 0xFF),
                    "claim" => Color.FromRgb(0x3F, 0xD6, 0xC9),
                    "warn" => Color.FromRgb(0xFF, 0xC2, 0x4D),
                    "error" => Color.FromRgb(0xFF, 0x4D, 0x55),
                    "action" => Color.FromRgb(0xCF, 0xD6, 0xE0),
                    _ => Color.FromRgb(0x9A, 0xA6, 0xB5)
                };
                var item = new TextBlock
                {
                    Text = $"{DateTime.Now:HH:mm:ss} {msg}",
                    Foreground = new SolidColorBrush(color),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(2)
                };
                LogBox.Items.Add(item);
                if (LogBox.Items.Count > 500) LogBox.Items.RemoveAt(0);
                LogBox.ScrollIntoView(LogBox.Items[^1]);
            });
        }

        // ================= 内置流程模板加载（建图逻辑在 FlowTemplates） =================
        private void LoadTemplate(FlowTemplates.Template t)
        {
            if (_moduleMode) { MessageBox.Show("模块编辑窗口不能加载主流程模板。"); return; }
            if (_g.Nodes.Count > 1 &&
                MessageBox.Show($"将替换当前画布为「{t.Name}」\n{t.Desc}\n\n继续？",
                "加载流程模板", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;

            _g = t.Graph;
            _filePath = null;
            _selectedNode = null; _selectedEdge = null;
            ClearNodeError();
            RenderAll(); BuildPropPanel();
            CanvasScroll.ScrollToTop();
            CanvasScroll.ScrollToHorizontalOffset(Math.Max(0, FlowLayout.SpineX - CanvasScroll.ViewportWidth / 2));
            AppendLog("claim", $"已加载模板【{t.Name}】：{t.Desc}（{_g.Nodes.Count}个节点，已从上到下自动排版）");
            foreach (var tip in t.Tips) AppendLog("warn", tip);
        }

        private void TplMailTest_Click(object sender, RoutedEventArgs e) => LoadTemplate(FlowTemplates.MailTest());
        private void TplWatch_Click(object sender, RoutedEventArgs e) => LoadTemplate(FlowTemplates.Watch());
        private void TplDipBuy_Click(object sender, RoutedEventArgs e) => LoadTemplate(FlowTemplates.DipBuy());
        private void TplQuickSell_Click(object sender, RoutedEventArgs e) => LoadTemplate(FlowTemplates.QuickSell());
        private void TplRecycle_Click(object sender, RoutedEventArgs e) => LoadTemplate(FlowTemplates.Recycle());
        private void TplStandard_Click(object sender, RoutedEventArgs e) => LoadTemplate(FlowTemplates.Standard());
        private void TplFull_Click(object sender, RoutedEventArgs e) => LoadTemplate(FlowTemplates.FullTrade());
        private void TplLoopForever_Click(object sender, RoutedEventArgs e) => LoadTemplate(FlowTemplates.LoopForever());
        private void TplAnnounceOnly_Click(object sender, RoutedEventArgs e) => LoadTemplate(FlowTemplates.AnnounceOnly());
        private void TplRelistLoop_Click(object sender, RoutedEventArgs e) => LoadTemplate(FlowTemplates.RelistLoop());
        private void TplMailRecycle_Click(object sender, RoutedEventArgs e) => LoadTemplate(FlowTemplates.MailRecycle());

        // ⚡ 更多模板：动态列出 FlowTemplates.All() 全部模板（以后加模板无需改这里）
        private void TplMore_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { Background = new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x20)) };
            foreach (var t in FlowTemplates.All())
            {
                var item = new MenuItem
                {
                    Header = t.Name,
                    ToolTip = t.Desc,
                    Foreground = Brushes.WhiteSmoke
                };
                var tpl = t; // 闭包捕获
                item.Click += (s, args) => LoadTemplate(tpl);
                menu.Items.Add(item);
            }
            menu.PlacementTarget = sender as Button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    // ================= 输入框对话框 =================
    public static class PromptDialog
    {
        public static string? Show(Window owner, string title, string label, string def)
        {
            var win = new Window
            {
                Title = title, Width = 420, Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner, ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x11, 0x16))
            };
            var sp = new StackPanel { Margin = new Thickness(16) };
            sp.Children.Add(new TextBlock { Text = label, Foreground = Brushes.WhiteSmoke, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
            var tb = new TextBox { Text = def };
            sp.Children.Add(tb);
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var ok = FlowEditorWindow.DarkBtn("确定");
            ok.Width = 80; ok.Margin = new Thickness(0, 0, 8, 0); ok.IsDefault = true;
            var cancel = FlowEditorWindow.DarkBtn("取消");
            cancel.Width = 80; cancel.IsCancel = true;
            bar.Children.Add(ok); bar.Children.Add(cancel);
            sp.Children.Add(bar);
            win.Content = sp;
            string? result = null;
            ok.Click += (s, e) => { result = tb.Text; win.DialogResult = true; };
            tb.Loaded += (s, e) => { tb.Focus(); tb.SelectAll(); };
            return win.ShowDialog() == true ? result : null;
        }
    }
}
