using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WindowSpy
{
    /// <summary>
    /// 行情监控台：K 线风格走势（收盘价面积+均线+RSI 副图）、策略信号流、成交流水、累计盈亏曲线。
    /// 全部数据来自进程内 TradeBus 事件总线；后台线程事件统一切到 UI 线程渲染。
    /// </summary>
    public partial class MonitorWindow : Window
    {
        private MarketTick? _lastTick;
        private double _realizedPnl;
        private bool _hooked;

        private static readonly Brush BuyBrush = Frozen("#58E07D");
        private static readonly Brush SellBrush = Frozen("#FF8A2A");
        private static readonly Brush RedBrush = Frozen("#FF4D55");
        private static readonly Brush GrayBrush = Frozen("#9AA4B2");
        private static readonly Brush LineUpBrush = Frozen("#58E07D");
        private static readonly Brush LineDownBrush = Frozen("#FF4D55");
        private static readonly Brush MaBrush = Frozen("#FF8A2A");
        private static readonly Brush RsiBrush = Frozen("#4DA3FF");
        private static readonly Pen GridPen = MakePen("#1E242E", 1);
        private static readonly Pen DashPen = MakePen("#39414D", 1, true);

        public MonitorWindow()
        {
            InitializeComponent();
            Loaded += MonitorWindow_Loaded;
            Closing += (_, _) => Unhook();
        }

        private void MonitorWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Hook();
            // 回放环形缓冲中的历史事件
            foreach (var s in TradeBus.RecentSignals().TakeLast(120)) AddSignal(s);
            var trades = TradeBus.RecentTrades();
            _realizedPnl = trades.Sum(t => t.RealizedPnl);
            foreach (var t in trades.TakeLast(80).Reverse()) AddTrade(t, false);
            var lastTick = TradeBus.RecentTicks().LastOrDefault();
            if (lastTick != null) { _lastTick = lastTick; RenderChart(); }
            UpdatePnl();
            RenderEquity();
        }

        private void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            TradeBus.Tick += OnTick;
            TradeBus.Signal += OnSignal;
            TradeBus.Trade += OnTrade;
            TradeBus.Status += OnStatus;
        }

        private void Unhook()
        {
            if (!_hooked) return;
            _hooked = false;
            TradeBus.Tick -= OnTick;
            TradeBus.Signal -= OnSignal;
            TradeBus.Trade -= OnTrade;
            TradeBus.Status -= OnStatus;
        }

        // ============ 事件入口（切 UI 线程）============
        private void OnTick(MarketTick t) => Dispatcher.BeginInvoke(new Action(() =>
        {
            _lastTick = t;
            RenderChart();
        }));

        private void OnSignal(SignalLog s) => Dispatcher.BeginInvoke(new Action(() => AddSignal(s)));

        private void OnTrade(TradeRecord r) => Dispatcher.BeginInvoke(new Action(() =>
        {
            AddTrade(r, true);
            _realizedPnl += r.RealizedPnl;
            UpdatePnl();
            RenderEquity();
        }));

        private void OnStatus(HostSnapshot s) => Dispatcher.BeginInvoke(new Action(() => RenderStatus(s)));

        // ============ 顶部状态栏 ============
        private void RenderStatus(HostSnapshot s)
        {
            string text; Brush bg; Brush fg;
            switch (s.Status)
            {
                case HostStatusKind.Running: text = "运行中"; bg = Frozen("#14361C"); fg = BuyBrush; break;
                case HostStatusKind.Resting: text = "休息中"; bg = Frozen("#3A2E12"); fg = Frozen("#FFD24D"); break;
                case HostStatusKind.BanHit: text = "封禁降频"; bg = Frozen("#3A161A"); fg = RedBrush; break;
                case HostStatusKind.Error: text = "错误"; bg = Frozen("#3A161A"); fg = RedBrush; break;
                case HostStatusKind.Stopped: text = "已停止"; bg = Frozen("#2A2F3A"); fg = GrayBrush; break;
                default: text = "待机"; bg = Frozen("#2A2F3A"); fg = GrayBrush; break;
            }
            MStatus.Text = text;
            MStatusPill.Background = bg;
            MStatus.Foreground = fg;
            MRound.Text = s.Round.ToString();
            if (s.Wallet > 0) MWallet.Text = s.Wallet.ToString("N0");
            if (s.Holding)
            {
                MHold.Text = $"{s.HoldName} ×{s.HoldLots:0}  买入 {s.HoldPrice:0}";
                MHold.Foreground = SellBrush;
            }
            else { MHold.Text = "空仓"; MHold.Foreground = GrayBrush; }
            if (s.LastPrice > 0)
                MLastPrice.Text = s.Holding ? $"{s.LastPrice:0} / {s.HoldPrice:0}" : $"{s.LastPrice:0}";
            MTrades.Text = s.TradeCount.ToString();
            _realizedPnl = s.RealizedPnl;
            UpdatePnl();
        }

        private void UpdatePnl()
        {
            MPnl.Text = (_realizedPnl >= 0 ? "+" : "") + _realizedPnl.ToString("N0");
            MPnl.Foreground = _realizedPnl > 0 ? BuyBrush : _realizedPnl < 0 ? RedBrush : GrayBrush;
        }

        // ============ 信号流 ============
        private void AddSignal(SignalLog s)
        {
            var (fg, bar) = LevelBrush(s.Level);
            var card = new Border
            {
                Background = Frozen("#161A21"),
                BorderBrush = Frozen("#232936"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(0)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var barRect = new Rectangle { Fill = bar, RadiusX = 1, RadiusY = 1 };
            Grid.SetColumn(barRect, 0);
            var panel = new StackPanel { Margin = new Thickness(8, 5, 8, 6) };
            panel.Children.Add(new TextBlock
            {
                Text = $"{s.Time:HH:mm:ss} · 第 {s.Round} 轮",
                Foreground = Frozen("#5A6472"),
                FontSize = 10
            });
            panel.Children.Add(new TextBlock
            {
                Text = s.Title,
                Foreground = fg,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });
            if (!string.IsNullOrWhiteSpace(s.Detail))
                panel.Children.Add(new TextBlock
                {
                    Text = s.Detail,
                    Foreground = Frozen("#8A93A2"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            Grid.SetColumn(panel, 1);
            grid.Children.Add(barRect);
            grid.Children.Add(panel);
            card.Child = grid;
            SignalPanel.Children.Add(card);
            while (SignalPanel.Children.Count > 150) SignalPanel.Children.RemoveAt(0);
            SignalScroll.ScrollToEnd();
        }

        private static (Brush fg, Brush bar) LevelBrush(SignalLevel lv) => lv switch
        {
            SignalLevel.Buy => (BuyBrush, BuyBrush),
            SignalLevel.Sell => (SellBrush, SellBrush),
            SignalLevel.Claim => (Frozen("#4DD0E1"), Frozen("#4DD0E1")),
            SignalLevel.Ban => (RedBrush, RedBrush),
            SignalLevel.Warn => (Frozen("#FFD24D"), Frozen("#FFD24D")),
            SignalLevel.Error => (RedBrush, RedBrush),
            SignalLevel.Wait => (Frozen("#7A8392"), Frozen("#39414D")),
            _ => (Frozen("#9AA4B2"), Frozen("#39414D"))
        };

        // ============ 成交流水 ============
        public class TradeRow
        {
            public string TimeText { get; set; } = "";
            public string SideText { get; set; } = "";
            public Brush SideBrush { get; set; } = GrayBrush;
            public string Bullet { get; set; } = "";
            public string PriceText { get; set; } = "";
            public int Lots { get; set; }
            public string AmountText { get; set; } = "";
            public string PnlText { get; set; } = "";
            public string RetText { get; set; } = "";
            public string Note { get; set; } = "";
        }

        private void AddTrade(TradeRecord r, bool newestOnTop)
        {
            var row = new TradeRow
            {
                TimeText = r.Time.ToString("HH:mm:ss"),
                SideText = r.IsBuy ? "▲ 买入" : "▼ 卖出",
                SideBrush = r.IsBuy ? BuyBrush : SellBrush,
                Bullet = r.Bullet,
                PriceText = r.Price.ToString("0"),
                Lots = r.Lots,
                AmountText = r.Amount.ToString("N0"),
                PnlText = r.IsBuy || r.RealizedPnl == 0 ? "" : (r.RealizedPnl >= 0 ? "+" : "") + r.RealizedPnl.ToString("N0"),
                RetText = !r.IsBuy && r.ReturnPct != 0 ? (r.ReturnPct >= 0 ? "+" : "") + r.ReturnPct.ToString("0.0") + "%" : "",
                Note = r.Note ?? ""
            };
            if (newestOnTop) TradeList.Items.Insert(0, row);
            else TradeList.Items.Add(row);
            while (TradeList.Items.Count > 200) TradeList.Items.RemoveAt(TradeList.Items.Count - 1);
        }

        // ============ K 线风格走势图 ============
        private void Chart_SizeChanged(object sender, SizeChangedEventArgs e) => RenderChart();

        private void RenderChart()
        {
            var t = _lastTick;
            CandleCanvas.Children.Clear();
            RsiCanvas.Children.Clear();
            if (t == null || t.History.Count < 2)
            {
                ChartName.Text = "等待行情…";
                return;
            }

            var d = t.History;
            int n = d.Count;
            int maN = Math.Max(2, t.MAPeriod);
            var maSeries = Rolling(d, (w) => QuantMath.MA(w, maN), maN);
            var rsiSeries = Rolling(d, (w) => QuantMath.RSI(w, 14), 2);

            double w = CandleCanvas.ActualWidth, h = CandleCanvas.ActualHeight;
            double rw = RsiCanvas.ActualWidth, rh = RsiCanvas.ActualHeight;
            if (w < 20 || h < 20) return;

            double padL = 8, padR = 60, padT = 10, padB = 18;
            double plotW = w - padL - padR, plotH = h - padT - padB;

            double min = d.Min(), max = d.Max();
            foreach (var mv in maSeries) { if (!double.IsNaN(mv)) { min = Math.Min(min, mv); max = Math.Max(max, mv); } }
            double pad = (max - min) * 0.08 + 0.5;
            min -= pad; max += pad;
            double X(int i) => padL + plotW * i / Math.Max(1, n - 1);
            double Y(double v) => padT + plotH * (1 - (v - min) / (max - min));

            // 网格 + 右轴价格
            for (int g = 0; g <= 4; g++)
            {
                double y = padT + plotH * g / 4.0;
                CandleCanvas.Children.Add(new Line { X1 = padL, Y1 = y, X2 = padL + plotW, Y2 = y, Stroke = GridPen.Brush });
                double pv = max - (max - min) * g / 4.0;
                var lbl = new TextBlock { Text = pv.ToString("0"), Foreground = Frozen("#5A6472"), FontSize = 10 };
                Canvas.SetLeft(lbl, padL + plotW + 6);
                Canvas.SetTop(lbl, y - 7);
                CandleCanvas.Children.Add(lbl);
            }

            bool up = d[n - 1] >= d[0];
            var lineBrush = up ? LineUpBrush : LineDownBrush;

            // 面积渐变
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(X(0), Y(d[0])), isFilled: true, isClosed: false);
                for (int i = 1; i < n; i++) ctx.LineTo(new Point(X(i), Y(d[i])), true, false);
                ctx.LineTo(new Point(X(n - 1), padT + plotH), false, false);
                ctx.LineTo(new Point(X(0), padT + plotH), false, false);
            }
            geo.Freeze();
            var grad = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                GradientStops = new GradientStopCollection
                {
                    new(Color.FromArgb(up ? (byte)70 : (byte)55, up ? (byte)0x58 : (byte)0xFF, up ? (byte)0xE0 : (byte)0x4D, up ? (byte)0x7D : (byte)0x55), 0),
                    new(Color.FromArgb(0, 0, 0, 0), 1)
                }
            };
            CandleCanvas.Children.Add(new Path { Data = geo, Fill = grad });

            // 收盘折线
            CandleCanvas.Children.Add(MakePolyline(Enumerable.Range(0, n).Select(i => new Point(X(i), Y(d[i]))), lineBrush, 2));
            // 均线
            var maPts = new List<Point>();
            for (int i = 0; i < n; i++) if (!double.IsNaN(maSeries[i])) maPts.Add(new Point(X(i), Y(maSeries[i])));
            if (maPts.Count > 1) CandleCanvas.Children.Add(MakePolyline(maPts, MaBrush, 1.4));

            // 持仓子弹的成交点标记（买↑ 绿 / 卖↓ 橙，按价格定位到右侧）
            int mk = 0;
            foreach (var tr in TradeBus.RecentTrades().Where(r => r.Bullet == t.Bullet).TakeLast(8))
            {
                if (tr.Price < min || tr.Price > max) continue;
                double cx = padL + plotW - 4 - mk * 16;
                double cy = Y(tr.Price);
                var tri = new Polygon
                {
                    Fill = tr.IsBuy ? BuyBrush : SellBrush,
                    Points = tr.IsBuy
                        ? new PointCollection { new(cx - 4, cy + 5), new(cx + 4, cy + 5), new(cx, cy - 3) }
                        : new PointCollection { new(cx - 4, cy - 5), new(cx + 4, cy - 5), new(cx, cy + 3) }
                };
                CandleCanvas.Children.Add(tri);
                mk++;
            }

            // 最新价点 + 标签
            double lx = X(n - 1), ly = Y(d[n - 1]);
            CandleCanvas.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = lineBrush, Margin = new Thickness(lx - 3.5, ly - 3.5, 0, 0) });

            // 头部数值
            ChartName.Text = t.Bullet + (t.Holding ? "（持仓中）" : "");
            ChartPrice.Text = t.Price.ToString("0");
            ChartPrice.Background = Frozen(up ? "#14361C" : "#3A161A");
            double chg = QuantMath.Change(d, Math.Min(20, n - 1));
            ChartChange.Text = $"{(chg >= 0 ? "▲" : "▼")} {chg:0.00}%（近{Math.Min(20, n - 1)}点）";
            ChartChange.Foreground = chg >= 0 ? BuyBrush : RedBrush;
            LegendMa.Text = t.MA.ToString("0");
            LegendRsi.Text = t.RSI.ToString("0.0");
            LegendPos.Text = t.Position.ToString("0") + "%";

            // ===== RSI 副图 =====
            if (rw > 20 && rh > 20)
            {
                double rPadR = 60;
                double rPlotW = rw - padL - rPadR;
                double RX(int i) => padL + rPlotW * i / Math.Max(1, n - 1);
                double RY(double v) => 8 + (rh - 20) * (1 - v / 100.0);
                foreach (var lv in new[] { 70.0, 50.0, 30.0 })
                {
                    var line = new Line
                    {
                        X1 = padL, Y1 = RY(lv), X2 = padL + rPlotW, Y2 = RY(lv),
                        Stroke = lv == 50 ? GridPen.Brush : DashPen.Brush
                    };
                    RsiCanvas.Children.Add(line);
                    var lbl = new TextBlock { Text = lv.ToString("0"), Foreground = Frozen("#5A6472"), FontSize = 9 };
                    Canvas.SetLeft(lbl, padL + rPlotW + 6);
                    Canvas.SetTop(lbl, RY(lv) - 7);
                    RsiCanvas.Children.Add(lbl);
                }
                var rsiPts = new List<Point>();
                for (int i = 0; i < n; i++) if (!double.IsNaN(rsiSeries[i])) rsiPts.Add(new Point(RX(i), RY(rsiSeries[i])));
                if (rsiPts.Count > 1)
                {
                    RsiCanvas.Children.Add(MakePolyline(rsiPts, RsiBrush, 1.4));
                    var last = rsiPts[^1];
                    RsiCanvas.Children.Add(new Ellipse { Width = 5, Height = 5, Fill = RsiBrush, Margin = new Thickness(last.X - 2.5, last.Y - 2.5, 0, 0) });
                }
                var title = new TextBlock { Text = "RSI(14)", Foreground = Frozen("#7A8392"), FontSize = 10 };
                Canvas.SetLeft(title, 6); Canvas.SetTop(title, 2);
                RsiCanvas.Children.Add(title);
            }
        }

        /// <summary>滚动指标序列；不足窗口的位置填 NaN</summary>
        private static double[] Rolling(IReadOnlyList<double> d, Func<List<double>, double> f, int minPts)
        {
            var r = new double[d.Count];
            var win = new List<double>();
            for (int i = 0; i < d.Count; i++)
            {
                win.Add(d[i]);
                r[i] = i + 1 >= minPts ? f(win.ToList()) : double.NaN;
            }
            return r;
        }

        // ============ 累计盈亏曲线 ============
        private void RenderEquity()
        {
            EquityCanvas.Children.Clear();
            double w = EquityCanvas.ActualWidth, h = EquityCanvas.ActualHeight;
            if (w < 20 || h < 20) return;

            var trades = TradeBus.RecentTrades();
            var cum = new List<double> { 0 };
            double run = 0;
            foreach (var r in trades) { run += r.RealizedPnl; cum.Add(run); }

            double pad = 8;
            double min = Math.Min(0, cum.Min()), max = Math.Max(0, cum.Max());
            if (Math.Abs(max - min) < 1) { max = 1; min = -1; }
            double span = max - min;
            double X(int i) => pad + (w - pad * 2) * i / Math.Max(1, cum.Count - 1);
            double Y(double v) => pad + (h - pad * 2) * (1 - (v - min) / span);

            // 零轴
            double zeroY = Y(0);
            EquityCanvas.Children.Add(new Line
            { X1 = pad, Y1 = zeroY, X2 = w - pad, Y2 = zeroY, Stroke = DashPen.Brush });

            if (cum.Count > 2)
            {
                var pts = cum.Select((v, i) => new Point(X(i), Y(v))).ToList();
                var positive = run >= 0;
                EquityCanvas.Children.Add(MakePolyline(pts, positive ? BuyBrush : RedBrush, 1.6));
                var lp = pts[^1];
                EquityCanvas.Children.Add(new Ellipse
                { Width = 6, Height = 6, Fill = positive ? BuyBrush : RedBrush, Margin = new Thickness(lp.X - 3, lp.Y - 3, 0, 0) });
            }
            var end = new TextBlock
            {
                Text = (run >= 0 ? "+" : "") + run.ToString("N0"),
                Foreground = run > 0 ? BuyBrush : run < 0 ? RedBrush : GrayBrush,
                FontWeight = FontWeights.Bold, FontSize = 11
            };
            Canvas.SetLeft(end, w - 66); Canvas.SetTop(end, 3);
            EquityCanvas.Children.Add(end);
            var start = new TextBlock { Text = "0", Foreground = Frozen("#5A6472"), FontSize = 9 };
            Canvas.SetLeft(start, 3); Canvas.SetTop(start, zeroY - 16 > 2 ? zeroY - 16 : zeroY + 3);
            EquityCanvas.Children.Add(start);
        }

        // ============ 绘图工具 ============
        private static Polyline MakePolyline(IEnumerable<Point> pts, Brush stroke, double thickness)
        {
            var pl = new Polyline
            {
                Stroke = stroke,
                StrokeThickness = thickness,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Points = new PointCollection(pts)
            };
            return pl;
        }

        private static SolidColorBrush Frozen(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        private static Pen MakePen(string hex, double thickness, bool dashed = false)
        {
            var pen = new Pen(Frozen(hex), thickness);
            if (dashed) pen.DashStyle = new DashStyle(new double[] { 4, 3 }, 0);
            pen.Freeze();
            return pen;
        }
    }
}
