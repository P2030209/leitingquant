using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace WindowSpy
{
    /// <summary>DeepSeek 提问模板：内置多套 + 用户自定义（ai_templates.json），加载后可在输入框继续修改</summary>
    public class AiPromptTemplate
    {
        public string Name { get; set; } = "";
        public string Prompt { get; set; } = "";
        public bool Builtin { get; set; }
    }

    public static class AiTemplates
    {
        private static string ConfigPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ai_templates.json");

        /// <summary>内置模板（提示词中的 {变量名} 执行时自动替换为实时值）</summary>
        public static readonly List<AiPromptTemplate> Builtins = new()
        {
            new AiPromptTemplate
            {
                Name = "① 买入决策（BUY/WAIT）", Builtin = true,
                Prompt = "你是三角洲行动子弹交易员。当前候选子弹：{PickName}，现价{Price}哈弗币，20期均价{Ma20}，RSI14={Rsi14}，今日涨跌{Change}%，区间位置{Pos20}%（0=最低100=最高）。规则：低于均价、RSI<40、区间位置<40 时适合买；暴涨或RSI>70不追。只回答一个单词：BUY 或 WAIT。"
            },
            new AiPromptTemplate
            {
                Name = "② 卖出决策（SELL/HOLD）", Builtin = true,
                Prompt = "你是三角洲行动子弹交易员。我持有{PickName}，买入价{BuyPrice}，现价{Price}，浮盈{ProfitPct}%，RSI14={Rsi14}，区间位置{Pos20}%。规则：RSI>70或区间位置>85为超买应止盈；仍在强势中位可持有。只回答一个单词：SELL 或 HOLD。"
            },
            new AiPromptTemplate
            {
                Name = "③ 超跌反弹确认", Builtin = true,
                Prompt = "子弹{PickName}现价{Price}，今日{Change}%，20期均价{Ma20}，RSI14={Rsi14}，区间低点{Min20}高点{Max20}。判断这是超跌即将反弹，还是下跌中继（基本面变差）。给出结论（只回答 BOUNCE 或 DROP），再用一句话说明依据。"
            },
            new AiPromptTemplate
            {
                Name = "④ 追涨风险评估", Builtin = true,
                Prompt = "子弹{PickName}今日上涨{Change}%，现价{Price}，20期均价{Ma20}，RSI14={Rsi14}，波动率{Vol20}。评估现在追涨的盈亏比与回撤风险。只回答 CHASE（可追） 或 AVOID（回避），再用一句话说明。"
            },
            new AiPromptTemplate
            {
                Name = "⑤ 持仓体检", Builtin = true,
                Prompt = "持仓体检：{PickName} 成本{BuyPrice} 现价{Price} 浮盈{ProfitPct}% RSI14={Rsi14} 位置{Pos20}%。请输出：1)趋势 强/中/弱；2)建议 加仓/持有/减仓/清仓；3)关键止盈价与止损价。共三行，不要废话。"
            },
            new AiPromptTemplate
            {
                Name = "⑥ 综合行情点评", Builtin = true,
                Prompt = "根据以下数据做30字内短线点评：{PickName} 现价{Price} 涨跌{Change}% MA20={Ma20} RSI={Rsi14} 位置={Pos20}% 七日后市预计卖出参考{ExitEst}。结论先行。"
            }
        };

        /// <summary>内置 + 用户自定义（自定义在前，方便首选）</summary>
        public static List<AiPromptTemplate> LoadAll()
        {
            var list = new List<AiPromptTemplate>();
            try
            {
                if (File.Exists(ConfigPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            list.Add(new AiPromptTemplate
                            {
                                Name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "自定义" : "自定义",
                                Prompt = el.TryGetProperty("prompt", out var p) ? p.GetString() ?? "" : "",
                                Builtin = false
                            });
                        }
                    }
                }
            }
            catch { }
            list.AddRange(Builtins);
            return list;
        }

        /// <summary>保存（覆盖）自定义模板列表</summary>
        public static void SaveCustom(List<AiPromptTemplate> custom)
        {
            try
            {
                var arr = new List<object>();
                foreach (var t in custom)
                    arr.Add(new { name = t.Name, prompt = t.Prompt });
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(arr,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public static List<AiPromptTemplate> LoadCustomOnly()
            => LoadAll().FindAll(t => !t.Builtin);
    }
}
