using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowSpy
{
    /// <summary>
    /// 流程图自动布局（工业视觉风格·自上而下）：
    /// 主出口（第一个输出口）沿固定主轴垂直向下；分叉时其余出口成为【右侧】并列分支列，
    /// 顶对齐排布；所有分支在「必经汇合节点」（后支配点）处收回到主轴继续向下。
    /// 循环回流边走左侧通道。这样整条主路径是一条直线，支线在右、回流在左。
    /// </summary>
    public static class FlowLayout
    {
        public const double NodeW = 172;
        public const double HeadH = 24;              // 卡片标题条高
        public const double BodyH = 26;              // 单出口卡片内容区高
        public const double PortRowH = 18;           // 多出口每行高
        public const double Y0 = 48;
        public const double GapV = 32;               // 上下层垂直间距
        public const double GapH = 36;               // 右侧并列分支间距
        public const double SpineX = 270;            // 主轴中心 X（主链固定于此）
        public const double MinLeft = 70;            // 左侧回流通道留白

        private const string ExitId = "\0EXIT";      // 虚拟汇点

        public static double NodeH(FlowNode n)
        {
            var def = FlowCatalog.Get(n.Type);
            return HeadH + (def.Outputs.Count > 1 ? def.Outputs.Count * PortRowH : BodyH);
        }

        /// <summary>
        /// 回流边（循环回边）：DFS 中指向【正在访问的祖先】的边（如 loop_while.done 回到循环头）。
        /// 这类边从左侧通道绕行，且不参与正向分层，避免环上最长路径无限增长。
        /// </summary>
        public static HashSet<string> ClassifyBackEdges(FlowGraph g) => Analyze(g).Backs;

        private sealed class Analysis
        {
            public Dictionary<string, int> Depth = new();
            public HashSet<string> Backs = new();
        }

        private static Analysis Analyze(FlowGraph g)
        {
            var an = new Analysis();
            foreach (var n in g.Nodes) an.Depth[n.Id] = 0;
            if (g.Nodes.Count == 0) return an;

            List<FlowEdge> Out(string u) => g.Edges
                .Where(e => e.From == u)
                .OrderBy(e => PortIndex(FlowCatalog.Get(g.Node(u)!.Type), e.Port))
                .ToList();

            // 1) DFS 三色标记：指向灰色祖先 = 回流边
            var color = new Dictionary<string, int>();
            foreach (var n in g.Nodes) color[n.Id] = 0;
            void Dfs(string u)
            {
                color[u] = 1;
                foreach (var e in Out(u))
                {
                    if (!color.ContainsKey(e.To)) continue;
                    if (color[e.To] == 0) Dfs(e.To);
                    else if (color[e.To] == 1) an.Backs.Add(e.Id);
                }
                color[u] = 2;
            }
            var start = g.Nodes.FirstOrDefault(n => n.Type == "start");
            if (start != null) Dfs(start.Id);
            foreach (var n in g.Nodes) if (color[n.Id] == 0) Dfs(n.Id);

            // 2) 去掉回流边后在 DAG 上做拓扑最长路径分层（仅用于后支配点迭代顺序）
            var indeg = new Dictionary<string, int>();
            foreach (var n in g.Nodes) indeg[n.Id] = 0;
            foreach (var e in g.Edges)
                if (!an.Backs.Contains(e.Id)) indeg[e.To]++;
            var tq = new Queue<string>();
            foreach (var n in g.Nodes) if (indeg[n.Id] == 0) tq.Enqueue(n.Id);
            while (tq.Count > 0)
            {
                var u = tq.Dequeue();
                foreach (var e in Out(u))
                {
                    if (an.Backs.Contains(e.Id)) continue;
                    if (an.Depth[e.To] < an.Depth[u] + 1) an.Depth[e.To] = an.Depth[u] + 1;
                    if (--indeg[e.To] == 0) tq.Enqueue(e.To);
                }
            }
            return an;
        }

        public static void Arrange(FlowGraph g)
        {
            if (g.Nodes.Count == 0) return;
            var an = Analyze(g);
            var backs = an.Backs;

            // 正向后继（去重，按出口定义顺序：第一个出口=主出口）
            var succ = new Dictionary<string, List<string>>();
            foreach (var n in g.Nodes) succ[n.Id] = ForwardSucc(g, n.Id, backs);
            var ipdom = ComputeImmediatePostDominators(g, succ, an.Depth);

            var placed = new HashSet<string>();
            var widthMemo = new Dictionary<(string, string), double>();

            double Bottom(string id)
            {
                var nn = g.Node(id);
                return nn == null ? 0 : nn.Y + NodeH(nn);
            }

            // 子树宽度（主轴固定、分支只向右扩展）
            double Width(string u, string stop)
            {
                if (u == stop || u == ExitId || !g.Nodes.Any(n => n.Id == u)) return 0;
                var key = (u, stop);
                if (widthMemo.TryGetValue(key, out double c)) return c;
                var ch = succ[u].Where(x => x != stop).ToList();
                double w;
                if (ch.Count == 0) w = NodeW;
                else if (ch.Count == 1) w = Math.Max(NodeW, Width(ch[0], stop));
                else
                {
                    string j = ipdom.GetValueOrDefault(u) ?? "";
                    bool hasJoin = j.Length > 0 && j != ExitId && j != stop;
                    var branches = ch.Where(x => !hasJoin || x != j).ToList();
                    double block = 0;
                    if (branches.Count > 0)
                    {
                        double mainW = Math.Max(NodeW, Width(branches[0], hasJoin ? j : stop));
                        double side = branches.Skip(1).Sum(c => GapH + Math.Max(NodeW, Width(c, hasJoin ? j : stop)));
                        block = mainW + side;
                    }
                    double after = hasJoin ? Width(j, stop) : 0;
                    w = Math.Max(block, Math.Max(NodeW, after));
                }
                widthMemo[key] = w;
                return w;
            }

            // 放置节点本体，再继续排后继
            double PlaceNode(string u, string stop, double cx, double yTop)
            {
                if (u == stop || u == ExitId) return yTop;
                var node = g.Node(u);
                if (node == null) return yTop;
                if (!placed.Add(u)) return Bottom(u);
                node.X = cx - NodeW / 2;
                node.Y = yTop;
                return Continue(u, stop, yTop + NodeH(node) + GapV);
            }

            // 从已放置的 u 向下排
            double Continue(string u, string stop, double yChild)
            {
                var ch = succ[u].Where(x => x != stop).ToList();
                if (ch.Count == 0) return Bottom(u);

                if (ch.Count == 1)
                {
                    var v = ch[0];
                    if (placed.Contains(v)) return Math.Max(Bottom(u), Bottom(v)); // 跨分支共用节点
                    return PlaceNode(v, stop, g.Node(u)!.X + NodeW / 2, yChild);  // 单后继：严格同轴
                }

                // 分叉：主支（第一出口）固定主轴；其余出口右侧并列
                string j = ipdom.GetValueOrDefault(u) ?? "";
                bool hasJoin = j.Length > 0 && j != ExitId && j != stop;
                string innerStop = hasJoin ? j : stop;
                var branches = ch.Where(x => !hasJoin || x != j).ToList();
                double cx = g.Node(u)!.X + NodeW / 2;
                double maxBot = Bottom(u);

                if (branches.Count > 0)
                {
                    var main = branches[0];
                    double mainW = Math.Max(NodeW, Width(main, innerStop));
                    if (!placed.Contains(main))
                        maxBot = Math.Max(maxBot, PlaceNode(main, innerStop, cx, yChild));
                    double sideLeft = cx - NodeW / 2 + mainW + GapH;
                    for (int i = 1; i < branches.Count; i++)
                    {
                        var c = branches[i];
                        double w = Math.Max(NodeW, Width(c, innerStop));
                        if (!placed.Contains(c))
                            maxBot = Math.Max(maxBot, PlaceNode(c, innerStop, sideLeft + w / 2, yChild));
                        sideLeft += w + GapH;
                    }
                }

                // 汇合收口：回到主轴后继续
                if (hasJoin && !placed.Contains(j))
                    return PlaceNode(j, stop, cx, maxBot + GapV);
                return maxBot;
            }

            var startNode = g.Nodes.FirstOrDefault(n => n.Type == "start");
            double globalBottom = Y0;
            if (startNode != null)
                globalBottom = PlaceNode(startNode.Id, ExitId, SpineX, Y0);
            else
            {
                var roots = g.Nodes.Where(n => !g.Edges.Any(e => e.To == n.Id && !backs.Contains(e.Id))).ToList();
                if (roots.Count == 0) roots = g.Nodes.Take(1).ToList();
                double y = Y0;
                foreach (var r in roots)
                {
                    if (placed.Contains(r.Id)) continue;
                    y = PlaceNode(r.Id, ExitId, SpineX, y) + GapV * 2;
                }
                globalBottom = y;
            }

            // 游离/不可达节点：主轴下方竖排
            foreach (var n in g.Nodes)
            {
                if (placed.Add(n.Id))
                {
                    n.X = SpineX - NodeW / 2;
                    n.Y = globalBottom + GapV;
                    globalBottom = n.Y + NodeH(n);
                }
            }

            // 左对齐到回流通道（不居中：主轴始终在左，分支在右）
            double minX = g.Nodes.Min(n => n.X);
            double shift = MinLeft - minX;
            if (Math.Abs(shift) > 0.5)
                foreach (var n in g.Nodes) n.X += shift;
        }

        /// <summary>正向后继列表：按节点出口定义顺序排序、目标去重</summary>
        private static List<string> ForwardSucc(FlowGraph g, string u, HashSet<string> backs)
        {
            var node = g.Node(u);
            if (node == null) return new List<string>();
            var def = FlowCatalog.Get(node.Type);
            return g.Edges
                .Where(e => e.From == u && !backs.Contains(e.Id))
                .Select((e, seq) => new { e.To, Idx = PortIndex(def, e.Port), Seq = seq })
                .GroupBy(x => x.To)
                .OrderBy(gr => gr.Min(x => x.Idx))
                .ThenBy(gr => gr.Min(x => x.Seq))
                .Select(gr => gr.Key)
                .ToList();
        }

        private static int PortIndex(FlowNodeDef def, string port)
        {
            if (def.Outputs.Count <= 1) return 0;
            int i = def.Outputs.FindIndex(o => o.Port == port);
            return i < 0 ? 99 : i;
        }

        /// <summary>
        /// 求每个节点的直接后支配点（集合迭代法）：
        /// pdom[u] = {u} ∩ ∩ pdom[s]（s 为 u 的所有后继，终节点接虚拟 EXIT）
        /// 直接后支配点 = 严格后支配者中集合最大（离 u 最近）的那个。
        /// 未连接的多出口补独占虚拟汇点，避免图上不存在的路径导致汇合点误判。
        /// </summary>
        private static Dictionary<string, string> ComputeImmediatePostDominators(
            FlowGraph g, Dictionary<string, List<string>> succ, Dictionary<string, int> depth)
        {
            string Sink(string u, int i) => "\0T:" + u + ":" + i;
            var extraSucc = new Dictionary<string, List<string>>();
            var sinkSet = new HashSet<string>();
            foreach (var n in g.Nodes)
            {
                var def = FlowCatalog.Get(n.Type);
                if (def.Outputs.Count <= 1) continue;
                var connected = new HashSet<string>(
                    g.Edges.Where(e => e.From == n.Id).Select(e => e.Port));
                var list = new List<string>();
                for (int i = 0; i < def.Outputs.Count; i++)
                {
                    if (!connected.Contains(def.Outputs[i].Port))
                    {
                        var s = Sink(n.Id, i);
                        list.Add(s); sinkSet.Add(s);
                    }
                }
                if (list.Count > 0) extraSucc[n.Id] = list;
            }

            var all = new HashSet<string>(g.Nodes.Select(n => n.Id)) { ExitId };
            foreach (var s in sinkSet) all.Add(s);
            var pdom = new Dictionary<string, HashSet<string>>();
            foreach (var id in all)
                pdom[id] = id == ExitId ? new HashSet<string> { ExitId } : new HashSet<string>(all);

            string[] DomSucc(string u)
            {
                if (sinkSet.Contains(u)) return new[] { ExitId };
                var real = succ.TryGetValue(u, out var list) ? list : new List<string>();
                if (extraSucc.TryGetValue(u, out var extra)) real = real.Concat(extra).ToList();
                return real.Count > 0 ? real.ToArray() : new[] { ExitId };
            }

            var order = sinkSet
                .Concat(g.Nodes.OrderByDescending(n => depth.GetValueOrDefault(n.Id)).Select(n => n.Id))
                .ToList();
            bool changed = true;
            int guard = 0;
            while (changed && guard++ < all.Count + 5)
            {
                changed = false;
                foreach (var u in order)
                {
                    var ss = DomSucc(u);
                    HashSet<string>? inter = null;
                    bool ready = true;
                    foreach (var s in ss)
                    {
                        var ps = pdom[s];
                        if (inter == null) inter = new HashSet<string>(ps);
                        else inter.IntersectWith(ps);
                        if (inter.Count == 0) { ready = false; break; }
                    }
                    if (!ready || inter == null) continue;
                    var nv = new HashSet<string>(inter) { u };
                    if (!nv.SetEquals(pdom[u])) { pdom[u] = nv; changed = true; }
                }
            }

            var ipdom = new Dictionary<string, string>();
            foreach (var u in g.Nodes.Select(n => n.Id))
            {
                string? best = null;
                int bestSize = -1;
                foreach (var c in pdom[u])
                {
                    if (c == u) continue;
                    int sz = pdom[c].Count;
                    if (sz > bestSize) { bestSize = sz; best = c; }
                }
                if (best != null) ipdom[u] = best;
            }
            return ipdom;
        }
    }
}
