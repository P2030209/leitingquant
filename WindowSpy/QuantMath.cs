using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowSpy
{
    /// <summary>
    /// 量化指标纯函数库：托管引擎 / 步骤引擎 / 行情监控台共用。
    /// 输入为老→新的价格序列。
    /// </summary>
    public static class QuantMath
    {
        public static double MA(IReadOnlyList<double> d, int n)
        {
            var w = Window(d, n);
            return w.Average();
        }

        public static double EMA(IReadOnlyList<double> d, int n)
        {
            var w = Window(d, n);
            double k = 2.0 / (w.Count + 1);
            double ema = w[0];
            for (int i = 1; i < w.Count; i++) ema = w[i] * k + ema * (1 - k);
            return ema;
        }

        /// <summary>相对强弱 0-100（简单均值版，>70 超买，&lt;30 超卖；数据不足返回 50）</summary>
        public static double RSI(IReadOnlyList<double> d, int n)
        {
            var w = Window(d, n);
            if (w.Count < 2) return 50;
            double gain = 0, loss = 0;
            for (int i = 1; i < w.Count; i++)
            {
                var diff = w[i] - w[i - 1];
                if (diff >= 0) gain += diff; else loss -= diff;
            }
            int cnt = w.Count - 1;
            double ag = gain / cnt, al = loss / cnt;
            if (ag < 1e-9 && al < 1e-9) return 50;
            if (al < 1e-9) return 100;
            return 100.0 - 100.0 / (1.0 + ag / al);
        }

        public static double Min(IReadOnlyList<double> d, int n) => Window(d, n).Min();
        public static double Max(IReadOnlyList<double> d, int n) => Window(d, n).Max();

        /// <summary>最新值相对 n 点之前的涨跌幅 %</summary>
        public static double Change(IReadOnlyList<double> d, int n)
        {
            if (d.Count == 0) return 0;
            double old = d.Count > n ? d[d.Count - 1 - n] : d[0];
            double last = d[d.Count - 1];
            return Math.Abs(old) < 1e-9 ? 0 : (last - old) / Math.Abs(old) * 100.0;
        }

        /// <summary>波动率：n 窗口总体标准差</summary>
        public static double Vol(IReadOnlyList<double> d, int n)
        {
            var w = Window(d, n);
            double avg = w.Average();
            return Math.Sqrt(w.Sum(x => (x - avg) * (x - avg)) / w.Count);
        }

        /// <summary>当前价在 n 窗口最低~最高区间的位置 0-100</summary>
        public static double Position(IReadOnlyList<double> d, int n)
        {
            var w = Window(d, n);
            double mn = w.Min(), mx = w.Max(), last = w[w.Count - 1];
            return Math.Abs(mx - mn) < 1e-9 ? 50 : (last - mn) / (mx - mn) * 100.0;
        }

        public static double Last(IReadOnlyList<double> d) => d.Count == 0 ? 0 : d[d.Count - 1];

        /// <summary>按指标名统一计算入口（与步骤引擎 QuantFunc 同名）</summary>
        public static double Compute(string func, IReadOnlyList<double> d, int n)
        {
            switch ((func ?? "").Trim().ToUpperInvariant())
            {
                case "MA":
                case "AVG": return MA(d, n);
                case "EMA": return EMA(d, n);
                case "RSI": return RSI(d, n);
                case "MIN": return Min(d, n);
                case "MAX": return Max(d, n);
                case "CHANGE": return Change(d, n);
                case "VOL": return Vol(d, n);
                case "POSITION": return Position(d, n);
                case "LAST": return d.Count == 0 ? 0 : d[d.Count - 1];
                default: return d.Count == 0 ? 0 : d[d.Count - 1];
            }
        }

        private static List<double> Window(IReadOnlyList<double> d, int n)
        {
            int take = Math.Max(1, Math.Min(n, d.Count));
            return d.Skip(d.Count - take).Take(take).ToList();
        }
    }
}
